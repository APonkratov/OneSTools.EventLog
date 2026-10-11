using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Manager;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class ExporterServiceLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-lifetime", Guid.NewGuid().ToString("N"));
    private string Base => Path.Combine(_root, "ib");
    private string Log => Path.Combine(Base, "1Cv8Log");
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ExporterServiceLifetimeTests()
    {
        Directory.CreateDirectory(Log);
        File.WriteAllText(Path.Combine(_root, "1CV8Clst.lst"), "{0,0,{0}}");
        File.WriteAllText(Path.Combine(Log, "1Cv8.lgf"), "{4,\"Test\",1}");
        File.WriteAllText(Path.Combine(Log, "20261011000000.lgp"), "{20261011120000,N,{0,0},0,0,0,1,1,I,\"\",0,{\"U\"},\"\",0,0,0,1,0,{0}}");
    }
    private IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Manager:ClstFolders:0:Folder"] = _root, ["Exporter:StorageType"] = "1", ["ClickHouse:ConnectionString"] = "Host=unused;",
        ["Exporter:LoadArchive"] = "true", ["Exporter:Portion"] = "1", ["Exporter:ShutdownTimeout"] = "2"
    }).Build();

    [Fact]
    public async Task SingleArchiveServiceStopsHostAfterConfirmedEof()
    {
        var storage = new BlockingStorage(); storage.Release.TrySetResult(true);
        using var provider = new ServiceCollection().AddTransient(_ => new EventLogExporter(
            new EventLogExporterSettings {LogFolder = Log, LoadArchive = true, Portion = 1}, storage)).BuildServiceProvider();
        var lifetime = new Lifetime();
        using var service = new EventLogExporterService(provider, NullLogger<EventLogExporterService>.Instance, Configuration(), lifetime);
        await service.StartAsync(default);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Equal(1, storage.Calls);
        Assert.True(storage.Disposed);
    }

    [Fact]
    public async Task SingleServiceWaitsForActiveWriteBeforeDisposingStorage()
    {
        var storage = new BlockingStorage();
        using var provider = new ServiceCollection().AddTransient(_ => new EventLogExporter(
            new EventLogExporterSettings {LogFolder = Log, LoadArchive = true, Portion = 1, ShutdownTimeout = 2}, storage)).BuildServiceProvider();
        using var service = new EventLogExporterService(provider, NullLogger<EventLogExporterService>.Instance, Configuration(), new Lifetime());
        await service.StartAsync(default);
        await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = service.StopAsync(default);
        try { Assert.False(stop.IsCompleted); Assert.False(storage.Disposed); }
        finally { storage.Release.TrySetResult(true); }
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(storage.Disposed);
    }

    [Fact]
    public async Task ManagerWaitsForWorkersAndReaddedSourceWaitsForRetiredWrite()
    {
        var first = new BlockingStorage(); var second = new BlockingStorage();
        var calls = 0;
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var manager = new TestManager(provider, Configuration(), () => Interlocked.Increment(ref calls) == 1 ? first : second);
        await manager.StartAsync(default);
        var start = typeof(ExportersManager).GetMethod("StartExporter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var remove = typeof(ExportersManager).GetMethod("StopExporter", BindingFlags.Instance | BindingFlags.NonPublic)!;
        start.Invoke(manager, new object[] {Base, "fixture", "fixture"});
        try
        {
            await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            remove.Invoke(manager, new object[] {Base, "fixture"});
            start.Invoke(manager, new object[] {Base, "fixture", "fixture"});
            Assert.False(second.Entered.Task.IsCompleted);
            Assert.Equal(1, calls);
            first.Release.TrySetResult(true);
            await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = manager.StopAsync(default);
            Assert.False(stop.IsCompleted); Assert.False(second.Disposed);
            second.Release.TrySetResult(true);
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(first.Disposed); Assert.True(second.Disposed);
        }
        finally
        {
            first.Release.TrySetResult(true); second.Release.TrySetResult(true);
            await manager.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ManagerPassesNoncorpOptionsToEachWorker()
    {
        File.WriteAllText(Path.Combine(Log, "1Cv8.lgf"), "{4,\"_$Data$_.NewPredefinedData\",1}");
        var configuration = Configuration();
        foreach (var obsolete in new[] { "UseCorrectedValueParsing", "UseStructuredDataParsing", "UseCurrentEventNames",
            "UseLatestDictionaryValues", "UseFinalTransactionStatuses" })
            configuration["Exporter:" + obsolete] = "false";
        configuration["Exporter:UseEventContext"] = "true";
        var storage = new BlockingStorage(); storage.Release.TrySetResult(true);
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var manager = new TestManager(provider, configuration, () => storage);
        await manager.StartAsync(default);
        typeof(ExportersManager).GetMethod("StartExporter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, new object[] { Base, "fixture", "fixture" });
        try
        {
            await storage.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain("_$", storage.Item!.Event);
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(storage.Item.Data)!;
            Assert.Equal("1c-event-context-v1", envelope["$format"]!.GetValue<string>());
        }
        finally { await manager.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task ManagerPassesDiagnosticStrictModeToItsWorker()
    {
        var configuration = Configuration(); configuration["Exporter:UseStrictParsing"] = "true";
        File.AppendAllText(Path.Combine(Log, "20261011000000.lgp"), ",{bad}");
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var problems = new List<Dictionary<string, object?>>();
        var logger = new CaptureLogger<EventLogExporter>((level, state, error) =>
        {
            if (level == LogLevel.Error)
                problems.Add(((IEnumerable<KeyValuePair<string, object?>>)state).ToDictionary(p => p.Key, p => p.Value));
        });
        var managerLogger = new CaptureLogger<ExportersManager>((level, state, error) =>
        { if (level == LogLevel.Critical) failure.TrySetResult(error!); });
        var storage = new BlockingStorage(); storage.Release.TrySetResult(true);
        using var provider = new ServiceCollection().AddSingleton<ILogger<EventLogExporter>>(logger).BuildServiceProvider();
        using var manager = new TestManager(provider, configuration, () => storage, managerLogger);
        await manager.StartAsync(default);
        typeof(ExportersManager).GetMethod("StartExporter", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, new object[] { Base, "fixture", "fixture" });
        try
        {
            Assert.IsType<EventLogParseException>(await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, storage.Calls);
            Assert.Equal(false, Assert.Single(problems)["Skipped"]);
        }
        finally { await manager.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class CaptureLogger<T>(Action<LogLevel, object, Exception?> capture) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) =>
            capture(level, state!, error);
    }

    private sealed class TestManager(IServiceProvider services, IConfiguration configuration, Func<IEventLogStorage> storage,
        ILogger<ExportersManager>? logger = null)
        : ExportersManager(logger ?? NullLogger<ExportersManager>.Instance, services, configuration)
    { protected override IEventLogStorage GetStorage(string dataBaseName) => storage(); }
    private sealed class BlockingStorage : IEventLogStorage
    {
        public TaskCompletionSource<bool> Entered { get; } = Signal();
        public TaskCompletionSource<bool> Release { get; } = Signal();
        public int Calls; public bool Disposed; public EventLogItem? Item;
        public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken cancellationToken = default) => Task.FromResult<EventLogPosition>(null!);
        public async Task WriteEventLogDataAsync(List<EventLogItem> entities, CancellationToken cancellationToken = default)
        { Calls++; Item = entities.FirstOrDefault(); Entered.TrySetResult(true); await Release.Task; Assert.False(Disposed); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource<bool> Stopped { get; } = Signal();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => Stopped.TrySetResult(true);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
