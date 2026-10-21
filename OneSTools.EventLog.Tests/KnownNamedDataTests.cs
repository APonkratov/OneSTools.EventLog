using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class KnownNamedDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-known-named", Guid.NewGuid().ToString("N"));
    public KnownNamedDataTests() => Directory.CreateDirectory(_folder);

    public static IEnumerable<object[]> PlatformCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "known-named-data-8.3.27.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { item.GetProperty("eventCode").GetString()!,
                item.GetProperty("data").GetString()!, item.GetProperty("expected").GetRawText() };
    }

    [Theory]
    [MemberData(nameof(PlatformCases))]
    public void VerifiedNamedLayoutsMatchPlatformWithAllDiagnosticAndContextSettings(string code, string wire, string expected)
    {
        foreach (var strict in new[] { false, true })
        foreach (var context in new[] { false, true })
        {
            var actual = JsonNode.Parse(Read(code, wire, strict, context).Data)!;
            if (context)
            {
                Assert.Equal("1c-event-context-v1", actual["$format"]!.GetValue<string>());
                Assert.Equal(code, actual["EventCode"]!.GetValue<string>());
                Assert.Equal("Имя", actual["DataNodes"]![1]![1]![0]!.GetValue<string>());
                actual = JsonNode.Parse(actual["Data"]!.GetValue<string>())!;
            }
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
            Assert.Single(actual.AsObject());
            Assert.NotNull(actual["Name"]);
        }
    }

    [Theory]
    [InlineData("{P,{2,{Имя,{S,Fixture}},{Future,{N,1}}}}", "1c-named-data-v1")]
    [InlineData("{P,{1,{Name,{S,Fixture}}}}", "1c-named-data-v1")]
    [InlineData("{P,{1,{Имя,{N,12345678901234567890123456789012345678}}}}", "1c-named-data-v1")]
    [InlineData("{P,{1,{Имя,{U}}}}", "1c-named-data-v1")]
    [InlineData("{P,{1,{Имя,{@,255:11111111111111111111111111111111}}}}", "1c-named-data-v1")]
    [InlineData("{P,{2,{Имя,{S,one}},{Имя,{S,two}}}}", "1c-brackets-v1")]
    [InlineData("{P,{1,{Имя,{S,Fixture,extra}}}}", "1c-brackets-v1")]
    [InlineData("{P,{1,{Имя,{S,Fixture}}},extra}", "1c-brackets-v1")]
    [InlineData("{P,{0}}", "1c-brackets-v1")]
    [InlineData("{P,{2,{Имя,{S,Fixture}}}}", "1c-brackets-v1")]
    public void ChangedLayoutsRetainTheGenericLosslessRepresentation(string wire, string format)
    {
        var reference = JsonNode.Parse(Read("Fixture.Future", wire).Data)!;
        foreach (var code in new[] { "_$User$_.AuthenticationLock", "_$User$_.AuthenticationUnlock", "_$User$_.AuthenticationUnlockError" })
        {
            var actual = JsonNode.Parse(Read(code, wire).Data)!;
            Assert.Equal(format, actual["$format"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(reference, actual));
        }
    }

    [Fact]
    public void UnknownEventsDoNotAcquirePlatformFieldNames()
    {
        var actual = JsonNode.Parse(Read("Fixture.Future", "{P,{1,{Имя,{S,Fixture}}}}").Data)!;
        Assert.Equal("1c-named-data-v1", actual["$format"]!.GetValue<string>());
        Assert.Equal("Fixture", actual["values"]!["Имя"]!.GetValue<string>());
        Assert.Null(actual["Name"]);
    }

    private EventLogItem Read(string code, string wire, bool strict = false, bool context = false)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"" + code + "\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261021000000.lgp"),
            "{20261021120000,N,{0,0},0,0,0,1,1,I,\"\",0," + wire + ",\"\",0,0,0,1,0,{0}}");
        using var reader = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc,
            UseStrictParsing = strict, UseEventContext = context
        });
        var item = reader.ReadNextEventLogItem();
        Assert.Null(reader.ReadNextEventLogItem());
        return item;
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
