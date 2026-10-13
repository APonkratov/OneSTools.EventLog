using System.Text.Json;
using NodaTime;
using OneSTools.EventLog;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Core.ClickHouse;

// Test-only executable. It runs the actual exporter in a process that can be
// killed independently, without hooks or changed runtimes in production.
var mode = args[0];
var folder = args[1];
var state = args[2];
var signal = args[3];
var connection = Environment.GetEnvironmentVariable("ONESTOOLS_HARNESS_CONNECTION")!;
var settings = new EventLogExporterSettings
{
    LogFolder = folder, CheckpointFile = state, LoadArchive = true,
    Portion = 2, TimeZone = DateTimeZone.Utc
};
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var filler = Path.Combine(Path.GetDirectoryName(state)!, "test-volume-filler");
try
{
    using var exporter = new EventLogExporter(settings,
        new PausingStorage(new ClickHouseStorage(connection), mode, state, signal, filler));
    await exporter.StartAsync(timeout.Token);
}
catch (IOException) when (mode == "disk-full" && File.Exists(signal))
{
    // The failed publication must retain the previously acknowledged prefix.
    using var previous = JsonDocument.Parse(File.ReadAllText(state));
    if (previous.RootElement.GetProperty("Id").GetInt64() != 2)
        throw new InvalidOperationException("Disk full advanced the checkpoint.");
    File.Delete(filler);
    using var restarted = new EventLogExporter(settings, new ClickHouseStorage(connection));
    await restarted.StartAsync(timeout.Token);
    File.WriteAllText(signal + ".checkpoint", File.ReadAllText(state));
    Console.WriteLine("RECOVERED_FROM_ENOSPC checkpoint=2 replayed=3,4");
}

sealed class PausingStorage(ClickHouseStorage inner, string mode, string state, string signal, string filler)
    : IEventLogStorage, IEventLogStorageIdentity
{
    private int _calls;
    public string CheckpointIdentity => inner.CheckpointIdentity;
    public Task<EventLogPosition> ReadEventLogPositionAsync(CancellationToken token = default) => inner.ReadEventLogPositionAsync(token);
    public async Task WriteEventLogDataAsync(List<EventLogItem> items, CancellationToken token = default)
    {
        await inner.WriteEventLogDataAsync(items, token);
        if (++_calls != 2) return;
        if (mode == "after-ack")
        {
            File.WriteAllText(signal, File.ReadAllText(state));
            await Task.Delay(Timeout.Infinite, token);
        }
        else if (mode == "disk-full")
        {
            // Refuse to fill a host filesystem. Only an explicitly supplied tiny
            // tmpfs in a disposable Linux container is eligible for this test.
            var drive = new DriveInfo(Path.GetDirectoryName(state)!);
            var isTmpfs = OperatingSystem.IsLinux() && File.ReadLines("/proc/mounts").Any(line =>
            {
                var fields = line.Split(' ');
                return fields.Length > 2 && fields[1] == drive.Name && fields[2] == "tmpfs";
            });
            if (!OperatingSystem.IsLinux() || !File.Exists("/.dockerenv") ||
                !isTmpfs || drive.TotalSize <= 0 || drive.TotalSize > 1024 * 1024)
                throw new InvalidOperationException($"Disk-full injection requires a disposable tmpfs <= 1 MiB: name={drive.Name}, format={drive.DriveFormat}, size={drive.TotalSize}, container={File.Exists("/.dockerenv")}.");
            try
            {
                using var fill = new FileStream(filler, FileMode.CreateNew, FileAccess.Write);
                var bytes = new byte[4096];
                while (true) fill.Write(bytes);
            }
            catch (IOException error) when ((error.HResult & 0xffff) == 28)
            {
                File.WriteAllText(signal, "ENOSPC");
            }
        }
    }
    public void Dispose() => inner.Dispose();
}
