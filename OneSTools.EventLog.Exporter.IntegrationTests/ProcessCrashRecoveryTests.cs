using System.Diagnostics;
using System.Text.Json;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

public sealed class DockerClickHouseFactAttribute : FactAttribute
{
    public DockerClickHouseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONESTOOLS_TEST_RUNTIME_IMAGE")))
            Skip = "Set the dedicated ClickHouse URL and ONESTOOLS_TEST_RUNTIME_IMAGE for disposable Docker tests.";
    }
}

public sealed class ProcessCrashRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "onestools-crash", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "log");
    private string State => Path.Combine(_root, "state", "delivery.json");
    private string Signal => Path.Combine(_root, "signal");
    private static string Harness => Path.Combine(AppContext.BaseDirectory, "ProcessHarness");
    private const string Assembly = "OneSTools.EventLog.Exporter.ProcessHarness.dll";
    public ProcessCrashRecoveryTests()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "1Cv8.lgf"), "{4,\"Test\",1}");
        File.WriteAllText(Path.Combine(Folder, "20261011000000.lgp"), string.Join(",\n", Enumerable.Range(1, 6).Select(id =>
            $"{{20261011120000,N,{{0,0}},0,0,0,1,1,I,\"{id}\",0,{{\"U\"}},\"\",0,0,0,1,0,{{0}}}}")));
    }

    [ClickHouseFact]
    public Task KillDuringPendingInsertResumesFromAcknowledgedPrefix() => KillAndResume(afterAck: false);

    [ClickHouseFact]
    public Task KillAfterDriverAcknowledgementBeforeCheckpointReplaysOnlyThatPacket() => KillAndResume(afterAck: true);

    private async Task KillAndResume(bool afterAck)
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable(); var schema = await server.Schema();
        await using var proxy = new ClickHouseFaultProxy(ClickHouseFault.PauseSecondInsert);
        await proxy.Start();
        var connection = server.ConnectionString(afterAck ? null : proxy.Address);
        using var killed = StartHarness(afterAck ? "after-ack" : "normal", connection);
        var output = killed.StandardOutput.ReadToEndAsync(); var errors = killed.StandardError.ReadToEndAsync();
        try
        {
            if (afterAck)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (!File.Exists(Signal))
                {
                    Assert.False(killed.HasExited, await ErrorsIfExited(killed, errors));
                    await Task.Delay(20, timeout.Token);
                }
            }
            else await proxy.FaultReached.Task.WaitAsync(TimeSpan.FromSeconds(25));
            AssertCursor(State, 2);
            Assert.Equal(afterAck ? "4\n" : "2\n", await server.Query($"SELECT count() FROM {server.Database}.EventLogItems FORMAT TSV"));
            killed.Kill(entireProcessTree: true);
            await killed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!killed.HasExited) { killed.Kill(true); await killed.WaitForExitAsync(); }
            proxy.Release.TrySetResult(true);
        }
        await output; await errors;
        AssertCursor(State, 2);
        // A completely new OS process must acquire the abandoned lock and use
        // the persistent checkpoint, not a later row already in ClickHouse.
        await Finish(StartHarness("normal", connection));
        AssertCursor(State, 6);
        await AssertRows(server, afterAck);
        var rows = await server.Query($"SELECT count() FROM {server.Database}.EventLogItems FORMAT TSV");
        await Finish(StartHarness("normal", connection));
        Assert.Equal(rows, await server.Query($"SELECT count() FROM {server.Database}.EventLogItems FORMAT TSV"));
        Assert.Equal(schema, await server.Schema());
    }

    [DockerClickHouseFact]
    public async Task RealEnospcAfterInsertKeepsOldCheckpointAndRecoversWhenSpaceIsFreed()
    {
        await using var server = new ClickHouseServer();
        await server.CreateLegacyTable(); var schema = await server.Schema();
        var name = "onestools-enospc-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        var connection = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = server.ConnectionString() };
        connection["Host"] = "host.docker.internal";
        start.Environment["ONESTOOLS_HARNESS_CONNECTION"] = connection.ConnectionString;
        foreach (var arg in new[] { "run", "--rm", "--name", name, "--add-host", "host.docker.internal:host-gateway",
            "--mount", "type=tmpfs,destination=/state,tmpfs-size=65536",
            "--mount", "type=bind,source=" + Harness + ",target=/app,readonly",
            "--mount", "type=bind,source=" + Folder + ",target=/fixtures,readonly",
            "--mount", "type=bind,source=" + _root + ",target=/results",
            "--env", "ONESTOOLS_HARNESS_CONNECTION", Environment.GetEnvironmentVariable("ONESTOOLS_TEST_RUNTIME_IMAGE")!,
            "dotnet", "/app/" + Assembly, "disk-full", "/fixtures", "/state/delivery.json", "/results/signal" })
            start.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Contains("RECOVERED_FROM_ENOSPC", await stdout);
            Assert.Equal("ENOSPC", File.ReadAllText(Signal));
            AssertCursor(Signal + ".checkpoint", 6);
            await AssertRows(server, duplicates: true);
            Assert.Equal(schema, await server.Schema());
        }
        finally
        {
            var cleanup = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "rm", "--force", "--volumes", name }) cleanup.ArgumentList.Add(arg);
            using var process = Process.Start(cleanup)!;
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            await process.WaitForExitAsync();
        }
    }

    private Process StartHarness(string mode, string connection)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(Harness, Assembly), mode, Folder, State, Signal }) start.ArgumentList.Add(arg);
        start.Environment["ONESTOOLS_HARNESS_CONNECTION"] = connection;
        return Process.Start(start)!;
    }
    private static async Task<string> ErrorsIfExited(Process process, Task<string> errors) => process.HasExited ? await errors : "";
    private static async Task Finish(Process process)
    {
        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Assert.True(process.ExitCode == 0, await errors); await output;
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
    }
    private static void AssertCursor(string path, long id)
    {
        using var cursor = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(id, cursor.RootElement.GetProperty("Id").GetInt64());
    }
    private static async Task AssertRows(ClickHouseServer server, bool duplicates)
    {
        var expected = string.Join("\n", Enumerable.Range(1, 6).Select(id => $"{id}\t{(duplicates && id is 3 or 4 ? 2 : 1)}")) + "\n";
        Assert.Equal(expected, await server.Query($"SELECT Id, count() FROM {server.Database}.EventLogItems GROUP BY Id ORDER BY Id FORMAT TSV"));
        Assert.Equal("6\t6\n", await server.Query($"SELECT uniqExact((FileName, EndPosition)), uniqExact(Comment) FROM {server.Database}.EventLogItems FORMAT TSV"));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
