using System.Text;
using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class EventLogFileRotationTests : IDisposable
{
    private const string First = "20261009000000.lgp";
    private const string Second = "20261010000000.lgp";
    private static readonly DateTime SameWriteTime = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-rotation-tests", Guid.NewGuid().ToString("N"));

    public EventLogFileRotationTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"),
            "1CV8LOG(ver 2.0)\n11111111-2222-3333-4444-555555555555\n\n{4,\"Corpus.Test\",1}",
            new UTF8Encoding(false));
    }

    [Fact]
    public void ReadsBothFilesWhenModificationTimesAreEqual()
    {
        Write(First, "20261009120000");
        Write(Second, "20261010120000");
        using var reader = Reader();
        Assert.Equal(new[] { First, Second }, ReadAll(reader).Select(item => item.FileName));
    }

    [Fact]
    public void UsesPeriodOrderWhenCopyingReversesModificationTimes()
    {
        Write(First, "20261009120000", SameWriteTime.AddDays(1));
        Write(Second, "20261010120000", SameWriteTime);
        using var reader = Reader();
        Assert.Equal(new[] { First, Second }, ReadAll(reader).Select(item => item.FileName));
    }

    [Fact]
    public void DiscoversNextPeriodWithAnOlderModificationTimeAfterEndOfFile()
    {
        Write(First, "20261009120000");
        using var reader = Reader();
        Assert.Equal(First, reader.ReadNextEventLogItem().FileName);
        Assert.Null(reader.ReadNextEventLogItem());
        Write(Second, "20261010120000", SameWriteTime.AddDays(-1));
        Assert.Equal(Second, reader.ReadNextEventLogItem().FileName);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void FiltersByEventDateWhenFileModificationTimePredatesCutoff()
    {
        Write(First, "20261009120000", SameWriteTime.AddYears(-2));
        Write(Second, "20261010120000", SameWriteTime.AddYears(-2));
        using var reader = Reader(settings => settings.SkipEventsBeforeDate = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));
        var item = Assert.Single(ReadAll(reader));
        Assert.Equal(Second, item.FileName);
    }

    [Fact]
    public void ResumesExistingCheckpointThenReadsNextFileWithEqualModificationTime()
    {
        Write(First, "20261009120000");
        File.AppendAllText(Path.Combine(_folder, First), ",\n" + Record("20261009130000"));
        File.SetLastWriteTimeUtc(Path.Combine(_folder, First), SameWriteTime);
        Write(Second, "20261010120000");
        EventLogItem checkpoint;
        using (var reader = Reader()) checkpoint = reader.ReadNextEventLogItem();
        using var resumed = Reader(settings =>
        {
            settings.LgpFileName = checkpoint.FileName;
            settings.LgpStartPosition = checkpoint.EndPosition;
            settings.LgfStartPosition = checkpoint.LgfEndPosition;
            settings.ItemId = checkpoint.Id;
        });
        var remaining = ReadAll(resumed);
        Assert.Equal(new[] { First, Second }, remaining.Select(item => item.FileName));
        Assert.Equal(new long[] { 2, 3 }, remaining.Select(item => item.Id));
        Assert.Equal(new DateTime(2026, 10, 9, 13, 0, 0, DateTimeKind.Utc), remaining[0].DateTime);
    }

    [Fact]
    public void EmptyArchiveReturnsNull()
    {
        using var reader = Reader();
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void EmptyLiveLogWaitsForConfiguredTimeout()
    {
        using var reader = Reader(settings => { settings.LiveMode = true; settings.ReadingTimeout = 30; });
        Assert.Throws<EventLogReaderTimeoutException>(() => reader.ReadNextEventLogItem());
    }

    [Fact]
    public void AppendedIncompleteTailIsReadOnceWithoutRewindingEarlierEvents()
    {
        Write(First, "20261009120000");
        var path = Path.Combine(_folder, First);
        var tail = Record("20261009130000");
        File.AppendAllText(path, ",\n" + tail[..^5]);
        using var reader = Reader();
        Assert.NotNull(reader.ReadNextEventLogItem());
        Assert.Null(reader.ReadNextEventLogItem());
        File.AppendAllText(path, tail[^5..]);
        Assert.Equal(new DateTime(2026, 10, 9, 13, 0, 0, DateTimeKind.Utc), reader.ReadNextEventLogItem().DateTime);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void DiscoversFirstFileAddedAfterAnEmptyArchiveRead()
    {
        using var reader = Reader();
        Assert.Null(reader.ReadNextEventLogItem());
        Write(First, "20261009120000");
        Assert.Equal(First, reader.ReadNextEventLogItem().FileName);
        Assert.Null(reader.ReadNextEventLogItem());
    }

    [Fact]
    public void ExistingFieldValuesRemainUnchangedByRotationFix()
    {
        Write(First, "20261009120000");
        using var reader = Reader();
        var item = reader.ReadNextEventLogItem();
        Assert.Equal("Corpus.Test", item.Event);
        Assert.Equal("Информация", item.Severity);
        Assert.Equal("Нет транзакции", item.TransactionStatus);
        Assert.Equal("Комментарий", item.Comment);
        // Scalar parsing is a separate change: preserve current output in this PR.
        Assert.Equal("Текст \"\"в кавычках\"\"", item.Data);
        Assert.Equal(1, item.Id);
        Assert.Equal(1, item.Session);
        Assert.Equal(1, item.Connection);
        Assert.Equal(new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), item.DateTime);
    }

    private EventLogReader Reader(Action<EventLogReaderSettings>? configure = null)
    {
        var settings = new EventLogReaderSettings { LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc };
        configure?.Invoke(settings);
        return new EventLogReader(settings);
    }

    private void Write(string name, string date, DateTime? modified = null)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, Record(date), new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, modified ?? SameWriteTime);
    }

    private static string Record(string date) =>
        "{" + date + ",N,{0,0},0,0,0,1,1,I,\"Комментарий\",0,{\"S\",\"  Текст \"\"в кавычках\"\"  \"},\"\",0,0,0,1,0,{0}}";

    private static List<EventLogItem> ReadAll(EventLogReader reader)
    {
        var items = new List<EventLogItem>();
        while (reader.ReadNextEventLogItem() is { } item)
        {
            items.Add(item);
            Assert.True(items.Count <= 10, "Unexpected rereading of earlier files");
        }
        return items;
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
