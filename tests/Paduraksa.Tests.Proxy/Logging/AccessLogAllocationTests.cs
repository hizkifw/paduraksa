using System.Net;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

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
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, host, path, expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, path, expected, Measured);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.Bytes == 0, $"gate allocated on Kestrel with the access log on: {server.GateStats}");
        // A line per request, in order, none dropped: the log really was on for these requests.
        Assert.True(server.AccessLog.EntriesWritten >= Measured, $"only {server.AccessLog.EntriesWritten} lines were queued");
        Assert.Equal(0, server.AccessLog.Dropped);
        Assert.True(server.AccessLog.TryRead(out var entry));
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
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request("quiet.test", "/x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/a/x", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, server.AccessLog.EntriesWritten);
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string host, string path, HttpStatusCode expected, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        var next = 0;
        var workers = new Task[Connections];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= count)
                    {
                        using var request = server.Request(host, path);
                        using var response = await client.SendAsync(request, ct);
                        Assert.Equal(expected, response.StatusCode);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                },
                ct);
        }

        await Task.WhenAll(workers);
    }
}
