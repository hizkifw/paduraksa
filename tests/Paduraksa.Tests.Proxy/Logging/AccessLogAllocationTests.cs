using System.Net;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>
/// The access log costs the gate nothing (PLAN §7.1, §7.4): with the outermost middleware recording a line for every
/// request, the gate's own decision is still 0 B on Kestrel, in steady state, keep-alive and HTTP/2 included, and every
/// outcome (forwarded, refused, answered, unknown host) is logged.
/// </summary>
public sealed class AccessLogAllocationTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private const int Connections = 4;
    private const int Warmup = 500;
    private const int Measured = 1_500;

    [Theory]
    [InlineData("app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData("unknown.test", "/", HttpStatusCode.NotFound)]
    public async Task TheGateAllocatesNothingWithTheAccessLogOn(string host, string path, HttpStatusCode expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost(
                    "app",
                    "app.test",
                    Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec()] },
                    Route("app-api", "web", "/api"),
                    Route("app-root", "web")),
                ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false },
            ],
        };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true });
        using HttpClient client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, host, path, expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, path, expected, Measured);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.Bytes == 0, $"gate allocated on Kestrel with the access log on: {server.GateStats}");
        // A line per request, in order, none dropped: the log really was on for these requests.
        Assert.True(server.AccessLog.EntriesWritten >= Measured, $"only {server.AccessLog.EntriesWritten} lines were queued");
        Assert.Equal(0, server.AccessLog.Dropped);
        Assert.True(server.AccessLog.TryRead(out AccessLogEntry entry));
        Assert.Equal(path, entry.Path);
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
    }

    [Fact]
    public async Task AHostWithTheLogOffIsProxiedAndNotLogged()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts = [ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false }],
        };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(model);
        using HttpClient client = ProxyTestServer.CreateClient();

        using HttpResponseMessage response = await client.SendAsync(server.Request("quiet.test", "/x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/a/x", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, server.AccessLog.EntriesWritten);
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string host, string path, HttpStatusCode expected, int count)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int next = 0;
        Task[] workers = new Task[Connections];
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= count)
                    {
                        using HttpRequestMessage request = server.Request(host, path);
                        using HttpResponseMessage response = await client.SendAsync(request, ct);
                        Assert.Equal(expected, response.StatusCode);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                },
                ct);
        }

        await Task.WhenAll(workers);
    }
}
