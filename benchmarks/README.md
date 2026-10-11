# Event log benchmarks

The harnesses compile unchanged against either fork or upstream. They consume
all records directly, with no reflection in the per-record loop. Inputs are
read-only LGF/LGP directories; journal contents and folder names are not printed.
Use Release builds, the same runtime, time zone, GC/JIT configuration, warmup and
corpus for both versions. Alternate version order across repeated measurements.

`ReaderBenchmark` measures complete archived scans, including reader creation,
dictionary resolution and disposal. It reports count, a lightweight aggregate
checksum, elapsed/CPU time, allocations, collections and process peak working
set. The checksum is a consistency check, not a cryptographic equality proof.
Input files must remain unchanged. Warmup scans prime JIT and filesystem cache;
these are warm-cache measurements, not cold-storage latency tests.

Modes:

| Mode | Enabled flags |
| --- | --- |
| default | None |
| data | UseCorrectedValueParsing, UseStructuredDataParsing |
| latest | UseLatestDictionaryValues only |
| strict | UseStrictParsing only |
| structured | CorrectedValueParsing, StructuredDataParsing, CurrentEventNames, LatestDictionaryValues, StrictParsing |
| context | All of structured plus UseEventContext |

UseFinalTransactionStatuses is never enabled. Upstream supports only default.
Reflection checks required settings at startup, outside the measured loop.

```powershell
dotnet build benchmarks/ReaderBenchmark/ReaderBenchmark.csproj -c Release -o C:/bench/fork
dotnet build benchmarks/ReaderBenchmark/ReaderBenchmark.csproj -c Release -o C:/bench/upstream '-p:ReaderProject=C:/bench/upstream-source/OneSTools.EventLog/OneSTools.EventLog.csproj'
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_gcServer = '0'
dotnet C:/bench/upstream/ReaderBenchmark.dll C:/bench/corpus default 1 7
dotnet C:/bench/fork/ReaderBenchmark.dll C:/bench/corpus default 1 7
dotnet C:/bench/fork/ReaderBenchmark.dll C:/bench/corpus data 1 7
```

For balanced order, run separate processes with one warmup/one measured scan,
alternating binaries across seven rounds. Compilation and process startup are
excluded from timed scans. The output includes the actual reader assembly hash.

To automate a balanced before/after comparison of two frozen fork builds:

```powershell
python benchmarks/compare-readers.py C:/bench/before C:/bench/after C:/bench/corpus C:/bench/comparison.json --runs 7
```

This runs all six reader modes in alternating order, with one warmup and one
measured scan per process. It checks count, aggregate checksum and Data length
for each pair, and saves individual samples plus medians/ranges. Python uses only
its standard library and is a benchmark tool, not an exporter dependency. Use
`--modes default` when comparing with upstream, which lacks the optional flags.

`ExportBenchmark` measures **sequential reading plus ClickHouseStorage writes**,
in portions of 10,000 with one write in flight. It compiles against each version's
Exporter.Core through `ExporterProject`. This measures common reader, bulk
serialization and storage cost, not either background service's queue scheduling,
shutdown or production network/HA behavior.

The export harness requires a dedicated loopback HTTP ClickHouse test server,
with user `contract` / password `contract-test-only`. It creates uniquely named
`onestools_benchmark_*` databases, verifies stored counts and the unchanged
24-column table, then drops those databases. Connection/table initialization,
validation and cleanup are outside the timed scan. Each warmup/measurement uses
a fresh database. Never point it at a production server.

```powershell
dotnet build benchmarks/ExportBenchmark/ExportBenchmark.csproj -c Release -o C:/bench/export-fork
dotnet build benchmarks/ExportBenchmark/ExportBenchmark.csproj -c Release -o C:/bench/export-upstream '-p:ExporterProject=C:/bench/upstream-source/OneSTools.EventLog.Exporter.Core/OneSTools.EventLog.Exporter.Core.csproj'
dotnet C:/bench/export-upstream/ExportBenchmark.dll C:/bench/corpus default http://127.0.0.1:18123/ 1 3
dotnet C:/bench/export-fork/ExportBenchmark.dll C:/bench/corpus default http://127.0.0.1:18123/ 1 3
```

The test harnesses target .NET 8; they do not change the production library or
service targets/dependencies. Results under .NET 8 cannot be presented as exact
.NET 5 service throughput. Peak working set includes JIT/warmup and native memory;
allocated bytes/event measures total managed allocation, not retained memory.
