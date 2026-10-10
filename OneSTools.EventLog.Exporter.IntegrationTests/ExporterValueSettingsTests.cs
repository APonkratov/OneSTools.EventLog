using System.Text;
using Microsoft.Extensions.Configuration;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

// No server required: exercise both public exporter constructors through actual delivery.
public sealed class ExporterValueSettingsTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothConstructorsPassOptionalModeToReader(bool configurationConstructor, bool? enabled)
    {
        var folder = Path.Combine(Path.GetTempPath(), "onestools-exporter-values", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n{4,\"Corpus.Test\",1}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "20261009000000.lgp"),
                "{20261009123456,C,{0,0},0,0,0,1,1,I,\"\"\"Комментарий\"\"\",0,{\"N\",123.45},\"\",0,0,0,1,0,{0}}", new UTF8Encoding(false));
            var storage = new RecordingStorage();
            using var exporter = CreateExporter(configurationConstructor, enabled, folder, storage);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
            try
            {
                var item = await storage.Received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(enabled == true ? "123.45" : "", item.Data);
                Assert.Equal(enabled == true ? "Зафиксирована" : "Отменена", item.TransactionStatus);
                Assert.Equal(enabled == true ? "\"Комментарий\"" : "\"\"Комментарий\"\"", item.Comment);
            }
            finally
            {
                cancellation.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static EventLogExporter CreateExporter(bool configurationConstructor, bool? enabled, string folder,
        RecordingStorage storage)
    {
        if (!configurationConstructor)
        {
            var settings = new EventLogExporterSettings { LogFolder = folder, Portion = 1, TimeZone = DateTimeZone.Utc };
            if (enabled.HasValue) settings.UseCorrectedValueParsing = enabled.Value;
            return new EventLogExporter(settings, storage);
        }
        var values = new Dictionary<string, string?>
        {
            ["Exporter:LogFolder"] = folder, ["Exporter:Portion"] = "1", ["Exporter:TimeZone"] = "UTC"
        };
        if (enabled.HasValue) values["Exporter:UseCorrectedValueParsing"] = enabled.Value.ToString();
        return new EventLogExporter(null, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), storage);
    }

    private sealed class RecordingStorage : IEventLogStorage
    {
        public TaskCompletionSource<EventLogItem> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<EventLogPosition>(null!);
        public Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default)
        {
            Received.TrySetResult(Assert.Single(entities));
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }
}
