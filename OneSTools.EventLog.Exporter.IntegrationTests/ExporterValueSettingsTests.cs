using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

// Exercise both public constructors through delivery, including stale configuration.
public sealed class ExporterValueSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-exporter-values", Guid.NewGuid().ToString("N"));
    private static readonly string[] RemovedFlags =
    [
        "UseCorrectedValueParsing", "UseStructuredDataParsing", "UseCurrentEventNames",
        "UseLatestDictionaryValues", "UseFinalTransactionStatuses"
    ];

    public ExporterValueSettingsTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"),
            "{4,\"_$Session$_.ExternalReportConnect\",1},{4,\"_$Transaction$_.Rollback\",2}," +
            "{4,\"_$InfoBase$_.ExclusiveModeChange\",3}," +
            "{5,11111111-1111-1111-1111-111111111111,\"old\",1}," +
            "{5,11111111-1111-1111-1111-111111111111,\"new\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261011000000.lgp"),
            Row("U", 1, "{\"N\",123.45}") + "," + Row("U", 2, "{\"U\"}") + "," +
            Row("N", 3, "{\"P\",{2,{\"B\",1}}}"));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "false")]
    [InlineData(true, "true")]
    [InlineData(true, "not-a-boolean")]
    public async Task CorrectedBehaviorIsUnconditionalAndObsoleteKeysAreIgnored(bool configurationConstructor, string? obsoleteValue)
    {
        var items = await Export(configurationConstructor, obsoleteValue);
        Assert.Equal(3, items.Count);
        var first = items[0];
        Assert.Equal("123.45", first.Data);
        Assert.Equal("\"Комментарий\"", first.Comment);
        Assert.Equal("new", first.Metadata);
        Assert.Equal("Отменена", first.TransactionStatus);
        Assert.Equal("Сеанс. Подключение внешнего отчета", first.Event);
        Assert.Equal("true", JsonNode.Parse(items[2].Data)!["ExclusiveMode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContextRemainsAnIndependentOption(bool configurationConstructor, bool context)
    {
        var items = await Export(configurationConstructor, context: context);
        if (context)
        {
            var envelope = JsonNode.Parse(items[0].Data)!;
            Assert.Equal("1c-event-context-v1", envelope["$format"]!.GetValue<string>());
            Assert.Equal("123.45", envelope["Data"]!.GetValue<string>());
            Assert.Equal("_$Session$_.ExternalReportConnect", envelope["EventCode"]!.GetValue<string>());
        }
        else Assert.Equal("123.45", items[0].Data);
        Assert.Equal("new", items[0].Metadata);
        Assert.Equal("Отменена", items[0].TransactionStatus);
    }

    [Fact]
    public void RemovedSwitchesAreNotPartOfPublicSettings()
    {
        foreach (var flag in RemovedFlags)
        {
            Assert.Null(typeof(EventLogReaderSettings).GetProperty(flag));
            Assert.Null(typeof(EventLogExporterSettings).GetProperty(flag));
        }
    }

    private async Task<List<EventLogItem>> Export(bool configurationConstructor, string? obsoleteValue = null, bool context = false)
    {
        var storage = new RecordingStorage();
        var values = new Dictionary<string, string?>
        {
            ["Exporter:LogFolder"] = _folder, ["Exporter:Portion"] = "3", ["Exporter:TimeZone"] = "UTC",
            ["Exporter:UseEventContext"] = context.ToString()
        };
        if (obsoleteValue != null)
            foreach (var flag in RemovedFlags) values["Exporter:" + flag] = obsoleteValue;
        using var exporter = configurationConstructor
            ? new EventLogExporter(null, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), storage)
            : new EventLogExporter(new EventLogExporterSettings
                { LogFolder = _folder, Portion = 3, TimeZone = DateTimeZone.Utc, UseEventContext = context }, storage);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
        try { return await storage.Received.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static string Row(string status, int ev, string data) =>
        "{20261011120000," + status + ",{0,1},0,0,0,1," + ev + ",I,\"\"\"Комментарий\"\"\",1," + data + ",\"\",0,0,0,1,0,{0}}";

    private sealed class RecordingStorage : IEventLogStorage
    {
        public TaskCompletionSource<List<EventLogItem>> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<EventLogPosition>(null!);
        public Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default)
        { Received.TrySetResult(entities.ToList()); return Task.CompletedTask; }
        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
