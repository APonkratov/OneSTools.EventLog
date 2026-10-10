using System.Data.Common;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using OneSTools.EventLog.Exporter.Core.ClickHouse;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

// Explicit opt-in; tests never reuse a configured application database.
public sealed class ClickHouseFactAttribute : FactAttribute
{
    public ClickHouseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_URL")))
            Skip = "Set ONESTOOLS_CLICKHOUSE_URL to a dedicated test server.";
    }
}

internal sealed class ClickHouseServer : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _connection;
    public string Database { get; } = "onestools_contract_" + Guid.NewGuid().ToString("N");

    public ClickHouseServer()
    {
        var url = new Uri(Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_URL")!);
        var username = Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_USER") ?? "default";
        var password = Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_PASSWORD") ?? "";
        _http = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        var connection = new DbConnectionStringBuilder
        {
            ["Host"] = url.Host, ["Port"] = url.Port, ["Protocol"] = url.Scheme,
            ["Username"] = username, ["Password"] = password, ["Database"] = Database
        };
        _connection = connection.ConnectionString;
    }

    public ClickHouseStorage Storage() => new(_connection);

    public async Task<string> Query(string sql)
    {
        using var body = new StringContent(sql, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync("", body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"ClickHouse returned {response.StatusCode}: {text}");
        return text;
    }

    public async Task CreateLegacyTable()
    {
        await Query($"CREATE DATABASE {Database}");
        await Query(await LegacyDdl());
    }

    public async Task<string> LegacyDdl()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "OneSTools.EventLog.Exporter.IntegrationTests.LegacyEventLogItems.sql")!;
        using var reader = new StreamReader(stream);
        return (await reader.ReadToEndAsync()).Replace("{database}", Database);
    }

    public Task<string> Schema() => Query($"SHOW CREATE TABLE {Database}.EventLogItems FORMAT TSVRaw");

    public async ValueTask DisposeAsync()
    {
        try { await Query($"DROP DATABASE IF EXISTS {Database} SYNC"); }
        finally { _http.Dispose(); }
    }
}
