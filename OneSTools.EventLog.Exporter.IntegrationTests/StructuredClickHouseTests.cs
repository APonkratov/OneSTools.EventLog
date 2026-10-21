using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class StructuredClickHouseTests
{
    [ClickHouseFact]
    public async Task NativeSystemNamesAndVerbatimApplicationNamesUseTheExistingEventColumn()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-native-names", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var names = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-catalog.json")))!;
            foreach (var application in new[] { "Обмен.Ошибка", "Обмен.  Ошибка", " Обмен.Ошибка ",
                "Сеанс.Подключение внешней компоненты", "Обмен.\"Ошибка\"\r\n日本語😀" })
                names.Add(application, application);
            var cases = names.ToArray();
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"), string.Join(",\n", cases.Select((c, i) =>
                "{4,\"" + c.Key.Replace("\"", "\"\"") + "\"," + (i + 1) + "}")));
            File.WriteAllText(Path.Combine(folder, "20261021000000.lgp"), string.Join(",\n", cases.Select((c, i) =>
                "{20261021120000,N,{0,0},0,0,0,1," + (i + 1) + ",I,\"\",0,{U},\"\",0,0,0,1,0,{0}}")));
            var items = new List<EventLogItem>();
            using (var reader = new EventLogReader(new EventLogReaderSettings
            { LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc }))
                while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
            Assert.Equal(cases.Length, items.Count);
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync(items);
            var rows = (await server.Query($"SELECT * FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow"))
                .TrimEnd().Split('\n');
            Assert.Equal(cases.Length, rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                using var row = JsonDocument.Parse(rows[i]);
                Assert.Equal(24, row.RootElement.EnumerateObject().Count());
                Assert.Equal(cases[i].Value, row.RootElement.GetProperty("Event").GetString());
            }
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }

    [ClickHouseFact]
    public async Task KnownNamedPayloadsMatchPlatformWithoutChangingTheExistingTable()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-known-named-clickhouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "known-named-data-8.3.27.json")));
            var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"), string.Join(",\n", cases.Select((c, i) =>
                "{4,\"" + c.GetProperty("eventCode").GetString() + "\"," + (i + 1) + "}")));
            File.WriteAllText(Path.Combine(folder, "20261021000000.lgp"), string.Join(",\n", cases.Select((c, i) =>
                "{20261021120000,N,{0,0},0,0,0,1," + (i + 1) + ",I,\"\",0," + c.GetProperty("data").GetString() + ",\"\",0,0,0,1,0,{0}}")));
            var items = new List<EventLogItem>();
            using (var reader = new EventLogReader(new EventLogReaderSettings
            {
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc
            }))
                while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
            Assert.Equal(cases.Length, items.Count);
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync(items);
            var stored = (await server.Query($"SELECT * FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow"))
                .TrimEnd().Split('\n');
            Assert.Equal(cases.Length, stored.Length);
            for (var i = 0; i < cases.Length; i++)
            {
                using var row = JsonDocument.Parse(stored[i]);
                Assert.Equal(24, row.RootElement.EnumerateObject().Count());
                var data = row.RootElement.GetProperty("Data").GetString()!;
                Assert.Equal(items[i].Data, data);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(cases[i].GetProperty("expected").GetRawText()), JsonNode.Parse(data)));
            }
            Assert.Equal("FixtureUser\n", await server.Query($"SELECT JSONExtractString(Data, 'Name') FROM {server.Database}.EventLogItems WHERE Id=1"));
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }

    [ClickHouseFact]
    public async Task PositionalPayloadsUseExistingDataStringAndKeepAllTwentyFourColumns()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-positional-clickhouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "positional-data-8.3.27.json")));
            var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"), string.Join(",\n", cases.Select((c, i) =>
                "{4,\"" + c.GetProperty("eventCode").GetString() + "\"," + (i + 1) + "}")));
            File.WriteAllText(Path.Combine(folder, "20261011000000.lgp"), string.Join(",\n", cases.Select((c, i) =>
                "{20261011120000,N,{0,0},0,0,0,1," + (i + 1) + ",I,\"\",0," + Wire(c.GetProperty("nodes")) + ",\"\",0,0,0,1,0,{0}}")));
            var items = new List<EventLogItem>();
            using (var reader = new EventLogReader(new EventLogReaderSettings
            {
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc
            }))
                while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
            Assert.Equal(cases.Length, items.Count);
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync(items);
            var stored = (await server.Query($"SELECT * FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow"))
                .TrimEnd().Split('\n');
            Assert.Equal(cases.Length, stored.Length);
            for (var i = 0; i < cases.Length; i++)
            {
                using var row = JsonDocument.Parse(stored[i]);
                Assert.Equal(24, row.RootElement.EnumerateObject().Count());
                Assert.Equal(items[i].Data, row.RootElement.GetProperty("Data").GetString());
                var data = JsonNode.Parse(row.RootElement.GetProperty("Data").GetString()!)!;
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(cases[i].GetProperty("nodes").GetRawText()), data["nodes"]));
            }
            Assert.Equal("3\t300\n", await server.Query($"SELECT JSONExtractString(Data, 'values', 'MaxUnsuccessfulAttemptsCount'), JSONExtractString(Data, 'values', 'LockDuration') FROM {server.Database}.EventLogItems WHERE JSONExtractString(Data, 'layout')='P/192'"));
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }

    private static string Wire(JsonElement node) => node.ValueKind == JsonValueKind.Array
        ? "{" + string.Join(",", node.EnumerateArray().Select(Wire)) + "}"
        : "\"" + node.GetString() + "\"";

    [ClickHouseFact]
    public async Task ContextAndTypedReferencesUseExistingColumnsAndRemainQueryable()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-context-clickhouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            const string identity = "255:11111111111111111111111111111111";
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "{4,\"_$Data$_.NewPredefinedData\",1},{9,11111111-1111-1111-1111-111111111111,\"Tenant\",1},{10,{\"N\",123},1,1}");
            File.WriteAllText(Path.Combine(folder, "20261011000000.lgp"),
                "{20261011120000,N,{0,0},0,0,0,1,1,I,\"\",0,{\"P\",{2,{\"Name\",{\"S\",\"sample\"}},{\"Reference\",{\"@\",\"" + identity + "\"}}}},\"\",0,0,0,1,0,{1,1,1},future}");
            using var reader = new EventLogReader(new EventLogReaderSettings
            {
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc,
                UseEventContext = true
            });
            var item = reader.ReadNextEventLogItem();
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync(new() { item });
            Assert.Equal("123\n", await server.Query($"SELECT JSONExtractString(Data, 'SessionDataSeparation', 'Tenant') FROM {server.Database}.EventLogItems"));
            Assert.Equal(identity + "\n", await server.Query($"SELECT JSONExtractString(JSONExtractString(Data, 'Data'), 'values', 'Reference', 'value') FROM {server.Database}.EventLogItems"));
            using var stored = JsonDocument.Parse(await server.Query($"SELECT * FROM {server.Database}.EventLogItems FORMAT JSONEachRow"));
            Assert.Equal(24, stored.RootElement.EnumerateObject().Count());
            Assert.Equal(item.Data, stored.RootElement.GetProperty("Data").GetString());
            Assert.Equal(item.Event, stored.RootElement.GetProperty("Event").GetString());
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }

    [ClickHouseFact]
    public async Task NamedObjectsAndFallbackTokensRemainQueryableInExistingDataString()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-structured-clickhouse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cases = new (string Event, string Wire, string Expected)[]
            {
                ("_$Session$_.Authentication", "{\"P\",{518,{\"S\",\"\"},{\"S\",\"test-computer\\test-user\"},{\"N\",1}}}",
                    "{\"Name\":\"\",\"CurrentOSUser\":\"test-computer\\\\test-user\",\"AuthenticationMethod\":\"1\"}"),
                ("_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"FixtureVendor\"},{\"S\",\"9.8.7\"}}}",
                    "{\"Vendor\":\"FixtureVendor\",\"Version\":\"9.8.7\"}"),
                ("_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",0}}}", "{\"ExclusiveMode\":\"false\"}"),
                ("_$Access$_.Access", "{\"P\",{1,{\"Z\",{2,1,{\"Свойство\",{\"Pattern\"}},{\"Значение\",{\"Pattern\"}},{{\"S\",\"Право\"},{\"S\",\"Администрирование\"}}}}}}",
                    "{\"Data\":{\"Column\":[{\"name\":\"Свойство\"},{\"name\":\"Значение\"}],\"Row\":{\"Свойство\":\"Право\",\"Значение\":\"Администрирование\"}}}"),
                ("Corpus.Future", "{\"P\",{999,{\"N\",12345678901234567890123456789012345678},{\"U\"}}}",
                    "{\"$format\":\"1c-brackets-v1\",\"nodes\":[\"P\",[\"999\",[\"N\",\"12345678901234567890123456789012345678\"],[\"U\"]]]}")
            };
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n" +
                string.Join(",\n", cases.Select((item, i) => "{4,\"" + item.Event + "\"," + (i + 1) + "}")), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "20261009000000.lgp"), string.Join(",\n", cases.Select((item, i) =>
                "{2026100912000" + i + ",N,{0,0},0,0,0,1," + (i + 1) + ",I,\"\",0," + item.Wire + ",\"\",0,0,0,1,0,{0}}")), new UTF8Encoding(false));
            var items = new List<EventLogItem>();
            using (var reader = new EventLogReader(new EventLogReaderSettings
            {
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc
            }))
                while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
            Assert.Equal(cases.Length, items.Count);
            using (var storage = server.Storage()) await storage.WriteEventLogDataAsync(items);

            var stored = (await server.Query($"SELECT Data FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow"))
                .TrimEnd().Split('\n');
            Assert.Equal(cases.Length, stored.Length);
            for (var i = 0; i < cases.Length; i++)
            {
                using var row = JsonDocument.Parse(stored[i]);
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(cases[i].Expected),
                    JsonNode.Parse(row.RootElement.GetProperty("Data").GetString()!)));
            }
            Assert.Equal("1\n", await server.Query($"SELECT JSONExtractString(Data, 'AuthenticationMethod') FROM {server.Database}.EventLogItems WHERE Id=1"));
            Assert.Equal("Администрирование\n", await server.Query($"SELECT JSONExtractString(Data, 'Data', 'Row', 'Значение') FROM {server.Database}.EventLogItems WHERE Id=4"));
            Assert.Equal("1c-brackets-v1\n", await server.Query($"SELECT JSONExtractString(Data, '$format') FROM {server.Database}.EventLogItems WHERE Id=5"));
            using (var reconnected = server.Storage())
            {
                var position = await reconnected.ReadEventLogPositionAsync();
                Assert.Equal(items[^1].FileName, position.FileName);
                Assert.Equal(items[^1].EndPosition, position.EndPosition);
                Assert.Equal(items[^1].LgfEndPosition, position.LgfEndPosition);
                Assert.Equal(items[^1].Id, position.Id);
            }
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
