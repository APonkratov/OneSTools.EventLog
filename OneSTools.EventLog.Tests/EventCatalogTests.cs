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
    public void CatalogNamesAreDefaultAndExplicitLegacyModeRemainsAvailable(string code, string expected)
    {
        Write(code);
        using var legacy = new EventLogReader(Settings(false));
        var before = legacy.ReadNextEventLogItem();
        using var current = new EventLogReader(Settings(true));
        var after = current.ReadNextEventLogItem();
        Assert.Equal(expected, after.Event);
        using var defaults = new EventLogReader(new EventLogReaderSettings
        { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc });
        Assert.Equal(expected, defaults.ReadNextEventLogItem().Event);
        after.Event = before.Event;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
    }

    [Fact]
    public void EveryCodeInPlatformModelHasPresentationAndUnknownCodesArePreserved()
    {
        var codes = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "platform-8.3.27-event-codes.json")))!;
        Assert.Equal(123, codes.Length);
        foreach (var code in codes)
        {
            Write(code);
            using var reader = new EventLogReader(Settings(true));
            Assert.NotEqual(code, reader.ReadNextEventLogItem().Event);
        }
        Write("_$Future$_.Unknown");
        var evidence = new List<EventLogDiagnostic>();
        var settings = Settings(true); settings.DiagnosticHandler = evidence.Add;
        using var future = new EventLogReader(settings);
        Assert.Equal("_$Future$_.Unknown", future.ReadNextEventLogItem().Event);
        Assert.Contains(evidence, d => d.Code == "UnknownEvent");
    }

    private EventLogReaderSettings Settings(bool current) => new()
    { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc, UseCurrentEventNames = current };
    private void Write(string code)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"" + code + "\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261011000000.lgp"), "{20261011120000,N,{0,0},0,0,0,1,1,I,\"\",0,{\"U\"},\"\",0,0,0,1,0,{0}}");
    }
    public void Dispose() => Directory.Delete(_folder, true);
}
