using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OneSTools.EventLog.Exporter.IntegrationTests;

internal enum ClickHouseFault { RejectTableInitialization, RejectSecondInsert, DropSecondInsertResponse }

// Loopback-only fault injection against a dedicated real ClickHouse. Kestrel is
// a test framework reference; production has no new packages or runtime targets.
internal sealed class ClickHouseFaultProxy(ClickHouseFault fault) : IAsyncDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    private WebApplication? _app;
    private int _faultUsed;
    private int _inserts;
    public Uri Address { get; private set; } = null!;
    public int Inserts => Volatile.Read(ref _inserts);
    public Action? BeforeFault { get; set; }
    public TaskCompletionSource<bool> FaultReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task Start()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(Forward);
        await _app.StartAsync();
        Address = new Uri(_app.Urls.Single());
    }

    private async Task Forward(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var bytes = body.ToArray();
        var sql = context.Request.Query["query"].ToString();
        if (string.IsNullOrEmpty(sql))
        {
            if (context.Request.Headers.ContentEncoding.ToString() == "gzip")
            {
                using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, Encoding.UTF8);
                sql = await reader.ReadToEndAsync(context.RequestAborted);
            }
            else sql = Encoding.UTF8.GetString(bytes);
        }
        sql = sql.TrimStart();
        var insert = sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);
        var insertNumber = insert ? Interlocked.Increment(ref _inserts) : 0;
        var shouldFail = fault == ClickHouseFault.RejectTableInitialization
            ? sql.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) : insertNumber == 2;
        var inject = shouldFail && Interlocked.CompareExchange(ref _faultUsed, 1, 0) == 0;
        if (inject)
        {
            try { BeforeFault?.Invoke(); }
            catch (Exception error) { FaultReached.TrySetException(error); throw; }
            if (fault != ClickHouseFault.DropSecondInsertResponse)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Injected temporary ClickHouse failure", context.RequestAborted);
                FaultReached.TrySetResult(true);
                return;
            }
        }

        var upstream = new Uri(new Uri(Environment.GetEnvironmentVariable("ONESTOOLS_CLICKHOUSE_URL")!),
            context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream)
            { Content = new ByteArrayContent(bytes) };
        foreach (var header in context.Request.Headers)
        {
            if (HopHeader(header.Key) || header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        using var response = await _http.SendAsync(request, context.RequestAborted);
        var result = await response.Content.ReadAsByteArrayAsync(context.RequestAborted);
        if (inject)
        {
            if (!response.IsSuccessStatusCode)
                FaultReached.TrySetException(new IOException("Upstream INSERT did not succeed before the response drop."));
            else FaultReached.TrySetResult(true);
            // The server committed the INSERT. Close the TCP response without
            // delivering status/headers/body to the production ClickHouse driver.
            context.Abort();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!HopHeader(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
        await context.Response.Body.WriteAsync(result, context.RequestAborted);
    }

    private static bool HopHeader(string name) => name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_app != null) { await _app.StopAsync(); await _app.DisposeAsync(); }
        _http.Dispose();
    }
}
