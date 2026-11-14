using System.Net;
using System.Net.Sockets;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The access log's behaviour that only a real server can show: the entry is written from the response's completion, so
/// Kestrel's final status (500 for an unhandled exception, 499 for a connection that ended without one) and the gate's
/// aborted request are only visible there. <see cref="Logging.AccessLogMiddlewareTests"/> keeps the pure
/// <see cref="AccessLogMiddleware.Record(Microsoft.AspNetCore.Http.HttpContext, AccessLogRing, long)"/> cases.
/// </summary>
public sealed class AccessLogPipelineTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", upstream.Address("a"))],
        Hosts =
        [
            ProxyHost(
                "app",
                "app.test",
                Route("app-drop", "web", "/drop") with { Policies = [new DropPolicySpec()] },
                Route("app-root", "web")),
            ProxyHost("quiet", "quiet.test", Route("quiet-root", "web")) with { AccessLog = false },
        ],
    };

    [Fact]
    public async Task AnEntryCarriesWhatTheGateResolvedAndTheResponseSays()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(), new() { ClientInfo = true });
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);
        using var request = server.Request("app.test", "/a/b?x=1");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entries = await ReadEntriesAsync(server.AccessLog, 1);
        var entry = Assert.Single(entries);
        Assert.Equal("127.0.0.1", entry.ClientAddress);
        Assert.Equal("app.test", entry.Host);
        Assert.Equal("GET", entry.Method);
        Assert.Equal("/a/b", entry.Path);
        Assert.Equal("?x=1", entry.Query);
        Assert.Equal("HTTP/1.1", entry.Protocol);
        Assert.Equal(200, entry.StatusCode);
        Assert.Equal("Mozilla/5.0", entry.UserAgent);
        Assert.True(entry.TotalTicks > 0, $"total was {entry.TotalTicks} ticks");
        Assert.True(entry.UpstreamTicks >= 0, $"upstream was {entry.UpstreamTicks} ticks");
        Assert.Equal(upstream.Address("a"), entry.UpstreamAddress);
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
    }

    /// <summary>
    /// Nothing handled the exception, so Kestrel answers 500 itself; the entry is written after that, from the response's
    /// completion, and reports the status the client actually got.
    /// </summary>
    [Fact]
    public async Task AnUnhandledExceptionIsLoggedAs500()
    {
        var options = new ProxyTestServerOptions
        {
            AfterGate = static (context, next) => context.Request.Path.StartsWithSegments("/boom")
                ? throw new InvalidOperationException("boom")
                : next(context),
        };
        await using var server = await ProxyTestServer.StartAsync(Model(), options);
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request("app.test", "/boom"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var entry = Assert.Single(await ReadEntriesAsync(server.AccessLog, 1));
        Assert.Equal(500, entry.StatusCode);
    }

    /// <summary>
    /// A body Kestrel rejects mid-read is answered 400 by Kestrel itself, after the completion callback; the line says the
    /// same, not the 200 the response object still reports.
    /// </summary>
    [Fact]
    public async Task ARejectedRequestBodyIsLoggedWithKestrelsStatus()
    {
        var options = new ProxyTestServerOptions
        {
            AfterGate = static async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/upload"))
                {
                    await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
                    return;
                }

                await next(context);
            },
        };
        await using var server = await ProxyTestServer.StartAsync(Model(), options);
        var ct = TestContext.Current.CancellationToken;

        using TcpClient tcp = new();
        await tcp.ConnectAsync(server.BaseAddress.Host, server.BaseAddress.Port, ct);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /upload HTTP/1.1\r\nHost: app.test\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n"), ct);
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, ct);

        Assert.StartsWith("HTTP/1.1 400", Encoding.ASCII.GetString(buffer, 0, read), StringComparison.Ordinal);
        var entry = Assert.Single(await ReadEntriesAsync(server.AccessLog, 1));
        Assert.Equal(400, entry.StatusCode);
    }

    /// <summary>A request the gate dropped sent nothing, so the line has no status at all (written as <c>-</c>).</summary>
    [Fact]
    public async Task AnAbortedRequestIsLoggedWithoutAStatus()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        // The gate aborts the connection; the client sees it as a failed request, which is the point.
        _ = await Record.ExceptionAsync(() => client.SendAsync(server.Request("app.test", "/drop"), TestContext.Current.CancellationToken));

        var entry = Assert.Single(await ReadEntriesAsync(server.AccessLog, 1));
        Assert.Equal(AccessLogEntry.NoResponse, entry.StatusCode);
    }

    [Fact]
    public async Task AHostWithTheLogOffWritesNothing()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        // One connection, so the logged request after the quiet one is only served once the quiet request's completion
        // callback has run: by then a line for it would be in the ring too.
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using var quiet = await client.SendAsync(server.Request("quiet.test", "/quiet"), TestContext.Current.CancellationToken);
        using var logged = await client.SendAsync(server.Request("app.test", "/logged"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
        Assert.Equal(HttpStatusCode.OK, logged.StatusCode);
        var entry = Assert.Single(await ReadEntriesAsync(server.AccessLog, 1));
        Assert.Equal("/logged", entry.Path);
    }

    /// <summary>
    /// Without the gate in the pipeline there is no snapshot, host or request ID, and the log still writes its line: the
    /// entry's "no snapshot means log" rule, on a real request.
    /// </summary>
    [Fact]
    public async Task ARequestThatNeverReachesTheGateIsStillLogged()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(), new() { WithoutGate = true });
        using var client = ProxyTestServer.CreateClient();

        // Routing matches YARP's internal route by itself; the proxy pipeline refuses it, as without the gate it can't
        // prove the gate chose the endpoint.
        using var response = await client.SendAsync(server.Request("app.test", "/.paduraksa/r/app-root/x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var entry = Assert.Single(await ReadEntriesAsync(server.AccessLog, 1));
        Assert.Equal(404, entry.StatusCode);
        Assert.Equal("app.test", entry.Host);
        Assert.Null(entry.RequestId);
    }

    /// <summary>
    /// Completion runs after Kestrel has written the response, so the client can see it first. Wait for the callback
    /// rather than racing it, then fail if more than <paramref name="count"/> lines turn up. Only lines for the test's own names
    /// (<c>*.test</c>) count: a dev tool probing new localhost ports sends <c>GET /</c> to the listener, with a Host of its own.
    /// </summary>
    private static async Task<AccessLogEntry[]> ReadEntriesAsync(AccessLogRing ring, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        List<AccessLogEntry> entries = new(count);
        void Drain()
        {
            while (ring.TryRead(out var entry))
            {
                if (entry.Host is { } host && host.EndsWith(".test", StringComparison.Ordinal))
                {
                    entries.Add(entry);
                }
            }
        }

        for (var attempt = 0; attempt < 3_000; attempt++)
        {
            Drain();
            if (entries.Count >= count)
            {
                // Absence window: a late extra entry would show in the second drain.
                await Task.Delay(20, ct);
                Drain();
                return [.. entries];
            }

            // Poll step of a wait bounded to 30 s.
            await Task.Delay(10, ct);
        }

        Assert.Fail($"only {entries.Count} access-log entries arrived, expected {count}");
        return [];
    }
}
