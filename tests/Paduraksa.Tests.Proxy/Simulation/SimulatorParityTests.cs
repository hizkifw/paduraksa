using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Proxy.Tls;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// Parity: the simulator's outcome equals the real gate's for the same request, over every kind of answer
/// the gate has. The real side builds its own <see cref="HttpContext"/> from the wire facts and runs
/// <see cref="GateMiddleware.InvokeAsync"/> over a published store, as the server does; the simulator side gets the
/// same facts as a <see cref="GateProbe"/>.
/// </summary>
public sealed class SimulatorParityTests : IDisposable
{
    private static readonly IPAddress Lan = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Outsider = IPAddress.Parse("198.51.100.1");
    private static readonly IPAddress CloudflareEdge = IPAddress.Parse("173.245.48.1");
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private static readonly string[] Hosts =
    [
        "app.test", "APP.test:8080", "x.app.test", "a.b.app.test", "secure.test", "old.test", "soon.test", "strict.test", "h1.test",
        "noh3.test", "plain.test", "locked.test", "unknown.test", "admin.test", "[::1]", "full.test", "localhost", "app.localhost",
        "127.0.0.1", "127.0.0.1:8443", "pow.test", "open.test", "shadow.test", string.Empty,
    ];

    private static readonly string[] Paths = ["/", "/api/x", "/health", "/health/x", "/blocked", "/drop", "/API/../api", "/%2Fapi", "/nothing", "/a;b/api", "/protect"];

    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    [Theory]
    [InlineData(UnknownHostBehaviour.NotFound)]
    [InlineData(UnknownHostBehaviour.MisdirectedRequest)]
    [InlineData(UnknownHostBehaviour.Close)]
    public async Task EveryHostAndRouteOutcomeMatches(UnknownHostBehaviour unknownHost)
    {
        using Fixture fixture = new(Model(new DefaultsSpec { UnknownHost = unknownHost, Certificate = TestIds.Certificate("shared") }), _pki);
        var compared = 0;
        foreach (var host in Hosts)
        {
            foreach (var (https, version, tls) in Connections)
            {
                foreach (var path in Paths)
                {
                    foreach (var client in (IPAddress[])[Lan, Outsider])
                    {
                        compared += await fixture.CompareAsync(new Wire(host, https, version, tls, path, client));
                    }
                }
            }
        }

        Assert.True(compared > 1000, $"only {compared} requests reached the gate");
        GateReason[] expected =
        [
            GateReason.Forwarded, GateReason.DefaultHost, GateReason.MisdirectedConnection, GateReason.HostPolicy, GateReason.HttpsRedirect,
            GateReason.FixedHost, GateReason.NoRoute, GateReason.RoutePolicy, GateReason.HandshakeRejected,
        ];
        foreach (var reason in expected)
        {
            Assert.Contains(reason, fixture.Reasons);
        }
    }

    [Fact]
    public async Task MethodsQueryAndRepeatedHostHeadersMatch()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        foreach (var method in (string[])["GET", "HEAD", "POST", "DELETE"])
        {
            Assert.Equal(1, await fixture.CompareAsync(new Wire("secure.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/p q", Lan) { Method = method, Query = "a=1&b=%20" }));
            Assert.Equal(1, await fixture.CompareAsync(new Wire("old.test", true, ProbeHttpVersion.Http2, SslProtocols.Tls13, "/deep/path", Lan) { Method = method, Query = "x=y" }));
        }

