using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class ErpDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-erp-data", Guid.NewGuid().ToString("N"));

    public ErpDataTests() => Directory.CreateDirectory(_folder);

    public static IEnumerable<object[]> PlatformCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "erp-data-8.3.27.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { item.GetProperty("eventCode").GetString()!,
                item.GetProperty("data").GetString()!, item.GetProperty("expected").GetRawText() };
    }

    [Theory]
    [MemberData(nameof(PlatformCases))]
    public void ObservedLayoutsMatchIndependentPlatformExport(string code, string wire, string expected)
    {
        foreach (var strict in new[] { false, true })
        {
            var diagnostics = new List<EventLogDiagnostic>();
            var actual = Read(code, wire, strict, false, diagnostics);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.Data)), actual.Data);
            Assert.DoesNotContain(diagnostics, d => d.Code == "UnknownDataFormat");
        }
    }

    [Theory]
    [MemberData(nameof(PlatformCases))]
    public void OptionalContextContainsTheSameDecodedData(string code, string wire, string expected)
    {
        var actual = JsonNode.Parse(Read(code, wire, false, true).Data)!;
        Assert.Equal("1c-event-context-v1", actual["$format"]!.GetValue<string>());
        Assert.Equal(code, actual["EventCode"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual["Data"]!.GetValue<string>())));
        Assert.NotNull(actual["DataNodes"]);
    }

    [Theory]
    [InlineData("_$Session$_.AddInAttach", "{P,{{S,Name},{S,Location},{U},{S,Future}}}")]
    [InlineData("_$Session$_.AddInAttach", "{P,{{S,Name},{S,Location},{U},{U},{S,Extra}}}")]
    [InlineData("_$Session$_.AddInAttach", "{P,{{S,Name},{S,Location},{U}}}")]
    [InlineData("_$Session$_.AddInAttach", "{P,{{S,Name},{S,Location},{U,Extra},{U}}}")]
    [InlineData("_$Session$_.AddInAttach", "{P,{{S,Name},{N,1},{U},{U}}}")]
    [InlineData("_$Session$_.Authentication", "{P,{515,{S,Name},{N,1}}}")]
    [InlineData("_$Session$_.Authentication", "{P,{514,{S,Name},{S,1}}}")]
    [InlineData("_$Session$_.Authentication", "{P,{514,{S,Name},{N,1},{U}}}")]
    [InlineData("_$Access$_.AccessDenied", "{P,{7,{S,Read},{U}}}")]
    [InlineData("_$Access$_.AccessDenied", "{P,{6,{S,Read},{S,Future}}}")]
    [InlineData("_$Access$_.AccessDenied", "{P,{6,{S,Read},{U,Extra}}}")]
    [InlineData("_$Access$_.AccessDenied", "{P,{6,{S,Read}}}")]
    [InlineData("_$InfoBase$_.SessionLockChange", "{P,{{B,2},{D,20261021010000},{D,20261021020000},{S,Message},{S,Code},{S,Parameter}}}")]
    [InlineData("_$InfoBase$_.SessionLockChange", "{P,{{B,0},{D,20260230010000},{D,20261021020000},{S,Message},{S,Code},{S,Parameter}}}")]
    [InlineData("_$InfoBase$_.SessionLockChange", "{P,{{B,0},{D,20261021010000},{D,invalid},{S,Message},{S,Code},{S,Parameter}}}")]
    [InlineData("_$InfoBase$_.SessionLockChange", "{P,{{B,0},{D,20261021010000},{D,20261021020000},{S,Message},{S,Code}}}")]
    [InlineData("_$Session$_.ExternalDataProcessorConnectError", "{P,{{S,Path},{S,Name},{B,0},{S,Profile},{B,2},{S,en}}}")]
    [InlineData("_$Session$_.ExternalReportConnectError", "{P,{{S,Path},{S,Name},{B,0},{S,Profile},{B,1},{S,en},{U}}}")]
    [InlineData("_$InfoBase$_.DBConfigUpdateError", "{P,{{S,Vendor},{N,1}}}")]
    [InlineData("_$InfoBase$_.ConfigUpdateError", "{P,{{S,Vendor},{S,Version},{U}}}")]
    [InlineData("Fixture.Future", "{P,{{S,Name},{S,Location},{U},{U}}}")]
    public void ChangedLayoutsPreserveTheEntireTree(string code, string wire)
    {
        var diagnostics = new List<EventLogDiagnostic>();
        var actual = JsonNode.Parse(Read(code, wire, false, false, diagnostics).Data)!;
        Assert.Equal("1c-brackets-v1", actual["$format"]!.GetValue<string>());
        // The same bytes under an unknown event provide a lossless-tree reference.
        var reference = JsonNode.Parse(Read("Fixture.Future", wire).Data)!;
        Assert.True(JsonNode.DeepEquals(reference["nodes"], actual["nodes"]));
        Assert.Contains(diagnostics, d => d.Code == "UnknownDataFormat");
    }

    private EventLogItem Read(string code, string wire, bool strict = false, bool context = false,
        List<EventLogDiagnostic>? diagnostics = null)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"" + code + "\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261021000000.lgp"),
            "{20261021120000,N,{0,0},0,0,0,1,1,I,\"\",0," + wire + ",\"\",0,0,0,1,0,{0}}");
        using var reader = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZoneProviders.Tzdb["Europe/Moscow"],
            UseStrictParsing = strict, UseEventContext = context, DiagnosticHandler = d => diagnostics?.Add(d)
        });
        var item = reader.ReadNextEventLogItem();
        Assert.Null(reader.ReadNextEventLogItem());
        return item;
    }

    public void Dispose() => Directory.Delete(_folder, true);
}
