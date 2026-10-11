# ClickHouse compatibility tests

These opt-in tests use the actual production `ClickHouseStorage` and its existing
ClickHouse.Client 3.0.0.357 dependency. No production schema or dependencies change.
Additional tests verify optional parser settings through both exporter constructors,
delivery checkpoints and service shutdown using controlled receivers without a server.
Service lifecycle tests reference the actual standalone/Manager assemblies; their
production target frameworks are unchanged.
The standalone test project requires .NET SDK 8+ and is outside the legacy solution.
The test-only ASP.NET Core framework reference provides a loopback Kestrel fault
proxy; production projects have no new framework or package requirements.

Use a **dedicated disposable server**. Each test creates a unique
`onestools_contract_<guid>` database and drops that database in `finally`.
The test user needs permission to create/drop databases, create/rename tables,
insert and select. No application database name is accepted by the tests.

Example (PowerShell, Docker):

```powershell
docker run --detach --name onestools-contract --publish 127.0.0.1:18123:8123 --env CLICKHOUSE_USER=contract --env CLICKHOUSE_PASSWORD=contract-test-only --env CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT=1 clickhouse:26.8.15.10@sha256:47ef918e86854015f4e75848fe37f893808dd379055b0fcab1adcde583e693d4
$env:ONESTOOLS_CLICKHOUSE_URL = 'http://127.0.0.1:18123/'
$env:ONESTOOLS_CLICKHOUSE_USER = 'contract'
$env:ONESTOOLS_CLICKHOUSE_PASSWORD = 'contract-test-only'
# Wait until the server responds successfully, then run:
dotnet test OneSTools.EventLog.Exporter.IntegrationTests/OneSTools.EventLog.Exporter.IntegrationTests.csproj --configuration Release
docker rm --force --volumes onestools-contract
```

Without `ONESTOOLS_CLICKHOUSE_URL`, server tests explicitly report **Skipped**. CI sets it
and starts a fresh service container. An unavailable configured server fails tests.
The official `clickhouse:lts` image resolved to **26.8.15.10** on 2026-10-10;
CI pins that version and manifest digest for reproducibility. Update both explicitly
when adding coverage for a newer LTS.

Checks:

- An existing upstream table accepts all 24 distinct field values without changes
  to `SHOW CREATE TABLE`, including UTF-8, quotes, whitespace, UTC timestamps and
  Int64 values larger than 2^53. Data is checked through independent HTTP reads.
- A newly created table matches the upstream DDL snapshot, including column
  order/types/codecs and MergeTree settings. Null strings and a missing transaction
  date keep their existing empty-string/1970 normalization.
- After disposal/reconnection, the saved four-field position resumes the actual
  reader inside an LGP, then continues to another period with the same mtime,
  with no duplicate/missing rows in this scenario.
- Corrected scalar/text/status values from the actual reader fit the existing
  table without changing its schema when `UseCorrectedValueParsing` is enabled.
- Named JSON objects and unknown token trees from `UseStructuredDataParsing`
  fit the existing Data String, preserve the schema/cursor and support nested
  `JSONExtractString` queries.
- Context envelopes, nonzero separation values and typed reference identities
  remain queryable in that same Data String, with all 24 columns and DDL unchanged.
  Both exporter constructors and Manager forward the independent opt-in settings.
  A strict parsing error acknowledges only the valid prefix and resumes after repair.
- Historical +1 byte checkpoints resume with opt-in latest LGF names and
  transaction outcomes, keeping all 24 columns and SHOW CREATE TABLE unchanged.
- The storage cursor follows file/offset when event timestamps move backwards.
- A deliberately partial first batch writes only its later row to the actual server;
  local recovery replays the whole batch, preserves all events and permits duplicates.
- Controlled receiver tests cover failed/lost acknowledgements, first/next partial
  batches, archive EOF, bounded drain on cancellation, non-cooperative writes,
  checkpoint corruption/locking/loss, truncated journals and failed state publication.
- Restarting after acknowledging 45 events in 15 retained daily files performs no
  INSERT, even if their mtimes change. A live restart exports only an appended event
  and an event in the next daily file, preserving all 47 unique file/offset positions.
- Standalone service and Manager await active writes before disposing receivers;
  a re-added source waits for its previous worker. Archive success does not restart.
- A real HTTP proxy rejects the second INSERT with 503 or drops its TCP response
  after ClickHouse commits. Both actual service retry loops resume from the last
  acknowledged checkpoint: rejected writes have no duplicates; unknown outcomes
  replay only the unacknowledged packet. Another restart sends no INSERT.
- The same storage instance retries setup after CREATE TABLE fails. Missing
  databases are created with mixed-case and quoted Database connection settings.

See [delivery semantics and configuration](../docs/export-delivery.md) and
[ClickHouse recovery results](../docs/clickhouse-recovery.md). This does not
establish exactly-once or power-loss durability. Process kills, network partitions,
server crashes, disk-full conditions, Elasticsearch and CORP are not exercised here.
