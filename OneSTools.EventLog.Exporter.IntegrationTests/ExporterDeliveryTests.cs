using System.Runtime.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class ExporterDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-delivery", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "log");
    private string State => Path.Combine(_root, "state", "delivery.json");
    private const string FileName = "20261011000000.lgp";
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExporterDeliveryTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"Test\",1}");
    }
    private void Write(int count) => File.WriteAllText(Path.Combine(Folder, FileName), string.Join(",\n", Enumerable.Range(1, count).Select(i =>
        $"{{20261011120000,N,{{0,0}},0,0,0,1,1,I,\"{i}\",0,{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}")));
    private EventLogExporterSettings Settings(bool checkpoint = false) => new()
    {
        LogFolder = Folder, Portion = 2, LoadArchive = true, TimeZone = DateTimeZone.Utc,
        CheckpointFile = checkpoint ? State : "", ShutdownTimeout = 1
    };
    private async Task Run(EventLogExporterSettings settings, Storage storage)
    {
        using var exporter = new EventLogExporter(settings, storage);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ArchiveCompletesAndFlushesOnlyNonemptyBatches(int count)
    {
        Write(count);
        var storage = new Storage();
        await Run(Settings(), storage);
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), storage.Stored.Select(i => i.Id));
        Assert.Equal((count + 1) / 2, storage.Calls);
    }

    [Fact]
    public async Task WriterFailureIsObservedAndLaterBatchesAreNeverStarted()
    {
        Write(8);
        var storage = new Storage { Behavior = (_, _) => throw new IOException("INSERT failed") };
        var settings = Settings(); settings.WritingMaxDop = 4;
        var error = await Assert.ThrowsAsync<IOException>(() => Run(settings, storage));
        Assert.Equal("INSERT failed", error.Message);
        Assert.Equal(1, storage.Calls);
        Assert.Empty(storage.Stored);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PartialOrLostResponseReplaysWholeUnacknowledgedBatch(int committed)
    {
        Write(5);
        var storage = new Storage();
        storage.Behavior = (items, _) =>
        {
            if (storage.Calls == 2)
            {
                storage.Stored.AddRange(items.Take(committed));
                throw new IOException("Response lost");
            }
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<IOException>(() => Run(Settings(true), storage));
        var acknowledged = File.ReadAllText(State);
        Assert.Contains("\"Id\":2", acknowledged);
        storage.Behavior = null;
        await Run(Settings(true), storage);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (long)i), storage.Stored.Select(i => i.Id).Distinct().Order());
        Assert.Equal(5 + committed, storage.Stored.Count); // Replays are explicit; no exactly-once claim.
        Assert.Equal(0, storage.Reads); // Archive startup and local recovery never use a possibly-ahead DB cursor.
        Assert.Contains("\"Id\":5", File.ReadAllText(State));
    }

    [Fact]
    public async Task EmptyInitialCheckpointSurvivesPartialFirstInsert()
    {
        Write(3);
        var storage = new Storage();
        storage.Behavior = (items, _) => { storage.Stored.Add(items[1]); throw new IOException("Only later row committed"); };
        await Assert.ThrowsAsync<IOException>(() => Run(Settings(true), storage));
        Assert.Contains("\"HasPosition\":false", File.ReadAllText(State));
        storage.Behavior = null;
        var settings = Settings(true); settings.LoadArchive = false;
        using var cancellation = new CancellationTokenSource();
        storage.AfterWrite = () => { if (storage.Stored.Any(i => i.Id == 3)) cancellation.Cancel(); };
        using var exporter = new EventLogExporter(settings, storage);
        await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] {1, 2, 3}, storage.Stored.Select(i => i.Id).Distinct().Order());
        Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public async Task StopWaitsForAnInFlightInsertAndPublishesItsCheckpointAfterAcknowledgement()
    {
        Write(2);
        var entered = Signal(); var release = Signal();
        var storage = new Storage {Behavior = async (_, token) => { entered.SetResult(true); await release.Task; Assert.False(token.IsCancellationRequested); }};
        using var cancellation = new CancellationTokenSource();
        using var exporter = new EventLogExporter(Settings(true), storage);
        var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(run.IsCompleted);
            Assert.Contains("\"HasPosition\":false", File.ReadAllText(State));
        }
        finally { release.TrySetResult(true); }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"Id\":2", File.ReadAllText(State));
    }

    [Fact]
    public async Task StopFlushesPartialBatchWhileReaderIsWaitingWithoutCancellingItsInsert()
    {
        Write(1);
        var read = new ReadLogger();
        var storage = new Storage {Behavior = (_, token) => {Assert.False(token.IsCancellationRequested); return Task.CompletedTask;}};
        var settings = Settings(true); settings.LoadArchive = false; settings.Portion = 100; settings.ReadingTimeout = -1;
        using var cancellation = new CancellationTokenSource();
        using var exporter = new EventLogExporter(settings, storage, read);
        var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
        await read.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(storage.Stored);
        Assert.Contains("\"Id\":1", File.ReadAllText(State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDeadlineFailsWithoutAcknowledgingCooperativeOrStuckStorage(bool cooperative)
    {
        Write(2);
        var entered = Signal(); var release = Signal();
        var storage = new Storage {Behavior = async (_, token) =>
        {
            entered.SetResult(true);
            if (cooperative) await Task.Delay(Timeout.Infinite, token); else await release.Task;
        }};
        using var cancellation = new CancellationTokenSource();
        using var exporter = new EventLogExporter(Settings(true), storage);
        var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
            await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("\"HasPosition\":false", File.ReadAllText(State));
        }
        finally { release.TrySetResult(true); }
    }

    [Fact]
    public async Task CheckpointLockRejectsConcurrentExporterBeforeReadingOrWriting()
    {
        Write(2);
        var entered = Signal(); var release = Signal();
        var first = new Storage {Behavior = async (_, _) => {entered.SetResult(true); await release.Task;}};
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var exporter = new EventLogExporter(Settings(true), first);
        var run = Task.Run(() => exporter.StartAsync(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = new Storage();
            await Assert.ThrowsAsync<IOException>(() => Run(Settings(true), second));
            Assert.Equal(0, second.Calls); Assert.Equal(0, second.Reads);
        }
        finally { release.TrySetResult(true); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task CorruptCheckpointFailsClosedAndDoesNotUseDatabasePosition()
    {
        Write(2); Directory.CreateDirectory(Path.GetDirectoryName(State)!); File.WriteAllText(State, "broken");
        var storage = new Storage();
        await Assert.ThrowsAsync<SerializationException>(() => Run(Settings(true), storage));
        Assert.Equal(0, storage.Calls); Assert.Equal(0, storage.Reads);
    }

    [Fact]
    public async Task CheckpointIsBoundToDestination()
    {
        Write(2); await Run(Settings(true), new Storage());
        var other = new Storage {CheckpointIdentity = "another-destination"};
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(Settings(true), other));
        Assert.Equal(0, other.Calls);
    }

    [Fact]
    public async Task MissingAcknowledgedFileFailsExplicitly()
    {
        Write(2); await Run(Settings(true), new Storage());
        File.Delete(Path.Combine(Folder, FileName));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Run(Settings(true), new Storage()));
    }

    [Fact]
    public async Task LostCheckpointDoesNotSilentlyBootstrapFromDatabaseAgain()
    {
        Write(2); var storage = new Storage(); await Run(Settings(true), storage);
        File.Delete(State);
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(Settings(true), storage));
        Assert.Equal(1, storage.Calls); Assert.Equal(0, storage.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedAcknowledgedJournalOrDictionaryFailsBeforeWriting(bool dictionary)
    {
        Write(2); await Run(Settings(true), new Storage());
        File.WriteAllText(Path.Combine(Folder, dictionary ? "1Cv8.lgf" : FileName), "");
        var storage = new Storage();
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(Settings(true), storage));
        Assert.Equal(0, storage.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationConstructorSupportsCheckpointAndArchiveCompletion(bool configured)
    {
        Write(3);
        var values = new Dictionary<string, string?> { ["Exporter:LogFolder"] = Folder, ["Exporter:Portion"] = "2", ["Exporter:LoadArchive"] = "true" };
        if (configured) {values["Exporter:CheckpointFile"] = State; values["Exporter:ShutdownTimeout"] = "1";}
        var storage = new Storage();
        using var exporter = new EventLogExporter(null, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), storage);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(3, storage.Stored.Count);
        Assert.Equal(configured, File.Exists(State));
    }

    [Fact]
    public async Task DatabaseCursorIsBootstrappedOnlyOnceThenLocalStateTakesPriority()
    {
        Write(3);
        var storage = new Storage();
        using (var reader = new EventLogReader(new EventLogReaderSettings { LogFolder = Folder, LiveMode = false }))
            storage.Stored.Add(reader.ReadNextEventLogItem());
        var settings = Settings(true); settings.LoadArchive = false;
        using var cancellation = new CancellationTokenSource();
        storage.AfterWrite = cancellation.Cancel;
        using (var exporter = new EventLogExporter(settings, storage))
            await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] {1, 2, 3}, storage.Stored.Select(i => i.Id));
        Assert.Equal(1, storage.Reads);
        await Run(Settings(true), storage);
        Assert.Equal(1, storage.Reads);
        Assert.Equal(3, storage.Stored.Count);
    }

    [Fact]
    public async Task CheckpointFailureAfterInsertStopsBeforeAnyLaterBatch()
    {
        Write(6);
        var storage = new Storage {AfterWrite = () => {File.Delete(State); Directory.CreateDirectory(State);}};
        await Assert.ThrowsAnyAsync<IOException>(() => Run(Settings(true), storage));
        Assert.Equal(1, storage.Calls);
        Assert.Equal(new long[] {1, 2}, storage.Stored.Select(i => i.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalDatabaseOffsetsOnePastEofRemainCompatible(bool checkpoint)
    {
        Write(1);
        var storage = new Storage();
        using (var reader = new EventLogReader(new EventLogReaderSettings {LogFolder = Folder, LiveMode = false}))
            storage.Stored.Add(reader.ReadNextEventLogItem());
        storage.Stored[0].EndPosition++;
        storage.Stored[0].LgfEndPosition++;
        File.Copy(Path.Combine(Folder, FileName), Path.Combine(Folder, "20261012000000.lgp"));
        var settings = Settings(checkpoint); settings.LoadArchive = false; settings.Portion = 1;
        using var cancellation = new CancellationTokenSource();
        storage.AfterWrite = cancellation.Cancel;
        using var exporter = new EventLogExporter(settings, storage);
        await Task.Run(() => exporter.StartAsync(cancellation.Token)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] {1, 2}, storage.Stored.Select(i => i.Id));
        Assert.Equal("20261012000000.lgp", storage.Stored[1].FileName);
    }

    [Fact]
    public async Task ValidPrefixIsFlushedBeforeALaterParseFailureIsReported()
    {
        Write(2);
        File.AppendAllText(Path.Combine(Folder, FileName), ",\n{20261011120000,N,{0,0},0}");
        var settings = Settings(true); settings.Portion = 100;
        var storage = new Storage();
        await Assert.ThrowsAnyAsync<Exception>(() => Run(settings, storage));
        Assert.Equal(new long[] {1, 2}, storage.Stored.Select(i => i.Id));
        Assert.Contains("\"Id\":2", File.ReadAllText(State));
    }

    private sealed class Storage : IEventLogStorage, IEventLogStorageIdentity
    {
        public string CheckpointIdentity { get; set; } = "test-destination";
        public List<EventLogItem> Stored { get; } = new();
        public int Calls; public int Reads;
        public Func<List<EventLogItem>, CancellationToken, Task>? Behavior;
        public Action? AfterWrite;
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            var last = Stored.OrderBy(i => i.FileName, StringComparer.Ordinal).ThenBy(i => i.EndPosition).LastOrDefault();
            return Task.FromResult(last == null ? null! : new EventLogPosition(last.FileName, last.EndPosition, last.LgfEndPosition, last.Id));
        }
        public async Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default)
        {
            Calls++;
            var copy = entities.ToList();
            if (Behavior != null) await Behavior(copy, cancellationToken);
            Stored.AddRange(copy); AfterWrite?.Invoke();
        }
        public void Dispose() { }
    }
    private sealed class ReadLogger : ILogger<EventLogExporter>
    {
        public TaskCompletionSource<bool> Reading { get; } = Signal();
        public IDisposable? BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (formatter(state, exception).StartsWith("Reader started reading", StringComparison.Ordinal)) Reading.TrySetResult(true); }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
