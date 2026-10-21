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
    [InlineData("{\"N\",123.45}", "123.45")]
    [InlineData("{\"N\",-123.45}", "-123.45")]
    [InlineData("{\"N\",0}", "0")]
    [InlineData("{\"N\",12345678901234567890123456789012345678}", "12345678901234567890123456789012345678")]
    [InlineData("{\"N\",0.00000000000000000001}", "0.00000000000000000001")]
    [InlineData("{\"D\",20261009123456}", "2026-10-09T12:34:56")]
    [InlineData("{\"D\",00010101000000}", "0001-01-01T00:00:00")]
    [InlineData("{\"S\",\"  пробелы  \"}", "  пробелы  ")]
    [InlineData("{\"S\",\"\"}", "")]
    [InlineData("{\"S\",\"  \"\"Текст\"\"\n{строка},日本語\t  \"}", "  \"Текст\"\n{строка},日本語\t  ")]
    [InlineData("{\"S\",\"\"\"\"}", "\"")]
    [InlineData("{\"B\",0}", "false")]
    [InlineData("{\"B\",1}", "true")]
    [InlineData("{\"U\"}", "")]
    [InlineData("{\"R\",11111111-2222-3333-4444-555555555555}", "11111111-2222-3333-4444-555555555555")]
    public void ScalarValuesAlwaysUseCorrectedDecoding(string data, string corrected)
    {
        Write(data);
        Assert.Equal(corrected, Read().Data);
    }

    [Theory]
    [InlineData("N", "Нет транзакции")]
    [InlineData("U", "Не завершена")]
    [InlineData("C", "Зафиксирована")]
    [InlineData("R", "Отменена")]
    public void TransactionStatusesAlwaysUsePlatformMeaning(string code, string corrected)
    {
        Write("{\"U\"}", code);
        Assert.Equal(corrected, Read().TransactionStatus);
    }

    [Fact]
    public void TextFieldsAlwaysDecodeEscaping()
    {
        Write("{\"S\",\"  \"\"данные\"\"  \"}");
        var item = Read();
        var quotes = "\"";
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
    public void UnknownComplexDataPreservesEveryToken()
    {
        Write("{\"P\",{518,{\"S\",\"\"},{\"S\",\"user\"},{\"N\",1}}}");
        using var data = System.Text.Json.JsonDocument.Parse(Read().Data);
        Assert.Equal("1c-brackets-v1", data.RootElement.GetProperty("$format").GetString());
        Assert.Equal("user", data.RootElement.GetProperty("nodes")[1][2][1].GetString());
    }

    [Fact]
    public void CorrectedDateValueDoesNotUseEventTimeZone()
    {
        Write("{\"D\",20261009123456}");
        var item = Read();
        Assert.Equal(new DateTime(2026, 10, 9, 9, 34, 56, DateTimeKind.Utc), item.DateTime);
        Assert.Equal("2026-10-09T12:34:56", item.Data);
    }

    [Fact]
    public void BytePositionsAreStableAndResumeFourFieldCheckpoint()
    {
        Write("{\"N\",123.45}");
        File.AppendAllText(Path.Combine(_folder, "20261009000000.lgp"), ",\n" + Record("{\"N\",456}", "C"));
        File.WriteAllText(Path.Combine(_folder, "20261010000000.lgp"), Record("{\"N\",789}", "R"));
        var items = ReadAll();
        Assert.Equal(3, items.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(Record("{\"N\",123.45}", "N")), items[0].EndPosition);
        Assert.Equal(new FileInfo(Path.Combine(_folder, "20261009000000.lgp")).Length, items[1].EndPosition);
        Assert.Equal(new FileInfo(Path.Combine(_folder, "20261010000000.lgp")).Length, items[2].EndPosition);
        var checkpoint = items[0];
        using var resumed = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc,
            LgpFileName = checkpoint.FileName, LgpStartPosition = checkpoint.EndPosition,
            LgfStartPosition = checkpoint.LgfEndPosition, ItemId = checkpoint.Id
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

    private List<EventLogItem> ReadAll()
    {
        using var reader = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc
        });
        var items = new List<EventLogItem>();
        while (reader.ReadNextEventLogItem() is { } item) items.Add(item);
        return items;
    }

    private EventLogItem Read()
    {
        var settings = new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZoneProviders.Tzdb["Europe/Moscow"]
        };
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
