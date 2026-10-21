using System.Text.Json;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class PositionalDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-positional", Guid.NewGuid().ToString("N"));

    public PositionalDataTests() => Directory.CreateDirectory(_folder);

    public static IEnumerable<object[]> Cases()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "positional-data-8.3.27.json")));
        foreach (var item in source.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { item.GetRawText() };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ControlledPropertyWritesDecodeWithoutLosingUnmappedPositions(string fixture)
    {
        var sample = JsonNode.Parse(fixture)!;
        var code = sample["eventCode"]!.ToString();
        var data = Wire(sample["nodes"]!);
        var item = Read(code, data);
        var parsed = JsonNode.Parse(item.Data)!;
        Assert.Equal("1c-positional-data-v1", parsed["$format"]!.ToString());
        Assert.Equal("P/" + sample["nodes"]![1]![0], parsed["layout"]!.ToString());
        Assert.True(JsonNode.DeepEquals(sample["nodes"], parsed["nodes"]));
        Assert.True(JsonNode.DeepEquals(sample["unnamedFields"], parsed["unnamedFields"]));
        foreach (var property in sample["expectedValues"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(property.Value, parsed["values"]![property.Key]),
                $"Operation {sample["operation"]}, field {property.Key}");

    }

    [Theory]
    [InlineData("unknown-mask")]
    [InlineData("missing-field")]
    [InlineData("extra-field")]
    [InlineData("wrong-type")]
    [InlineData("invalid-boolean")]
    [InlineData("invalid-date")]
    [InlineData("invalid-reference")]
    [InlineData("invalid-array-count")]
    [InlineData("unknown-role-type")]
    [InlineData("unknown-event")]
    public void ChangedUserLayoutsKeepCompleteFallbackInsteadOfMislabelingFields(string mutation)
    {
        var sample = JsonNode.Parse(Cases().First()[0].ToString()!)!;
        var nodes = sample["nodes"]!.DeepClone();
        var body = nodes[1]!.AsArray();
        var code = sample["eventCode"]!.ToString();
        switch (mutation)
        {
            case "unknown-mask": body[0] = "65533"; break;
            case "missing-field": body.RemoveAt(13); break;
            case "extra-field": body.Add(new JsonArray("U")); break;
            case "wrong-type": body[19]![0] = "N"; break;
            case "invalid-boolean": body[1]![1] = "2"; break;
            case "invalid-date": body[24]![1] = "20260230000000"; break;
            case "invalid-reference": body[5]![1]![2] = "not-a-uuid"; break;
            case "invalid-array-count": body[12]![1]![0] = "2"; break;
            case "unknown-role-type": body[12]![1]![1]![0] = "Future"; break;
            case "unknown-event": code = "Fixture.Future"; break;
        }
        var result = JsonNode.Parse(Read(code, Wire(nodes)).Data)!;
        Assert.Equal("1c-brackets-v1", result["$format"]!.ToString());
        Assert.True(JsonNode.DeepEquals(nodes, result["nodes"]));
    }

    [Theory]
    [InlineData("_$InfoBase$_.ParametersUpdate", "{P,{256,{N,9}}}")]
    [InlineData("_$InfoBase$_.ParametersUpdate", "{P,{192,{N,3}}}")]
    [InlineData("_$InfoBase$_.ParametersUpdate", "{P,{128,{S,60}}}")]
    [InlineData("_$InfoBase$_.ParametersUpdate", "{P,{65,{N,1},{N,3}}}")]
    [InlineData("_$InfoBase$_.UserPasswordPolicyNew", "{P,{254,{S,Fixture},{N,0},{N,0},{N,0},{N,0},{B,0},{N,0},{B,0},{N,2}}}")]
    [InlineData("_$InfoBase$_.DBConfigUpdate", "{P,{}}")]
    [InlineData("_$InfoBase$_.DBConfigExtensionUpdate", "{P,{}}")]
    public void UnverifiedMasksAndEmptyPayloadsRemainUninterpreted(string code, string wire)
    {
        Assert.Equal("1c-brackets-v1", JsonNode.Parse(Read(code, wire).Data)!["$format"]!.ToString());
    }

    private EventLogItem Read(string code, string data)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"" + code + "\",1}");
        File.WriteAllText(Path.Combine(_folder, "20261011000000.lgp"),
            "{20261011120000,N,{0,0},0,0,0,1,1,I,\"\",0," + data + ",\"\",0,0,0,1,0,{0}}");
        using var reader = new EventLogReader(new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc
        });
        var item = reader.ReadNextEventLogItem();
        Assert.Null(reader.ReadNextEventLogItem());
        return item;
    }

    private static string Wire(JsonNode node) => node is JsonArray array
        ? "{" + string.Join(",", array.Select(value => Wire(value!))) + "}"
        : "\"" + node.ToString() + "\"";

    public void Dispose() => Directory.Delete(_folder, true);
}
