using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using NodaTime;
using OneSTools.EventLog;

// Identical executable source is built against each reader. Reflection configures
// optional settings once, never reads a record in the measured loop.
if (args.Length < 2) throw new ArgumentException("Usage: ReaderBenchmark <corpus> <default|current|context> [warmups=1] [runs=1]");
var root = Path.GetFullPath(args[0]);
var mode = args[1];
if (mode is not ("default" or "current" or "context")) throw new ArgumentException("Unknown mode");
var warmups = args.Length > 2 ? int.Parse(args[2]) : 1;
var runs = args.Length > 3 ? int.Parse(args[3]) : 1;
if (warmups < 0 || runs < 1) throw new ArgumentException("Invalid run count");
var folders = File.Exists(Path.Combine(root, "1Cv8.lgf")) ? new[] { root } :
    Directory.GetDirectories(root).Where(f => File.Exists(Path.Combine(f, "1Cv8.lgf"))).Order(StringComparer.Ordinal).ToArray();
if (folders.Length == 0) throw new ArgumentException("No journal folders");
var settings = folders.Select(f => new EventLogReaderSettings
{
    LogFolder = f, LiveMode = false, TimeZone = DateTimeZoneProviders.Tzdb["Europe/Moscow"]
}).ToArray();
// default/current use each version's actual production behavior. Historical
// switch combinations belong to frozen historical builds, not this reader API.
if (mode == "context")
    foreach (var setting in settings)
        (typeof(EventLogReaderSettings).GetProperty("UseEventContext") ??
            throw new InvalidOperationException("This reader does not support context")).SetValue(setting, true);
var inputBytes = folders.Sum(f => Directory.GetFiles(f).Where(p => Path.GetExtension(p).Equals(".lgf", StringComparison.OrdinalIgnoreCase) ||
    Path.GetExtension(p).Equals(".lgp", StringComparison.OrdinalIgnoreCase)).Sum(p => new FileInfo(p).Length));
var assembly = typeof(EventLogReader).Assembly;
var process = Process.GetCurrentProcess();
for (var i = 0; i < warmups; i++) Consume();
for (var run = 0; run < runs; run++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    process.Refresh();
    var cpuStart = process.TotalProcessorTime;
    var allocatedStart = GC.GetTotalAllocatedBytes(true);
    var g0 = GC.CollectionCount(0); var g1 = GC.CollectionCount(1); var g2 = GC.CollectionCount(2);
    var timer = Stopwatch.StartNew();
    var result = Consume();
    timer.Stop();
    var allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
    process.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        mode, run, records = result.Count, result.Checksum, result.DataCharacters,
        seconds = timer.Elapsed.TotalSeconds, cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
        eventsPerSecond = result.Count / timer.Elapsed.TotalSeconds,
        inputMiBPerSecond = inputBytes / 1048576d / timer.Elapsed.TotalSeconds,
        allocatedBytes = allocated, allocatedBytesPerEvent = (double)allocated / result.Count,
        gen0 = GC.CollectionCount(0) - g0, gen1 = GC.CollectionCount(1) - g1, gen2 = GC.CollectionCount(2) - g2,
        peakWorkingSetBytes = process.PeakWorkingSet64, inputBytes, bases = folders.Length,
        runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        serverGC = GCSettings.IsServerGC, tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
        readerAssemblyVersion = assembly.GetName().Version!.ToString(), readerAssemblySha256 =
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location)))
    }));
}

(long Count, long Checksum, long DataCharacters) Consume()
{
    long count = 0, checksum = 0, dataCharacters = 0;
    foreach (var setting in settings)
    {
        using var reader = new EventLogReader(setting);
        while (reader.ReadNextEventLogItem() is { } item)
        {
            count++;
            dataCharacters += item.Data.Length;
            checksum += item.Comment.Length + item.Data.Length + item.User.Length + item.Event.Length + item.Session;
        }
        // EventLogReader mutates its ItemId setting. Reset for the next scan.
        setting.ItemId = 0;
    }
    return (count, checksum, dataCharacters);
}