        // Two Host headers, none, or one that isn't a host: Kestrel answers 400 before the gate (real sockets pin that in
        // SimulatorSocketParityTests), so there is no gate answer to compare.
        foreach (var hosts in (IReadOnlyList<string>[])[["app.test", "secure.test"], [], ["a@b"], ["app.test/x"]])
        {
            Assert.Equal(0, await fixture.CompareAsync(new Wire("app.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Lan) { HostHeaders = hosts }));
        }
    }

    [Fact]
    public async Task ACoalescedConnectionIsMisdirectedAsTheGateSays()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        // Negotiated for lax name (SNI), asking for the stricter hosts.
        foreach (var host in (string[])["strict.test", "h1.test", "noh3.test", "plain.test", "app.test"])
        {
            foreach (var (version, tls) in Coalesced)
            {
                await fixture.CompareAsync(new Wire(host, true, version, tls, "/", Lan) { ServerName = "lax.test" });
            }
        }
    }

    [Fact]
    public async Task ClientResolutionMatchesInEveryTrustedProxyMode()
    {
        (string Name, TrustedProxySpec Spec)[] modes =
        [
            ("off", TrustedProxySpec.Off),
            ("generic", new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] }),
            ("generic-proto", new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"], TrustForwardedProto = true }),
            ("generic-real-ip", new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"], Header = "X-Real-IP" }),
            ("tunnel", new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"] }),
            ("tunnel-all", new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"], UseTrueClientIp = true, ForwardedForFallback = true }),
            ("cloudflare", new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy }),
            ("cloudflare-all", new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy, UseTrueClientIp = true, ForwardedForFallback = true }),
        ];
        (string, string)[][] headerSets =
        [
            [],
            [("X-Forwarded-For", "198.51.100.1")],
            [("X-Forwarded-For", "198.51.100.1, 10.0.0.9")],
            [("X-Forwarded-For", "bogus")],
            [("X-Forwarded-For", "10.0.0.5"), ("X-Forwarded-For", "203.0.113.7, 10.0.0.1")],
            [("X-Real-IP", "198.51.100.1")],
            [("CF-Connecting-IP", "198.51.100.1")],
            [("CF-Connecting-IP", "10.0.0.5")],
            [("CF-Connecting-IP", "not-an-ip"), ("X-Forwarded-For", "10.0.0.5")],
            [("True-Client-IP", "10.0.0.5")],
            [("CF-Connecting-IP", "198.51.100.1"), ("CF-Visitor", "{\"scheme\":\"https\"}")],
            [("CF-Visitor", "{\"scheme\":\"https\"}")],
            [("CF-Visitor", "{\"scheme\":\"http\"}"), ("CF-Connecting-IP", "198.51.100.1")],
            [("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "10.0.0.5")],
        ];

        var compared = 0;
        foreach (var (name, spec) in modes)
        {
            using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared"), TrustedProxies = spec }), _pki);
            foreach (var peer in (IPAddress[])[Loopback, Lan, CloudflareEdge, Outsider])
            {
                foreach (var headers in headerSets)
                {
                    foreach (var host in (string[])["locked.test", "secure.test", "app.test"])
                    {
                        foreach (var proxyProtocol in (bool[])[false, true])
                        {
                            Wire wire = new(host, false, ProbeHttpVersion.Http11, SslProtocols.None, "/", peer) { Headers = headers, ProxyProtocol = proxyProtocol };
                            compared += await fixture.CompareAsync(wire, $"mode {name}");
                        }
                    }
                }
            }
        }

        Assert.True(compared > 1000, $"only {compared} requests compared");
    }

    [Fact]
    public async Task AcmeChallengePathsFallThroughLikeAnyRequest()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        var answered = 0;
        foreach (var host in (string[])["secure.test", "app.test", "unknown.test", "old.test", "strict.test", "locked.test"])
        {
            foreach (var https in (bool[])[false, true])
            {
                foreach (var method in (string[])["GET", "HEAD", "POST"])
                {
                    // A published token is answered on plain HTTP before host lookup and policy; unknown, nested, or HTTPS falls through.
                    foreach (var path in (string[])["/.well-known/acme-challenge/tok-en_1", "/.well-known/acme-challenge/token123", "/.well-known/acme-challenge/tok-en_1/x", "/.well-known/acme-challenge/"])
                    {
                        foreach (var client in (IPAddress[])[Lan, Outsider])
                        {
                            await fixture.CompareAsync(new Wire(host, https, ProbeHttpVersion.Http11, SslProtocols.Tls13, path, client) { Method = method });
                        }
                    }

                    var answer = GateSimulator.Run(fixture.Snapshot, new GateProbe { Https = https, Host = host, Method = method, Path = "/.well-known/acme-challenge/tok-en_1", ClientAddress = Outsider }, challenges: fixture.Challenges);
                    if (answer.Outcome.Reason == GateReason.AcmeChallenge)
                    {
                        answered++;
                        Assert.False(https);
                        Assert.Equal(200, answer.Outcome.StatusCode);
                    }
                }
            }
        }

        Assert.True(answered > 0, "no challenge was ever answered");

        var run = GateSimulator.Run(fixture.Snapshot, new GateProbe { Host = "secure.test", ClientAddress = Outsider, Path = "/.well-known/acme-challenge/token123" });
        var acme = Assert.Single(run.Steps, s => s.Kind == GateStepKind.AcmeChallenge);
        Assert.Equal(GateStepVerdict.Passed, acme.Verdict);
        Assert.Contains("nothing answers it", acme.Summary, StringComparison.Ordinal);
    }

    private static readonly (ProbeHttpVersion Version, SslProtocols Tls)[] Coalesced =
        [(ProbeHttpVersion.Http2, SslProtocols.Tls12), (ProbeHttpVersion.Http2, SslProtocols.Tls13), (ProbeHttpVersion.Http3, SslProtocols.Tls13)];

    private static readonly (bool Https, ProbeHttpVersion Version, SslProtocols Tls)[] Connections =
    [
        (false, ProbeHttpVersion.Http11, SslProtocols.None),
        (true, ProbeHttpVersion.Http11, SslProtocols.Tls12),
        (true, ProbeHttpVersion.Http2, SslProtocols.Tls12),
        (true, ProbeHttpVersion.Http2, SslProtocols.Tls13),
        (true, ProbeHttpVersion.Http3, SslProtocols.Tls13),
    ];

    [Fact]
    public void AnAdminHostTracesAsForwardedToTheAdminApp()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        var run = GateSimulator.Run(fixture.Snapshot, new GateProbe
        {
            Https = true,
            Host = "admin.test",
            Path = "/api/v1/auth/me",
            ClientAddress = Lan,
            ServerName = "admin.test",
        });

        Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
        Assert.NotNull(run.Upstream);
        Assert.True(run.Upstream!.Admin);
        Assert.Equal(UpstreamId.Empty, run.Upstream.Id);
        var upstream = Assert.Single(run.Steps, step => step.Kind == GateStepKind.Upstream);
        Assert.Equal(GateStepVerdict.Passed, upstream.Verdict);
        Assert.Contains("admin app", upstream.Summary, StringComparison.Ordinal);
        Assert.Contains(upstream.Facts, fact => fact is { Name: "target", Value: "admin" });
        var route = Assert.Single(run.Steps, step => step.Kind == GateStepKind.RouteMatch);
        Assert.Contains("admin route", route.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACatchAllListedFirstStillTracesTheMoreSpecificRoute()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);
        Wire wire = new("shadow.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/api/x", Lan);

        Assert.Equal(1, await fixture.CompareAsync(wire));
        var run = fixture.Simulate(wire);

        Assert.Equal((GateOutcomeAction.Respond, GateReason.RoutePolicy, 451), (run.Outcome.Action, run.Outcome.Reason, run.Outcome.StatusCode));
        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.RouteMatch);
        Assert.Equal(TestIds.Route("shadow-api"), step.Route);
        Assert.Equal("/api", Assert.Single(step.Facts, fact => fact.Name == "routePath").Value);
    }

