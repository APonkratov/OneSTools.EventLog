using System.Text;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class DictionaryRefreshTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-lgf", Guid.NewGuid().ToString("N"));
    private string Lgf => Path.Combine(_folder, "1Cv8.lgf");
    private const string FileName = "20261011000000.lgp";
    public DictionaryRefreshTests() => Directory.CreateDirectory(_folder);
    private static string Metadata(string value) => $"{{5,11111111-1111-1111-1111-111111111111,\"{value}\",1}}";

    [Theory]
    [InlineData(null, "old")]
    [InlineData(false, "old")]
    [InlineData(true, "new")]
    public void LatestCompletedValuesAreIndependentAndOptIn(bool? enabled, string expected)
    {
        File.WriteAllText(Lgf, "{4,\"Test\",1},\n" + Metadata("old") + ",\n" + Metadata("new"));
        File.WriteAllText(Path.Combine(_folder, FileName), JournalCursorTests.Record(metadata: 1));
        var settings = Settings();
        if (enabled.HasValue) settings.UseLatestDictionaryValues = enabled.Value;
        using var reader = new EventLogReader(settings);
        Assert.Equal(expected, reader.ReadNextEventLogItem().Metadata);
    }

    [Theory]
    [InlineData("", "filled")]
    [InlineData("old", "renamed")]
    [InlineData("old", "")]
    public void CachedEntriesRefreshAfterAppendAndResume(string initial, string updated)
    {
        File.WriteAllText(Lgf, "{4,\"Test\",1},\n" + Metadata(initial));
        File.WriteAllText(Path.Combine(_folder, FileName), JournalCursorTests.Record(metadata: 1) + ",\n" + JournalCursorTests.Record(metadata: 1));
        var settings = Settings(); settings.UseLatestDictionaryValues = true;
        using var reader = new EventLogReader(settings);
        var first = reader.ReadNextEventLogItem();
        Assert.Equal(initial, first.Metadata);
        File.AppendAllText(Lgf, ",\n{19,\"unknown\",1},\n" + Metadata(updated));
        var next = reader.ReadNextEventLogItem();
        Assert.Equal(updated, next.Metadata);
        Assert.Equal(new FileInfo(Lgf).Length, next.LgfEndPosition);
        settings.LgpFileName = first.FileName; settings.LgpStartPosition = first.EndPosition;
        settings.LgfStartPosition = first.LgfEndPosition; settings.ItemId = first.Id;
        using var resumed = new EventLogReader(settings);
        var actual = resumed.ReadNextEventLogItem();
        Assert.Equal((next.Metadata, next.LgfEndPosition, next.EndPosition, next.Id),
            (actual.Metadata, actual.LgfEndPosition, actual.EndPosition, actual.Id));
    }

    [Fact]
    public void SplitUtf8DictionaryUpdateIsRetriedAfterAppend()
    {
        File.WriteAllText(Lgf, "{4,\"Test\",1},\n" + Metadata("old") + ",\n");
        var tail = Encoding.UTF8.GetBytes(Metadata("😀new"));
        var split = Array.IndexOf(tail, (byte)0xf0) + 1;
        using (var append = new FileStream(Lgf, FileMode.Append)) append.Write(tail[..split]);
        File.WriteAllText(Path.Combine(_folder, FileName), JournalCursorTests.Record(metadata: 1) + ",\n" + JournalCursorTests.Record(metadata: 1));
        var settings = Settings(); settings.UseLatestDictionaryValues = true;
        using var reader = new EventLogReader(settings);
        Assert.Equal("old", reader.ReadNextEventLogItem().Metadata);
        using (var append = new FileStream(Lgf, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) append.Write(tail[split..]);
        Assert.Equal("😀new", reader.ReadNextEventLogItem().Metadata);
    }

    private EventLogReaderSettings Settings() => new() {LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc};
    public void Dispose() => Directory.Delete(_folder, true);
}
