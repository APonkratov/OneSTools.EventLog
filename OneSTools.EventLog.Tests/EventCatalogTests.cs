using System.Text.Json;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class EventCatalogTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-catalog", Guid.NewGuid().ToString("N"));
    public EventCatalogTests() => Directory.CreateDirectory(_folder);
    public static IEnumerable<object[]> Names() => JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-catalog.json")))!.Select(p => new object[] { p.Key, p.Value });

    [Theory]
    [MemberData(nameof(Names))]
    public void CatalogNamesMatchIndependentNativeExports(string code, string expected)
    {
        Write(code);
        using var reader = new EventLogReader(Settings());
        Assert.Equal(expected, reader.ReadNextEventLogItem().Event);
    }

    [Fact]
    public void ModelCodesMatchNativePresentationEvenWhenPlatformKeepsTheRawCode()
    {
        var codes = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "platform-8.3.27-event-codes.json")))!;
        var names = Names().ToDictionary(p => (string)p[0], p => (string)p[1]);
        Assert.Equal(123, codes.Length);
        foreach (var code in codes)
        {
            Write(code);
            using var reader = new EventLogReader(Settings());
            Assert.Equal(names[code], reader.ReadNextEventLogItem().Event);
        }
        Write("_$Future$_.Unknown");
        var evidence = new List<EventLogDiagnostic>();
        var settings = Settings(); settings.DiagnosticHandler = evidence.Add;
        using var future = new EventLogReader(settings);
        Assert.Equal("_$Future$_.Unknown", future.ReadNextEventLogItem().Event);
        Assert.Contains(evidence, d => d.Code == "UnknownEvent");
    }

    [Theory]
    [InlineData("Обмен.ОшибкаЗагрузки")]
    [InlineData("Обмен. ОшибкаЗагрузки")]
    [InlineData("Обмен.  ОшибкаЗагрузки")]
    [InlineData(" Обмен.ОшибкаЗагрузки ")]
    [InlineData("Получение внешних компонент.")]
    [InlineData("Сеанс.Подключение внешней компоненты")]
    [InlineData("Данные.Проведение")]
    [InlineData("Обмен.\"Ошибка\"\r\n日本語😀")]
    [InlineData("_$Session$_.Start.Application")]
    [InlineData("_$user$_.AuthenticationLock")]
    public void ApplicationNamesAndUnknownCodesArePreservedExactly(string name)
    {
        Write(name);
        using var reader = new EventLogReader(Settings());
        Assert.Equal(name, reader.ReadNextEventLogItem().Event);
    }

    private EventLogReaderSettings Settings() => new()
    { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc };
    private void Write(string code)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"" + code.Replace("\"", "\"\"") + "\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261011000000.lgp"), "{20261011120000,N,{0,0},0,0,0,1,1,I,\"\",0,{\"U\"},\"\",0,0,0,1,0,{0}}");
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
