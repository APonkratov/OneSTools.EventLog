using System.Text;
using System.Text.Json;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class StrictParsingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-strict", Guid.NewGuid().ToString("N"));
    private const string Name = "20261011000000.lgp";
    private string Lgp => Path.Combine(_folder, Name);
    private string Lgf => Path.Combine(_folder, "1Cv8.lgf");
    private static string Row(string marker = "N") =>
        "{20261011120000," + marker + ",{0,0},0,0,0,1,1,I,\"good\",0,{\"U\"},\"\",0,0,0,1,0,{0}}";
    private EventLogReaderSettings Settings(bool strict, List<EventLogProblemRecord> problems) => new()
    {
        LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc,
        UseStrictParsing = strict, ProblemRecordHandler = problems.Add
    };

    public StrictParsingTests()
    { Directory.CreateDirectory(_folder); File.WriteAllText(Lgf, "{4,\"Test\",1}"); }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StrictModeLogsWithoutSkippingAndKeepsTheSameErrorOnRetry(bool badDate, bool resume)
    {
        var bad = badDate ? Row().Replace("20261011120000", "invalid") : "{bad}";
        var prefix = Row() + ",";
        File.WriteAllText(Lgp, prefix + bad + "," + Row());
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(true, problems);
        if (resume)
        {
            settings.LgpFileName = Name;
            settings.LgpStartPosition = Encoding.UTF8.GetByteCount(Row());
            settings.ItemId = 1;
        }
        using (var reader = new EventLogReader(settings))
        {
            if (!resume) Assert.Equal(1, reader.ReadNextEventLogItem().Id);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
                Assert.Equal(Encoding.UTF8.GetByteCount(prefix), error.Position);
                Assert.Equal(1, settings.ItemId);
                Assert.False(problems[^1].Skipped);
                Assert.Equal(bad, problems[^1].RawRecord);
            }
        }
        var normal = Settings(false, problems);
        normal.LgpFileName = Name; normal.LgpStartPosition = Encoding.UTF8.GetByteCount(Row()); normal.ItemId = 1;
        using var restarted = new EventLogReader(normal);
        Assert.Equal(2, restarted.ReadNextEventLogItem().Id);
        Assert.True(problems[^1].Skipped);
        Assert.Null(restarted.ReadNextEventLogItem());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictModePreservesInvalidUtf8Bytes(bool strict)
    {
        byte[] bad = [(byte)'{', (byte)'"', 0xff, (byte)'"', (byte)'}'];
        File.WriteAllBytes(Lgp, bad.Concat(Encoding.UTF8.GetBytes("," + Row())).ToArray());
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(strict, problems);
        using var reader = new EventLogReader(settings);
        if (strict) Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        else Assert.Equal(1, reader.ReadNextEventLogItem().Id);
        var problem = Assert.Single(problems);
        Assert.Equal("base64", problem.Encoding);
        Assert.Equal(bad, Convert.FromBase64String(problem.RawRecord));
        Assert.Equal(!strict, problem.Skipped);
        Assert.Equal(strict ? 0 : 1, settings.ItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictModeAlsoStopsDictionaryAndContextScans(bool context)
    {
        File.WriteAllText(Lgp, Row() + "," + Row());
        var problems = new List<EventLogProblemRecord>();
        var settings = Settings(true, problems); settings.UseEventContext = context;
        using var reader = new EventLogReader(settings);
        Assert.Equal(1, reader.ReadNextEventLogItem().Id);
        var prefix = File.ReadAllText(Lgf) + ",";
        File.AppendAllText(Lgf, ",{4,\"bad\",invalid},{4,\"Test\",2}");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
            Assert.Equal("1Cv8.lgf", error.FileName);
            Assert.Equal(Encoding.UTF8.GetByteCount(prefix), error.Position);
            Assert.Equal(1, settings.ItemId);
            Assert.False(problems[^1].Skipped);
            Assert.Equal("{4,\"bad\",invalid}", problems[^1].RawRecord);
            Assert.Equal(context ? "dictionary context" : "dictionary entry", problems[^1].Stage);
        }
    }

    [Fact]
    public void StrictModeStopsTransactionScanWithoutConsumingThePendingEvent()
    {
        File.WriteAllText(Lgp, Row("U") + ",{bad}," + Row());
        var problems = new List<EventLogProblemRecord>(); var settings = Settings(true, problems);
        using var reader = new EventLogReader(settings);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.ThrowsAny<Exception>(() => reader.ReadNextEventLogItem());
            Assert.Equal(0, settings.ItemId);
            Assert.Equal("transaction index", problems[^1].Stage);
            Assert.False(problems[^1].Skipped);
            Assert.Equal("{bad}", problems[^1].RawRecord);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownValidEventsAndDataAreAcceptedInBothModes(bool strict)
    {
        File.WriteAllText(Lgf, "{4,\"_$Future$_.Event\",1},{14,\"future dictionary\"}");
        File.WriteAllText(Lgp, Row().Replace("{\"U\"}", "{\"Future\",{\"field\",\"value\"}}"));
        var problems = new List<EventLogProblemRecord>(); var diagnostics = new List<EventLogDiagnostic>();
        var settings = Settings(strict, problems); settings.DiagnosticHandler = diagnostics.Add;
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("_$Future$_.Event", item.Event);
        Assert.Equal("1c-brackets-v1", JsonDocument.Parse(item.Data).RootElement.GetProperty("$format").GetString());
        Assert.Empty(problems);
        Assert.Contains(diagnostics, d => d.Code == "UnknownEvent");
        Assert.Contains(diagnostics, d => d.Code == "UnknownDataFormat");
        Assert.Contains(diagnostics, d => d.Code == "DictionaryRecord");
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrictModeStillWaitsForAnIncompleteLiveTail(bool strict)
    {
        File.WriteAllText(Lgp, Row()[..^1]);
        var problems = new List<EventLogProblemRecord>(); var settings = Settings(strict, problems);
        settings.LiveMode = true; settings.ReadingTimeout = 0;
        using var reader = new EventLogReader(settings);
        Assert.Throws<EventLogReaderTimeoutException>(() => reader.ReadNextEventLogItem());
        Assert.Empty(problems); Assert.Equal(0, settings.ItemId);
        File.AppendAllText(Lgp, "}");
        Assert.Equal(1, reader.ReadNextEventLogItem().Id);
        Assert.Empty(problems);
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
