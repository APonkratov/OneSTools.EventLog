using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class BatchMaxAgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-batch-age", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "log");
    private string Lgp => Path.Combine(Folder, "20261013000000.lgp");
    private string State => Path.Combine(_root, "state.json");
    internal static string Row(int id) =>
        $"{{20261013120000,N,{{0,0}},0,0,0,1,1,I,\"{id}\",0,{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}";

    public BatchMaxAgeTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"Test\",1}");
    }

    private EventLogExporterSettings Settings() => new()
    {
        LogFolder = Folder, Portion = 10000, BatchMaxAge = 1, ReadingTimeout = -1,
        CheckpointFile = State, TimeZone = DateTimeZone.Utc, ShutdownTimeout = 2
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SparseBatchFlushesEvenWhenReaderWaitsForever(bool configured, bool useDefault)
    {
        File.WriteAllText(Lgp, Row(1));
        var settings = Settings(); if (useDefault) settings.BatchMaxAge = new EventLogExporterSettings().BatchMaxAge;
        var logger = new ReadLogger(); var storage = new Storage();
        var values = new Dictionary<string, string?>
        {
            ["Exporter:LogFolder"] = Folder, ["Exporter:Portion"] = "10000",
            ["Exporter:ReadingTimeout"] = "-1", ["Exporter:CheckpointFile"] = State,
            ["Exporter:TimeZone"] = "UTC"
        };
        if (!useDefault) values["Exporter:BatchMaxAge"] = "1";
        using var exporter = configured
            ? new EventLogExporter(logger, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), storage)
            : new EventLogExporter(settings, storage, logger);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = Task.Run(() => exporter.StartAsync(stop.Token));
        try
        {
            var started = await logger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var batch = await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(useDefault ? 8 : 4));
            var age = Stopwatch.GetElapsedTime(started);
            Assert.True(age >= TimeSpan.FromSeconds(useDefault ? 4 : .8), $"Unexpected age: {age}");
            Assert.Equal(1, Assert.Single(batch).Id);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        Assert.Equal(1, storage.Calls); Assert.Equal(1, CheckpointId());
    }

    [Fact]
    public async Task ContinuousReadingDoesNotPostponeTheFirstBatchUntilEof()
    {
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"_$Future$_.Event\",1}");
        File.WriteAllText(Lgp, string.Join(",", Enumerable.Range(1, 200).Select(Row)));
        var settings = Settings(); settings.LoadArchive = true;
        // The real reader remains busy for ~12 s and never reaches its idle timeout.
        settings.DiagnosticHandler = d => { if (d.Code == "UnknownEvent") Thread.Sleep(60); };
        var storage = new Storage(); var logger = new ReadLogger();
        using var exporter = new EventLogExporter(settings, storage, logger);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => exporter.StartAsync(stop.Token));
        try
        {
            await logger.FirstRead.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var batch = await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.InRange(batch.Length, 1, 100);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        var ids = storage.Stored.Select(i => i.Id).ToArray();
        Assert.Equal(Enumerable.Range(1, ids.Length).Select(i => (long)i), ids);
        Assert.Equal(ids[^1], CheckpointId()); Assert.Equal(1, storage.MaxActive);
    }

    [Fact]
    public async Task NextBatchStartsItsOwnDeadlineAndEmptyIdleTimeMakesNoInserts()
    {
        File.WriteAllText(Lgp, Row(1));
        var storage = new Storage();
        using var exporter = new EventLogExporter(Settings(), storage);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => exporter.StartAsync(stop.Token));
        try
        {
            Assert.Equal(1, Assert.Single(await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4))).Id);
            await Task.Delay(1200);
            Assert.Equal(1, storage.Calls);
            var age = Stopwatch.StartNew(); File.AppendAllText(Lgp, "," + Row(2));
            Assert.Equal(2, Assert.Single(await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4))).Id);
            Assert.True(age.Elapsed >= TimeSpan.FromMilliseconds(800));
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        Assert.Equal(2, storage.Calls); Assert.Equal(2, CheckpointId());
    }

    [Fact]
    public async Task FullBatchDoesNotWaitForTheAgeLimit()
    {
        File.WriteAllText(Lgp, Row(1) + "," + Row(2));
        var settings = Settings(); settings.BatchMaxAge = 30; settings.Portion = 2;
        var storage = new Storage();
        using var exporter = new EventLogExporter(settings, storage);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var run = Task.Run(() => exporter.StartAsync(stop.Token));
        try { Assert.Equal(2, (await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Length); }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        Assert.Equal(1, storage.Calls);
    }

    [Fact]
    public async Task FailedTimedInsertKeepsThePreviousCheckpointAndRestartReplaysOnlyItsSuffix()
    {
        File.WriteAllText(Lgp, Row(1));
        var settings = Settings(); var storage = new Storage();
        storage.Behavior = (_, _) => storage.Calls == 2 ? throw new IOException("INSERT failed") : Task.CompletedTask;
        using (var exporter = new EventLogExporter(settings, storage))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var run = Task.Run(() => exporter.StartAsync(stop.Token));
            try
            {
                await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                await WaitForCheckpoint(1);
                File.AppendAllText(Lgp, "," + Row(2));
                await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(4)));
            }
            finally { stop.Cancel(); try { await run.WaitAsync(TimeSpan.FromSeconds(4)); } catch (IOException) { } }
        }
        Assert.Equal(1, CheckpointId()); Assert.Equal(1, Assert.Single(storage.Stored).Id);
        storage.Behavior = null; settings.LoadArchive = true;
        using (var restarted = new EventLogExporter(settings, storage))
            await restarted.StartAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(new long[] { 1, 2 }, storage.Stored.Select(i => i.Id));
        Assert.Equal(2, CheckpointId());
    }

    [Fact]
    public async Task TimedInsertHasNoReadDeadlineTokenAndNeverOverlapsAnotherInsert()
    {
        File.WriteAllText(Lgp, Row(1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new Storage();
        storage.Behavior = async (_, token) =>
        {
            if (storage.Calls == 1)
            {
                entered.TrySetResult(); await release.Task;
                Assert.False(token.IsCancellationRequested);
            }
        };
        using var exporter = new EventLogExporter(Settings(), storage);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => exporter.StartAsync(stop.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            File.AppendAllText(Lgp, "," + Row(2));
            await Task.Delay(1200);
            Assert.Equal(1, storage.Calls); Assert.Equal(0, CheckpointId());
            release.TrySetResult();
            await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(2, Assert.Single(await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4))).Id);
        }
        finally { release.TrySetResult(); stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        Assert.Equal(1, storage.MaxActive); Assert.Equal(2, CheckpointId());
    }

    [Fact]
    public async Task DeadlineDuringTransactionScanPreservesThePendingEventAndItsId()
    {
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"Test\",1},{4,\"_$Transaction$_.Commit\",2}");
        var pending = Row(2).Replace(",N,{0,0}", ",U,{0,1}");
        var commit = Row(3).Replace(",N,{0,0}", ",U,{0,1}").Replace(",1,I,", ",2,I,");
        File.WriteAllText(Lgp, Row(1) + "," + pending + ",{bad}," + commit);
        var settings = Settings(); settings.LoadArchive = true;
        settings.ProblemRecordHandler = problem =>
        {
            if (problem.Stage == "transaction index") Thread.Sleep(1100);
        };
        var storage = new Storage();
        using var exporter = new EventLogExporter(settings, storage);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.Run(() => exporter.StartAsync(stop.Token)).WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(1, Assert.Single(await storage.Writes.Reader.ReadAsync()).Id);
        Assert.Equal(new long[] { 1, 2, 3 }, storage.Stored.Select(i => i.Id));
        Assert.Equal(new[] { "1", "2", "3" }, storage.Stored.Select(i => i.Comment));
        Assert.Equal("Зафиксирована", storage.Stored.Single(i => i.Id == 2).TransactionStatus);
        Assert.Equal(3, CheckpointId());
    }

    [ClickHouseFact]
    public async Task TimedBatchesReachRealClickHouseAndRestartDoesNotReplayConfirmedEvents()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable(); var schema = await server.Schema();
        File.WriteAllText(Lgp, Row(1));
        var settings = Settings();
        using var inner = server.Storage();
        var storage = new Storage { Behavior = inner.WriteEventLogDataAsync };
        using (var exporter = new EventLogExporter(settings, storage))
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var run = Task.Run(() => exporter.StartAsync(stop.Token));
            try
            {
                await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                await WaitForCheckpoint(1); File.AppendAllText(Lgp, "," + Row(2));
                await storage.Writes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            }
            finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(4)); }
        }
        Assert.Equal("2\n", await server.Query($"SELECT count() FROM {server.Database}.EventLogItems FORMAT TSV"));
        var cursor = await inner.ReadEventLogPositionAsync();
        Assert.Equal(2, cursor.Id); Assert.Equal(new FileInfo(Lgp).Length, cursor.EndPosition);
        Assert.Equal(2, CheckpointId());
        settings.LoadArchive = true;
        var restartedStorage = new Storage { Behavior = inner.WriteEventLogDataAsync };
        using (var restarted = new EventLogExporter(settings, restartedStorage))
            await restarted.StartAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, restartedStorage.Calls); Assert.Equal(schema, await server.Schema());
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 3601)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 3601)]
    public void InvalidAgeFailsAtStartup(bool configured, int age)
    {
        var settings = Settings(); settings.BatchMaxAge = age;
        var values = new Dictionary<string, string?> { ["Exporter:LogFolder"] = Folder, ["Exporter:BatchMaxAge"] = age.ToString() };
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => configured
            ? new EventLogExporter(null, new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new Storage())
            : new EventLogExporter(settings, new Storage()));
        Assert.Equal("BatchMaxAge", error.ParamName);
    }

    private long CheckpointId()
    {
        using var state = JsonDocument.Parse(File.ReadAllText(State));
        return state.RootElement.TryGetProperty("Id", out var id) ? id.GetInt64() : 0;
    }

    private async Task WaitForCheckpoint(long id)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (CheckpointId() != id) await Task.Delay(10, limit.Token);
    }

    private sealed class ReadLogger : ILogger<EventLogExporter>
    {
        public TaskCompletionSource<long> FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        { if (formatter(state, error).StartsWith("Reader started reading", StringComparison.Ordinal)) FirstRead.TrySetResult(Stopwatch.GetTimestamp()); }
    }

    private sealed class Storage : IEventLogStorage
    {
        public readonly System.Threading.Channels.Channel<EventLogItem[]> Writes = System.Threading.Channels.Channel.CreateUnbounded<EventLogItem[]>();
        public readonly ConcurrentQueue<EventLogItem> Stored = new();
        public Func<List<EventLogItem>, CancellationToken, Task>? Behavior;
        public int Calls, MaxActive;
        private int _active;
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken token = default) => Task.FromResult<EventLogPosition>(null!);
        public async Task WriteEventLogDataAsync(List<EventLogItem> items, CancellationToken token = default)
        {
            var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active);
            Interlocked.Increment(ref Calls);
            try
            {
                Assert.NotEmpty(items); Assert.False(token.IsCancellationRequested);
                if (Behavior != null) await Behavior(items, token);
                foreach (var item in items) Stored.Enqueue(item);
                Writes.Writer.TryWrite(items.ToArray());
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
