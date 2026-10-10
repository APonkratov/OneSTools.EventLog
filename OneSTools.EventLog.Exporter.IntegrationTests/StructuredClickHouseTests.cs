using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class StructuredClickHouseTests
{
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
                LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc, UseStructuredDataParsing = true
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
