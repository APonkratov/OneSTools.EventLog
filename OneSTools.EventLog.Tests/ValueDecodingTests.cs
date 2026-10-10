using System.Text;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class ValueDecodingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-values", Guid.NewGuid().ToString("N"));

    public ValueDecodingTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"),
            "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n" +
            "{1,11111111-1111-1111-1111-111111111111,\"  Пользователь \"\"Тест\"\"  \",1},\n" +
            "{2,\"Компьютер \"\"Тест\"\"\",1},\n" +
            "{3,\"Приложение \"\"Тест\"\"\",1},\n" +
            "{4,\"Corpus.\"\"Test\"\"\",1},\n" +
            "{5,22222222-2222-2222-2222-222222222222,\"Catalog.\"\"Test\"\"\",1},\n" +
            "{6,\"Сервер \"\"Тест\"\"\",1},\n{7,1541,1},\n{8,1560,1}", new UTF8Encoding(false));
    }

    [Theory]
    [InlineData("{\"N\",123.45}", "", "123.45")]
    [InlineData("{\"N\",-123.45}", "", "-123.45")]
    [InlineData("{\"N\",0}", "", "0")]
    [InlineData("{\"N\",12345678901234567890123456789012345678}", "", "12345678901234567890123456789012345678")]
    [InlineData("{\"N\",0.00000000000000000001}", "", "0.00000000000000000001")]
    [InlineData("{\"D\",20261009123456}", "", "2026-10-09T12:34:56")]
    [InlineData("{\"D\",00010101000000}", "", "0001-01-01T00:00:00")]
    [InlineData("{\"S\",\"  пробелы  \"}", "пробелы", "  пробелы  ")]
    [InlineData("{\"S\",\"\"}", "", "")]
    [InlineData("{\"S\",\"  \"\"Текст\"\"\n{строка},日本語\t  \"}", "\"\"Текст\"\"\n{строка},日本語", "  \"Текст\"\n{строка},日本語\t  ")]
    [InlineData("{\"S\",\"\"\"\"}", "\"\"", "\"")]
    [InlineData("{\"B\",0}", "false", "false")]
    [InlineData("{\"B\",1}", "true", "true")]
    [InlineData("{\"U\"}", "", "")]
    [InlineData("{\"R\",11111111-2222-3333-4444-555555555555}", "11111111-2222-3333-4444-555555555555", "11111111-2222-3333-4444-555555555555")]
    public void ScalarValuesHaveExplicitLegacyAndCorrectedContracts(string data, string legacy, string corrected)
    {
        Write(data);
        Assert.Equal(legacy, Read().Data);
        Assert.Equal(legacy, Read(false).Data);
        Assert.Equal(corrected, Read(true).Data);
    }

    [Theory]
    [InlineData("N", "Нет транзакции", "Нет транзакции")]
    [InlineData("U", "Зафиксирована", "Не завершена")]
    [InlineData("C", "Отменена", "Зафиксирована")]
    [InlineData("R", "Не завершена", "Отменена")]
    public void TransactionStatusCorrectionIsOptIn(string code, string legacy, string corrected)
    {
        Write("{\"U\"}", code);
        Assert.Equal(legacy, Read().TransactionStatus);
        Assert.Equal(legacy, Read(false).TransactionStatus);
        Assert.Equal(corrected, Read(true).TransactionStatus);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void TextFieldsDecodeEscapingOnlyWhenEnabled(bool? enabled)
    {
        Write("{\"S\",\"  \"\"данные\"\"  \"}");
        var item = Read(enabled);
        var quotes = enabled == true ? "\"" : "\"\"";
        Assert.Equal($"  Пользователь {quotes}Тест{quotes}  ", item.User);
        Assert.Equal($"Компьютер {quotes}Тест{quotes}", item.Computer);
        Assert.Equal($"Приложение {quotes}Тест{quotes}", item.Application);
        Assert.Equal($"Corpus.{quotes}Test{quotes}", item.Event);
        Assert.Equal($"Catalog.{quotes}Test{quotes}", item.Metadata);
        Assert.Equal($"Сервер {quotes}Тест{quotes}", item.Server);
        Assert.Equal($"  Комментарий {quotes}Тест{quotes}\n  ", item.Comment);
        Assert.Equal($"  Представление {quotes}Тест{quotes}  ", item.DataPresentation);
        Assert.Equal("11111111-1111-1111-1111-111111111111", item.UserUuid);
        Assert.Equal("22222222-2222-2222-2222-222222222222", item.MetadataUuid);
        Assert.Equal(1541, item.MainPort);
        Assert.Equal(1560, item.AddPort);
        Assert.Equal(17, item.Connection);
        Assert.Equal(23, item.Session);
        Assert.Equal("Информация", item.Severity);
        Assert.Equal(new DateTime(2026, 10, 9, 9, 34, 56, DateTimeKind.Utc), item.DateTime);
    }

    [Fact]
    public void ComplexDataRetainsItsExistingRepresentationInBothModes()
    {
        Write("{\"P\",{518,{\"S\",\"\"},{\"S\",\"user\"},{\"N\",1}}}");
        Assert.Equal("Item 2: user", Read().Data);
        Assert.Equal("Item 2: user", Read(true).Data);
    }

    [Fact]
    public void CorrectedDateValueDoesNotUseEventTimeZone()
    {
        Write("{\"D\",20261009123456}");
        var item = Read(true);
        Assert.Equal(new DateTime(2026, 10, 9, 9, 34, 56, DateTimeKind.Utc), item.DateTime);
        Assert.Equal("2026-10-09T12:34:56", item.Data);
    }

    [Fact]
    public void CorrectedModeUsesSameBytePositionsAndResumesLegacyCheckpoint()
    {
        Write("{\"N\",123.45}");
        File.AppendAllText(Path.Combine(_folder, "20261009000000.lgp"), ",\n" + Record("{\"N\",456}", "C"));
        File.WriteAllText(Path.Combine(_folder, "20261010000000.lgp"), Record("{\"N\",789}", "R"));
        var legacy = ReadAll(false);
        var corrected = ReadAll(true);
        Assert.Equal(3, corrected.Count);
        Assert.Equal(legacy.Select(item => (item.FileName, item.EndPosition, item.LgfEndPosition, item.Id)),
            corrected.Select(item => (item.FileName, item.EndPosition, item.LgfEndPosition, item.Id)));
        var checkpoint = legacy[0];
        using var resumed = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc,
            LgpFileName = checkpoint.FileName, LgpStartPosition = checkpoint.EndPosition,
            LgfStartPosition = checkpoint.LgfEndPosition, ItemId = checkpoint.Id, UseCorrectedValueParsing = true
        });
        var next = resumed.ReadNextEventLogItem();
        Assert.Equal("456", next.Data);
        Assert.Equal("Зафиксирована", next.TransactionStatus);
        Assert.Equal(2, next.Id);
        var following = resumed.ReadNextEventLogItem();
        Assert.Equal("789", following.Data);
        Assert.Equal("Отменена", following.TransactionStatus);
        Assert.Equal(3, following.Id);
        Assert.Null(resumed.ReadNextEventLogItem());
    }

    private List<EventLogItem> ReadAll(bool enabled)
    {
        using var reader = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc, UseCorrectedValueParsing = enabled
        });
        var items = new List<EventLogItem>();
        while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
        return items;
    }

    private EventLogItem Read(bool? enabled = null)
    {
        var settings = new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZoneProviders.Tzdb["Europe/Moscow"]
        };
        if (enabled.HasValue) settings.UseCorrectedValueParsing = enabled.Value;
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        Assert.Null(reader.ReadNextEventLogItem());
        return item;
    }

    private void Write(string data, string transaction = "N") =>
        File.WriteAllText(Path.Combine(_folder, "20261009000000.lgp"),
            Record(data, transaction), new UTF8Encoding(false));

    private static string Record(string data, string transaction) =>
        "{20261009123456," + transaction + ",{0,0},1,1,1,17,1,I,\"  Комментарий \"\"Тест\"\"\n  \",1," +
        data + ",\"  Представление \"\"Тест\"\"  \",1,1,1,23,0,{0}}";

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
