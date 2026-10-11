using System.Data.Common;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NodaTime;
using OneSTools.EventLog;
using OneSTools.EventLog.Exporter.Core.ClickHouse;

// Deliberately sequential read/insert batches isolate common reader+storage cost.
// This does not benchmark either version's background service scheduling.
if (args.Length < 3) throw new ArgumentException("Usage: ExportBenchmark <corpus> <default|data|structured|context> <dedicated-loopback-ClickHouse-url> [warmups=1] [runs=1]");
var root = Path.GetFullPath(args[0]); var mode = args[1]; var url = new Uri(args[2]);
if (!url.IsLoopback || url.Scheme != "http") throw new ArgumentException("Dedicated loopback HTTP test server required");
if (mode is not ("default" or "data" or "structured" or "context")) throw new ArgumentException("Unknown mode");
var warmups = args.Length > 3 ? int.Parse(args[3]) : 1;
var runs = args.Length > 4 ? int.Parse(args[4]) : 1;
var folders = Directory.GetDirectories(root).Where(f => File.Exists(Path.Combine(f, "1Cv8.lgf"))).Order(StringComparer.Ordinal).ToArray();
if (folders.Length == 0 || warmups < 0 || runs < 1) throw new ArgumentException("Invalid corpus/run count");
using var http = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("contract:contract-test-only")));
for (var run = -warmups; run < runs; run++)
{
    var database = "onestools_benchmark_" + Guid.NewGuid().ToString("N");
    try
    {
        var connection = new DbConnectionStringBuilder
        {
            ["Host"] = url.Host, ["Port"] = url.Port, ["Protocol"] = "http", ["Username"] = "contract",
            ["Password"] = "contract-test-only", ["Database"] = database
        };
        using var storage = new ClickHouseStorage(connection.ConnectionString);
        await storage.ReadEventLogPositionAsync(); // Initialize connection/table before the timed scan.
        var beforeSchema = await Query($"SHOW CREATE TABLE {database}.EventLogItems FORMAT TSVRaw");
        var columns = int.Parse((await Query($"SELECT count() FROM system.columns WHERE database='{database}' AND table='EventLogItems'")).Trim());
        if (columns != 24) throw new Exception("Unexpected ClickHouse schema");
        var settings = folders.Select(f => Settings(f)).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var timer = Stopwatch.StartNew();
        long records = 0, dataCharacters = 0;
        foreach (var setting in settings)
        {
            using var reader = new EventLogReader(setting);
            var batch = new List<EventLogItem>(10000);
            while (reader.ReadNextEventLogItem() is { } item)
            {
                records++; dataCharacters += item.Data.Length; batch.Add(item);
                if (batch.Count == 10000)
                {
                    await storage.WriteEventLogDataAsync(batch);
                    batch.Clear();
                }
            }
            if (batch.Count != 0) await storage.WriteEventLogDataAsync(batch);
        }
        timer.Stop();
        allocated = GC.GetTotalAllocatedBytes(true) - allocated;
        var stored = long.Parse((await Query($"SELECT count() FROM {database}.EventLogItems")).Trim());
        if (stored != records || await Query($"SHOW CREATE TABLE {database}.EventLogItems FORMAT TSVRaw") != beforeSchema)
            throw new Exception("Storage count/schema mismatch");
        if (run >= 0) Console.WriteLine(JsonSerializer.Serialize(new
        {
            mode, run, records, stored, seconds = timer.Elapsed.TotalSeconds,
            eventsPerSecond = records / timer.Elapsed.TotalSeconds, allocatedBytesPerEvent = (double)allocated / records,
            dataCharacters, columns, portion = 10000, parallelWrites = 1,
            runtime = RuntimeInformation.FrameworkDescription, serverGC = GCSettings.IsServerGC,
            readerAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(EventLogReader).Assembly.Location)))
        }));
    }
    finally { await Query($"DROP DATABASE IF EXISTS {database} SYNC"); }
}

EventLogReaderSettings Settings(string folder)
{
    var setting = new EventLogReaderSettings { LogFolder = folder, LiveMode = false, TimeZone = DateTimeZoneProviders.Tzdb["Europe/Moscow"] };
    var options = mode switch
    {
        "default" => Array.Empty<string>(),
        "data" => new[] { "UseCorrectedValueParsing", "UseStructuredDataParsing" },
        _ => new[] { "UseCorrectedValueParsing", "UseStructuredDataParsing", "UseCurrentEventNames", "UseLatestDictionaryValues", "UseStrictParsing" }
    };
    foreach (var option in options) (typeof(EventLogReaderSettings).GetProperty(option) ?? throw new Exception("Unsupported " + option)).SetValue(setting, true);
    if (mode == "context") typeof(EventLogReaderSettings).GetProperty("UseEventContext")!.SetValue(setting, true);
    return setting;
}

async Task<string> Query(string sql)
{
    using var response = await http.PostAsync("", new StringContent(sql, Encoding.UTF8, "text/plain"));
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}
