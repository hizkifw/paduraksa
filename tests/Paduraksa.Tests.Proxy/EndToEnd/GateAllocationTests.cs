using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Tests.Proxy.AccessLists;
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
    private static readonly AccessListSpec Loopback = TestAccessLists.Allow("loopback", "127.0.0.1");

    private static readonly Lazy<System.Security.Cryptography.X509Certificates.X509Certificate2> Certificate = new(static () => TestCertificates.SelfSigned());

    private const int Connections = 4;
    private const int Warmup = 1_000;
    private const int Measured = 3_000;

    [Theory]
    [InlineData("h1", "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("h1", "x.app.test:8080", "/", HttpStatusCode.OK)]
    [InlineData("h1", "app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData("h1", "soon.test", "/", HttpStatusCode.ServiceUnavailable)]
    [InlineData("h1", "unknown.test", "/", HttpStatusCode.NotFound)]
    [InlineData("h1", "limited.test", "/", HttpStatusCode.OK)]
    [InlineData("h1", "limited.test", "/limited", HttpStatusCode.TooManyRequests)]
    [InlineData("h2c", "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("h2c", "app.test", "/blocked", HttpStatusCode.Forbidden)]
    [InlineData("h2c", "unknown.test", "/", HttpStatusCode.NotFound)]
    [InlineData("h1-tls", "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("h1-tls", "unknown.test", "/", HttpStatusCode.NotFound)]
    [InlineData("h2-tls", "app.test", "/api/items", HttpStatusCode.OK)]
    [InlineData("h2-tls", "app.test", "/blocked", HttpStatusCode.Forbidden)]
    public async Task GateDecisionAllocatesNothingOnKestrel(string transport, string host, string path, HttpStatusCode expected)
    {
        RateLimitPolicyId openPolicy = TestIds.RateLimitPolicy("open");
        RateLimitPolicyId tightPolicy = TestIds.RateLimitPolicy("tight");
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            AccessLists = [Loopback],
            RateLimitPolicies =
            [
                // Enough tokens for the whole measured run, so the allowed path stays allowed.
                new RateLimitPolicySpec { Id = openPolicy, Name = "open", Parameters = new RateLimitParameters(RateLimitParameters.MaxTokensPerSecond, 1_000_000) },
                // One token and a slow refill: the first request takes it, and every measured one is refused.
                new RateLimitPolicySpec { Id = tightPolicy, Name = "tight", Parameters = new RateLimitParameters(1.0 / 60, 1) },
            ],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test", "*.app.test"],
                    Policies = [Loopback.Policy()],
                    Routes =
                    [
                        Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec()] },
                        Route("app-api", "web", "/api"),
                        Route("app-root", "web"),
                    ],
                },
                new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
                new HostSpec
                {
                    Id = TestIds.Host("limited"),
                    Names = ["limited.test"],
                    Routes =
                    [
                        Route("limited-open", "web") with { RateLimit = openPolicy, RateLimitRouteOverride = true },
                        Route("limited-tight", "web", "/limited") with { RateLimit = tightPolicy, RateLimitRouteOverride = true },
                    ],
                },
            ],
        };
        // h2c: one connection, a few concurrent streams. Kestrel pools streams that end gracefully, so after warm-up
        // the per-stream state in IPersistentStateFeature is reused just like on an H1 keep-alive connection. TLS adds the
        // handshake timing to the connection, which each request reads.
        await using var server = await ProxyTestServer.StartAsync(
            model,
            new()
            {
                MeasureGate = true,
                Protocols = transport.StartsWith("h2", StringComparison.Ordinal) ? HttpProtocols.Http2 : HttpProtocols.Http1,
                Certificate = transport.EndsWith("-tls", StringComparison.Ordinal) ? Certificate.Value : null,
            });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        if (expected == HttpStatusCode.TooManyRequests)
        {
            // The bucket starts full: spend its one token before the warm-up, so every measured request is refused.
            using var spend = await client.SendAsync(server.Request(host, path), TestContext.Current.CancellationToken);
            await spend.Content.LoadIntoBufferAsync(TestContext.Current.CancellationToken);
        }

        await SendAsync(server, client, host, path, expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, path, expected, Measured);

        // A request can be counted twice (the client retries an idempotent request on a connection that closed
        // under it), never less: every measured request passed the gate before its response was sent.
        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        // Steady state is 0 B a request: one request in the run may carry a one-off of the runtime's own on that thread
        // (CI's official build has shown 4-25 KB once in 3000), while an allocation of the gate's shows on every request.
        // A client reconnect (or a new H2 stream beyond the pooled ones) creates the gate's pooled state once (spike S3
        // §3); those requests are counted apart and must stay rare.
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// The access list's paths on Kestrel: an address it allows, an address it refuses, a password it remembers and a request
    /// with no credentials (401). The one PBKDF2 a password costs happens before the measured run, in the warm-up.
    /// </summary>
    [Theory]
    [InlineData("acl-ip.test", null, HttpStatusCode.OK)]
    [InlineData("acl-deny.test", null, HttpStatusCode.Forbidden)]
    [InlineData("acl-auth.test", "alice", HttpStatusCode.OK)]
    [InlineData("acl-auth.test", null, HttpStatusCode.Unauthorized)]
    public async Task AccessListsAllocateNothingOnKestrel(string host, string? user, HttpStatusCode expected)
    {
        var lan = AccessListFixtures.List("lan", allow: ["127.0.0.1"], users: false);
        var closed = AccessListFixtures.List("closed", allow: ["10.0.0.0/8"], users: false);
        var passwords = AccessListFixtures.List("passwords");
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            AccessLists = [lan, closed, passwords],
            Hosts =
            [
                new HostSpec { Id = TestIds.Host("acl-ip"), Names = ["acl-ip.test"], Policies = [new AccessListPolicySpec(lan.Id)], Routes = [Route("acl-ip", "web")] },
                new HostSpec { Id = TestIds.Host("acl-deny"), Names = ["acl-deny.test"], Policies = [new AccessListPolicySpec(closed.Id)], Routes = [Route("acl-deny", "web")] },
                new HostSpec { Id = TestIds.Host("acl-auth"), Names = ["acl-auth.test"], Routes = [Route("acl-auth", "web") with { Policies = [new AccessListPolicySpec(passwords.Id)] }] },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(
            model, new() { MeasureGate = true, CompilerOptions = new SnapshotCompilerOptions { AccessLists = AccessListFixtures.NewRuntime() } });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);
        Action<HttpRequestMessage>? credentials = user is null
            ? null
            : request => request.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{AccessListFixtures.Password}")));

        await SendAsync(server, client, host, "/", expected, Warmup, credentials);
        server.GateStats!.Reset();
        await SendAsync(server, client, host, "/", expected, Measured, credentials);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// The rule sets' verdicts on Kestrel: a path deny, a user-agent deny, a path that needs canonicalising, an allow that carves an
    /// exception, and a request no rule matches. The regex is left out on purpose: its allocation is its own test
    /// (<c>ARegexRuleMatchesWithoutAllocatingOncePathShapesAreKnown</c>).
    /// </summary>
    [Theory]
    [InlineData("/index.html", null, HttpStatusCode.OK)]
    [InlineData("/admin/users", null, HttpStatusCode.Forbidden)]
    [InlineData("/admin/public/logo.png", null, HttpStatusCode.OK)]
    [InlineData("/a/..%5Cadmin", null, HttpStatusCode.Forbidden)]
    [InlineData("/x/.env", null, HttpStatusCode.Forbidden)]
    [InlineData("/index.html", "sqlmap/1.7", HttpStatusCode.Forbidden)]
    public async Task RuleSetsAllocateNothingOnKestrel(string path, string? userAgent, HttpStatusCode expected)
    {
        var rules = TestIds.RuleSet("kestrel");
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            RuleSets =
            [
                new RuleSetSpec
                {
                    Id = rules,
                    Name = "kestrel",
                    Rules =
                    [
                        new RuleSpec { Action = RuleAction.Allow, PathKind = RulePathKind.Prefix, Pattern = "/admin/public" },
                        new RuleSpec { Action = RuleAction.Deny, PathKind = RulePathKind.Prefix, Pattern = "/admin" },
                        new RuleSpec { Action = RuleAction.Deny, PathKind = RulePathKind.Glob, Pattern = "/**/.env" },
                        new RuleSpec { Action = RuleAction.Deny, UserAgentContains = ["sqlmap", "nikto"] },
                    ],
                },
            ],
            Hosts = [ProxyHost("rules", "rules.test", Route("rules-root", "web") with { Policies = [new RuleSetPolicySpec(rules)] })],
        };
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);
        void Agent(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? "Mozilla/5.0 Firefox/130.0");

        await SendAsync(server, client, "rules.test", path, expected, Warmup, Agent);
        server.GateStats!.Reset();
        await SendAsync(server, client, "rules.test", path, expected, Measured, Agent);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// The decision of a forward-auth route allocates nothing on Kestrel while the provider's allow is remembered: the key is hashed in a
    /// stack buffer and the cache read is a few loads. The one call that fills the cache is outside the decision (the gate awaits it
    /// after <c>Decide</c> returns), and the deferring verdict itself is 0 B too.
    /// </summary>
    [Fact]
    public async Task AForwardAuthRouteAllocatesNothingOnKestrelOnceTheAllowIsRemembered()
    {
        await using var auth = await FakeAuthServer.StartAsync(TestContext.Current.CancellationToken);
        auth.Handler = static context =>
        {
            context.Response.Headers["Remote-User"] = "alice";
            return Task.CompletedTask;
        };
        var provider = TestIds.AuthProvider("kestrel");
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            AuthProviders =
            [
                new AuthProviderSpec { Id = provider, Name = "kestrel", Url = auth.Address, ResponseHeaders = ["Remote-User"], CacheTtl = TimeSpan.FromMinutes(5) },
            ],
            Hosts = [ProxyHost("auth", "auth.test", Route("auth-root", "web") with { AuthProvider = provider })],
        };
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true, CompilerOptions = new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime() } });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);
        static void Session(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation("Cookie", "session=abc");

        await SendAsync(server, client, "auth.test", "/page", HttpStatusCode.OK, Warmup, Session);
        server.GateStats!.Reset();
        await SendAsync(server, client, "auth.test", "/page", HttpStatusCode.OK, Measured, Session);

        Assert.True(auth.CallCount <= Connections + 1, $"{auth.CallCount} calls: the allow wasn't remembered");
        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// Client-IP resolution behind trusted proxies is part of the 0 B decision: the connection's ClientInfo
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
        var client = TestAccessLists.Allow("client", expectedClient);
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            AccessLists = [client],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Policies = [client.Policy()],
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
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true, CompilerOptions = options, ClientInfo = true });
        using var http = ProxyTestServer.CreateClient(maxConnections: Connections);
        void WithHeader(HttpRequestMessage request) => request.Headers.TryAddWithoutValidation(header, value);

        await SendAsync(server, http, "app.test", "/", HttpStatusCode.OK, Warmup, WithHeader);
        server.GateStats!.Reset();
        await SendAsync(server, http, "app.test", "/", HttpStatusCode.OK, Measured, WithHeader);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        // Steady state is 0 B a request: one request in the run may carry a one-off of the runtime's own on that thread
        // (CI's official build has shown 4-25 KB once in 3000), while an allocation of the gate's shows on every request.
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// Origin lockdown is part of the 0 B decision, refusing and allowing alike: the verdict is a read of the peer's trust and
    /// a constant answer. Loopback is the peer, standing in for a stranger or for Cloudflare's edge by the ranges.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.0/8", HttpStatusCode.Forbidden)]
    [InlineData("127.0.0.0/8", HttpStatusCode.OK)]
    public async Task OriginLockdownAllocatesNothingOnKestrel(string edge, HttpStatusCode expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "web")) with { Cloudflare = new HostCloudflareSpec { OriginLockdown = OriginLockdownMode.Forbid } }],
        };
        SnapshotCompilerOptions options = new() { CloudflareEdgeRanges = CidrSet.Parse([edge]) };
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true, CompilerOptions = options, ClientInfo = true });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);

        await SendAsync(server, client, "app.test", "/", expected, Warmup);
        server.GateStats!.Reset();
        await SendAsync(server, client, "app.test", "/", expected, Measured);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");
        Assert.True(server.GateStats.FreshState <= 10, $"too many fresh connections/streams: {server.GateStats}");
    }

    /// <summary>
    /// The request limits are part of the 0 B decision, on the allowed path (the header scan runs for every request of the
    /// host) and on every refusal: a request target, the header lines, the header bytes and a declared body. A request with a
    /// body is the awkward one, because Kestrel allocates a string for its Content-Length whenever the headers are copied.
    /// A refused body is measured over HTTP/2 only: on HTTP/1.1 the server closes the connection after answering without
    /// having read the body, which is not a steady state a keep-alive measurement can sit on.
    /// </summary>
    [Theory]
    [InlineData(false, "allowed", HttpStatusCode.OK)]
    [InlineData(false, "allowedBody", HttpStatusCode.OK)]
    [InlineData(false, "url", HttpStatusCode.RequestUriTooLong)]
    [InlineData(false, "headers", HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(false, "headerBytes", HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(true, "allowed", HttpStatusCode.OK)]
    [InlineData(true, "allowedBody", HttpStatusCode.OK)]
    [InlineData(true, "url", HttpStatusCode.RequestUriTooLong)]
    [InlineData(true, "headers", HttpStatusCode.RequestHeaderFieldsTooLarge)]
    [InlineData(true, "body", HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(true, "routeBody", HttpStatusCode.RequestEntityTooLarge)]
    public async Task RequestLimitsAllocateNothingOnKestrel(bool h2c, string kind, HttpStatusCode expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost("limits", "limits.test", Route("limits-small", "web", "/small") with { MaxBodySize = 1024, MaxBodySizeRouteOverride = true }, Route("limits-root", "web"))
                    with
                {
                    RequestLimits = new HostRequestLimitsSpec { MaxBodySize = 4096, MaxUrlLength = 512, MaxHeaderCount = 12, MaxHeaderBytes = 1024 },
                },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(model, new() { MeasureGate = true, Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1 });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);
        var path = kind switch
        {
            "url" => "/" + new string('a', 600),
            "routeBody" => "/small",
            _ => "/",
        };
        Action<HttpRequestMessage> configure = kind switch
        {
            "headers" => WithManyHeaders,
            "headerBytes" => request => request.Headers.TryAddWithoutValidation("X-Pad", new string('p', 1100)),
            "allowedBody" => request => Post(request, 1000),
            "body" => request => Post(request, 8192),
            "routeBody" => request => Post(request, 2048),
            _ => request => request.Headers.TryAddWithoutValidation("X-Pad", "v"),
        };

        await SendAsync(server, client, "limits.test", path, expected, Warmup, configure);
        server.GateStats!.Reset();
        await SendAsync(server, client, "limits.test", path, expected, Measured, configure);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        Assert.True(server.GateStats.NonZero <= 1, $"gate allocated on Kestrel: {server.GateStats}");

        // A refused body over HTTP/2 (the host's limit or the route's) leaves the stream with unread data, and Kestrel resets rather than pools such a stream,
        // so a fresh one now and then is the protocol's doing, not a leak. How many depends on timing: 3 in 3000 here, 12 and
        // once 113 on CI. The bound only keeps the measurement from being mostly fresh streams, where allocation is expected.
        var freshAllowed = h2c && kind is "body" or "routeBody" ? Measured / 10 : 10;
        Assert.True(server.GateStats.FreshState <= freshAllowed, $"too many fresh connections/streams: {server.GateStats}");
    }

    private static void WithManyHeaders(HttpRequestMessage request)
    {
        for (var i = 0; i < 20; i++)
        {
            request.Headers.TryAddWithoutValidation($"X-Pad-{i}", "v");
        }
    }

    private static void Post(HttpRequestMessage request, int length)
    {
        request.Method = HttpMethod.Post;
        request.Content = new ByteArrayContent(new byte[length]);
    }

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string host, string path, HttpStatusCode expected, int count, Action<HttpRequestMessage>? configure = null)
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
                        configure?.Invoke(request);
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
