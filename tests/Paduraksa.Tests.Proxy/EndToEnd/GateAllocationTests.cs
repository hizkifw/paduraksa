using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// Spike S3 §8: on real Kestrel (H1, keep-alive) the gate's own decision allocates nothing per request. Only
/// <c>Decide</c> is measured, on the request thread; the body write and YARP are Kestrel's and YARP's.
/// </summary>
public sealed class GateAllocationTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private const int Connections = 4;
    private const int Warmup = 1_000;
    private const int Measured = 3_000;

    [Theory]
    [InlineData(false, "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData(false, "x.app.test:8080", "/", HttpStatusCode.OK)]
    [InlineData(false, "app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData(false, "soon.test", "/", HttpStatusCode.ServiceUnavailable)]
    [InlineData(false, "unknown.test", "/", HttpStatusCode.NotFound)]
    [InlineData(true, "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData(true, "app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData(true, "unknown.test", "/", HttpStatusCode.NotFound)]
    public async Task GateDecisionAllocatesNothingOnKestrel(bool h2c, string host, string path, HttpStatusCode expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test", "*.app.test"],
                    Policies = [new IpAllowListPolicySpec(["127.0.0.1"])],
                    Routes =
                    [
                        Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec()] },
                        Route("app-api", "web", "/api"),
                        Route("app-root", "web"),
                    ],
                },
                new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
            ],
        };
        // h2c: one connection, a few concurrent streams. Kestrel pools streams that end gracefully, so after warm-up
        // the per-stream state in IPersistentStateFeature is reused just like on an H1 keep-alive connection.
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(
            model,
            new() { MeasureGate = true, Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1 });
        using HttpClient client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, host, path, expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, path, expected, Measured);

        // A request can be counted twice (the client retries an idempotent request on a connection that closed
        // under it), never less: every measured request passed the gate before its response was sent.
        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        // Steady state is 0 B. A client reconnect (or a new H2 stream beyond the pooled ones) creates the gate's pooled
        // state once (spike S3 §3); those requests are counted apart and must stay rare.
        Assert.True(server.GateStats.Bytes == 0, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// Client-IP resolution behind trusted proxies is part of the 0 B decision (PLAN §7.6): the connection's ClientInfo
    /// (stage A on the listener), the peer check against the edge ranges and the header parse. The host only admits the
    /// address the header carries, so a 200 proves the header was honoured.
    /// </summary>
    [Theory]
    [InlineData(TrustedProxyMode.CloudflareProxy, "CF-Connecting-IP", "198.51.100.7", "198.51.100.7")]
    [InlineData(TrustedProxyMode.CloudflareProxy, "CF-Connecting-IP", "2001:db8::7", "2001:db8::7")]
    [InlineData(TrustedProxyMode.CloudflareTunnel, "CF-Connecting-IP", "198.51.100.7", "198.51.100.7")]
    [InlineData(TrustedProxyMode.Generic, "X-Forwarded-For", "203.0.113.5, 198.51.100.7, 127.0.0.2", "198.51.100.7")]
    public async Task ResolvingTheClientBehindTrustedProxiesAllocatesNothingOnKestrel(TrustedProxyMode mode, string header, string value, string expectedClient)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Policies = [new IpAllowListPolicySpec([expectedClient])],
                    Routes = [Route("app-root", "web")],
                },
            ],
            Defaults = new DefaultsSpec
            {
                TrustedProxies = new TrustedProxySpec
                {
                    Mode = mode,
                    Proxies = mode == TrustedProxyMode.CloudflareProxy ? [] : ["127.0.0.0/8"],
                },
            },
        };
        // Loopback stands in for Cloudflare's edge.
        SnapshotCompilerOptions options = new() { CloudflareEdgeRanges = CidrSet.Parse(["127.0.0.0/8"]) };
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true, CompilerOptions = options, ClientInfo = true });
        using HttpClient client = ProxyTestServer.CreateClient(maxConnections: Connections);
        void WithHeader(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation(header, value);

        await SendAsync(server, client, "app.test", "/", HttpStatusCode.OK, Warmup, WithHeader);
        server.GateStats!.Reset();
        await SendAsync(server, client, "app.test", "/", HttpStatusCode.OK, Measured, WithHeader);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.Bytes == 0, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string host, string path, HttpStatusCode expected, int count, Action<HttpRequestMessage>? configure = null)
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
                        configure?.Invoke(request);
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
