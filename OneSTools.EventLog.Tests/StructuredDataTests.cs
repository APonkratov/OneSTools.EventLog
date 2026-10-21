using System.Text;
using System.Text.Json.Nodes;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class StructuredDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-structured", Guid.NewGuid().ToString("N"));

    public StructuredDataTests() => Directory.CreateDirectory(_folder);

    // Wire shapes and field names checked against ibcmd 8.3.27.2325 exports.
    // Values/identifiers are artificial; original journals are not distributed.
    public static TheoryData<string, string, string> KnownCases => new()
    {
        { "_$Session$_.Authentication", "{\"P\",{518,{\"S\",\"\"},{\"S\",\"test-computer\\test-user\"},{\"N\",1}}}", "{\"Name\":\"\",\"CurrentOSUser\":\"test-computer\\\\test-user\",\"AuthenticationMethod\":\"1\"}" },
        { "_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"\"},{\"S\",\"\"}}}", "{\"Vendor\":\"\",\"Version\":\"\"}" },
        { "_$InfoBase$_.DBConfigUpdate", "{\"P\",{{\"S\",\"\"},{\"S\",\"\"}}}", "{\"Vendor\":\"\",\"Version\":\"\"}" },
        { "_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"FixtureVendor\"},{\"S\",\"9.8.7\"}}}", "{\"Vendor\":\"FixtureVendor\",\"Version\":\"9.8.7\"}" },
        { "_$InfoBase$_.DBConfigUpdate", "{\"P\",{{\"S\",\"FixtureVendor\"},{\"S\",\"9.8.7\"}}}", "{\"Vendor\":\"FixtureVendor\",\"Version\":\"9.8.7\"}" },
        { "_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",1}}}", "{\"ExclusiveMode\":\"true\"}" },
        { "_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",0}}}", "{\"ExclusiveMode\":\"false\"}" },
        { "_$Access$_.Access", "{\"P\",{1,{\"Z\",{2,1,{\"Свойство\",{\"Pattern\"}},{\"Значение\",{\"Pattern\"}},{{\"S\",\"Право\"},{\"S\",\"Администрирование\"}}}}}}", "{\"Data\":{\"Column\":[{\"name\":\"Свойство\"},{\"name\":\"Значение\"}],\"Row\":{\"Свойство\":\"Право\",\"Значение\":\"Администрирование\"}}}" }
    };

    [Theory]
    [MemberData(nameof(KnownCases))]
    public void KnownShapesAlwaysMatchPlatform(string ev, string data, string expected)
    {
        AssertJson(expected, Read(ev, data).Data);
    }

    [Theory]
    [InlineData("_$Session$_.Authentication", "{\"P\",{999,{\"S\",\"future\"}}}", "[\"P\",[\"999\",[\"S\",\"future\"]]]")]
    [InlineData("_$Session$_.Authentication", "{\"P\",{518,{\"U\"},{\"S\",\"user\"},{\"N\",1}}}", "[\"P\",[\"518\",[\"U\"],[\"S\",\"user\"],[\"N\",\"1\"]]]")]
    [InlineData("_$Session$_.Authentication", "{\"P\",{518,{\"S\",\"\"},{\"S\",\"user\"},{\"N\",1},{\"S\",\"extra\"}}}", "[\"P\",[\"518\",[\"S\",\"\"],[\"S\",\"user\"],[\"N\",\"1\"],[\"S\",\"extra\"]]]")]
    [InlineData("_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"vendor\"},{\"S\",\"version\"},{\"U\"}}}", "[\"P\",[[\"S\",\"vendor\"],[\"S\",\"version\"],[\"U\"]]]")]
    [InlineData("_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"vendor\"},{\"N\",1}}}", "[\"P\",[[\"S\",\"vendor\"],[\"N\",\"1\"]]]")]
    [InlineData("_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",2}}}", "[\"P\",[\"2\",[\"B\",\"2\"]]]")]
    [InlineData("_$InfoBase$_.RegionalSettingsChange", "{P,{{S,en_US},{B,2}}}", "[\"P\",[[\"S\",\"en_US\"],[\"B\",\"2\"]]]")]
    [InlineData("_$InfoBase$_.UserPasswordPolicyDelete", "{P,{{S,Fixture},{S,extra}}}", "[\"P\",[[\"S\",\"Fixture\"],[\"S\",\"extra\"]]]")]
    [InlineData("Corpus.Future", "{\"P\",{2,{\"B\",1}}}", "[\"P\",[\"2\",[\"B\",\"1\"]]]")]
    [InlineData("Corpus.Future", "{\"P\",{999,{\"P\",{111,{\"X\",\"\"},{\"S\",\"a\"\"b\"},{\"N\",12345678901234567890123456789012345678}}}}}", "[\"P\",[\"999\",[\"P\",[\"111\",[\"X\",\"\"],[\"S\",\"a\\\"\\\"b\"],[\"N\",\"12345678901234567890123456789012345678\"]]]]]")]
    [InlineData("Corpus.Future", "{\"X\",\"future-data\"}", "[\"X\",\"future-data\"]")]
    [InlineData("Corpus.Future", "{\"P\",{}}", "[\"P\",[]]")]
    [InlineData("Corpus.Future", "{\"P\",\"unexpected\"}", "[\"P\",\"unexpected\"]")]
    [InlineData("Corpus.Future", "{\"P\"}", "[\"P\"]")]
    [InlineData("_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",1}},\"extra\"}", "[\"P\",[\"2\",[\"B\",\"1\"]],\"extra\"]")]
    public void UnknownOrChangedShapesPreserveEveryParsedToken(string ev, string data, string nodes)
    {
        var parsed = JsonNode.Parse(Read(ev, data).Data)!;
        Assert.Equal("1c-brackets-v1", parsed["$format"]!.GetValue<string>());
        Assert.Equal(2, parsed.AsObject().Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(nodes), parsed["nodes"]));
    }

    [Fact]
    public void ScalarAndStructuredDataAreBothDecodedWithoutOptions()
    {
        Assert.Equal("123.45", Read("Corpus.Test", "{\"N\",123.45}").Data);
        AssertJson("{\"ExclusiveMode\":\"true\"}", Read("_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",1}}}").Data);
    }

    [Fact]
    public void KnownTextValuesPreserveWhitespaceEscapesAndControlCharacters()
    {
        const string text = "  \"Тест\"\\path\n\r\t\b\f\0\u001f日本語😀  ";
        var value = text.Replace("\"", "\"\"");
        var item = Read("_$InfoBase$_.ConfigUpdate", "{\"P\",{{\"S\",\"" + value + "\"},{\"S\",\"1.2\"}}}");
        var parsed = JsonNode.Parse(item.Data)!;
        Assert.Equal(text, parsed["Vendor"]!.GetValue<string>());
        Assert.Equal("1.2", parsed["Version"]!.GetValue<string>());
    }

    [Fact]
    public void MultipleTableRowsRetainEveryCell()
    {
        const string data = "{\"P\",{1,{\"Z\",{2,2,{\"A\",{\"Pattern\"}},{\"B\",{\"Pattern\"}},{{\"S\",\"one\"},{\"S\",\"two\"}},{{\"S\",\"three\"},{\"S\",\"four\"}}}}}}";
        var parsed = JsonNode.Parse(Read("_$Access$_.Access", data).Data)!;
        AssertJson("{\"Data\":{\"Column\":[{\"name\":\"A\"},{\"name\":\"B\"}],\"Row\":[{\"A\":\"one\",\"B\":\"two\"},{\"A\":\"three\",\"B\":\"four\"}]}}", parsed.ToJsonString());
    }

    public static IEnumerable<object[]> PlatformFixtures()
    {
        using var fixtures = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "noncorp-data-8.3.27.json")));
        foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { item.GetProperty("eventCode").GetString()!, item.GetProperty("data").GetString()!, item.GetProperty("expected").GetRawText() };
    }

    [Theory]
    [MemberData(nameof(PlatformFixtures))]
    public void ExpandedLayoutsMatchIndependentPlatformFixtures(string code, string data, string expected)
    {
        AssertJson(expected, Read(code, data).Data);
    }

    [Fact]
    public void DuplicateColumnNamesFallBackInsteadOfOverwritingValues()
    {
        const string data = "{\"P\",{1,{\"Z\",{2,1,{\"A\",{\"Pattern\"}},{\"A\",{\"Pattern\"}},{{\"S\",\"one\"},{\"S\",\"two\"}}}}}}";
        var parsed = JsonNode.Parse(Read("_$Access$_.Access", data).Data)!;
        Assert.Equal("1c-brackets-v1", parsed["$format"]!.GetValue<string>());
        Assert.Equal("two", parsed["nodes"]![1]![1]![1]![4]![1]![1]!.GetValue<string>());
    }

    [Fact]
    public void UnknownColumnDescriptorFallsBackInsteadOfDiscardingIt()
    {
        const string data = "{\"P\",{1,{\"Z\",{2,1,{\"A\",{\"FuturePattern\",\"metadata\"}},{\"B\",{\"Pattern\"}},{{\"S\",\"one\"},{\"S\",\"two\"}}}}}}";
        var parsed = JsonNode.Parse(Read("_$Access$_.Access", data).Data)!;
        Assert.Equal("1c-brackets-v1", parsed["$format"]!.GetValue<string>());
        AssertJson("[\"FuturePattern\",\"metadata\"]", parsed["nodes"]![1]![1]![1]![2]![1]!.ToJsonString());
    }

    [Fact]
    public void FourFieldCheckpointResumesStructuredDataAcrossPeriods()
    {
        Read("_$InfoBase$_.ExclusiveModeChange", "{\"P\",{2,{\"B\",1}}}");
        var firstPath = Path.Combine(_folder, "20261009000000.lgp");
        var record = File.ReadAllText(firstPath);
        File.AppendAllText(firstPath, ",\n" + record.Replace("{\"B\",1}", "{\"B\",0}"));
        File.WriteAllText(Path.Combine(_folder, "20261010000000.lgp"), record);
        using var legacy = new EventLogReader(Settings());
        var checkpoint = legacy.ReadNextEventLogItem();
        var remaining = legacy.ReadNextEventLogItem();
        var following = legacy.ReadNextEventLogItem();
        var settings = Settings();
        settings.LgpFileName = checkpoint.FileName;
        settings.LgpStartPosition = checkpoint.EndPosition;
        settings.LgfStartPosition = checkpoint.LgfEndPosition;
        settings.ItemId = checkpoint.Id;
        using var resumed = new EventLogReader(settings);
        foreach (var expected in new[] { remaining, following })
        {
            var actual = resumed.ReadNextEventLogItem();
            Assert.Equal((expected.FileName, expected.EndPosition, expected.LgfEndPosition, expected.Id),
                (actual.FileName, actual.EndPosition, actual.LgfEndPosition, actual.Id));
            AssertJson(expected == remaining ? "{\"ExclusiveMode\":\"false\"}" : "{\"ExclusiveMode\":\"true\"}", actual.Data);
        }
        Assert.Null(resumed.ReadNextEventLogItem());
    }

    private EventLogReaderSettings Settings() =>
        new() { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc };

    private EventLogItem Read(string ev, string data)
    {
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"),
            "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n{4,\"" + ev + "\",1}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_folder, "20261009000000.lgp"),
            "{20261009123456,C,{0,0},0,0,0,17,1,I,\"\"\"Тест\"\"\",0," + data + ",\"\",0,0,0,23,0,{0}}", new UTF8Encoding(false));
        var settings = new EventLogReaderSettings
        {
            LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc
        };
        using var reader = new EventLogReader(settings);
        var item = reader.ReadNextEventLogItem();
        Assert.Null(reader.ReadNextEventLogItem());
        return item;
    }

    private static void AssertJson(string expected, string actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Unexpected JSON: {actual}");

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
