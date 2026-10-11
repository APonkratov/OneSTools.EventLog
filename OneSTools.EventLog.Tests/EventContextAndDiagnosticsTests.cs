using System.Text;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class EventContextAndDiagnosticsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-context", Guid.NewGuid().ToString("N"));
    private const string Name = "20261011000000.lgp";
    private string Lgp => Path.Combine(_folder, Name);
    private string Lgf => Path.Combine(_folder, "1Cv8.lgf");
    private const string Dictionary = "{4,\"Corpus.Test\",1},{3,\"1CV8C\",1}," +
        "{5,11111111-1111-1111-1111-111111111111,\"old\",1}," +
        "{9,11111111-1111-1111-1111-111111111111,\"TenantA\",1}," +
        "{9,22222222-2222-2222-2222-222222222222,\"TenantB\",2}," +
        "{10,{\"N\",123},1,1},{10,{\"N\",456},2,1}";
    private static string Row(string data = "{\"S\",\"  alpha  \"}", string extra = "0,{2,1,1,2,1},future") =>
        "{20261011120000,N,{0,0},0,0,1,1,1,I,\"\",1," + data + ",\"\",0,0,0,1," + extra + "}";
    private EventLogReaderSettings Settings(bool context = false, bool strict = false) => new()
    { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc, UseEventContext = context, UseStrictParsing = strict };
    public EventContextAndDiagnosticsTests()
    { Directory.CreateDirectory(_folder); File.WriteAllText(Lgf, Dictionary); File.WriteAllText(Lgp, Row()); }

    [Fact]
    public void SeparationValuesMatchIndependentPlatformFixtures()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "context-8.3.27.json")))!;
        File.WriteAllText(Lgf, fixtures["dictionary"]!.GetValue<string>());
        foreach (var fixture in fixtures["cases"]!.AsArray())
        {
            File.WriteAllText(Lgp, fixture!["row"]!.GetValue<string>());
            using var reader = new EventLogReader(Settings(context: true, strict: true));
            var actual = JsonNode.Parse(reader.ReadNextEventLogItem().Data)!;
            Assert.True(JsonNode.DeepEquals(fixture["expected"], actual["SessionDataSeparation"]));
        }
    }

    [Fact]
    public void NamedReferencesMatchNativeFixturesWithoutLosingTheirIdentity()
    {
        var fixtures = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "named-data-8.3.27.json")))!;
        foreach (var fixture in fixtures["cases"]!.AsArray())
        {
            File.WriteAllText(Lgf, "{4,\"" + fixture!["eventCode"]!.GetValue<string>() + "\",1}");
            File.WriteAllText(Lgp, Row(fixture["data"]!.GetValue<string>()));
            var settings = Settings(); settings.UseStructuredDataParsing = true;
            using var reader = new EventLogReader(settings);
            var actual = JsonNode.Parse(reader.ReadNextEventLogItem().Data)!;
            Assert.True(JsonNode.DeepEquals(fixture["nodes"], actual["nodes"]));
            var values = actual["values"]!.DeepClone().AsObject();
            foreach (var name in values.Select(p => p.Key).ToArray())
            {
                if (values[name] is not JsonObject reference) continue;
                Assert.Equal("@", reference["$type"]!.GetValue<string>());
                var identity = reference["value"]!.GetValue<string>();
                var native = fixture["expectedNative"]![name];
                if (native == null) Assert.EndsWith(new string('0', 32), identity);
                else Assert.EndsWith("(" + identity + ")", native.GetValue<string>());
                values[name] = native?.DeepClone();
            }
            Assert.True(JsonNode.DeepEquals(fixture["expectedNative"], values));
        }
    }

    [Fact]
    public void ContextKeepsRawDataExtrasAndResolvedNonzeroSeparationWithoutChangingOtherFields()
    {
        using var old = new EventLogReader(Settings());
        var baseline = old.ReadNextEventLogItem();
        using var current = new EventLogReader(Settings(context: true));
        var item = current.ReadNextEventLogItem();
        var envelope = JsonNode.Parse(item.Data)!;
        Assert.Equal("1c-event-context-v1", envelope["$format"]!.GetValue<string>());
        Assert.Equal("Corpus.Test", envelope["EventCode"]!.GetValue<string>());
        Assert.Equal("1CV8C", envelope["ApplicationCode"]!.GetValue<string>());
        Assert.Equal(baseline.Data, envelope["Data"]!.GetValue<string>());
        Assert.Equal("  alpha  ", envelope["DataNodes"]![1]!.GetValue<string>());
        Assert.Equal("future", envelope["ExtraFields"]![2]!.GetValue<string>());
        Assert.Equal("123", envelope["SessionDataSeparation"]!["TenantA"]!.GetValue<string>());
        Assert.Equal("456", envelope["SessionDataSeparation"]!["TenantB"]!.GetValue<string>());
        Assert.Equal(4, envelope["SeparationDictionary"]!.AsArray().Count);
        item.Data = baseline.Data;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(baseline), System.Text.Json.JsonSerializer.Serialize(item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContextUpdatesAndRestoresIndependentlyOfLegacyDictionaryNames(bool live)
    {
        var settings = Settings(context: true); settings.LiveMode = live; settings.ReadingTimeout = 0;
        using var reader = new EventLogReader(settings);
        var first = reader.ReadNextEventLogItem();
        File.AppendAllText(Lgf, ",{10,{\"N\",789},1,1},{5,11111111-1111-1111-1111-111111111111,\"new\",1}");
        File.AppendAllText(Lgp, "," + Row());
        var second = reader.ReadNextEventLogItem();
        Assert.Equal("old", second.Metadata);
        Assert.Equal("789", JsonNode.Parse(second.Data)!["SessionDataSeparation"]!["TenantA"]!.GetValue<string>());
        var restart = Settings(context: true);
        restart.LgpFileName = first.FileName; restart.LgpStartPosition = first.EndPosition;
        restart.LgfStartPosition = first.LgfEndPosition; restart.ItemId = first.Id;
        using var resumed = new EventLogReader(restart);
        var after = resumed.ReadNextEventLogItem();
        Assert.Equal(second.Data, after.Data);
        Assert.Equal(second.Metadata, after.Metadata);
        Assert.Equal(second.Id, after.Id);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void DictionaryPollingAfterEofStillDetectsAppendAndTruncation(bool context, bool strict, bool latest)
    {
        File.WriteAllText(Lgp, string.Join(",", Enumerable.Repeat(Row(), 4)));
        var settings = Settings(context, strict); settings.UseLatestDictionaryValues = latest;
        using var reader = new EventLogReader(settings);
        Assert.NotNull(reader.ReadNextEventLogItem());
        Assert.NotNull(reader.ReadNextEventLogItem()); // unchanged LGF at EOF
        File.AppendAllText(Lgf, ",{5,11111111-1111-1111-1111-111111111111,\"new\",1},{10,{\"N\",789},1,1}");
        var updated = reader.ReadNextEventLogItem();
        Assert.Equal(latest ? "new" : "old", updated.Metadata);
        if (context)
            Assert.Equal("789", JsonNode.Parse(updated.Data)!["SessionDataSeparation"]!["TenantA"]!.GetValue<string>());
        using (var writer = new FileStream(Lgf, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) writer.SetLength(0);
        Assert.ThrowsAny<IOException>(() => reader.ReadNextEventLogItem());
    }

    [Fact]
    public void StrictPollingAtEofDoesNotHideANewIncompleteDictionaryTail()
    {
        File.WriteAllText(Lgp, Row() + "," + Row());
        using var reader = new EventLogReader(Settings(strict: true));
        Assert.NotNull(reader.ReadNextEventLogItem());
        File.AppendAllText(Lgf, ",{4,\"unfinished");
        Assert.Equal("1Cv8.lgf", Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).FileName);
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        File.AppendAllText(Lgf, "\",2}");
        Assert.Equal(2, reader.ReadNextEventLogItem().Id);
    }

    [Fact]
    public void UnresolvedContextAndUnknownDictionaryRecordsKeepEvidence()
    {
        File.WriteAllText(Lgf, "{4,\"Corpus.Test\",1},{3,\"1CV8C\",1},{14,\"future\",{\"N\",42}}");
        var diagnostics = new List<EventLogDiagnostic>();
        var settings = Settings(context: true); settings.DiagnosticHandler = diagnostics.Add;
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        var envelope = JsonNode.Parse(item.Data)!;
        Assert.Null(envelope["SessionDataSeparation"]);
        Assert.Equal("2", envelope["ExtraFields"]![1]![0]!.GetValue<string>());
        Assert.Contains(diagnostics, d => d.Code == "UnresolvedSeparation" && d.NodesJson!.Contains("\"2\""));
        Assert.Contains(diagnostics, d => d.Code == "DictionaryRecord" && d.NodesJson!.Contains("future"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedPairsKeepReferenceIdentityAndUndefinedEvenWhenNativePresentationIsEmpty(bool emptyReference)
    {
        var reference = "255:" + (emptyReference ? new string('0', 32) : new string('1', 32));
        File.WriteAllText(Lgp, Row("{\"P\",{3,{\"Ссылка\",{\"@\",\"" + reference + "\"}},{\"Имя\",{\"S\",\"fixture\"}},{\"Неопределено\",{\"U\"}}}}"));
        var settings = Settings(); settings.UseStructuredDataParsing = true;
        using var reader = new EventLogReader(settings);
        var data = JsonNode.Parse(reader.ReadNextEventLogItem().Data)!;
        Assert.Equal("1c-named-data-v1", data["$format"]!.GetValue<string>());
        Assert.Equal(reference, data["values"]!["Ссылка"]!["value"]!.GetValue<string>());
        Assert.Equal("@", data["values"]!["Ссылка"]!["$type"]!.GetValue<string>());
        Assert.Equal("U", data["values"]!["Неопределено"]!["$type"]!.GetValue<string>());
        Assert.Equal("fixture", data["values"]!["Имя"]!.GetValue<string>());
        Assert.Equal(reference, data["nodes"]![1]![1]![1]![1]!.GetValue<string>());
    }

    [Fact]
    public void MalformedDictionaryEntryRetainsItsPositionAndCanBeRepaired()
    {
        const string prefix = "{4,\"Corpus.Test\",1},";
        File.WriteAllText(Lgf, prefix + "{3,\"1CV8C\",invalid}");
        using var reader = new EventLogReader(Settings());
        var error = Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        Assert.Equal("1Cv8.lgf", error.FileName);
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), error.Position);
        Assert.Equal(error.Position, Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).Position);
        File.WriteAllText(Lgf, prefix + "{3,\"1CV8C\",1},{5,11111111-1111-1111-1111-111111111111,\"old\",1}");
        Assert.Equal(1, reader.ReadNextEventLogItem().Id);
    }

    [Fact]
    public void DiagnosticCallbackFailureDoesNotConsumeUnknownDictionaryEntry()
    {
        File.WriteAllText(Lgf, "{4,\"Corpus.Test\",1},{14,\"future\"},{3,\"1CV8C\",1},{5,11111111-1111-1111-1111-111111111111,\"old\",1}");
        var count = 0; var fail = true;
        var settings = Settings(); settings.DiagnosticHandler = d =>
        {
            if (d.Code != "DictionaryRecord") return;
            count++;
            if (fail) throw new IOException("callback failure");
        };
        using var reader = new EventLogReader(settings);
        Assert.Equal("callback failure", Assert.Throws<IOException>(() => reader.ReadNextEventLogItem()).Message);
        fail = false;
        Assert.Equal(1, reader.ReadNextEventLogItem().Id);
        Assert.Equal(2, count);
    }

    [Fact]
    public void InvalidRecordDoesNotAdvanceReaderOrIdAndCanBeRepaired()
    {
        var first = Row(); var bad = "{20261011120000,N,{0,0},0}";
        File.WriteAllText(Lgp, first + ",\n" + bad);
        using var reader = new EventLogReader(Settings());
        Assert.Equal(1, reader.ReadNextEventLogItem().Id);
        var error = Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        Assert.Equal(Encoding.UTF8.GetByteCount(first + ",\n"), error.Position);
        Assert.Equal(Name, error.FileName);
        Assert.Equal(error.Position, Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).Position);
        File.WriteAllText(Lgp, first + ",\n" + Row());
        Assert.Equal(2, reader.ReadNextEventLogItem().Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDateRetainsLegacyBehaviorOrFailsExplicitly(bool strict)
    {
        File.WriteAllText(Lgp, Row().Replace("20261011120000", "invalid"));
        var diagnostics = new List<EventLogDiagnostic>();
        var settings = Settings(strict: strict); settings.DiagnosticHandler = diagnostics.Add;
        using var reader = new EventLogReader(settings);
        if (strict) Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        else Assert.Equal(DateTime.MinValue, reader.ReadNextEventLogItem().DateTime);
        Assert.Contains(diagnostics, d => d.Code == "InvalidDate");
    }

    [Fact]
    public void StrictArchiveRejectsIncompleteRecordButLiveWaitsForItsTail()
    {
        var full = Row(); File.WriteAllText(Lgp, full[..^2]);
        using (var archive = new EventLogReader(Settings(strict: true)))
            Assert.Throws<EventLogParseException>(() => archive.ReadNextEventLogItem());
        var settings = Settings(strict: true); settings.LiveMode = true; settings.ReadingTimeout = 0;
        using var live = new EventLogReader(settings);
        Assert.Throws<EventLogReaderTimeoutException>(() => live.ReadNextEventLogItem());
        File.AppendAllText(Lgp, full[^2..]);
        Assert.Equal(1, live.ReadNextEventLogItem().Id);
    }

    [Fact]
    public void StrictModeDoesNotSkipIncompletePeriodWhenNextFileExists()
    {
        File.WriteAllText(Lgp, Row()[..^2]);
        File.WriteAllText(Path.Combine(_folder, "20261012000000.lgp"), Row());
        var settings = Settings(strict: true); settings.LiveMode = true;
        using var reader = new EventLogReader(settings);
        var error = Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        Assert.Equal(Name, error.FileName);
    }

    [Fact]
    public void StrictArchiveRejectsIncompleteDictionaryAndMissingReferences()
    {
        File.AppendAllText(Lgf, ",{4,\"unfinished");
        using (var reader = new EventLogReader(Settings(strict: true)))
            Assert.Equal("1Cv8.lgf", Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).FileName);
        File.WriteAllText(Lgf, "{4,\"Corpus.Test\",1}");
        using var missing = new EventLogReader(Settings(strict: true));
        Assert.Throws<EventLogParseException>(() => missing.ReadNextEventLogItem());
        Assert.Throws<EventLogParseException>(() => missing.ReadNextEventLogItem());
    }

    [Fact]
    public void WatcherNotificationAlreadyQueuedAtDisposalDoesNotEscapeItsCallback()
    {
        var settings = Settings(); settings.LiveMode = true; settings.ReadingTimeout = 0;
        var reader = new EventLogReader(settings);
        Assert.NotNull(reader.ReadNextEventLogItem());
        var field = typeof(EventLogReader).GetField("_lgpChangedCreated", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var signal = field.GetValue(reader);
        reader.Dispose();
        var callback = typeof(EventLogReader).GetMethod("LgpFilesWatcher_Event", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var notification = new FileSystemEventArgs(WatcherChangeTypes.Changed, _folder, Name);
        callback.Invoke(reader, new object[] { reader, notification });
        // Also exercise a callback which captured the handle before disposal.
        field.SetValue(reader, signal);
        callback.Invoke(reader, new object[] { reader, notification });
    }

    [Fact]
    public void FormattingAfterQuotesAndQuotedDelimitersDoNotChangeValuesOrByteCursor()
    {
        var data = "{\"P\",{1,{\"Имя\"\n,{\"S\",\"{a}, \"\"b\"\"\"\n}\n}\n}\n}";
        var text = Row(data);
        File.WriteAllText(Lgp, text);
        var settings = Settings(); settings.UseStructuredDataParsing = true;
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        var json = JsonNode.Parse(item.Data)!;
        Assert.Equal("{a}, \"b\"", json["values"]!["Имя"]!.GetValue<string>());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), item.EndPosition);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Theory]
    [InlineData("{\"P\",{2,{\"Name\",{\"S\",\"a\"}},{\"Name\",{\"S\",\"b\"}}}}")]
    [InlineData("{\"P\",{1,{\"Name\",{\"Future\",\"a\"}}}}")]
    [InlineData("{\"P\",{2,{\"Name\",{\"S\",\"a\"}}}}")]
    public void InvalidOrUnknownNamedLayoutFallsBackAsAWhole(string wire)
    {
        File.WriteAllText(Lgp, Row(wire));
        var settings = Settings(); settings.UseStructuredDataParsing = true;
        using var reader = new EventLogReader(settings);
        var json = JsonNode.Parse(reader.ReadNextEventLogItem().Data)!;
        Assert.Equal("1c-brackets-v1", json["$format"]!.GetValue<string>());
        Assert.Equal("P", json["nodes"]![0]!.GetValue<string>());
        Assert.Null(json["values"]);
    }

    [Fact]
    public void InvalidUtf8AndExcessiveNestingFailWithPositionInsteadOfReplacementOrStackOverflow()
    {
        File.WriteAllBytes(Lgp, Encoding.UTF8.GetBytes(Row()));
        var bytes = File.ReadAllBytes(Lgp);
        bytes[Array.IndexOf(bytes, (byte)'a')] = 0xff;
        File.WriteAllBytes(Lgp, bytes);
        using (var invalid = new EventLogReader(Settings()))
            Assert.Equal(0, Assert.Throws<EventLogParseException>(() => invalid.ReadNextEventLogItem()).Position);
        File.WriteAllText(Lgp, Row(new string('{', 260) + "0" + new string('}', 260)));
        using var deep = new EventLogReader(Settings());
        Assert.Throws<EventLogParseException>(() => deep.ReadNextEventLogItem());
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