    private static readonly AccessListSpec AppLan = TestAccessLists.Allow("app-lan", "127.0.0.1", "10.0.0.5");

    private static readonly AccessListSpec LanList = TestAccessLists.Allow("lan", "10.0.0.5");

    private static ProxyConfigModel Model(DefaultsSpec defaults) => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/", "http://127.0.0.1:3/")],
        Defaults = defaults,
        AccessLists = [AppLan, LanList],
        Hosts =
        [
            new HostSpec
            {
                Id = TestIds.Host("app"),
                Names = ["app.test", "*.app.test"],
                Policies = [AppLan.Policy()],
                Routes =
                [
                    Route("app-api", "api", "/api"),
                    Route("app-health", "api", "/health", RouteMatchKind.Exact),
                    Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec(451, "legal\n")] },
                    Route("app-drop", "web", "/drop") with { Policies = [new DropPolicySpec()] },
                    Route("app-root", "web"),
                ],
            },
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true, Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("full", "full.test", Route("full-root", "web")) with
            {
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_000, IncludeSubDomains = true, Preload = true },
            },
            ProxyHost("local", "localhost", Route("local-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("v4", "127.0.0.1", Route("v4-root", "web")) with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("custom", "custom.test", Route("custom-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
            },
            ProxyHost("upstream", "upstream.test", Route("upstream-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream },
            },
            ProxyHost("bare", "bare.test", Route("bare-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            new HostSpec { Id = TestIds.Host("old"), Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 } },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with { Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 } },
            ProxyHost("h1", "h1.test", Route("h1-root", "web")) with { Tls = new HostTlsSpec { TcpAlpn = TcpAlpn.Http11Only } },
            ProxyHost("noh3", "noh3.test", Route("noh3-root", "web")) with { Tls = new HostTlsSpec { Http3 = false } },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")) with { Https = false },
            ProxyHost("locked", "locked.test", Route("locked-api", "api", "/api")) with { ForceHttps = true, Policies = [LanList.Policy()] },
            ProxyHost("ipv6", "[::1]", Route("ipv6-root", "web")),
            ProxyHost("lax", "lax.test", Route("lax-root", "web")),
            ProxyHost("robots", "robots.test", Route("robots-root", "web")) with { Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers } },
            ProxyHost("maint", "maint.test", Route("maint-root", "web")) with
            {
                Maintenance = new HostMaintenanceSpec { Enabled = true, RetryAfterSeconds = 60, BypassAccessListId = LanList.Id },
            },
            ProxyHost("maint-page", "maint-page.test", Route("maint-page-root", "web")) with
            {
                Maintenance = new HostMaintenanceSpec { Enabled = true, Status = 200 },
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [ErrorPageStatuses.MaintenanceKey] = new() { Heading = "Back soon", Source = ErrorPageSource.Profile },
                    },
                },
            },
            ProxyHost("paged", "paged.test", Route("paged-app", "web", "/app")) with
            {
                Policies = [LanList.Policy()],
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [403] = new() { Mode = ErrorPageMode.Html, Html = "<p>{status}</p>", Source = ErrorPageSource.GlobalDefaults },
                        [404] = new() { Heading = "Nothing here", Source = ErrorPageSource.Host },
                    },
                },
            },
            new HostSpec
            {
                Id = TestIds.Host("pow"),
                Names = ["pow.test"],
                ProofOfWork = Spec,
                Routes =
                [
                    Route("pow-api", "web", "/api") with { ProofOfWork = SpecOff, ProofOfWorkRouteOverride = true },
                    Route("pow-root", "web") with { ProofOfWork = Spec },
                ],
            },
            new HostSpec
            {
                Id = TestIds.Host("open"),
                Names = ["open.test"],
                ProofOfWork = SpecOff,
                Routes =
                [
                    Route("open-protect", "web", "/protect") with { ProofOfWork = Spec, ProofOfWorkRouteOverride = true },
                    Route("open-root", "web"),
                ],
            },
            new HostSpec
            {
                // The catch-all is listed first: the more specific route must still win, and its policy must run.
                Id = TestIds.Host("shadow"),
                Names = ["shadow.test"],
                Routes =
                [
                    Route("shadow-root", "web"),
                    Route("shadow-api", "api", "/api") with { Policies = [new DenyPolicySpec(451, "legal\n")] },
                ],
            },
            new HostSpec { Id = TestIds.Host("admin"), Names = ["admin.test"], Kind = HostKind.Admin, ForceHttps = true },
            ProxyHost("limits", "limits.test", Route("limits-upload", "web", "/upload") with { MaxBodySize = 100 * Mebibyte, MaxBodySizeRouteOverride = true }, Route("limits-root", "web")) with
            {
                RequestLimits = new HostRequestLimitsSpec { MaxBodySize = 10 * Mebibyte, MaxHeaderCount = 30, MaxHeaderBytes = 2048, MaxUrlLength = 256 },
            },
        ],
    };

    private const long Mebibyte = 1024 * 1024;

    private static HostProofOfWorkSpec Spec => new() { Enabled = true, Difficulty = 10, TokenLifetimeHours = 24 };

    private static HostProofOfWorkSpec SpecOff => new() { Enabled = false, Difficulty = 10, TokenLifetimeHours = 24 };

    /// <summary>The facts of a request as they reach the server, before anything interprets them.</summary>
    private sealed record Wire(string Host, bool Https, ProbeHttpVersion Version, SslProtocols TlsVersion, string Path, IPAddress Peer)
    {
        public string Method { get; init; } = "GET";

        public string? Query { get; init; }

        public IReadOnlyList<(string Name, string Value)> Headers { get; init; } = [];

        /// <summary>HTTP/1.1: the Host headers actually sent; null: one, <see cref="Host"/>.</summary>
        public IReadOnlyList<string>? HostHeaders { get; init; }

        public bool ProxyProtocol { get; init; }

        public string? ServerName { get; init; }
    }

    [Fact]
    public async Task RequestLimitsMatchTheGateAndExplainEachRefusal()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);
        static Wire Request(string path = "/", string method = "GET", params (string Name, string Value)[] headers) =>
            new("limits.test", true, ProbeHttpVersion.Http2, SslProtocols.Tls13, path, Lan) { Method = method, Headers = headers };

        // The target, the header lines, the header bytes and the declared body, each on both sides of its limit and on both
        // routes; the real gate and the simulator must agree on every one.
        foreach (var length in (int[])[1, 100, 255, 256, 257, 400])
        {
            await fixture.CompareAsync(Request("/" + new string('a', length - 1)));
        }

        foreach (var count in (int[])[0, 10, 28, 29, 30, 40])
        {
            await fixture.CompareAsync(Request(headers: [.. Enumerable.Range(0, count).Select(i => ($"X-{i}", "v"))]));
        }

        foreach (var size in (int[])[100, 1900, 2000, 2100, 4000])
        {
            await fixture.CompareAsync(Request(headers: [("X-Pad", new string('p', size))]));
        }

        foreach (var length in (long[])[0, 1, 10 * Mebibyte, (10 * Mebibyte) + 1, 100 * Mebibyte, (100 * Mebibyte) + 1])
        {
            await fixture.CompareAsync(Request("/", "POST", ("Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            await fixture.CompareAsync(Request("/upload", "POST", ("Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        // Each refusal names its reason, its numbers and the answer.
        var body = fixture.Simulate(Request("/", "POST", ("Content-Length", (12 * Mebibyte).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal((GateOutcomeAction.Respond, GateReason.RequestLimits, 413), (body.Outcome.Action, body.Outcome.Reason, body.Outcome.StatusCode));
        var bodyStep = Assert.Single(body.Steps, step => step is { Kind: GateStepKind.RequestLimits, Verdict: GateStepVerdict.Stopped });
        Assert.Equal("body 12 MiB declared, limit 10 MiB: 413", bodyStep.Summary);
        Assert.Equal("host", Assert.Single(bodyStep.Facts, fact => fact.Name == "limitFrom").Value);

        var upload = fixture.Simulate(Request("/upload", "POST", ("Content-Length", (12 * Mebibyte).ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(GateOutcomeAction.Forward, upload.Outcome.Action);
        var uploadStep = Assert.Single(upload.Steps, step => step is { Kind: GateStepKind.RequestLimits } && step.Facts.Any(fact => fact.Name == "contentLength"));
        Assert.Equal("body 12 MiB declared, limit 100 MiB", uploadStep.Summary);
        Assert.Equal("route", Assert.Single(uploadStep.Facts, fact => fact.Name == "limitFrom").Value);

        var headers = fixture.Simulate(Request(headers: [.. Enumerable.Range(0, 34).Select(i => ($"X-{i}", "v"))]));
        Assert.Equal((GateReason.RequestLimits, 431), (headers.Outcome.Reason, headers.Outcome.StatusCode));
        Assert.Equal("35 headers, limit 30: 431", Assert.Single(headers.Steps, step => step is { Kind: GateStepKind.RequestLimits, Verdict: GateStepVerdict.Stopped }).Summary);

        var url = fixture.Simulate(Request("/" + new string('a', 300)));
        Assert.Equal((GateReason.RequestLimits, 414), (url.Outcome.Reason, url.Outcome.StatusCode));
        Assert.Contains("301 bytes", Assert.Single(url.Steps, step => step is { Kind: GateStepKind.RequestLimits, Verdict: GateStepVerdict.Stopped }).Summary, StringComparison.Ordinal);

        // A request with no body says nothing about one, and a chunked one says the server decides.
        var plain = fixture.Simulate(Request());
        Assert.Single(plain.Steps, step => step.Kind == GateStepKind.RequestLimits);
        var chunked = fixture.Simulate(Request("/", "POST", ("Transfer-Encoding", "chunked")));
        Assert.Contains("no declared length", Assert.Single(chunked.Steps, step => step is { Kind: GateStepKind.RequestLimits } && step.Facts.Any(fact => fact.Name == "contentLength")).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheManagedRobotsTxtStepSaysServedOrPassedThrough()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        var managed = new Wire("robots.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/robots.txt", Lan);
        Assert.Equal(1, await fixture.CompareAsync(managed));
        var run = fixture.Simulate(managed);
        Assert.Equal((GateOutcomeAction.Respond, GateReason.RobotsTxt, 200), (run.Outcome.Action, run.Outcome.Reason, run.Outcome.StatusCode));
        var served = Assert.Single(run.Steps, step => step.Kind == GateStepKind.RobotsTxt);
        Assert.Equal(GateStepVerdict.Stopped, served.Verdict);
        Assert.Contains("blockAiCrawlers", served.Summary, StringComparison.Ordinal);
        Assert.Equal("blockAiCrawlers", Assert.Single(served.Facts, fact => fact.Name == "mode").Value);
        Assert.Contains("User-agent: GPTBot", run.Outcome.Body!, StringComparison.Ordinal);

        // A passthrough host still says what happened to the path, and the request goes on.
        var passthrough = new Wire("app.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/robots.txt", Lan);
        Assert.Equal(1, await fixture.CompareAsync(passthrough));
        var passed = fixture.Simulate(passthrough);
        Assert.Equal(GateOutcomeAction.Forward, passed.Outcome.Action);
        var step = Assert.Single(passed.Steps, candidate => candidate.Kind == GateStepKind.RobotsTxt);
        Assert.Equal(GateStepVerdict.Passed, step.Verdict);
        Assert.Contains("not managed", step.Summary, StringComparison.Ordinal);
        Assert.Equal("passthrough", Assert.Single(step.Facts, fact => fact.Name == "mode").Value);
    }

    [Fact]
    public async Task MaintenanceMatchesTheGateAndTheStepSaysWhetherItIsOnOffOrBypassed()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        // A client the bypass list's allow rules admit goes on; any other is answered 503 with the page.
        var bypassed = new Wire("maint.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/x", Lan);
        Assert.Equal(1, await fixture.CompareAsync(bypassed));
        var passed = fixture.Simulate(bypassed);
        Assert.Equal(GateOutcomeAction.Forward, passed.Outcome.Action);
        var passStep = Assert.Single(passed.Steps, step => step.Kind == GateStepKind.Maintenance);
        Assert.Equal(GateStepVerdict.Passed, passStep.Verdict);
        Assert.Contains("lan", passStep.Summary, StringComparison.Ordinal);
        Assert.Equal("yes", Assert.Single(passStep.Facts, fact => fact.Name == "bypassed").Value);

        var refused = new Wire("maint.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/x", Outsider);
        Assert.Equal(1, await fixture.CompareAsync(refused));
        var answered = fixture.Simulate(refused);
        Assert.Equal((GateOutcomeAction.Respond, GateReason.Maintenance, 503), (answered.Outcome.Action, answered.Outcome.Reason, answered.Outcome.StatusCode));
        var stopped = Assert.Single(answered.Steps, step => step.Kind == GateStepKind.Maintenance);
        Assert.Equal(GateStepVerdict.Stopped, stopped.Verdict);
        Assert.Equal("lan", Assert.Single(stopped.Facts, fact => fact.Name == "bypassList").Value);
        Assert.Equal("60", Assert.Single(stopped.Facts, fact => fact.Name == "retryAfter").Value);
        Assert.Equal("no", Assert.Single(stopped.Facts, fact => fact.Name == "bypassed").Value);

        // A host that isn't in maintenance says so, and the request goes on.
        var off = new Wire("secure.test", true, ProbeHttpVersion.Http2, SslProtocols.Tls13, "/x", Lan);
        Assert.Equal(1, await fixture.CompareAsync(off));
        var skipped = Assert.Single(fixture.Simulate(off).Steps, step => step.Kind == GateStepKind.Maintenance);
        Assert.Equal(GateStepVerdict.Skipped, skipped.Verdict);
        Assert.Equal("off", Assert.Single(skipped.Facts, fact => fact.Name == "maintenance").Value);

        // The HTTPS redirect still comes first on a host that forces it, as it does in the gate.
        Assert.Equal(1, await fixture.CompareAsync(new Wire("maint.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/robots.txt", Outsider)));
    }

    [Fact]
    public async Task TheOutcomeSaysWhichErrorPageIsServedAndFromWhichLayer()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        // The host's own message for 404, and its document (from the global defaults) for the 403 of the host chain.
        var notFound = new Wire("paged.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/nothing", Lan);
        Assert.Equal(1, await fixture.CompareAsync(notFound));
        Assert.Equal(new GateErrorPage(Custom: true, ErrorPageSource.Host, ErrorPageMode.Message), fixture.Simulate(notFound).Outcome.ErrorPage);

        var forbidden = new Wire("paged.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/app", Outsider);
        Assert.Equal(1, await fixture.CompareAsync(forbidden));
        Assert.Equal(new GateErrorPage(Custom: true, ErrorPageSource.GlobalDefaults, ErrorPageMode.Html), fixture.Simulate(forbidden).Outcome.ErrorPage);

        // A host with no page of its own for the status serves the built-in one; so does the unknown host.
        var builtIn = new Wire("locked.test", true, ProbeHttpVersion.Http2, SslProtocols.Tls13, "/api", Outsider);
        Assert.Equal(1, await fixture.CompareAsync(builtIn));
        Assert.Equal(new GateErrorPage(Custom: false, ErrorPageSource.BuiltIn, ErrorPageMode.Message), fixture.Simulate(builtIn).Outcome.ErrorPage);
        Assert.Equal(new GateErrorPage(Custom: false, ErrorPageSource.BuiltIn, ErrorPageMode.Message), fixture.Simulate(new Wire("unknown.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Lan)).Outcome.ErrorPage);

        // The maintenance page is the host's own when it set one, else the built-in message.
        var withPage = new Wire("maint-page.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Outsider);
        Assert.Equal(1, await fixture.CompareAsync(withPage));
        Assert.Equal(new GateErrorPage(Custom: true, ErrorPageSource.Profile, ErrorPageMode.Message), fixture.Simulate(withPage).Outcome.ErrorPage);
        Assert.Equal(200, fixture.Simulate(withPage).Outcome.StatusCode);
        Assert.Equal(
            new GateErrorPage(Custom: false, ErrorPageSource.BuiltIn, ErrorPageMode.Message),
            fixture.Simulate(new Wire("maint.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Outsider)).Outcome.ErrorPage);

        // An answer that is only text (a Respond host's) has no page; nor does a redirect.
        Assert.Null(fixture.Simulate(new Wire("soon.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Lan)).Outcome.ErrorPage);
        Assert.Null(fixture.Simulate(new Wire("secure.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Lan)).Outcome.ErrorPage);
    }

    [Fact]
    public async Task ProofOfWorkMatchesAndExplainsEveryCase()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);
        var now = (uint)fixture.ProofOfWork.Time.GetUtcNow().ToUnixTimeSeconds();
        var host = fixture.Snapshot.HostsByIndex.Single(h => h.Id == TestIds.Host("pow"));
        var scope = host.ProofOfWorkHostScope;

        string Mint(uint expiresAt, int difficulty = 10, byte[]? tokenScope = null, IPAddress? client = null)
        {
            var buffer = new char[ProofOfWorkToken.EncodedLength];
            Assert.True(ProofOfWorkToken.TryEncode(fixture.ProofOfWork.Key!, tokenScope ?? scope, IpKey.FromIPAddress(client ?? Lan), expiresAt, difficulty, buffer, out var written));
            return new string(buffer, 0, written);
        }

        Wire WireFor(string path, string method = "GET", string? token = null)
        {
            List<(string Name, string Value)> headers = [("Accept", "text/html")];
            if (token is not null)
            {
                headers.Add(("Cookie", $"{ProofOfWorkToken.CookieName}={token}"));
            }

            return new Wire("pow.test", false, ProbeHttpVersion.Http11, SslProtocols.None, path, Lan) { Method = method, Headers = headers };
        }

        // No token: the browser gets the challenge page, and both sides agree.
        var noToken = WireFor("/");
        Assert.Equal(1, await fixture.CompareAsync(noToken));
        var run = fixture.Simulate(noToken);
        Assert.Equal(200, run.Outcome.StatusCode);
        Assert.Contains("challenge page", PolicyStep(run).Summary, StringComparison.Ordinal);

        // A valid token goes through, and the trace names its expiry.
        var valid = WireFor("/", token: Mint(now + 3600));
        Assert.Equal(1, await fixture.CompareAsync(valid));
        run = fixture.Simulate(valid);
        Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
        Assert.Contains("expiring at", PolicyStep(run).Summary, StringComparison.Ordinal);

        // Expired, too easy, another host scope, another client: a challenge, each with its reason.
        var expired = WireFor("/", token: Mint(now - 1));
        Assert.Equal(1, await fixture.CompareAsync(expired));
        Assert.Contains("expired", PolicyStep(fixture.Simulate(expired)).Summary, StringComparison.Ordinal);

        var tooEasy = WireFor("/", token: Mint(now + 3600, difficulty: 8));
        Assert.Equal(1, await fixture.CompareAsync(tooEasy));
        Assert.Contains("difficulty", PolicyStep(fixture.Simulate(tooEasy)).Summary, StringComparison.Ordinal);

        var otherHost = WireFor("/", token: Mint(now + 3600, tokenScope: new byte[16]));
        Assert.Equal(1, await fixture.CompareAsync(otherHost));
        Assert.Contains("other host or IP", PolicyStep(fixture.Simulate(otherHost)).Summary, StringComparison.Ordinal);

        var otherClient = WireFor("/", token: Mint(now + 3600, client: Outsider));
        Assert.Equal(1, await fixture.CompareAsync(otherClient));
        Assert.Contains("other host or IP", PolicyStep(fixture.Simulate(otherClient)).Summary, StringComparison.Ordinal);

        // A non-browser gets 403, and the trace says why.
        var api = WireFor("/", "POST");
        Assert.Equal(1, await fixture.CompareAsync(api));
        Assert.Equal(403, fixture.Simulate(api).Outcome.StatusCode);
        Assert.Contains("403, because", PolicyStep(fixture.Simulate(api)).Summary, StringComparison.Ordinal);

        // robots.txt is exempt.
        var robots = WireFor(ProofOfWorkTargets.RobotsPath);
        Assert.Equal(1, await fixture.CompareAsync(robots));
        Assert.Contains("robots.txt", PolicyStep(fixture.Simulate(robots)).Summary, StringComparison.Ordinal);

        // The route's own switch exempts /api, and turns the check on for /protect on a host that is otherwise open.
        var apiRoute = WireFor("/api/x");
        Assert.Equal(1, await fixture.CompareAsync(apiRoute));
        Assert.Equal(GateOutcomeAction.Forward, fixture.Simulate(apiRoute).Outcome.Action);
        Assert.DoesNotContain(fixture.Simulate(apiRoute).Steps, step => step.Kind == GateStepKind.RoutePolicy);

        var protectedRoute = new Wire("open.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/protect", Lan)
        {
            Headers = [("Accept", "text/html")],
        };
        Assert.Equal(1, await fixture.CompareAsync(protectedRoute));
        var protectedStep = PolicyStep(fixture.Simulate(protectedRoute));
        Assert.Contains("route's own override", protectedStep.Summary, StringComparison.Ordinal);
        Assert.Contains("challenge page", protectedStep.Summary, StringComparison.Ordinal);

        // The verify endpoint is a step of its own; a bodyless post is refused by the simulator and the real gate alike.
        var verify = new Wire("pow.test", false, ProbeHttpVersion.Http11, SslProtocols.None, ProofOfWorkTargets.VerifyPath, Lan) { Method = "POST" };
        Assert.Equal(1, await fixture.CompareAsync(verify));
        var verifyRun = fixture.Simulate(verify);
        Assert.Equal((GateOutcomeAction.Respond, GateReason.ProofOfWork, 403), (verifyRun.Outcome.Action, verifyRun.Outcome.Reason, verifyRun.Outcome.StatusCode));
        Assert.Contains(verifyRun.Steps, step => step.Kind == GateStepKind.ProofOfWork);
    }

    private static GateStep PolicyStep(GateSimulation run) =>
        Assert.Single(run.Steps, step => step.Kind == GateStepKind.RoutePolicy);

    private sealed class Fixture : IDisposable
    {
        private readonly SnapshotStore _store = new();
        private readonly GateMiddleware _gate;
        private readonly TlsHandshakeSelector _handshakes;

        public Fixture(ProxyConfigModel model, TestPki pki)
        {
            Challenges.Publish("tok-en_1", "tok-en_1.thumbprint");
            ProofOfWork = new ProofOfWorkRuntime(new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));
            ProofOfWork.UseKey(new TestMacKey());
            var shared = pki.Leaf("shared", "*.test", "*.app.test", "app.test");
            Snapshot = Bound(model, options: new SnapshotCompilerOptions
            {
                Certificates = new Single(shared),
                PublicHttpsPort = 8443,
                AdminUpstream = true,
                ProofOfWork = ProofOfWork,
            });
            _store.Publish(Snapshot);
            _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource(), Challenges, ProofOfWork);
            _handshakes = new TlsHandshakeSelector(_store);
        }

        public RuntimeSnapshot Snapshot { get; }

        /// <summary>The proof-of-work key and clock both sides use: the real gate and the simulator.</summary>
        public ProofOfWorkRuntime ProofOfWork { get; }

        /// <summary>The live HTTP-01 answers, shared by the real gate and the simulations.</summary>
        public AcmeChallengeStore Challenges { get; } = new();

        /// <summary>Every reason the simulations gave, so a test can see it covered the gate's answers.</summary>
        public HashSet<GateReason> Reasons { get; } = [];

        public void Dispose() => _store.Dispose();

        /// <summary>The simulator's answer alone, for a test that reads the trace.</summary>
        public GateSimulation Simulate(Wire wire) => GateSimulator.Run(Snapshot, ProbeOf(wire), Challenges, proofOfWork: ProofOfWork);

        private static GateProbe ProbeOf(Wire wire)
        {
            GateProbe probe = new()
            {
                Https = wire.Https,
                Host = wire.Host,
                Path = wire.Path,
                Query = wire.Query,
                Method = wire.Method,
                Headers = [.. wire.Headers.Select(h => new ProbeHeader(h.Name, h.Value))],
                ClientAddress = wire.Peer,
                PeerAddress = wire.Peer,
                ProxyProtocol = wire.ProxyProtocol,
                TransportAddress = wire.ProxyProtocol ? IPAddress.Parse("192.0.2.77") : null,
                HttpVersion = wire.Version,
                TlsVersion = wire.TlsVersion == SslProtocols.None ? SslProtocols.Tls13 : wire.TlsVersion,
                ServerName = wire.ServerName,
            };
            if (wire.HostHeaders is not null)
            {
                probe = probe with { HostHeaders = wire.HostHeaders };
            }

            return probe;
        }

        /// <summary>Runs the request both ways and asserts the outcomes are equal. Returns 1 when it reached the gate, else 0.</summary>
        public async Task<int> CompareAsync(Wire wire, string? note = null)
        {
            var probe = ProbeOf(wire);

            var simulated = GateSimulator.Run(Snapshot, probe, challenges: Challenges, proofOfWork: ProofOfWork);
            Reasons.Add(simulated.Outcome.Reason);
            var label = $"{note} {(wire.Https ? "https" : "http")} {wire.Version} {wire.TlsVersion} {wire.Method} {wire.Host}{wire.Path} from {wire.Peer} via {string.Join(",", wire.Headers)}";

            // What the handshake would do, read off the real selector: a request behind a failed handshake never exists.
            var (reached, negotiated, tlsVersion) = Handshake(wire, probe);
            if (!reached)
            {
                Assert.True(simulated.Outcome.Reason == GateReason.HandshakeRejected, $"{label}: the handshake fails, the simulation said {simulated.Outcome}");
                Assert.Equal(GateOutcomeAction.Abort, simulated.Outcome.Action);
                return 0;
            }

            Assert.True(simulated.Outcome.Reason != GateReason.HandshakeRejected, $"{label}: the handshake works, the simulation said {simulated.Outcome}");

            // Kestrel's own 400s come before the gate: a Host that isn't exactly one valid value on HTTP/1.1.
            var badHost = negotiated == ProbeHttpVersion.Http11 && wire.HostHeaders is { } sent
                                                                && (sent.Count != 1 || sent[0].IndexOfAny(['@', '/', '\\', ' ']) >= 0);
            Assert.True(
                badHost == (simulated.Outcome.Reason == GateReason.RejectedByServer),
                $"{label}: the server {(badHost ? "rejects" : "accepts")} the Host, the simulation said {simulated.Outcome}");
            if (badHost)
            {
                Assert.Equal(400, simulated.Outcome.StatusCode);
                return 0;
            }

            var (action, status, location, hsts, server) = await RealAsync(wire, negotiated, tlsVersion);
            Assert.True(
                action == simulated.Outcome.Action && status == simulated.Outcome.StatusCode && location == simulated.Outcome.Location,
                $"{label}: the real gate gave {action} {status} {location}, the simulation {simulated.Outcome.Action} {simulated.Outcome.StatusCode} {simulated.Outcome.Location} ({simulated.Outcome.Reason})");
            Assert.NotEmpty(simulated.Steps);
            Assert.Equal(action == GateOutcomeAction.Forward, simulated.Upstream is not null);

            // The one response-headers step says what each header carries, so a "why" view can't disagree with the wire.
            // A dropped request has no answer, so no step at all.
            var headers = simulated.Steps.FirstOrDefault(s => s.Kind == GateStepKind.ResponseHeaders);
            var simulatedHsts = headers is null ? null : ValueOf(headers, "Strict-Transport-Security");
            Assert.True((hsts ?? string.Empty) == (simulatedHsts ?? string.Empty), $"{label}: the response carried HSTS '{hsts}', the trace said '{simulatedHsts}'");

            // The same step says what the gate sets for Server, which is all a gate answer can have (no upstream here).
            var simulatedServer = headers is null ? null : ValueOf(headers, "Server");
            Assert.True((server ?? string.Empty) == (simulatedServer ?? string.Empty), $"{label}: the response carried Server '{server}', the trace said '{simulatedServer}'");
            return 1;
        }

        private (bool Reached, ProbeHttpVersion Negotiated, SslProtocols Tls) Handshake(Wire wire, GateProbe probe)
        {
            if (!wire.Https)
            {
                return (true, probe.HttpVersion, SslProtocols.None);
            }

            var sni = wire.ServerName ?? (wire.Host.Length == 0 || wire.Host[0] == '[' ? string.Empty : HostTable.HostName(wire.Host).ToString());
            var quic = wire.Version == ProbeHttpVersion.Http3;
            SslServerAuthenticationOptions options;
            try
            {
                options = quic ? _handshakes.SelectQuic(sni, null, null) : _handshakes.SelectTcp(sni);
            }
            catch (TlsHandshakeRejectedException)
            {
                return (false, wire.Version, SslProtocols.None);
            }

            if (quic)
            {
                return (true, wire.Version, SslProtocols.Tls13);
            }

            if ((options.EnabledSslProtocols & probe.TlsVersion) == 0)
            {
                return (false, wire.Version, SslProtocols.None);
            }

            var h2 = options.ApplicationProtocols!.Contains(SslApplicationProtocol.Http2);
            return (true, wire.Version == ProbeHttpVersion.Http2 && !h2 ? ProbeHttpVersion.Http11 : wire.Version, probe.TlsVersion);
        }

        private async Task<(GateOutcomeAction Action, int? Status, string? Location, string? Hsts, string? Server)> RealAsync(Wire wire, ProbeHttpVersion version, SslProtocols tls)
        {
            var target = wire.Path + (wire.Query is null ? string.Empty : "?" + wire.Query);
            var context = TestContexts.Create(
                wire.HostHeaders is not null || wire.Host.Length == 0 ? null : wire.Host,
                wire.Path,
                wire.Method,
                wire.Https ? "https" : "http",
                wire.ProxyProtocol ? IPAddress.Parse("192.0.2.77") : wire.Peer,
                target);
            context.Request.Protocol = version switch
            {
                ProbeHttpVersion.Http3 => "HTTP/3",
                ProbeHttpVersion.Http2 => "HTTP/2",
                _ => "HTTP/1.1",
            };
            foreach (var (name, value) in wire.Headers)
            {
                context.Request.Headers.Append(name, value);
            }

            foreach (var host in wire.HostHeaders ?? [])
            {
                context.Request.Headers.Append("Host", host);
            }

            if (wire.ProxyProtocol)
            {
                var peer = IpKey.FromIPAddress(wire.Peer);
                context.Features.Set<IClientInfoFeature>(new ClientInfo(peer, 40000, ConnectionAddressSource.ProxyProtocol, IpKey.FromIPAddress(IPAddress.Parse("192.0.2.77")), 40000));
            }

            if (wire.Https)
            {
                context.Features.Set<ITlsHandshakeFeature>(new FakeTls(tls));
            }

            Lifetime lifetime = new();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            var forwarded = false;
            await _gate.InvokeAsync(context, _ =>
            {
                forwarded = true;
                return Task.CompletedTask;
            });

            if (forwarded)
            {
                return (GateOutcomeAction.Forward, null, null, HstsOf(context), ServerOf(context));
            }

            if (lifetime.Aborted)
            {
                return (GateOutcomeAction.Abort, null, null, HstsOf(context), ServerOf(context));
            }

            var location = context.Response.Headers.Location.ToString();
            return (GateOutcomeAction.Respond, context.Response.StatusCode, context.Response.StatusCode / 100 == 3 && location.Length > 0 ? location : null, HstsOf(context), ServerOf(context));
        }

        /// <summary>The value the trace's response-headers step gives <paramref name="name"/>, or null when it isn't sent.</summary>
        private static string? ValueOf(GateStep step, string name)
        {
            var value = step.Facts.FirstOrDefault(f => f.Name == name).Value;
            return string.IsNullOrEmpty(value) || value.StartsWith("not sent", StringComparison.Ordinal) || value.StartsWith("removed", StringComparison.Ordinal)
                ? null
                : value;
        }

        /// <summary>The <c>Strict-Transport-Security</c> the real response carries, or null.</summary>
        private static string? HstsOf(HttpContext context) =>
            context.Response.Headers.TryGetValue("Strict-Transport-Security", out var value) && value.Count > 0
                ? value.ToString()
                : null;

        private static string? ServerOf(HttpContext context) =>
            context.Response.Headers.TryGetValue("Server", out var value) && value.Count > 0
                ? value.ToString()
                : null;
    }

    private sealed class Lifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }

        public CancellationToken RequestAborted { get; set; }

        public void Abort() => Aborted = true;
    }

    // The interface still carries the members SslStream obsoleted; nothing reads them.
#pragma warning disable SYSLIB0058
    private sealed class FakeTls(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol { get; } = protocol;

        public TlsCipherSuite? NegotiatedCipherSuite => null;

        public string HostName => string.Empty;

        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.None;

        public int CipherStrength => 0;

        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.None;

        public int HashStrength => 0;

        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;

        public int KeyExchangeStrength => 0;
    }
#pragma warning restore SYSLIB0058

    private sealed class Single(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
