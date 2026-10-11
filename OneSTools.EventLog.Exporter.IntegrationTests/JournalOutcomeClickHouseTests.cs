using System.Text;
using System.Text.Json;
using NodaTime;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class JournalOutcomeClickHouseTests
{
    [ClickHouseFact]
    public async Task ExistingTableResumesOldCursorWithLatestNamesAndTransactionOutcomes()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-outcomes-clickhouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "{4,\"Test\",1},\n{4,\"_$Transaction$_.Rollback\",2},\n" +
                "{5,11111111-1111-1111-1111-111111111111,\"old\",1},\n" +
                "{5,11111111-1111-1111-1111-111111111111,\"новое имя\",1}");
            const string row = "{20261011120000,U,{0,1},0,0,0,1,1,I,\"😀\",1,{\"U\"},\"\",0,0,0,1,0,{0}}";
            var path = Path.Combine(folder, "20261011000000.lgp");
            File.WriteAllText(path, row + ",\n" + row.Replace(",1,I,", ",2,I,"), new UTF8Encoding(false));
            var settings = new EventLogReaderSettings
            {
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc
            };
            EventLogItem old;
            using (var reader = new EventLogReader(settings)) old = reader.ReadNextEventLogItem();
            old.Metadata = "old";
            old.TransactionStatus = "Зафиксирована";
            old.EndPosition++; // Position produced by the historical decoder at a UTF-8 boundary.
            old.LgfEndPosition++;
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync([old]);
            using (var reconnected = server.Storage())
            {
                var checkpoint = await reconnected.ReadEventLogPositionAsync();
                settings.LgpFileName = checkpoint.FileName; settings.LgpStartPosition = checkpoint.EndPosition;
                settings.LgfStartPosition = checkpoint.LgfEndPosition; settings.ItemId = checkpoint.Id;
                using var resumed = new EventLogReader(settings);
                var item = resumed.ReadNextEventLogItem();
                Assert.Equal(2, item.Id);
                Assert.Equal("Отменена", item.TransactionStatus);
                Assert.Equal("новое имя", item.Metadata);
                Assert.Equal(new FileInfo(path).Length, item.EndPosition);
                Assert.Null(resumed.ReadNextEventLogItem());
                await reconnected.WriteEventLogDataAsync([item]);
            }
            var stored = (await server.Query($"SELECT * FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow")).Trim().Split('\n');
            Assert.Equal(2, stored.Length);
            using var first = JsonDocument.Parse(stored[0]);
            using var last = JsonDocument.Parse(stored[1]);
            Assert.Equal(24, last.RootElement.EnumerateObject().Count());
            Assert.Equal("old", first.RootElement.GetProperty("Metadata").GetString());
            Assert.Equal("Зафиксирована", first.RootElement.GetProperty("TransactionStatus").GetString());
            Assert.Equal("новое имя", last.RootElement.GetProperty("Metadata").GetString());
            Assert.Equal("Отменена", last.RootElement.GetProperty("TransactionStatus").GetString());
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }
}
