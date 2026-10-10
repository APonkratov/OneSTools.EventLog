using System.Text.Json;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class DeliveryClickHouseTests
{
    [ClickHouseFact]
    public async Task CursorFollowsFileAndOffsetWhenEventClockMovesBackwards()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        using var storage = server.Storage();
        await storage.WriteEventLogDataAsync([
            Item("20261009000000.lgp", 999, 900, new DateTime(2026, 10, 11)),
            Item("20261010000000.lgp", 100, 2, new DateTime(2026, 10, 10, 12, 0, 0)),
            Item("20261010000000.lgp", 200, 3, new DateTime(2026, 10, 10, 11, 0, 0))
        ]);
        var cursor = await storage.ReadEventLogPositionAsync();
        Assert.Equal("20261010000000.lgp", cursor.FileName);
        Assert.Equal(200, cursor.EndPosition);
        Assert.Equal(3, cursor.Id);
        Assert.Equal(schema, await server.Schema());
    }

    [ClickHouseFact]
    public async Task LocalCheckpointReplaysFirstBatchAfterOnlyItsLaterRowReachedClickHouse()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var root = Path.Combine(Path.GetTempPath(), "onestools-ch-delivery", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "log");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"), "{4,\"Test\",1}");
            File.WriteAllText(Path.Combine(folder, "20261011000000.lgp"), string.Join(",\n", Enumerable.Range(1, 3).Select(i =>
                $"{{20261011120000,N,{{0,0}},0,0,0,1,1,I,\"{i}\",0,{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}")));
            var state = Path.Combine(root, "state.json");
            var settings = new EventLogExporterSettings
            {
                LogFolder = folder, CheckpointFile = state, Portion = 2,
                LoadArchive = true, TimeZone = DateTimeZone.Utc
            };
            using (var exporter = new EventLogExporter(settings, new PartialStorage(server.Storage())))
                await Assert.ThrowsAsync<IOException>(() => exporter.StartAsync());
            using (var storage = server.Storage())
                Assert.Equal(2, (await storage.ReadEventLogPositionAsync()).Id);
            using (var stateJson = JsonDocument.Parse(File.ReadAllText(state)))
                Assert.False(stateJson.RootElement.GetProperty("HasPosition").GetBoolean());

            using (var exporter = new EventLogExporter(settings, server.Storage()))
                await exporter.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("1\t1\n2\t2\n3\t1\n", await server.Query(
                $"SELECT Id, count() FROM {server.Database}.EventLogItems GROUP BY Id ORDER BY Id FORMAT TSV"));
            using (var exporter = new EventLogExporter(settings, server.Storage()))
                await exporter.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("4\n", await server.Query($"SELECT count() FROM {server.Database}.EventLogItems FORMAT TSV"));
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static EventLogItem Item(string file, long offset, long id, DateTime time) => new()
    {
        FileName = file, EndPosition = offset, Id = id, DateTime = DateTime.SpecifyKind(time, DateTimeKind.Utc)
    };

    // Deterministically inject a partial commit at the receiver boundary, using the real production driver.
    private sealed class PartialStorage(IEventLogStorage inner) : IEventLogStorage, IEventLogStorageIdentity
    {
        public string CheckpointIdentity => ((IEventLogStorageIdentity)inner).CheckpointIdentity;
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default) =>
            inner.ReadEventLogPositionAsync(cancellationToken);
        public async Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default)
        {
            await inner.WriteEventLogDataAsync([entities[1]], cancellationToken);
            throw new IOException("Injected lost response after committing only a later row.");
        }
        public void Dispose() => inner.Dispose();
    }
}
