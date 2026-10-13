using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Core.ClickHouse;
using OneSTools.EventLog.Exporter.Manager;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class ClickHouseRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-recovery", Guid.NewGuid().ToString("N"));
    private string Base => Path.Combine(_root, "ib");
    private string Folder => Path.Combine(Base, "1Cv8Log");
    private string State => Path.Combine(_root, "state", "delivery.json");
    private const string FileName = "20261011000000.lgp";
    public ClickHouseRecoveryTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(_root, "1CV8Clst.lst"), "{0,0,{0}}");
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"Test\",1}");
        File.WriteAllText(Path.Combine(Folder, FileName), string.Join(",\n", Enumerable.Range(1, 6).Select(i =>
            $"{{20261011120000,N,{{0,0}},0,0,0,1,1,I,\"{i}\",0,{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}")));
    }

    [ClickHouseFact]
    public async Task SameStorageRetriesInitializationAfterCreateTableFails()
    {
        await using var server = new ClickHouseServer();
        await server.Query($"CREATE DATABASE {server.Database}");
        await using var proxy = new ClickHouseFaultProxy(ClickHouseFault.RejectTableInitialization);
        await proxy.Start();
        using var storage = server.Storage(proxy.Address);
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => storage.ReadEventLogPositionAsync());
        Assert.True(proxy.FaultReached.Task.IsCompleted, failure.ToString());
        await proxy.FaultReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await storage.ReadEventLogPositionAsync());
        Assert.Contains("MergeTree", await server.Schema());
    }

    [ClickHouseFact]
    public async Task MissingDatabaseIsCreatedWithMixedCaseOrQuotedDatabaseSetting()
    {
        foreach (var key in new[] { "Database", "database", "dAtAbAsE" })
        {
            await using var server = new ClickHouseServer();
            var connection = Regex.Replace(server.ConnectionString(), "database=[^;]+",
                key + "=\"" + server.Database + "\"", RegexOptions.IgnoreCase);
            using var storage = new ClickHouseStorage(connection);
            Assert.Null(await storage.ReadEventLogPositionAsync());
            Assert.Contains("MergeTree", await server.Schema());
        }
    }

    [ClickHouseFact]
    public async Task MissingDatabaseIsCreatedWithMixedCaseUnquotedDatabaseSetting()
    {
        await using var server = new ClickHouseServer();
        var connection = Regex.Replace(server.ConnectionString(), "database=[^;]+",
            "dAtAbAsE=" + server.Database, RegexOptions.IgnoreCase);
        using var storage = new ClickHouseStorage(connection);
        Assert.Null(await storage.ReadEventLogPositionAsync());
        Assert.Contains("MergeTree", await server.Schema());
    }

    [ClickHouseFact]
    public Task SingleServiceRetriesRejectedInsertFromAcknowledgedCheckpoint() =>
        RunRecovery(manager: false, dropResponse: false);

    [ClickHouseFact]
    public Task SingleServiceReplaysUnknownInsertOutcomeWithoutLosingEvents() =>
        RunRecovery(manager: false, dropResponse: true);

    [ClickHouseFact]
    public Task ManagerRetriesRejectedInsertFromAcknowledgedCheckpoint() =>
        RunRecovery(manager: true, dropResponse: false);

    [ClickHouseFact]
    public Task ManagerReplaysUnknownInsertOutcomeWithoutLosingEvents() =>
        RunRecovery(manager: true, dropResponse: true);

    private async Task RunRecovery(bool manager, bool dropResponse)
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable(); var schema = await server.Schema();
        await using var proxy = new ClickHouseFaultProxy(dropResponse
            ? ClickHouseFault.DropSecondInsertResponse : ClickHouseFault.RejectSecondInsert);
        await proxy.Start();
        string? stateAtFailure = null;
        proxy.BeforeFault = () => stateAtFailure = File.ReadAllText(manager
            ? Directory.GetFiles(Path.Combine(_root, "state"), "*.json").Single() : State);
        var attempts = 0;
        var logPath = Path.Combine(_root, "log.txt");
        using var logs = LoggerFactory.Create(builder => builder.AddFile(logPath));
        var settings = new EventLogExporterSettings
        {
            LogFolder = Folder, CheckpointFile = State, LoadArchive = true, Portion = 2,
            TimeZone = DateTimeZone.Utc, ShutdownTimeout = 2
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Manager:ClstFolders:0:Folder"] = _root, ["Exporter:StorageType"] = "1",
            ["ClickHouse:ConnectionString"] = "Host=unused;", ["Exporter:LoadArchive"] = "true",
            ["Exporter:Portion"] = "2", ["Exporter:CheckpointDirectory"] = Path.Combine(_root, "state"),
            ["Exporter:TimeZone"] = "UTC", ["Exporter:ShutdownTimeout"] = "2"
        }).Build();
        using var services = new ServiceCollection().AddScoped(_ =>
        {
            Interlocked.Increment(ref attempts);
            return new EventLogExporter(settings, server.Storage(proxy.Address));
        }).BuildServiceProvider();

        async Task RunService()
        {
            if (manager)
            {
                using var worker = new RecoveryManager(services, configuration, logs.CreateLogger<ExportersManager>(), () =>
                {
                    Interlocked.Increment(ref attempts); return server.Storage(proxy.Address);
                });
                await worker.StartAsync(default);
                try
                {
                    typeof(ExportersManager).GetMethod("StartExporter", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(worker, new object[] { Base, "fixture", "fixture" });
                    var runs = (IDictionary)typeof(ExportersManager).GetField("_runExporters", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(worker)!;
                    var run = runs[Base]!;
                    var task = (Task)run.GetType().GetField("Task")!.GetValue(run)!;
                    await task.WaitAsync(TimeSpan.FromSeconds(25));
                }
                finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            else
            {
                var lifetime = new Lifetime();
                using var worker = new EventLogExporterService(services, logs.CreateLogger<EventLogExporterService>(), configuration, lifetime);
                await worker.StartAsync(default);
                try { await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
                finally { await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }

        await RunService();
        await proxy.FaultReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, attempts); Assert.NotNull(stateAtFailure);
        var log = File.ReadAllText(logPath);
        Assert.Contains("Retry delay: 5000 ms", log);
        if (!dropResponse) Assert.Contains("Injected temporary ClickHouse failure", log);
        using (var cursor = JsonDocument.Parse(stateAtFailure!))
            Assert.Equal(2, cursor.RootElement.GetProperty("Id").GetInt64());
        var expected = string.Join("\n", Enumerable.Range(1, 6).Select(id =>
            $"{id}\t{(dropResponse && id is 3 or 4 ? 2 : 1)}")) + "\n";
        Assert.Equal(expected, await server.Query(
            $"SELECT Id, count() FROM {server.Database}.EventLogItems GROUP BY Id ORDER BY Id FORMAT TSV"));
        Assert.Equal($"{(dropResponse ? 8 : 6)}\t6\t6\n", await server.Query(
            $"SELECT count(), uniqExact((FileName, EndPosition)), uniqExact(Comment) FROM {server.Database}.EventLogItems FORMAT TSV"));
        var state = manager ? Directory.GetFiles(Path.Combine(_root, "state"), "*.json").Single() : State;
        using (var cursor = JsonDocument.Parse(File.ReadAllText(state)))
        {
            Assert.Equal(6, cursor.RootElement.GetProperty("Id").GetInt64());
            Assert.Equal(FileName, cursor.RootElement.GetProperty("FileName").GetString());
            Assert.Equal(new FileInfo(Path.Combine(Folder, FileName)).Length, cursor.RootElement.GetProperty("EndPosition").GetInt64());
        }
        var insertCount = proxy.Inserts;
        await RunService();
        Assert.Equal(insertCount, proxy.Inserts); Assert.Equal(3, attempts);
        Assert.Equal(schema, await server.Schema());
    }

    private sealed class RecoveryManager(IServiceProvider services, IConfiguration configuration,
        ILogger<ExportersManager> logger, Func<IEventLogStorage> storage)
        : ExportersManager(logger, services, configuration)
    { protected override IEventLogStorage GetStorage(string dataBaseName) => storage(); }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource<bool> Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => default;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => Stopped.TrySetResult(true);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
