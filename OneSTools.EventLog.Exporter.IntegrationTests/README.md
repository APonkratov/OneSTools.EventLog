# ClickHouse compatibility tests

These opt-in tests use the actual production `ClickHouseStorage` and its existing
ClickHouse.Client 3.0.0.357 dependency. No production schema or dependencies change.
Twelve additional tests verify optional value-parsing settings through both exporter
constructors with an in-memory receiver; those tests do not require a server.
The standalone test project requires .NET SDK 8+ and is outside the legacy solution.

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
- Historical +1 byte checkpoints resume with opt-in latest LGF names and
  transaction outcomes, keeping all 24 columns and SHOW CREATE TABLE unchanged.

This verifies the storage contract and an orderly restart. It does not establish
exactly-once delivery under concurrent batches, crashes or shutdown races, and does
not exercise Elasticsearch or CORP journals.
