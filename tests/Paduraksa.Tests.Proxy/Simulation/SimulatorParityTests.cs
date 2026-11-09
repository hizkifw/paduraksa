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
/// PLAN §7.7 parity: the simulator's outcome equals the real gate's for the same request, over every kind of answer
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
        "127.0.0.1", "127.0.0.1:8443", string.Empty,
    ];

    private static readonly string[] Paths = ["/", "/api/x", "/health", "/health/x", "/blocked", "/drop", "/API/../api", "/%2Fapi", "/nothing", "/a;b/api"];

    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    [Theory]
    [InlineData(UnknownHostMode.NotFound)]
    [InlineData(UnknownHostMode.MisdirectedRequest)]
    [InlineData(UnknownHostMode.Close)]
    public async Task EveryHostAndRouteOutcomeMatches(UnknownHostMode unknownHost)
    {
        using Fixture fixture = new(Model(new DefaultsSpec { UnknownHost = unknownHost, Certificate = TestIds.Certificate("shared") }), _pki);
        int compared = 0;
        foreach (string host in Hosts)
        {
            foreach ((bool https, ProbeHttpVersion version, SslProtocols tls) in Connections)
            {
                foreach (string path in Paths)
                {
                    foreach (IPAddress client in (IPAddress[])[Lan, Outsider])
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
        foreach (GateReason reason in expected)
        {
            Assert.Contains(reason, fixture.Reasons);
        }
    }

    [Fact]
    public async Task MethodsQueryAndRepeatedHostHeadersMatch()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        foreach (string method in (string[])["GET", "HEAD", "POST", "DELETE"])
        {
            Assert.Equal(1, await fixture.CompareAsync(new Wire("secure.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/p q", Lan) { Method = method, Query = "a=1&b=%20" }));
            Assert.Equal(1, await fixture.CompareAsync(new Wire("old.test", true, ProbeHttpVersion.Http2, SslProtocols.Tls13, "/deep/path", Lan) { Method = method, Query = "x=y" }));
        }

        // Two Host headers, none, or one that isn't a host: Kestrel answers 400 before the gate (real sockets pin that in
        // SimulatorSocketParityTests), so there is no gate answer to compare.
        foreach (IReadOnlyList<string> hosts in (IReadOnlyList<string>[])[["app.test", "secure.test"], [], ["a@b"], ["app.test/x"]])
        {
            Assert.Equal(0, await fixture.CompareAsync(new Wire("app.test", false, ProbeHttpVersion.Http11, SslProtocols.None, "/", Lan) { HostHeaders = hosts }));
        }
    }

    [Fact]
    public async Task ACoalescedConnectionIsMisdirectedAsTheGateSays()
    {
        using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), _pki);

        // Negotiated for lax name (SNI), asking for the stricter hosts.
        foreach (string host in (string[])["strict.test", "h1.test", "noh3.test", "plain.test", "app.test"])
        {
            foreach ((ProbeHttpVersion version, SslProtocols tls) in Coalesced)
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

        int compared = 0;
        foreach ((string name, TrustedProxySpec spec) in modes)
        {
            using Fixture fixture = new(Model(new DefaultsSpec { Certificate = TestIds.Certificate("shared"), TrustedProxies = spec }), _pki);
            foreach (IPAddress peer in (IPAddress[])[Loopback, Lan, CloudflareEdge, Outsider])
            {
                foreach ((string, string)[] headers in headerSets)
                {
                    foreach (string host in (string[])["locked.test", "secure.test", "app.test"])
                    {
                        foreach (bool proxyProtocol in (bool[])[false, true])
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

        int answered = 0;
        foreach (string host in (string[])["secure.test", "app.test", "unknown.test", "old.test", "strict.test", "locked.test"])
        {
            foreach (bool https in (bool[])[false, true])
            {
                foreach (string method in (string[])["GET", "HEAD", "POST"])
                {
                    // A published token is answered on plain HTTP before host lookup and policy; unknown, nested, or HTTPS falls through.
                    foreach (string path in (string[])["/.well-known/acme-challenge/tok-en_1", "/.well-known/acme-challenge/token123", "/.well-known/acme-challenge/tok-en_1/x", "/.well-known/acme-challenge/"])
                    {
                        foreach (IPAddress client in (IPAddress[])[Lan, Outsider])
                        {
                            await fixture.CompareAsync(new Wire(host, https, ProbeHttpVersion.Http11, SslProtocols.Tls13, path, client) { Method = method });
                        }
                    }

                    GateSimulation answer = GateSimulator.Run(fixture.Snapshot, new GateProbe { Https = https, Host = host, Method = method, Path = "/.well-known/acme-challenge/tok-en_1", ClientAddress = Outsider }, challenges: fixture.Challenges);
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

        GateSimulation run = GateSimulator.Run(fixture.Snapshot, new GateProbe { Host = "secure.test", ClientAddress = Outsider, Path = "/.well-known/acme-challenge/token123" });
        GateStep acme = Assert.Single(run.Steps, s => s.Kind == GateStepKind.AcmeChallenge);
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

        GateSimulation run = GateSimulator.Run(fixture.Snapshot, new GateProbe
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
        GateStep upstream = Assert.Single(run.Steps, step => step.Kind == GateStepKind.Upstream);
        Assert.Equal(GateStepVerdict.Passed, upstream.Verdict);
        Assert.Contains("admin app", upstream.Summary, StringComparison.Ordinal);
        Assert.Contains(upstream.Facts, fact => fact is { Name: "target", Value: "admin" });
        GateStep route = Assert.Single(run.Steps, step => step.Kind == GateStepKind.RouteMatch);
        Assert.Contains("admin route", route.Summary, StringComparison.Ordinal);
    }

    private static ProxyConfigModel Model(DefaultsSpec defaults) => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/"), Upstream("api", "http://127.0.0.1:2/", "http://127.0.0.1:3/")],
        Defaults = defaults,
        Hosts =
        [
            new HostSpec
            {
                Id = TestIds.Host("app"),
                Names = ["app.test", "*.app.test"],
                Policies = [new IpAllowListPolicySpec(["127.0.0.1", "10.0.0.5"])],
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
            new HostSpec { Id = TestIds.Host("old"), Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 } },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with { Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 } },
            ProxyHost("h1", "h1.test", Route("h1-root", "web")) with { Tls = new HostTlsSpec { TcpAlpn = TcpAlpn.Http11Only } },
            ProxyHost("noh3", "noh3.test", Route("noh3-root", "web")) with { Tls = new HostTlsSpec { Http3 = false } },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")) with { Https = false },
            ProxyHost("locked", "locked.test", Route("locked-api", "api", "/api")) with { ForceHttps = true, Policies = [new IpAllowListPolicySpec(["10.0.0.5"])] },
            ProxyHost("ipv6", "[::1]", Route("ipv6-root", "web")),
            ProxyHost("lax", "lax.test", Route("lax-root", "web")),
            new HostSpec { Id = TestIds.Host("admin"), Names = ["admin.test"], Kind = HostKind.Admin, ForceHttps = true },
        ],
    };

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

    private sealed class Fixture : IDisposable
    {
        private readonly SnapshotStore _store = new();
        private readonly GateMiddleware _gate;
        private readonly TlsHandshakeSelector _handshakes;

        public Fixture(ProxyConfigModel model, TestPki pki)
        {
            Challenges.Publish("tok-en_1", "tok-en_1.thumbprint");
            ServerCertificate shared = pki.Leaf("shared", "*.test", "*.app.test", "app.test");
            Snapshot = Bound(model, options: new SnapshotCompilerOptions { Certificates = new Single(shared), PublicHttpsPort = 8443, AdminUpstream = true });
            _store.Publish(Snapshot);
            _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource(), Challenges);
            _handshakes = new TlsHandshakeSelector(_store);
        }

        public RuntimeSnapshot Snapshot { get; }

        /// <summary>The live HTTP-01 answers, shared by the real gate and the simulations.</summary>
        public AcmeChallengeStore Challenges { get; } = new();

        /// <summary>Every reason the simulations gave, so a test can see it covered the gate's answers.</summary>
        public HashSet<GateReason> Reasons { get; } = [];

        public void Dispose() => _store.Dispose();

        /// <summary>Runs the request both ways and asserts the outcomes are equal. Returns 1 when it reached the gate, else 0.</summary>
        public async Task<int> CompareAsync(Wire wire, string? note = null)
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

            GateSimulation simulated = GateSimulator.Run(Snapshot, probe, challenges: Challenges);
            Reasons.Add(simulated.Outcome.Reason);
            string label = $"{note} {(wire.Https ? "https" : "http")} {wire.Version} {wire.TlsVersion} {wire.Method} {wire.Host}{wire.Path} from {wire.Peer} via {string.Join(",", wire.Headers)}";

            // What the handshake would do, read off the real selector: a request behind a failed handshake never exists.
            (bool reached, ProbeHttpVersion negotiated, SslProtocols tlsVersion) = Handshake(wire, probe);
            if (!reached)
            {
                Assert.True(simulated.Outcome.Reason == GateReason.HandshakeRejected, $"{label}: the handshake fails, the simulation said {simulated.Outcome}");
                Assert.Equal(GateOutcomeAction.Abort, simulated.Outcome.Action);
                return 0;
            }

            Assert.True(simulated.Outcome.Reason != GateReason.HandshakeRejected, $"{label}: the handshake works, the simulation said {simulated.Outcome}");

            // Kestrel's own 400s come before the gate: a Host that isn't exactly one valid value on HTTP/1.1.
            bool badHost = negotiated == ProbeHttpVersion.Http11 && wire.HostHeaders is { } sent
                && (sent.Count != 1 || sent[0].IndexOfAny(['@', '/', '\\', ' ']) >= 0);
            Assert.True(
                badHost == (simulated.Outcome.Reason == GateReason.RejectedByServer),
                $"{label}: the server {(badHost ? "rejects" : "accepts")} the Host, the simulation said {simulated.Outcome}");
            if (badHost)
            {
                Assert.Equal(400, simulated.Outcome.StatusCode);
                return 0;
            }

            (GateOutcomeAction action, int? status, string? location, string? hsts) = await RealAsync(wire, negotiated, tlsVersion);
            Assert.True(
                action == simulated.Outcome.Action && status == simulated.Outcome.StatusCode && location == simulated.Outcome.Location,
                $"{label}: the real gate gave {action} {status} {location}, the simulation {simulated.Outcome.Action} {simulated.Outcome.StatusCode} {simulated.Outcome.Location} ({simulated.Outcome.Reason})");
            Assert.NotEmpty(simulated.Steps);
            Assert.Equal(action == GateOutcomeAction.Forward, simulated.Upstream is not null);

            // The trace's HSTS step says what the response carries, so "why" views can't disagree with the wire.
            string? simulatedHsts = simulated.Steps
                .Where(s => s.Kind == GateStepKind.Hsts)
                .SelectMany(s => s.Facts)
                .Where(f => f.Name == "value")
                .Select(f => f.Value)
                .FirstOrDefault();
            Assert.True((hsts ?? string.Empty) == (simulatedHsts ?? string.Empty), $"{label}: the response carried HSTS '{hsts}', the trace said '{simulatedHsts}'");
            return 1;
        }

        private (bool Reached, ProbeHttpVersion Negotiated, SslProtocols Tls) Handshake(Wire wire, GateProbe probe)
        {
            if (!wire.Https)
            {
                return (true, probe.HttpVersion, SslProtocols.None);
            }

            string sni = wire.ServerName ?? (wire.Host.Length == 0 || wire.Host[0] == '[' ? string.Empty : HostTable.HostName(wire.Host).ToString());
            bool quic = wire.Version == ProbeHttpVersion.Http3;
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

            bool h2 = options.ApplicationProtocols!.Contains(SslApplicationProtocol.Http2);
            return (true, wire.Version == ProbeHttpVersion.Http2 && !h2 ? ProbeHttpVersion.Http11 : wire.Version, probe.TlsVersion);
        }

        private async Task<(GateOutcomeAction Action, int? Status, string? Location, string? Hsts)> RealAsync(Wire wire, ProbeHttpVersion version, SslProtocols tls)
        {
            string target = wire.Path + (wire.Query is null ? string.Empty : "?" + wire.Query);
            DefaultHttpContext context = TestContexts.Create(
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
            foreach ((string name, string value) in wire.Headers)
            {
                context.Request.Headers.Append(name, value);
            }

            foreach (string host in wire.HostHeaders ?? [])
            {
                context.Request.Headers.Append("Host", host);
            }

            if (wire.ProxyProtocol)
            {
                IpKey peer = IpKey.FromIPAddress(wire.Peer);
                context.Features.Set<IClientInfoFeature>(new ClientInfo(peer, 40000, ConnectionAddressSource.ProxyProtocol, IpKey.FromIPAddress(IPAddress.Parse("192.0.2.77")), 40000));
            }

            if (wire.Https)
            {
                context.Features.Set<ITlsHandshakeFeature>(new FakeTls(tls));
            }

            Lifetime lifetime = new();
            context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
            bool forwarded = false;
            await _gate.InvokeAsync(context, _ =>
            {
                forwarded = true;
                return Task.CompletedTask;
            });

            if (forwarded)
            {
                return (GateOutcomeAction.Forward, null, null, HstsOf(context));
            }

            if (lifetime.Aborted)
            {
                return (GateOutcomeAction.Abort, null, null, HstsOf(context));
            }

            string location = context.Response.Headers.Location.ToString();
            return (GateOutcomeAction.Respond, context.Response.StatusCode, context.Response.StatusCode / 100 == 3 && location.Length > 0 ? location : null, HstsOf(context));
        }

        /// <summary>The <c>Strict-Transport-Security</c> the real response carries, or null.</summary>
        private static string? HstsOf(HttpContext context) =>
            context.Response.Headers.TryGetValue("Strict-Transport-Security", out Microsoft.Extensions.Primitives.StringValues value) && value.Count > 0
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
}
