using System.Text;
using System.Text.Json;
using NodaTime;
using Xunit.Abstractions;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class ClickHouseContractTests(ITestOutputHelper output)
{
    [ClickHouseFact]
    public async Task LoggedProblemTailResumesWithoutDuplicateRowsOrTableMigration()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-problem-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"), "{4,\"Test\",1}");
            const string good = "{20261011120000,N,{0,0},0,0,0,1,1,I,\"good\",0,{\"U\"},\"\",0,0,0,1,0,{0}}";
            File.WriteAllText(Path.Combine(folder, "20261011000000.lgp"), "{bad}," + good + ",{bad tail}");
            var problems = new List<EventLogProblemRecord>();
            var settings = new Core.EventLogExporterSettings
            {
                LogFolder = folder, LoadArchive = true, TimeZone = DateTimeZone.Utc,
                CheckpointFile = Path.Combine(folder, "delivery.json"), ProblemRecordHandler = problems.Add
            };
            for (var i = 0; i < 2; i++)
            {
                using var exporter = new Core.EventLogExporter(settings, server.Storage());
                await exporter.StartAsync();
            }
            Assert.Equal(2, problems.Count);
            Assert.Equal("1\t1\tgood", (await server.Query(
                $"SELECT count(), any(Id), any(Comment) FROM {server.Database}.EventLogItems FORMAT TSVRaw")).Trim());
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, true); }
    }

    [ClickHouseFact]
    public async Task ExistingTableKeepsSchemaAndAll24ValuesAcrossReconnect()
    {
        await using var server = new ClickHouseServer();
        output.WriteLine("ClickHouse " + (await server.Query("SELECT version()")).Trim());
        await server.CreateLegacyTable();
        var before = await server.Schema();
        var item = new EventLogItem
        {
            FileName = "20261009000000.lgp", EndPosition = 901, LgfEndPosition = 702, Id = 53,
            DateTime = new DateTime(2026, 10, 9, 12, 34, 56, DateTimeKind.Utc),
            TransactionStatus = "Зафиксирована",
            TransactionDateTime = new DateTime(2026, 10, 9, 12, 33, 45, DateTimeKind.Utc),
            TransactionNumber = 9007199254740993, UserUuid = "11111111-2222-3333-4444-555555555555",
            User = "Тестовый пользователь", Computer = "test-computer", Application = "1CV8C",
            Connection = 101, Event = "Corpus.Test", Severity = "Информация",
            Comment = "Комментарий \"с кавычками\"\nи новой строкой", MetadataUuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            Metadata = "Catalog.Test", Data = "  Строка\t\"значения\"\n  ", DataPresentation = "Представление данных",
            Server = "test-server", MainPort = 1541, AddPort = 1560, Session = 9007199254740995
        };
        using (var storage = server.Storage())
        {
            Assert.Null(await storage.ReadEventLogPositionAsync());
            await storage.WriteEventLogDataAsync([item]);
        }
        // Independent HTTP read avoids a symmetric encoder/decoder error in the old driver.
        using var json = JsonDocument.Parse(await server.Query(
            $"SELECT * FROM {server.Database}.EventLogItems FORMAT JSONEachRow"));
        AssertRow(json.RootElement, new Dictionary<string, object>
        {
            ["FileName"] = "20261009000000.lgp", ["EndPosition"] = 901L, ["LgfEndPosition"] = 702L, ["Id"] = 53L,
            ["DateTime"] = "2026-10-09 12:34:56", ["TransactionStatus"] = "Зафиксирована",
            ["TransactionDate"] = "2026-10-09 12:33:45", ["TransactionNumber"] = 9007199254740993L,
            ["UserUuid"] = "11111111-2222-3333-4444-555555555555", ["User"] = "Тестовый пользователь",
            ["Computer"] = "test-computer", ["Application"] = "1CV8C", ["Connection"] = 101L,
            ["Event"] = "Corpus.Test", ["Severity"] = "Информация",
            ["Comment"] = "Комментарий \"с кавычками\"\nи новой строкой",
            ["MetadataUuid"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", ["Metadata"] = "Catalog.Test",
            ["Data"] = "  Строка\t\"значения\"\n  ", ["DataPresentation"] = "Представление данных",
            ["Server"] = "test-server", ["MainPort"] = 1541L, ["AddPort"] = 1560L, ["Session"] = 9007199254740995L
        });
        using (var reconnected = server.Storage())
        {
            var position = await reconnected.ReadEventLogPositionAsync();
            Assert.NotNull(position);
            Assert.Equal("20261009000000.lgp", position.FileName);
            Assert.Equal(901, position.EndPosition);
            Assert.Equal(702, position.LgfEndPosition);
            Assert.Equal(53, position.Id);
        }
        Assert.Equal(before, await server.Schema());
    }

    [ClickHouseFact]
    public async Task NewTableMatchesLegacyDdlAndNormalizesNulls()
    {
        await using var server = new ClickHouseServer();
        // Separate expected table lets ClickHouse normalize both DDLs on this version.
        await server.CreateLegacyTable();
        var expectedSchema = (await server.Schema()).Replace("EventLogItems", "ExpectedItems");
        await server.Query($"RENAME TABLE {server.Database}.EventLogItems TO {server.Database}.ExpectedItems");
        using (var storage = server.Storage())
        {
            Assert.Null(await storage.ReadEventLogPositionAsync());
            await storage.WriteEventLogDataAsync([new EventLogItem
            {
                DateTime = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                TransactionDateTime = DateTime.MinValue,
                FileName = null, TransactionStatus = null, UserUuid = null, User = null,
                Computer = null, Application = null, Event = null, Severity = null,
                Comment = null, MetadataUuid = null, Metadata = null, Data = null,
                DataPresentation = null, Server = null
            }]);
        }
        Assert.Equal(expectedSchema, (await server.Schema()).Replace("EventLogItems", "ExpectedItems"));
        using var json = JsonDocument.Parse(await server.Query(
            $"SELECT * FROM {server.Database}.EventLogItems FORMAT JSONEachRow"));
        AssertRow(json.RootElement, new Dictionary<string, object>
        {
            ["FileName"] = "", ["EndPosition"] = 0L, ["LgfEndPosition"] = 0L, ["Id"] = 0L,
            ["DateTime"] = "2026-10-09 00:00:00", ["TransactionStatus"] = "",
            ["TransactionDate"] = "1970-01-01 00:00:00", ["TransactionNumber"] = 0L,
            ["UserUuid"] = "", ["User"] = "", ["Computer"] = "", ["Application"] = "", ["Connection"] = 0L,
            ["Event"] = "", ["Severity"] = "", ["Comment"] = "", ["MetadataUuid"] = "", ["Metadata"] = "",
            ["Data"] = "", ["DataPresentation"] = "", ["Server"] = "", ["MainPort"] = 0L, ["AddPort"] = 0L, ["Session"] = 0L
        });
    }

    [ClickHouseFact]
    public async Task SavedPositionResumesReaderWithinFileThenAcrossEqualMtimePeriods()
    {
        await using var server = new ClickHouseServer();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n{4,\"Corpus.Test\",1}", new UTF8Encoding(false));
            const string first = "20261009000000.lgp", second = "20261010000000.lgp";
            File.WriteAllText(Path.Combine(folder, first), Record("20261009120000") + ",\n" + Record("20261009130000"));
            File.WriteAllText(Path.Combine(folder, second), Record("20261010120000"));
            foreach (var name in new[] { first, second })
                File.SetLastWriteTimeUtc(Path.Combine(folder, name), new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc));
            using (var reader = new EventLogReader(Settings(folder)))
            using (var storage = server.Storage())
                await storage.WriteEventLogDataAsync([reader.ReadNextEventLogItem()]);

            using (var storage = server.Storage())
            {
                var position = await storage.ReadEventLogPositionAsync();
                var settings = Settings(folder);
                settings.LgpFileName = position.FileName;
                settings.LgpStartPosition = position.EndPosition;
                settings.LgfStartPosition = position.LgfEndPosition;
                settings.ItemId = position.Id;
                using var reader = new EventLogReader(settings);
                var next = reader.ReadNextEventLogItem();
                Assert.Equal(first, next.FileName);
                Assert.Equal(2, next.Id);
                Assert.Equal("Corpus.Test", next.Event);
                Assert.Equal(new DateTime(2026, 10, 9, 13, 0, 0, DateTimeKind.Utc), next.DateTime);
                var following = reader.ReadNextEventLogItem();
                Assert.Equal(second, following.FileName);
                Assert.Equal(3, following.Id);
                Assert.Null(reader.ReadNextEventLogItem());
                await storage.WriteEventLogDataAsync([next, following]);
            }
            Assert.Equal("1\t20261009000000.lgp\n2\t20261009000000.lgp\n3\t20261010000000.lgp\n",
                await server.Query($"SELECT Id, FileName FROM {server.Database}.EventLogItems ORDER BY Id FORMAT TSV"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static EventLogReaderSettings Settings(string folder) =>
        new() { LogFolder = folder, LiveMode = false, TimeZone = DateTimeZone.Utc };

    [ClickHouseFact]
    public async Task CorrectedScalarAndStatusValuesFitExistingTableWithoutSchemaChanges()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable();
        var schema = await server.Schema();
        var folder = Path.Combine(Path.GetTempPath(), "onestools-corrected-contract", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "1Cv8.lgf"),
                "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n{4,\"Corpus.Test\",1}", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "20261009000000.lgp"), string.Join(",\n", new[]
            {
                "{20261009120000,C,{0,0},0,0,0,1,1,I,\"\"\"Тест\"\"\",0,{\"N\",12345678901234567890123456789012345678},\"\",0,0,0,1,0,{0}}",
                "{20261009120001,R,{0,0},0,0,0,1,1,I,\"\",0,{\"D\",20261009123456},\"\",0,0,0,1,0,{0}}",
                "{20261009120002,U,{0,0},0,0,0,1,1,I,\"\",0,{\"S\",\"  \"\"Текст\"\"  \"},\"\",0,0,0,1,0,{0}}"
            }), new UTF8Encoding(false));
            var settings = Settings(folder);
            settings.UseCorrectedValueParsing = true;
            using var reader = new EventLogReader(settings);
            var items = new List<EventLogItem>();
            while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
            Assert.Equal(3, items.Count);
            using var storage = server.Storage();
            await storage.WriteEventLogDataAsync(items);
            var rows = (await server.Query($"SELECT Data, TransactionStatus, Comment FROM {server.Database}.EventLogItems ORDER BY Id FORMAT JSONEachRow"))
                .TrimEnd().Split('\n').Select(line => JsonSerializer.Deserialize<Dictionary<string, string>>(line)!).ToArray();
            Assert.Equal("12345678901234567890123456789012345678", rows[0]["Data"]);
            Assert.Equal("Зафиксирована", rows[0]["TransactionStatus"]);
            Assert.Equal("\"Тест\"", rows[0]["Comment"]);
            Assert.Equal("2026-10-09T12:34:56", rows[1]["Data"]);
            Assert.Equal("Отменена", rows[1]["TransactionStatus"]);
            Assert.Equal("  \"Текст\"  ", rows[2]["Data"]);
            Assert.Equal("Не завершена", rows[2]["TransactionStatus"]);
            Assert.Equal(schema, await server.Schema());
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static string Record(string date) =>
        "{" + date + ",N,{0,0},0,0,0,1,1,I,\"Проверка\",0,{\"S\",\"текст\"},\"\",0,0,0,1,0,{0}}";

    private static void AssertRow(JsonElement row, IReadOnlyDictionary<string, object> expected)
    {
        Assert.Equal(expected.Keys, row.EnumerateObject().Select(property => property.Name));
        foreach (var (name, value) in expected)
        {
            var actual = row.GetProperty(name);
            if (value is long number)
                Assert.Equal(number, actual.ValueKind == JsonValueKind.String
                    ? long.Parse(actual.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : actual.GetInt64());
            else Assert.Equal((string)value, actual.GetString());
        }
    }
}
