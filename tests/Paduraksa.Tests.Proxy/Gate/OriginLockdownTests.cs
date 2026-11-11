using System.Net;
using System.Net.Security;
using System.Security.Authentication;

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

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// Origin lockdown: a host that only takes Cloudflare's connections refuses any other peer before anything else of its chain,
/// answering 403 or dropping, with the trust decision <see cref="ClientIpResolution"/> already makes. The simulator reaches the
/// same verdicts, and the refused and the allowed path allocate nothing.
/// </summary>
public sealed class OriginLockdownTests : IDisposable
{
    private static readonly IPAddress Edge = IPAddress.Parse("173.245.48.1");
    private static readonly IPAddress Outsider = IPAddress.Parse("198.51.100.9");
    private static readonly IPAddress Cloudflared = IPAddress.Parse("172.18.0.5");

    private static readonly HostCloudflareSpec Forbid = new() { OriginLockdown = OriginLockdownMode.Forbid };
    private static readonly HostCloudflareSpec Drop = new() { OriginLockdown = OriginLockdownMode.Drop };

    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    private static ProxyConfigModel Model(TrustedProxySpec proxies, params HostSpec[] extra) => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Defaults = new DefaultsSpec { TrustedProxies = proxies },
        Hosts =
        [
            ProxyHost("forbid", "forbid.test", Route("forbid-root", "web")) with { Cloudflare = Forbid },
            ProxyHost("drop", "drop.test", Route("drop-root", "web")) with { Cloudflare = Drop },
            ProxyHost("open", "open.test", Route("open-root", "web")),
            .. extra,
        ],
    };

    private static readonly TrustedProxySpec CloudflareProxy = new() { Mode = TrustedProxyMode.CloudflareProxy };
    private static readonly TrustedProxySpec CloudflareTunnel = new() { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["172.18.0.0/16"] };

    private static GateDecision Decide(RuntimeSnapshot snapshot, string host, IPAddress peer, string path = "/", string scheme = "http")
    {
        using SnapshotStore store = new();
        store.Publish(snapshot);
        return new GateMiddleware(store, new TrustedProxyClientAddressSource()).Decide(TestContexts.Create(host, path, "GET", scheme, peer));
    }

    [Theory]
    [InlineData(TrustedProxyMode.CloudflareProxy, OriginLockdownMode.Forbid, OriginLockdownMode.Forbid)]
    [InlineData(TrustedProxyMode.CloudflareTunnel, OriginLockdownMode.Drop, OriginLockdownMode.Drop)]
    [InlineData(TrustedProxyMode.Generic, OriginLockdownMode.Forbid, OriginLockdownMode.Off)]
    [InlineData(TrustedProxyMode.Off, OriginLockdownMode.Drop, OriginLockdownMode.Off)]
    public void TheSettingOnlyTakesEffectUnderACloudflareMode(TrustedProxyMode mode, OriginLockdownMode set, OriginLockdownMode compiled)
    {
        TrustedProxySpec proxies = new() { Mode = mode, Proxies = mode is TrustedProxyMode.Generic or TrustedProxyMode.CloudflareTunnel ? ["10.0.0.0/8"] : [] };
        var snapshot = Compile(Model(proxies, ProxyHost("set", "set.test", Route("set-root", "web")) with { Cloudflare = new HostCloudflareSpec { OriginLockdown = set } }));

        Assert.Equal(compiled, snapshot.Hosts.Find("set.test")!.OriginLockdown);
    }

    [Fact]
    public void AnUndefinedModeIsACompileError()
    {
        var result = SnapshotCompiler.Compile(Model(
            CloudflareProxy, ProxyHost("odd", "odd.test", Route("odd-root", "web")) with { Cloudflare = new HostCloudflareSpec { OriginLockdown = (OriginLockdownMode)9 } }));

        Assert.Contains(result.Errors, e => e.Message.Contains("unknown origin lockdown mode 9", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUntrustedPeerIsAnsweredWithTheBuiltInForbiddenPageOrDropped()
    {
        var snapshot = Bound(Model(CloudflareProxy));

        var forbidden = Decide(snapshot, "forbid.test", Outsider);
        Assert.Equal(GateAction.Respond, forbidden.Action);
        Assert.Equal(403, forbidden.Response!.StatusCode);
        Assert.NotNull(forbidden.Response.HtmlPage);
        Assert.Equal("forbidden\n", System.Text.Encoding.UTF8.GetString(forbidden.Response.Body));

        Assert.Equal(GateAction.Abort, Decide(snapshot, "drop.test", Outsider).Action);
    }

    [Fact]
    public void ACloudflareEdgePeerGoesOnAndAHostWithoutLockdownTakesAnyone()
    {
        var snapshot = Bound(Model(CloudflareProxy));

        Assert.Equal(GateAction.Forward, Decide(snapshot, "forbid.test", Edge).Action);
        Assert.Equal(GateAction.Forward, Decide(snapshot, "drop.test", Edge).Action);
        Assert.Equal(GateAction.Forward, Decide(snapshot, "open.test", Outsider).Action);
    }

    [Fact]
    public void InTunnelModeOnlyTheConfiguredCloudflaredIsTrustedNotTheEdge()
    {
        var snapshot = Bound(Model(CloudflareTunnel));

        Assert.Equal(GateAction.Forward, Decide(snapshot, "forbid.test", Cloudflared).Action);
        Assert.Equal(GateAction.Respond, Decide(snapshot, "forbid.test", Edge).Action);
        Assert.Equal(GateAction.Abort, Decide(snapshot, "drop.test", Outsider).Action);
    }

    [Fact]
    public void ThePeerIsTheProxyProtocolSourceWhenThereIsOne()
    {
        var snapshot = Bound(Model(CloudflareProxy));
        using SnapshotStore store = new();
        store.Publish(snapshot);
        var gate = new GateMiddleware(store, new TrustedProxyClientAddressSource());

        // The socket is a load balancer, the PROXY header names the peer behind it: the header's address is judged.
        foreach (var (source, expected) in new[] { (Edge, GateAction.Forward), (Outsider, GateAction.Respond) })
        {
            var context = TestContexts.Create("forbid.test", remote: IPAddress.Parse("192.0.2.77"));
            context.Features.Set<IClientInfoFeature>(new ClientInfo(IpKey.FromIPAddress(source), 40000, ConnectionAddressSource.ProxyProtocol, IpKey.FromIPAddress(IPAddress.Parse("192.0.2.77")), 40000));
            Assert.Equal(expected, gate.Decide(context).Action);
        }
    }

    [Fact]
    public void ForgedCloudflareHeadersFromAnUntrustedPeerChangeNothing()
    {
        var snapshot = Bound(Model(CloudflareProxy));
        using SnapshotStore store = new();
        store.Publish(snapshot);
        var gate = new GateMiddleware(store, new TrustedProxyClientAddressSource());
        var context = TestContexts.Create("forbid.test", remote: Outsider);
        context.Request.Headers["CF-Connecting-IP"] = "173.245.48.1";
        context.Request.Headers["X-Forwarded-For"] = "173.245.48.1";

        Assert.Equal(GateAction.Respond, gate.Decide(context).Action);
    }

    [Fact]
    public void TheLockdownComesBeforeTheTlsCheckThePoliciesAndTheRedirect()
    {
        var snapshot = Bound(Model(
            CloudflareProxy,
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with
            {
                Cloudflare = Forbid,
                ForceHttps = true,
                Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
                Policies = [new DenyPolicySpec(451, "legal\n")],
            }));

        // Cloudflare itself would meet the 451 policy over plain HTTP and the 421 of a connection weaker than TLS 1.3 over
        // HTTPS (a test connection reports no TLS version). A stranger gets the lockdown's 403 either way, and learns nothing
        // else about the host.
        Assert.Equal(451, Decide(snapshot, "strict.test", Edge).Response!.StatusCode);
        Assert.Equal(421, Decide(snapshot, "strict.test", Edge, scheme: "https").Response!.StatusCode);
        Assert.Equal(403, Decide(snapshot, "strict.test", Outsider).Response!.StatusCode);
        Assert.Equal(403, Decide(snapshot, "strict.test", Outsider, scheme: "https").Response!.StatusCode);
    }

    [Fact]
    public void TheRefusalCarriesTheHostsServerHeader()
    {
        var snapshot = Bound(Model(
            CloudflareProxy,
            ProxyHost("named", "named.test", Route("named-root", "web")) with
            {
                Cloudflare = Forbid,
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/1" },
            }));
        using SnapshotStore store = new();
        store.Publish(snapshot);
        var context = TestContexts.Create("named.test", remote: Outsider);

        _ = new GateMiddleware(store, new TrustedProxyClientAddressSource()).Decide(context);

        Assert.Equal("edge/1", context.Response.Headers.Server.ToString());
    }

    [Fact]
    public void UnknownHostsAreTheDefaultBehaviourNotALockdown()
    {
        var snapshot = Bound(Model(CloudflareProxy));

        Assert.Equal(404, Decide(snapshot, "nobody.test", Outsider).Response!.StatusCode);
    }

    [Fact]
    public void AnAcmeChallengeIsAnsweredForAnyPeerBeforeTheLockdownApplies()
    {
        var snapshot = Bound(Model(CloudflareProxy));
        using SnapshotStore store = new();
        store.Publish(snapshot);
        AcmeChallengeStore challenges = new();
        challenges.Publish("tok", "tok.thumb");
        var gate = new GateMiddleware(store, new TrustedProxyClientAddressSource(), challenges);

        var answered = gate.Decide(TestContexts.Create("forbid.test", "/.well-known/acme-challenge/tok", remote: Outsider));
        Assert.Equal(200, answered.Response!.StatusCode);

        // Any other path of the host is refused as before, and so is a token that isn't waiting.
        Assert.Equal(403, gate.Decide(TestContexts.Create("forbid.test", "/", remote: Outsider)).Response!.StatusCode);
        Assert.Equal(403, gate.Decide(TestContexts.Create("forbid.test", "/.well-known/acme-challenge/other", remote: Outsider)).Response!.StatusCode);
    }

    [Theory]
    [InlineData("forbid.test")]
    [InlineData("drop.test")]
    public void RefusedAndAllowedDecisionsAllocateNothing(string host)
    {
        var snapshot = Bound(Model(CloudflareProxy));
        using SnapshotStore store = new();
        store.Publish(snapshot);
        var gate = new GateMiddleware(store, new TrustedProxyClientAddressSource());
        var refused = TestContexts.Create(host, remote: Outsider);
        var allowed = TestContexts.Create(host, remote: Edge);

        AllocAssert.Zero((gate, refused), static s => _ = s.gate.Decide(s.refused), $"lockdown refusing {host}");
        AllocAssert.Zero((gate, allowed), static s => _ = s.gate.Decide(s.allowed), $"lockdown allowing {host}");
    }

    [Fact]
    public void EveryHostLockedDownMeansTheConnectionStageDecidesOnce()
    {
        Assert.False(Compile(Model(CloudflareProxy)).LockdownAtConnection, "an open host is left");
        Assert.False(Compile(new ProxyConfigModel { Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy } }).LockdownAtConnection, "no hosts, nothing to lock");

        ProxyConfigModel all = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy },
            Hosts =
            [
                ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid },
                ProxyHost("b", "b.test", Route("b-root", "web")) with { Cloudflare = Drop },
            ],
        };
        Assert.True(Compile(all).LockdownAtConnection);

        // A disabled host serves nothing, so it doesn't count; an enabled open one does.
        var withDisabledOpen = all with { Hosts = [.. all.Hosts, ProxyHost("c", "c.test", Route("c-root", "web")) with { Enabled = false }] };
        Assert.True(Compile(withDisabledOpen).LockdownAtConnection);
        var withOpen = all with { Hosts = [.. all.Hosts, ProxyHost("c", "c.test", Route("c-root", "web"))] };
        Assert.False(Compile(withOpen).LockdownAtConnection);

        // An admin host is a host like any other.
        var withAdmin = all with { Hosts = [.. all.Hosts, new HostSpec { Id = TestIds.Host("admin"), Names = ["admin.test"], Kind = HostKind.Admin, ForceHttps = true }] };
        Assert.False(Compile(withAdmin, new SnapshotCompilerOptions { AdminUpstream = true }).LockdownAtConnection);
        var lockedAdmin = withAdmin with { Hosts = [.. all.Hosts, new HostSpec { Id = TestIds.Host("admin"), Names = ["admin.test"], Kind = HostKind.Admin, ForceHttps = true, Cloudflare = Forbid }] };
        Assert.True(Compile(lockedAdmin, new SnapshotCompilerOptions { AdminUpstream = true }).LockdownAtConnection);

        // Not under a mode with no trusted Cloudflare peer.
        Assert.False(Compile(all with { Defaults = new DefaultsSpec() }).LockdownAtConnection);
    }

    [Fact]
    public void TheConnectionStageRefusesOnlyAnUntrustedPeerOfAFullyLockedConfig()
    {
        var snapshot = Compile(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy },
            Hosts = [ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid }],
        });

        Assert.True(OriginLockdown.RefusesBeforeTls(snapshot, IpKey.FromIPAddress(Outsider)));
        Assert.True(OriginLockdown.RefusesBeforeTls(snapshot, IpKey.Parse("::1")));
        Assert.False(OriginLockdown.RefusesBeforeTls(snapshot, IpKey.FromIPAddress(Edge)));
        Assert.False(OriginLockdown.RefusesBeforeTls(snapshot, IpKey.Parse("2606:4700::1")));
        Assert.False(OriginLockdown.RefusesBeforeTls(Compile(Model(CloudflareProxy)), IpKey.FromIPAddress(Outsider)));
        AllocAssert.Zero((snapshot, IpKey.FromIPAddress(Outsider)), static s => _ = OriginLockdown.RefusesBeforeTls(s.snapshot, s.Item2), "connection-stage check");
    }

    [Fact]
    public void TheQuicGateRefusesAnUntrustedPeerBeforeTheHandshakeContinues()
    {
        using SnapshotStore store = new();
        var snapshot = Bound(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy },
            Hosts = [ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid }],
        });
        store.Publish(snapshot);
        OriginLockdownCounters counters = new();
        var gate = OriginLockdown.CreateQuicGate(store, counters);

        Assert.True(gate(new IPEndPoint(Edge, 40000), null));
        Assert.False(gate(new IPEndPoint(Outsider, 40000), null));
        Assert.False(gate(new IPEndPoint(IPAddress.IPv6Loopback, 40000), null));
        Assert.Equal(2, counters.Quic);

        // A new publish is read by the next handshake.
        store.Publish(Bound(Model(CloudflareProxy), generation: 2));
        Assert.True(gate(new IPEndPoint(Outsider, 40000), null));
    }

    [Fact]
    public async Task ARealTcpListenerDropsAnUntrustedPeerBeforeAnyBytesAreRead()
    {
        var model = new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy },
            Hosts = [ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid }],
        };
        OriginLockdownCounters counters = new();
        using var untrusted = new HttpClient();

        // Loopback isn't Cloudflare: the connection is aborted, so no HTTP answer exists.
        await using (var server = await ProxyTestServer.StartAsync(model, new() { ClientInfo = true, OriginLockdown = counters }))
        {
            using var request = server.Request("a.test", "/");

            // The abort can land while the client is still connecting, which SocketsHttpHandler reports as the socket's own error.
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => untrusted.SendAsync(request, TestContext.Current.CancellationToken));
            Assert.True(failure is HttpRequestException or System.Net.Sockets.SocketException, failure.ToString());
            Assert.True(counters.Tcp >= 1);
        }

        // Loopback standing in for Cloudflare's edge: the connection goes on, and the gate answers its own way.
        SnapshotCompilerOptions options = new() { CloudflareEdgeRanges = CidrSet.Parse(["127.0.0.0/8"]) };
        await using (var server = await ProxyTestServer.StartAsync(model, new() { ClientInfo = true, OriginLockdown = new OriginLockdownCounters(), CompilerOptions = options }))
        {
            using var request = server.Request("a.test", "/");
            using var response = await untrusted.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public void TheSimulatorRefusesAndAllowsLikeTheGateInBothDirections()
    {
        var shared = _pki.Leaf("shared", "*.test");
        var certificateModel = Model(CloudflareProxy) with { Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy, Certificate = TestIds.Certificate("shared") } };
        var snapshot = Bound(certificateModel, options: new SnapshotCompilerOptions { Certificates = new Single(shared) });
        using SnapshotStore store = new();
        store.Publish(snapshot);
        var gate = new GateMiddleware(store, new TrustedProxyClientAddressSource());

        foreach (var host in new[] { "forbid.test", "drop.test", "open.test", "nobody.test" })
        {
            foreach (var peer in new[] { Edge, Outsider, IPAddress.Loopback, IPAddress.Parse("2606:4700::7") })
            {
                var real = gate.Decide(TestContexts.Create(host, remote: peer));
                var simulated = GateSimulator.Run(snapshot, new GateProbe { Host = host, ClientAddress = peer, PeerAddress = peer });

                var label = $"{host} from {peer}";
                var action = real.Action switch { GateAction.Forward => GateOutcomeAction.Forward, GateAction.Abort => GateOutcomeAction.Abort, _ => GateOutcomeAction.Respond };
                Assert.True(action == simulated.Outcome.Action, $"{label}: gate {real.Action}, simulation {simulated.Outcome}");
                if (real.Response is { } response)
                {
                    Assert.Equal(response.StatusCode, simulated.Outcome.StatusCode);
                }
            }
        }

        // Via Cloudflare the simulator's own peer is an edge address; direct, it is the client's.
        var viaCloudflare = GateSimulator.Run(snapshot, new GateProbe { Host = "forbid.test", ClientAddress = Outsider, Arrival = ProbeArrival.CloudflareProxy });
        Assert.Equal(GateOutcomeAction.Forward, viaCloudflare.Outcome.Action);
        var direct = GateSimulator.Run(snapshot, new GateProbe { Host = "forbid.test", ClientAddress = Outsider });
        Assert.Equal((GateOutcomeAction.Respond, GateReason.OriginLockdown, 403), (direct.Outcome.Action, direct.Outcome.Reason, direct.Outcome.StatusCode));
        var step = Assert.Single(direct.Steps, s => s.Kind == GateStepKind.OriginLockdown);
        Assert.Equal(GateStepVerdict.Stopped, step.Verdict);
        Assert.Equal($"peer {Outsider} is not a Cloudflare edge address: refused (403)", step.Summary);
        Assert.Equal(GateOutcomeAction.Abort, GateSimulator.Run(snapshot, new GateProbe { Host = "drop.test", ClientAddress = Outsider }).Outcome.Action);

        var passed = Assert.Single(viaCloudflare.Steps, s => s.Kind == GateStepKind.OriginLockdown);
        Assert.Equal(GateStepVerdict.Passed, passed.Verdict);
        Assert.DoesNotContain(GateSimulator.Run(snapshot, new GateProbe { Host = "open.test", ClientAddress = Outsider }).Steps, s => s.Kind == GateStepKind.OriginLockdown);
    }

    [Fact]
    public void TheSimulatorDropsAtTheConnectionStageOnlyWhenEveryHostIsLockedDownAndOverTls()
    {
        var shared = _pki.Leaf("shared", "*.test");
        var all = new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy, Certificate = TestIds.Certificate("shared") },
            Hosts = [ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid }],
        };
        var snapshot = Bound(all, options: new SnapshotCompilerOptions { Certificates = new Single(shared) });
        ListenerFacts listeners = new(null, null, Http3: true);

        var tls = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "a.test", ClientAddress = Outsider, PeerAddress = Outsider, TlsVersion = SslProtocols.Tls13 }, listeners: listeners);
        Assert.Equal((GateOutcomeAction.Abort, GateReason.ConnectionRejected), (tls.Outcome.Action, tls.Outcome.Reason));
        Assert.Contains("dropped before TLS", Assert.Single(tls.Steps).Summary, StringComparison.Ordinal);

        var quic = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "a.test", ClientAddress = Outsider, PeerAddress = Outsider, HttpVersion = ProbeHttpVersion.Http3 }, listeners: listeners);
        Assert.Equal(GateReason.ConnectionRejected, quic.Outcome.Reason);

        // Plain HTTP is the gate's to decide, so an ACME validation can still arrive.
        var plain = GateSimulator.Run(snapshot, new GateProbe { Host = "a.test", ClientAddress = Outsider, PeerAddress = Outsider }, listeners: listeners);
        Assert.Equal((GateOutcomeAction.Respond, GateReason.OriginLockdown, 403), (plain.Outcome.Action, plain.Outcome.Reason, plain.Outcome.StatusCode));

        var edge = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "a.test", ClientAddress = Outsider, Arrival = ProbeArrival.CloudflareProxy }, listeners: listeners);
        Assert.Equal(GateOutcomeAction.Forward, edge.Outcome.Action);

        // With one open host the connection is let through and the host decides.
        var mixed = Bound(Model(CloudflareProxy) with { Defaults = all.Defaults }, options: new SnapshotCompilerOptions { Certificates = new Single(shared) });
        var open = GateSimulator.Run(mixed, new GateProbe { Https = true, Host = "forbid.test", ClientAddress = Outsider, PeerAddress = Outsider }, listeners: listeners);
        Assert.Equal(GateReason.OriginLockdown, open.Outcome.Reason);
    }

    [Fact]
    public void ForQuicOnAFullyLockedConfigTheNameSelectionComesBeforeTheLockdownAsInTheHandshakeCallback()
    {
        var shared = _pki.Leaf("shared", "*.test");
        var id = TestIds.Certificate("shared");
        var model = new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Defaults = new DefaultsSpec { TrustedProxies = CloudflareProxy },
            Hosts =
            [
                ProxyHost("a", "a.test", Route("a-root", "web")) with { Cloudflare = Forbid, Certificate = id },
                ProxyHost("noh3", "noh3.test", Route("noh3-root", "web")) with { Cloudflare = Forbid, Certificate = id, Tls = new HostTlsSpec { Http3 = false } },
            ],
        };
        var snapshot = Bound(model, options: new SnapshotCompilerOptions { Certificates = new Single(shared) });
        Assert.True(snapshot.LockdownAtConnection);
        using SnapshotStore store = new();
        store.Publish(snapshot);
        TlsHandshakeSelector selector = new(store, OriginLockdown.CreateQuicGate(store));
        ListenerFacts listeners = new(null, null, Http3: true);

        foreach (var (name, expected) in new[]
        {
            ("unknown.test", TlsRejectionReason.UnknownServerName),
            ("noh3.test", TlsRejectionReason.Http3Disabled),
            ("a.test", TlsRejectionReason.ConnectionDenied),
        })
        {
            // Production: the selection throws with the first thing that is wrong.
            var thrown = Assert.Throws<TlsHandshakeRejectedException>(() => selector.SelectQuic(name, new IPEndPoint(Outsider, 40000), null));
            Assert.Equal(expected, thrown.Reason);

            var run = GateSimulator.Run(
                snapshot,
                new GateProbe { Https = true, Host = name, ClientAddress = Outsider, PeerAddress = Outsider, HttpVersion = ProbeHttpVersion.Http3, ServerName = name },
                listeners: listeners);
            var reason = expected == TlsRejectionReason.ConnectionDenied ? GateReason.ConnectionRejected : GateReason.HandshakeRejected;
            Assert.Equal((GateOutcomeAction.Abort, reason), (run.Outcome.Action, run.Outcome.Reason));
            Assert.Equal(expected == TlsRejectionReason.ConnectionDenied, run.Steps.Any(step => step.Kind == GateStepKind.Connection && step.Verdict == GateStepVerdict.Stopped));
            var handshake = run.Steps.ToList().FindIndex(step => step.Kind == GateStepKind.TlsHandshake);
            Assert.True(handshake >= 0, "the handshake step is reported");
            Assert.True(run.Steps.ToList().FindIndex(step => step.Kind == GateStepKind.Connection && step.Verdict == GateStepVerdict.Stopped) is var dropped && (dropped < 0 || dropped > handshake));
        }
    }

    [Fact]
    public void TheLockdownStepNamesTheConfiguredCloudflaredAddressInTunnelMode()
    {
        var snapshot = Bound(Model(CloudflareTunnel));

        var refused = GateSimulator.Run(snapshot, new GateProbe { Host = "forbid.test", ClientAddress = Outsider });
        Assert.Equal($"peer {Outsider} is not the configured cloudflared address: refused (403)", Assert.Single(refused.Steps, s => s.Kind == GateStepKind.OriginLockdown).Summary);
        var allowed = GateSimulator.Run(snapshot, new GateProbe { Host = "forbid.test", ClientAddress = Outsider, Arrival = ProbeArrival.CloudflareTunnel });
        Assert.Contains("is the configured cloudflared address: allowed", Assert.Single(allowed.Steps, s => s.Kind == GateStepKind.OriginLockdown).Summary, StringComparison.Ordinal);
    }

    private sealed class Single(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
