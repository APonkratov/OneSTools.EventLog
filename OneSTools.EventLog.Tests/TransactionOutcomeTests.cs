using NodaTime;

namespace OneSTools.EventLog.Tests;

public sealed class TransactionOutcomeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "onestools-transactions", Guid.NewGuid().ToString("N"));
    private const string FileName = "20261011000000.lgp";
    public TransactionOutcomeTests()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "1Cv8.lgf"),
            "{4,\"Test\",1},\n{4,\"_$Transaction$_.Commit\",2},\n{4,\"_$Transaction$_.Rollback\",3}");
    }
    private static string Row(string marker = "U", string tx = "123456789abcd,1", int connection = 1, int ev = 1) =>
        JournalCursorTests.Record(status: marker, transaction: tx, connection: connection, ev: ev);

    [Theory]
    [InlineData(2, "Зафиксирована")]
    [InlineData(3, "Отменена")]
    public void ResolvesAcrossPeriodsAndFromFourFieldCheckpoint(int finalizer, string expected)
    {
        // The same transaction number/time in another connection must not match.
        Write(FileName, Row() + ",\n" + Row(connection: 2));
        Write("20261012000000.lgp", Row(ev: finalizer));
        var legacySettings = Settings();
        using var legacy = new EventLogReader(legacySettings);
        var checkpoint = legacy.ReadNextEventLogItem();
        Assert.Equal(expected, checkpoint.TransactionStatus);
        var settings = Settings();
        using (var reader = new EventLogReader(settings))
        {
            Assert.Equal(expected, reader.ReadNextEventLogItem().TransactionStatus);
            Assert.Equal("Не завершена", reader.ReadNextEventLogItem().TransactionStatus);
        }
        settings = Settings();
        settings.LgpFileName = checkpoint.FileName; settings.LgpStartPosition = checkpoint.EndPosition;
        settings.LgfStartPosition = checkpoint.LgfEndPosition; settings.ItemId = checkpoint.Id;
        using var resumed = new EventLogReader(settings);
        Assert.Equal("Не завершена", resumed.ReadNextEventLogItem().TransactionStatus);
        Assert.Equal(expected, resumed.ReadNextEventLogItem().TransactionStatus);
    }

    [Theory]
    [InlineData("N", "Нет транзакции")]
    [InlineData("C", "Зафиксирована")]
    [InlineData("R", "Отменена")]
    [InlineData("U", "Не завершена")]
    public void DirectMarkersUsePlatformMeaning(string marker, string expected)
    {
        Write(FileName, Row(marker));
        var settings = Settings();
        using var reader = new EventLogReader(settings);
        Assert.Equal(expected, reader.ReadNextEventLogItem().TransactionStatus);
    }

    [Fact]
    public void ScansBeforeCheckpointAndDateFilterAndUsesUnroundedTransactionTime()
    {
        var prior = Row(ev: 3).Replace("20261011120000", "20261010120000");
        Write(FileName, prior + ",\n" + Row() + ",\n" + Row(tx: "123456789abcc,1"));
        var settings = Settings();
        settings.SkipEventsBeforeDate = new DateTime(2026, 10, 11);
        settings.LgpFileName = FileName; settings.LgpStartPosition = System.Text.Encoding.UTF8.GetByteCount(prior);
        using var reader = new EventLogReader(settings);
        Assert.Equal("Отменена", reader.ReadNextEventLogItem().TransactionStatus);
        Assert.Equal("Не завершена", reader.ReadNextEventLogItem().TransactionStatus);
    }

    [Fact]
    public void LiveIndexRetriesIncompleteFinalizerAndDoesNotRewriteDeliveredItems()
    {
        Write(FileName, Row() + ",\n" + Row());
        var settings = Settings(); settings.LiveMode = true;
        settings.ReadingTimeout = 500;
        using var reader = new EventLogReader(settings);
        var delivered = reader.ReadNextEventLogItem();
        Assert.Equal("Не завершена", delivered.TransactionStatus);
        var finalizer = Row(ev: 3);
        File.AppendAllText(Path.Combine(_folder, FileName), ",\n" + finalizer[..40]);
        Assert.Equal("Не завершена", reader.ReadNextEventLogItem().TransactionStatus);
        File.AppendAllText(Path.Combine(_folder, FileName), finalizer[40..] + ",\n" + Row());
        Assert.Equal("Отменена", reader.ReadNextEventLogItem().TransactionStatus);
        Assert.Equal("Отменена", reader.ReadNextEventLogItem().TransactionStatus);
        Assert.Equal("Не завершена", delivered.TransactionStatus);
    }

    [Fact]
    public void ConflictingOutcomesFailWithoutAdvancingDeliveryId()
    {
        Write(FileName, Row() + ",\n" + Row(ev: 2) + ",\n" + Row(ev: 3));
        var settings = Settings();
        using var reader = new EventLogReader(settings);
        Assert.Throws<InvalidDataException>(() => reader.ReadNextEventLogItem());
        Assert.Throws<InvalidDataException>(() => reader.ReadNextEventLogItem());
        Assert.Equal(0, settings.ItemId);
    }

    [Fact]
    public void MissingDictionaryReferenceCanBeFixedAndRetriedWithoutLosingPendingEvent()
    {
        Write(FileName, Row() + ",\n" + Row(ev: 4));
        var settings = Settings(); settings.LiveMode = true;
        using var reader = new EventLogReader(settings);
        Assert.Throws<InvalidDataException>(() => reader.ReadNextEventLogItem());
        Assert.Equal(0, settings.ItemId);
        File.AppendAllText(Path.Combine(_folder, "1Cv8.lgf"), ",\n{4,\"_$Transaction$_.Rollback\",4}");
        var item = reader.ReadNextEventLogItem();
        Assert.Equal(1, item.Id);
        Assert.Equal("Отменена", item.TransactionStatus);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(Row()), item.EndPosition);
    }

    private void Write(string file, string value) => File.WriteAllText(Path.Combine(_folder, file), value);
    private EventLogReaderSettings Settings() => new() {LogFolder = _folder, LiveMode = false, TimeZone = DateTimeZone.Utc};
    public void Dispose() => Directory.Delete(_folder, true);
}
