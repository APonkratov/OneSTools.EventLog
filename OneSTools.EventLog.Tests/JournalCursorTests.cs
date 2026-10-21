using System.Text;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class JournalCursorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-cursors", Guid.NewGuid().ToString("N"));
    private const string FileName = "20261011000000.lgp";
    public JournalCursorTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"), "{4,\"Test\",1}", new UTF8Encoding(false));
    }

    internal static string Record(string comment = "", int metadata = 0, string status = "N", string transaction = "0,0", int connection = 1, int ev = 1) =>
        $"{{20261011120000,{status},{{{transaction}}},0,0,0,{connection},{ev},I,\"{comment}\",{metadata},{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}";

    [Theory]
    [InlineData(1000)]
    [InlineData(1010)]
    [InlineData(8190)]
    [InlineData(16380)]
    public void CursorCountsOriginalBytesAndResumesOldDelimiterOffsets(int length)
    {
        var prefix = "1CV8LOG(ver 2.0)\r\n";
        var first = Record(new string('я', length) + "日本語😀\"\"{текст}\"\"\r\n");
        var second = Record("следующий");
        File.WriteAllText(Path.Combine(_folder, FileName), prefix + first + ",\r\n" + second, new UTF8Encoding(true));
        using var reader = new EventLogReader(Settings());
        var checkpoint = reader.ReadNextEventLogItem();
        Assert.Equal(3 + Encoding.UTF8.GetByteCount(prefix + first), checkpoint.EndPosition);
        var expected = reader.ReadNextEventLogItem();
        Assert.Equal(new FileInfo(Path.Combine(_folder, FileName)).Length, expected.EndPosition);
        foreach (var delta in new[] { 0, 1 })
        {
            var settings = Settings();
            settings.LgpFileName = checkpoint.FileName;
            settings.LgpStartPosition = checkpoint.EndPosition + delta;
            settings.LgfStartPosition = checkpoint.LgfEndPosition + delta;
            settings.ItemId = checkpoint.Id;
            using var resumed = new EventLogReader(settings);
            var actual = resumed.ReadNextEventLogItem();
            Assert.Equal((expected.Comment, expected.EndPosition, expected.LgfEndPosition, expected.Id),
                (actual.Comment, actual.EndPosition, actual.LgfEndPosition, actual.Id));
            Assert.Null(resumed.ReadNextEventLogItem());
        }
    }

    [Fact]
    public void EveryCursorMatchesBytesAcrossManyDecoderBufferBoundaries()
    {
        var records = Enumerable.Range(0, 500).Select(i => Record(new string('я', i % 79) + "日本😀")).ToArray();
        File.WriteAllText(Path.Combine(_folder, FileName), string.Join(",\n", records), new UTF8Encoding(false));
        using var reader = new EventLogReader(Settings());
        long end = 0;
        for (var i = 0; i < records.Length; i++)
        {
            end += Encoding.UTF8.GetByteCount(records[i]) + (i == 0 ? 0 : 2);
            Assert.Equal(end, reader.ReadNextEventLogItem().EndPosition);
        }
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void IncompleteUtf8TailCanBeRetriedWithoutSkippingTheRecord()
    {
        var bytes = Encoding.UTF8.GetBytes(Record("😀конец"));
        var split = Array.IndexOf(bytes, (byte)0xf0) + 2;
        var path = Path.Combine(_folder, FileName);
        File.WriteAllBytes(path, bytes[..split]);
        var settings = Settings();
        settings.LgpFileName = FileName;
        using var reader = new EventLogReader(settings);
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            append.Write(bytes[split..]);
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("😀конец", item.Comment);
        Assert.Equal(bytes.Length, item.EndPosition);
        Assert.Equal(1, item.Id);
    }

    [Fact]
    public void DictionaryCheckpointIncludesReferencesResolvedByTheEvent()
    {
        File.AppendAllText(Path.Combine(_folder, "1Cv8.lgf"), ",\n{5,11111111-1111-1111-1111-111111111111,\"Catalog.Test\",1}");
        File.WriteAllText(Path.Combine(_folder, FileName), Record(metadata: 1));
        using var reader = new EventLogReader(Settings());
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("Catalog.Test", item.Metadata);
        Assert.Equal(new FileInfo(Path.Combine(_folder, "1Cv8.lgf")).Length, item.LgfEndPosition);
    }

    [Theory]
    [InlineData(8180)]
    [InlineData(8181)]
    [InlineData(8184)]
    [InlineData(8185)]
    [InlineData(8186)]
    [InlineData(8187)]
    [InlineData(8188)]
    [InlineData(8189)]
    [InlineData(8190)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    [InlineData(8194)]
    [InlineData(8195)]
    [InlineData(8196)]
    [InlineData(8197)]
    [InlineData(16383)]
    [InlineData(16384)]
    public void BufferedAndSpanningRecordsPreserveUtf8QuotesAndFollowingRecord(int specialOffset)
    {
        var commentStart = Record().IndexOf("I,\"", StringComparison.Ordinal) + 3;
        var wireComment = new string('x', specialOffset - commentStart) + "😀\"\"{я}\"\"";
        var first = Record(wireComment);
        var second = Record("next");
        File.WriteAllText(Path.Combine(_folder, FileName), first + ",\n" + second, new UTF8Encoding(false));
        using var reader = new EventLogReader(Settings());
        var item = reader.ReadNextEventLogItem();
        Assert.Equal(wireComment.Replace("\"\"", "\""), item.Comment);
        Assert.Equal(Encoding.UTF8.GetByteCount(first), item.EndPosition);
        var next = reader.ReadNextEventLogItem();
        Assert.Equal("next", next.Comment);
        Assert.Equal(Encoding.UTF8.GetByteCount(first + ",\n" + second), next.EndPosition);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(8193)]
    public void RecordEndingAtBufferBoundaryDoesNotConsumeFollowingRecord(int end)
    {
        var first = Record("first");
        var second = Record("second");
        var prefix = new string(' ', end - Encoding.UTF8.GetByteCount(first));
        File.WriteAllText(Path.Combine(_folder, FileName), prefix + first + ",\n" + second, new UTF8Encoding(false));
        using var reader = new EventLogReader(Settings());
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("first", item.Comment);
        Assert.Equal(end, item.EndPosition);
        var next = reader.ReadNextEventLogItem();
        Assert.Equal("second", next.Comment);
        Assert.Equal(end + 2 + Encoding.UTF8.GetByteCount(second), next.EndPosition);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void SpanningIncompleteRecordCanBeRetriedBeforeAndAfterItsTailArrives()
    {
        var comment = new string('я', 12000) + "😀end";
        var bytes = Encoding.UTF8.GetBytes(Record(comment));
        var split = Array.IndexOf(bytes, (byte)0xf0) + 2;
        var path = Path.Combine(_folder, FileName);
        File.WriteAllBytes(path, bytes[..split]);
        var settings = Settings(); settings.LgpFileName = FileName;
        using var reader = new EventLogReader(settings);
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        Assert.Throws<EventLogParseException>(() => reader.ReadNextEventLogItem());
        using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            append.Write(bytes[split..]);
        var item = reader.ReadNextEventLogItem();
        Assert.Equal(comment, item.Comment);
        Assert.Equal(bytes.Length, item.EndPosition);
        Assert.Equal(1, item.Id);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    internal EventLogReaderSettings Settings() => new()
    { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc };
    public void Dispose() => Directory.Delete(_folder, true);
}
