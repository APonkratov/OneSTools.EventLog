using System.Text;
using System.Text.Json;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class ProblemRecordTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-problems", Guid.NewGuid().ToString("N"));
    private const string Name = "20261011000000.lgp";
    private string Lgp => Path.Combine(_folder, Name);
    private string Lgf => Path.Combine(_folder, "1Cv8.lgf");
    private static string Row(string comment = "good", string date = "20261011120000", string marker = "N") =>
        "{" + date + "," + marker + ",{0,0},0,0,0,1,1,I,\"" + comment + "\",0,{\"U\"},\"\",0,0,0,1,0,{0}}";
    private EventLogReaderSettings Settings(Action<EventLogProblemRecord>? sink = null, bool strict = false) => new()
    { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc, UseStrictParsing = strict, ProblemRecordHandler = sink };
    public ProblemRecordTests()
    { Directory.CreateDirectory(_folder); File.WriteAllText(Lgf, "{4,\"Test\",1}"); }

    [Theory]
    [InlineData("{short}")]
    [InlineData("{1,,2}")]
    [InlineData("{\"quoted {nested} \"\" text\"}")]
    public void CompletedBadRecordIsLoggedExactlyOnceThenReadingContinues(string bad)
    {
        var prefix = "\uFEFFjournal\n" + Row("first") + ",\n";
        File.WriteAllText(Lgp, prefix + bad + ",\n" + Row("last"), new UTF8Encoding(false));
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add));
        Assert.Equal("first", reader.ReadNextEventLogItem().Comment);
        var last = reader.ReadNextEventLogItem();
        Assert.Equal("last", last.Comment); Assert.Equal(2, last.Id);
        Assert.Null(reader.ReadNextEventLogItem());
        var problem = Assert.Single(problems);
        Assert.Equal(Name, problem.FileName); Assert.Equal("LGP record", problem.Stage);
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), problem.Position);
        Assert.Equal(problem.Position + Encoding.UTF8.GetByteCount(bad), problem.EndPosition);
        Assert.True(problem.Skipped); Assert.NotNull(problem.Error);
        Assert.Equal(bad, problem.RawRecord);
        Assert.Equal(bad, JsonSerializer.Deserialize<string>(problem.RawRecordJson));
    }

    [Fact]
    public void InvalidUtf8IsPreservedAsOriginalBytesAndDoesNotHideTheNextRecord()
    {
        byte[] bad = [(byte)'{', (byte)'"', 0xc3, 0x28, (byte)'"', (byte)'}'];
        File.WriteAllBytes(Lgp, bad.Concat(Encoding.UTF8.GetBytes("," + Row())).ToArray());
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add));
        Assert.Equal("good", reader.ReadNextEventLogItem().Comment);
        var problem = Assert.Single(problems);
        Assert.Equal("base64", problem.Encoding); Assert.Equal(bad, Convert.FromBase64String(problem.RawRecord));
        Assert.Equal(bad.Length, problem.EndPosition); Assert.Null(reader.ReadNextEventLogItem());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryRespectsQuotedBracesAndBufferBoundaries(bool deep)
    {
        var bad = deep ? new string('{', 260) + "\"{\"" + new string('}', 260) :
            "{\"" + new string('я', 12000) + "\n{ } \"\"quoted\"\"\"}";
        File.WriteAllText(Lgp, bad + "," + Row());
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add));
        Assert.Equal("good", reader.ReadNextEventLogItem().Comment);
        Assert.Equal(bad, Assert.Single(problems).RawRecord);
    }

    [Fact]
    public void HandlerFailureDoesNotConsumeRecordOrIncrementId()
    {
        File.WriteAllText(Lgp, "{bad}," + Row());
        var attempts = 0;
        var settings = Settings(p => { attempts++; if (attempts == 1) throw new FormatException("log unavailable"); });
        using var reader = new EventLogReader(settings);
        var error = Assert.Throws<IOException>(() => reader.ReadNextEventLogItem());
        Assert.IsType<FormatException>(error.InnerException); Assert.Equal(0, settings.ItemId);
        Assert.Equal(1, reader.ReadNextEventLogItem().Id); Assert.Equal(2, attempts);
    }

    [Fact]
    public void DefaultLibraryBehaviorStillRejectsAndRewindsMalformedRecords()
    {
        File.WriteAllText(Lgp, "{bad}," + Row());
        using var reader = new EventLogReader(Settings());
        Assert.Equal(0, Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).Position);
        Assert.Equal(0, Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem()).Position);
    }

    [Fact]
    public void DiagnosticCallbackFormatExceptionIsNotMistakenForACorruptEvent()
    {
        File.WriteAllText(Lgf, "{4,\"_$Future$_.Event\",1}"); File.WriteAllText(Lgp, Row());
        var problems = new List<EventLogProblemRecord>(); var attempts = 0;
        var settings = Settings(problems.Add);
        settings.DiagnosticHandler = _ => { if (++attempts == 1) throw new FormatException("diagnostic sink failed"); };
        using var reader = new EventLogReader(settings);
        var error = Assert.Throws<FormatException>(() => reader.ReadNextEventLogItem());
        Assert.Equal("diagnostic sink failed", error.Message); Assert.Empty(problems); Assert.Equal(0, settings.ItemId);
        Assert.Equal(1, reader.ReadNextEventLogItem().Id); Assert.Empty(problems);
    }

    [Fact]
    public void DiagnosticCallbackFormatExceptionIsNotMistakenForACorruptDictionaryEntry()
    {
        File.WriteAllText(Lgf, "{11,\"unknown\",1},{4,\"Test\",1}"); File.WriteAllText(Lgp, Row());
        var problems = new List<EventLogProblemRecord>(); var settings = Settings(problems.Add);
        settings.DiagnosticHandler = _ => throw new FormatException("diagnostic sink failed");
        Assert.Throws<FormatException>(() => new EventLogReader(settings)); Assert.Empty(problems);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MalformedDictionaryEntriesDoNotBlockLaterValidEntries(bool strict, bool context)
    {
        File.WriteAllText(Lgf, "{4,\"bad\",oops},{1,,2},{4,\"Test\",1}");
        File.WriteAllText(Lgp, Row());
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(problems.Add, strict); settings.UseEventContext = context;
        using var reader = new EventLogReader(settings);
        Assert.Equal("Test", reader.ReadNextEventLogItem().Event);
        Assert.All(problems, p => Assert.Equal("1Cv8.lgf", p.FileName));
        Assert.Contains(problems, p => p.RawRecord == "{4,\"bad\",oops}");
        Assert.Contains(problems, p => p.RawRecord == "{1,,2}");
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void LiveIncompleteTailIsNotLoggedOrSkippedAndCanBeAppended()
    {
        var row = Row("tail"); File.WriteAllText(Lgp, row[..^1]);
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(problems.Add, true); settings.LiveMode = true; settings.ReadingTimeout = 0;
        using var reader = new EventLogReader(settings);
        Assert.Throws<EventLogReaderTimeoutException>(() => reader.ReadNextEventLogItem());
        Assert.Empty(problems); Assert.Equal(0, settings.ItemId);
        File.AppendAllText(Lgp, "}");
        Assert.Equal("tail", reader.ReadNextEventLogItem().Comment); Assert.Empty(problems);
    }

    [Fact]
    public void IncompleteArchivedTailIsLoggedForDiagnosisButNotSkipped()
    {
        var tail = Row("tail")[..^1]; File.WriteAllText(Lgp, tail);
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add, true));
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        var problem = Assert.Single(problems); Assert.False(problem.Skipped); Assert.Equal(tail, problem.RawRecord);
    }

    [Fact]
    public void IncompleteArchivedDictionaryTailIsLoggedWithoutDiscardingIt()
    {
        const string tail = "{4,\"unfinished";
        File.AppendAllText(Lgf, "," + tail); File.WriteAllText(Lgp, Row());
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add, true));
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        var problem = Assert.Single(problems); Assert.False(problem.Skipped); Assert.Equal(tail, problem.RawRecord);
        Assert.Equal("1Cv8.lgf", problem.FileName);
    }

    [Fact]
    public void CancellationBeforeRecoveryLeavesTheFailedRecordForTheNextCall()
    {
        File.WriteAllText(Lgp, "{bad}," + Row());
        var problems = new List<EventLogProblemRecord>();
        using var cancellation = new CancellationTokenSource();
        var settings = Settings(problems.Add);
        settings.DiagnosticHandler = d => { if (d.Code == "InvalidRecord") cancellation.Cancel(); };
        using var reader = new EventLogReader(settings);
        Assert.Throws<OperationCanceledException>(() => reader.ReadNextEventLogItem(cancellation.Token));
        Assert.Empty(problems); Assert.Equal(0, settings.ItemId);
        Assert.Equal("good", reader.ReadNextEventLogItem().Comment);
        Assert.Equal("{bad}", Assert.Single(problems).RawRecord);
    }

    [Fact]
    public void UnknownValidEventsAndDataRemainExportableWithoutProblemLogs()
    {
        File.WriteAllText(Lgf, "{4,\"Unknown.Future.Event\",1}");
        File.WriteAllText(Lgp, Row().Replace("{\"U\"}", "{\"Future\",{\"field\",\"value\"}}"));
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(problems.Add, true); settings.UseStructuredDataParsing = true;
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("Unknown.Future.Event", item.Event); Assert.Contains("Future", item.Data);
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDatesLogRawContentAndRetainTheConfiguredCompatibilityMode(bool strict)
    {
        var bad = Row("date", "invalid"); File.WriteAllText(Lgp, bad + "," + Row());
        var problems = new List<EventLogProblemRecord>();
        using var reader = new EventLogReader(Settings(problems.Add, strict));
        var item = reader.ReadNextEventLogItem();
        Assert.Equal(strict ? "good" : "date", item.Comment);
        if (!strict) Assert.Equal(DateTime.MinValue, item.DateTime);
        var problem = Assert.Single(problems); Assert.Equal(strict, problem.Skipped); Assert.Equal(bad, problem.RawRecord);
    }

    [Fact]
    public void OptionalTransactionIndexRecoversWithoutWaitingForFutureOutcomes()
    {
        File.WriteAllText(Lgp, Row("in transaction", marker: "U") + ",{bad}," + Row());
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(problems.Add); settings.UseFinalTransactionStatuses = true;
        using var reader = new EventLogReader(settings);
        Assert.Equal("Не завершена", reader.ReadNextEventLogItem().TransactionStatus);
        Assert.Equal("good", reader.ReadNextEventLogItem().Comment);
        Assert.Contains(problems, p => p.Stage == "transaction index");
        Assert.Contains(problems, p => p.Stage == "LGP record");
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
