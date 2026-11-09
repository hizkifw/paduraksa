using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Authentication;

using Microsoft.AspNetCore.Http;

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

/// <summary>What the trace says about each gate step, for hand-made snapshots (the outcomes are the parity tests').</summary>
public sealed class GateSimulatorTraceTests : IDisposable
{
    private static readonly IPAddress Lan = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Outsider = IPAddress.Parse("198.51.100.1");

    private readonly TestPki _pki = TestPki.Create();

    public void Dispose() => _pki.Dispose();

    [Fact]
    public void AForwardedRequestTracesEveryStepInOrder()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });

        GateSimulation run = GateSimulator.Run(snapshot, new GateProbe
        {
            Https = true,
            Host = "x.app.test",
            Path = "/API/../api//items;x",
            Query = "a=1",
            ClientAddress = Lan,
            HttpVersion = ProbeHttpVersion.Http2,
        });

        Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
        Assert.Equal(GateReason.Forwarded, run.Outcome.Reason);
        Assert.Equal(
            [
                GateStepKind.TlsHandshake, GateStepKind.Request, GateStepKind.ClientAddress, GateStepKind.AcmeChallenge, GateStepKind.HostMatch,
                GateStepKind.TlsPolicy, GateStepKind.Hsts, GateStepKind.HostPolicy, GateStepKind.RouteMatch, GateStepKind.Upstream,
            ],
            run.Steps.Select(s => s.Kind));
        Assert.DoesNotContain(run.Steps, s => s.Verdict == GateStepVerdict.Stopped);

        GateStep host = Step(run, GateStepKind.HostMatch);
        Assert.Equal(TestIds.Host("app"), host.Host);
        Assert.Contains("wildcard", host.Summary, StringComparison.Ordinal);
        Assert.Equal("wildcard", Fact(host, "match"));

        // The matching view of the path, not the path as sent.
        GateStep route = Step(run, GateStepKind.RouteMatch);
        Assert.Equal("/API/../api//items;x", Fact(Step(run, GateStepKind.Request), "path"));
        Assert.Equal("/api//items;x", Fact(route, "path")); // after Kestrel's dot-segment removal
        Assert.Equal("/api/items", Fact(route, "matchedPath"));
        Assert.Equal(TestIds.Route("app-api"), route.Route);

        GateStep policy = Step(run, GateStepKind.HostPolicy);
        Assert.Equal(0, policy.PolicyIndex);
        Assert.Contains("allow list", policy.Summary, StringComparison.Ordinal);

        // The upstream: its destinations as configured, never a chosen one.
        GateUpstream upstream = run.Upstream!;
        Assert.Equal(TestIds.Upstream("api"), upstream.Id);
        Assert.Equal(new[] { TestIds.Destination("api-d0"), TestIds.Destination("api-d1") }.Order(), upstream.Destinations.Select(d => d.Id).Order());
    }

    [Fact]
    public void ExactAndIpv6HostMatchesAreNamed()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());

        Assert.Equal("exact", Fact(Step(Run(snapshot, "app.test"), GateStepKind.HostMatch), "match"));
        Assert.Equal("exact", Fact(Step(Run(snapshot, "APP.test:8080"), GateStepKind.HostMatch), "match"));
        Assert.Equal("ipv6Literal", Fact(Step(Run(snapshot, "[::1]"), GateStepKind.HostMatch), "match"));
    }

    [Theory]
    [InlineData(UnknownHostMode.NotFound, GateOutcomeAction.Respond, 404)]
    [InlineData(UnknownHostMode.MisdirectedRequest, GateOutcomeAction.Respond, 421)]
    [InlineData(UnknownHostMode.Close, GateOutcomeAction.Abort, null)]
    public void AnUnknownHostIsAnsweredByTheDefaultBehaviour(UnknownHostMode mode, GateOutcomeAction action, int? status)
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { UnknownHost = mode });

        GateSimulation run = Run(snapshot, "nobody.test");

        Assert.Equal(action, run.Outcome.Action);
        Assert.Equal(GateReason.DefaultHost, run.Outcome.Reason);
        Assert.Equal(status, run.Outcome.StatusCode);
        GateStep step = Assert.Single(run.Steps, s => s.Verdict == GateStepVerdict.Stopped);
        Assert.Equal(GateStepKind.HostMatch, step.Kind);
        Assert.Contains("nobody.test", step.Summary, StringComparison.Ordinal);
        Assert.Equal(mode == UnknownHostMode.Close ? "close" : $"respond {status}", Fact(step, "defaultHost"));
    }

    [Theory]
    [InlineData("strict.test", ProbeHttpVersion.Http2, SslProtocols.Tls12, "requires TLS 1.3")]
    [InlineData("h1.test", ProbeHttpVersion.Http2, SslProtocols.Tls13, "http/1.1 only")]
    [InlineData("noh3.test", ProbeHttpVersion.Http3, SslProtocols.Tls13, "HTTP/3, which the host doesn't allow")]
    [InlineData("plain.test", ProbeHttpVersion.Http2, SslProtocols.Tls13, "isn't served over HTTPS")]
    public void ACoalescedConnectionIsMisdirectedForTheReasonTheTraceGives(string host, ProbeHttpVersion version, SslProtocols tls, string reason)
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });

        // The handshake names another host (lax.test), so the connection is negotiated for that one's settings.
        GateSimulation run = GateSimulator.Run(snapshot, new GateProbe
        {
            Https = true,
            Host = host,
            ServerName = "lax.test",
            ClientAddress = Lan,
            HttpVersion = version,
            TlsVersion = tls,
        });

        Assert.Equal(GateReason.MisdirectedConnection, run.Outcome.Reason);
        Assert.Equal(421, run.Outcome.StatusCode);
        GateStep step = Step(run, GateStepKind.TlsPolicy);
        Assert.Equal(GateStepVerdict.Stopped, step.Verdict);
        Assert.Contains(reason, step.Summary, StringComparison.Ordinal);
        Assert.Equal(TestIds.Host(host.Split('.')[0]), step.Host);
    }

    [Fact]
    public void APlainHttpRequestSkipsTheTlsCheck()
    {
        GateSimulation run = Run(Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") }), "strict.test");

        Assert.Equal(GateStepVerdict.Skipped, Step(run, GateStepKind.TlsPolicy).Verdict);
        Assert.DoesNotContain(run.Steps, s => s.Kind == GateStepKind.TlsHandshake);
    }

    [Fact]
    public void TheHandshakeIsRejectedWhenNoCertificateTakesTheName()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());

        GateSimulation run = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "app.test", ClientAddress = Lan });

        Assert.Equal(GateReason.HandshakeRejected, run.Outcome.Reason);
        Assert.Equal(GateOutcomeAction.Abort, run.Outcome.Action);
        GateStep only = Assert.Single(run.Steps);
        Assert.Equal(GateStepKind.TlsHandshake, only.Kind);
        Assert.Contains("no certificate", only.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHandshakeSettlesTheVersionAndAlpnTheRequestSees()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });

        // h1.test offers no h2: the client's HTTP/2 becomes HTTP/1.1 (so no 421 for h1.test itself).
        GateSimulation downgraded = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "h1.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 });
        Assert.Equal(GateReason.Forwarded, downgraded.Outcome.Reason);
        Assert.Equal("http/1.1", Fact(Step(downgraded, GateStepKind.TlsHandshake), "alpn"));

        // strict.test needs 1.3: a TLS 1.2-only client never completes the handshake.
        GateSimulation tooOld = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "strict.test", ClientAddress = Lan, TlsVersion = SslProtocols.Tls12 });
        Assert.Equal(GateReason.HandshakeRejected, tooOld.Outcome.Reason);

        // noh3.test refuses QUIC handshakes.
        GateSimulation quic = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "noh3.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http3 });
        Assert.Equal(GateReason.HandshakeRejected, quic.Outcome.Reason);
        Assert.Contains("HTTP/3 is disabled", Step(quic, GateStepKind.TlsHandshake).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PoliciesAreTracedInOrderHostFirst()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());

        GateSimulation allowed = GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", Path = "/blocked", ClientAddress = Lan });
        Assert.Equal(GateReason.RoutePolicy, allowed.Outcome.Reason);
        Assert.Equal(451, allowed.Outcome.StatusCode);
        Assert.Equal("legal\n", allowed.Outcome.Body);
        Assert.Equal(
            [(GateStepKind.HostPolicy, GateStepVerdict.Passed), (GateStepKind.RoutePolicy, GateStepVerdict.Stopped)],
            allowed.Steps.Where(s => s.Kind is GateStepKind.HostPolicy or GateStepKind.RoutePolicy).Select(s => (s.Kind, s.Verdict)));
        Assert.Equal(TestIds.Route("app-blocked"), Step(allowed, GateStepKind.RoutePolicy).Route);
        Assert.DoesNotContain(allowed.Steps, s => s.Kind == GateStepKind.Upstream);

        // The host's list stops an outsider before any route is matched.
        GateSimulation blocked = GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", Path = "/blocked", ClientAddress = Outsider });
        Assert.Equal(GateReason.HostPolicy, blocked.Outcome.Reason);
        Assert.Equal(403, blocked.Outcome.StatusCode);
        Assert.DoesNotContain(blocked.Steps, s => s.Kind == GateStepKind.RouteMatch);
        Assert.Contains("198.51.100.1", Step(blocked, GateStepKind.HostPolicy).Summary, StringComparison.Ordinal);

        GateSimulation dropped = GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", Path = "/drop", ClientAddress = Lan });
        Assert.Equal(GateOutcomeAction.Abort, dropped.Outcome.Action);
        Assert.Equal(GateReason.RoutePolicy, dropped.Outcome.Reason);
    }

    [Fact]
    public void TheHttpsRedirectNamesItsLocationAndIsSkippedWhenTheSchemeIsTrusted()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec
        {
            Certificate = TestIds.Certificate("shared"),
            TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.1"] },
        });

        GateSimulation redirected = GateSimulator.Run(snapshot, new GateProbe { Host = "secure.test", Path = "/a+b", Query = "q=1", ClientAddress = Outsider, Arrival = ProbeArrival.Direct });
        Assert.Equal(GateReason.HttpsRedirect, redirected.Outcome.Reason);
        Assert.Equal(301, redirected.Outcome.StatusCode);
        Assert.Equal("https://secure.test:8443/a+b?q=1", redirected.Outcome.Location);

        GateSimulation post = GateSimulator.Run(snapshot, new GateProbe { Host = "secure.test", Method = "POST", ClientAddress = Outsider });
        Assert.Equal(308, post.Outcome.StatusCode);

        // cloudflared terminated TLS: the visitor's scheme is https, so no redirect, and the trace says whose word it is.
        GateSimulation terminated = GateSimulator.Run(snapshot, new GateProbe
        {
            Host = "secure.test",
            ClientAddress = Outsider,
            Arrival = ProbeArrival.CloudflareTunnel,
            Headers = [new ProbeHeader("CF-Visitor", "{\"scheme\":\"https\"}")],
        });
        Assert.Equal(GateReason.Forwarded, terminated.Outcome.Reason);
        Assert.Equal("cfVisitor", Fact(Step(terminated, GateStepKind.ClientAddress), "schemeSource"));
        Assert.Equal(GateStepVerdict.Passed, Step(terminated, GateStepKind.HttpsRedirect).Verdict);
    }

    [Fact]
    public void RedirectAndRespondHostsAnswerWithTheirFixedResponse()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());

        GateSimulation redirect = GateSimulator.Run(snapshot, new GateProbe { Host = "old.test", Path = "/x/y", Query = "z=1", ClientAddress = Lan });
        Assert.Equal(GateReason.FixedHost, redirect.Outcome.Reason);
        Assert.Equal(308, redirect.Outcome.StatusCode);
        Assert.Equal("https://new.test/x/y?z=1", redirect.Outcome.Location);
        Assert.Equal(GateStepKind.FixedResponse, redirect.Steps[^1].Kind);

        GateSimulation respond = GateSimulator.Run(snapshot, new GateProbe { Host = "soon.test", ClientAddress = Lan });
        Assert.Equal(503, respond.Outcome.StatusCode);
        Assert.Equal("soon", respond.Outcome.Body);
        Assert.Null(respond.Outcome.Location);
    }

    [Fact]
    public void NoRouteIsA404WithTheMatchedPathInTheTrace()
    {
        GateSimulation run = GateSimulator.Run(Snapshot(new DefaultsSpec()), new GateProbe { Host = "narrow.test", Path = "/other//x/", ClientAddress = Lan });

        Assert.Equal(GateReason.NoRoute, run.Outcome.Reason);
        Assert.Equal(404, run.Outcome.StatusCode);
        GateStep step = Step(run, GateStepKind.RouteMatch);
        Assert.Equal(GateStepVerdict.Stopped, step.Verdict);
        Assert.Equal("/other/x/", Fact(step, "matchedPath"));
        Assert.Null(step.Route);
    }

    [Fact]
    public void ClientResolutionNamesTheTrustedPeerAndHeader()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] } });

        // Behind the trusted proxy: the right-most untrusted entry of the header it sent.
        GateStep trusted = Step(
            GateSimulator.Run(snapshot, new GateProbe
            {
                Host = "narrow.test",
                ClientAddress = Outsider,
                Arrival = ProbeArrival.TrustedProxy,
                Headers = [new ProbeHeader("X-Forwarded-For", "203.0.113.9, 198.51.100.1, 10.0.0.9")],
            }),
            GateStepKind.ClientAddress);
        Assert.Equal("198.51.100.1", Fact(trusted, "client"));
        Assert.Equal("forwardingHeader", Fact(trusted, "clientSource"));
        Assert.Equal("X-Forwarded-For", Fact(trusted, "header"));
        Assert.Equal("yes", Fact(trusted, "peerTrusted"));
        Assert.StartsWith("10.0.0.0 (socket)", Fact(trusted, "peer"), StringComparison.Ordinal);

        // The arrival adds the header it would send, and defaults the peer to a trusted address.
        GateStep synthesized = Step(
            GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Outsider, Arrival = ProbeArrival.TrustedProxy }),
            GateStepKind.ClientAddress);
        Assert.Equal("198.51.100.1", Fact(synthesized, "client"));
        Assert.Equal("yes", Fact(synthesized, "peerTrusted"));
        Assert.StartsWith("10.0.0.0 (socket)", Fact(synthesized, "peer"), StringComparison.Ordinal);

        // A peer that isn't trusted: the header is ignored and the trace says why.
        GateStep untrusted = Step(
            GateSimulator.Run(snapshot, new GateProbe
            {
                Host = "narrow.test",
                ClientAddress = Outsider,
                Headers = [new ProbeHeader("X-Forwarded-For", "203.0.113.9")],
            }),
            GateStepKind.ClientAddress);
        Assert.Equal("198.51.100.1", Fact(untrusted, "client"));
        Assert.Equal("no", Fact(untrusted, "peerTrusted"));
        Assert.Contains("not a trusted proxy", untrusted.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CloudflareAndProxyProtocolArrivalsAreTraced()
    {
        RuntimeSnapshot cloudflare = Snapshot(new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } });

        GateStep edge = Step(
            GateSimulator.Run(cloudflare, new GateProbe { Host = "narrow.test", ClientAddress = Outsider, Arrival = ProbeArrival.CloudflareProxy }),
            GateStepKind.ClientAddress);
        Assert.Equal("cfConnectingIp", Fact(edge, "clientSource"));
        Assert.Equal("CF-Connecting-IP", Fact(edge, "header"));
        Assert.Equal("yes", Fact(edge, "cloudflareHeadersTrusted"));
        Assert.Equal("198.51.100.1", Fact(edge, "client"));

        // The trusted proxy reached us through a PROXY protocol load balancer: the peer is the header's source.
        RuntimeSnapshot generic = Snapshot(new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] } });
        GateStep viaLoadBalancer = Step(
            GateSimulator.Run(generic, new GateProbe
            {
                Host = "narrow.test",
                ClientAddress = Outsider,
                PeerAddress = Lan,
                ProxyProtocol = true,
                TransportAddress = IPAddress.Parse("192.0.2.1"),
                Arrival = ProbeArrival.TrustedProxy,
            }),
            GateStepKind.ClientAddress);
        Assert.Equal("10.0.0.5 (PROXY protocol)", Fact(viaLoadBalancer, "peer"));
        Assert.Equal("198.51.100.1", Fact(viaLoadBalancer, "client"));
    }

    [Fact]
    public void ASimulationHasNoSideEffects()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] } });

        // A trusted proxy's unusable header is a fallback the real source counts (and a plain-HTTP Cloudflare visitor it logs).
        GateSimulation run = GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Outsider, Arrival = ProbeArrival.TrustedProxy, Headers = [new ProbeHeader("X-Forwarded-For", "bogus")] });
        Assert.Equal("headerInvalid", Fact(Step(run, GateStepKind.ClientAddress), "outcome"));

        // The simulator's resolution is the same code with no counters: the real source counts it, the simulation doesn't.
        DefaultHttpContext context = TestContexts.Create("narrow.test", remote: Lan);
        context.Request.Headers["X-Forwarded-For"] = "bogus";
        TrustedProxyClientAddressSource real = new();
        ClientIpResolution counted = real.Resolve(context, snapshot);
        ClientIpResolution simulated = TrustedProxyClientAddressSource.ResolveCore(context, snapshot, counters: null, logger: null);
        Assert.Equal(1, real.Counters.HeaderInvalid);
        Assert.Equal(counted, simulated);
        Assert.Equal(1, real.Counters.HeaderInvalid);
        Assert.Equal(0, new TlsHandshakeSelector(new SnapshotStore()).Counters.UnknownServerNameTcp);
    }

    [Fact]
    public void AnUnboundSnapshotStillSaysWhereARouteForwards()
    {
        ProxyConfigModel model = Model(new DefaultsSpec());
        RuntimeSnapshot unbound = Compile(model);

        GateSimulation run = GateSimulator.Run(unbound, new GateProbe { Host = "narrow.test", Path = "/api", ClientAddress = Lan });

        Assert.Equal(GateReason.Forwarded, run.Outcome.Reason);
        Assert.Equal(TestIds.Upstream("api"), run.Upstream!.Id);
        Assert.False(unbound.IsBound);
    }

    [Fact]
    public void AnAnsweredChallengeIsTracedAndAnUnknownTokenFallsThrough()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());
        AcmeChallengeStore challenges = new();
        challenges.Publish("tok-en_1", "tok-en_1.thumbprint");

        // Plain HTTP, any host (even an unknown one), before every policy: answered with the key authorization.
        GateSimulation answered = GateSimulator.Run(
            snapshot, new GateProbe { Host = "nobody.test", Path = AcmeChallengeStore.PathPrefix + "tok-en_1", ClientAddress = Outsider }, challenges: challenges);
        Assert.Equal(GateReason.AcmeChallenge, answered.Outcome.Reason);
        Assert.Equal(200, answered.Outcome.StatusCode);
        Assert.Equal("tok-en_1.thumbprint", answered.Outcome.Body);
        GateStep step = answered.Steps[^1];
        Assert.Equal((GateStepKind.AcmeChallenge, GateStepVerdict.Stopped), (step.Kind, step.Verdict));
        Assert.Equal("tok-en_1", Fact(step, "token"));
        Assert.DoesNotContain(answered.Steps, s => s.Kind == GateStepKind.HostMatch);

        // An unknown token, HTTPS, or another method: handled like any request.
        foreach (GateProbe probe in (GateProbe[])
        [
            new GateProbe { Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "other", ClientAddress = Lan },
            new GateProbe { Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "tok-en_1", ClientAddress = Lan, Method = "POST" },
        ])
        {
            GateSimulation fallthrough = GateSimulator.Run(snapshot, probe, challenges: challenges);
            Assert.NotEqual(GateReason.AcmeChallenge, fallthrough.Outcome.Reason);
            Assert.Equal(GateStepVerdict.Passed, Step(fallthrough, GateStepKind.AcmeChallenge).Verdict);
        }

        // Without a store nothing is answered.
        Assert.NotEqual(GateReason.AcmeChallenge, GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "tok-en_1", ClientAddress = Lan }).Outcome.Reason);
    }

    [Fact]
    public void TheAcmeStepSaysWhyAChallengePathWasNotAnswered()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });
        AcmeChallengeStore challenges = new();
        challenges.Publish("tok", "tok.key");

        // HTTPS: an HTTP-01 answer is only served over plain HTTP.
        GateStep secure = Step(
            GateSimulator.Run(
                snapshot,
                new GateProbe { Https = true, Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "tok", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 },
                challenges: challenges),
            GateStepKind.AcmeChallenge);
        Assert.Equal(GateStepVerdict.Passed, secure.Verdict);
        Assert.Contains("HTTPS", Fact(secure, "reason"), StringComparison.Ordinal);
        Assert.Equal("tok", Fact(secure, "token"));

        // Another method: only GET and HEAD are answered.
        GateStep posted = Step(
            GateSimulator.Run(
                snapshot,
                new GateProbe { Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "tok", Method = "POST", ClientAddress = Lan },
                challenges: challenges),
            GateStepKind.AcmeChallenge);
        Assert.Equal(GateStepVerdict.Passed, posted.Verdict);
        Assert.Contains("POST", Fact(posted, "reason"), StringComparison.Ordinal);

        // A token nothing published.
        GateStep unknown = Step(
            GateSimulator.Run(
                snapshot,
                new GateProbe { Host = "app.test", Path = AcmeChallengeStore.PathPrefix + "other", ClientAddress = Lan },
                challenges: challenges),
            GateStepKind.AcmeChallenge);
        Assert.Equal(GateStepVerdict.Passed, unknown.Verdict);
        Assert.Contains("no challenge is waiting", Fact(unknown, "reason"), StringComparison.Ordinal);

        // Not a challenge path at all: skipped, and it doesn't claim to be one.
        GateStep other = Step(
            GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", Path = "/index.html", ClientAddress = Lan }, challenges: challenges),
            GateStepKind.AcmeChallenge);
        Assert.Equal(GateStepVerdict.Skipped, other.Verdict);
        Assert.Contains("not an ACME HTTP-01 challenge path", other.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Facts, f => f.Name == "token");
    }

    [Fact]
    public void TheHandshakeNamesTheCertificateThatWouldBeServed()
    {
        ServerCertificate certificate = new(_pki.Leaf("named", "*.test").Context) { Id = TestIds.Certificate("named") };
        RuntimeSnapshot snapshot = Bound(Model(new DefaultsSpec { Certificate = TestIds.Certificate("named") }), options: new SnapshotCompilerOptions { Certificates = new Single(certificate) });

        GateStep handshake = Step(
            GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "app.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 }),
            GateStepKind.TlsHandshake);

        Assert.Equal(TestIds.Certificate("named").ToString(), Fact(handshake, "certificate"));
        Assert.Contains(TestIds.Certificate("named").ToString(), handshake.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Http1NeedsExactlyOneValidHostOrTheServerAnswers400()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());

        foreach (string[] hosts in (string[][])[[], ["app.test", "app.test"], ["a@b"], ["app.test/x"], ["app test"]])
        {
            GateSimulation run = GateSimulator.Run(snapshot, new GateProbe { Host = "app.test", HostHeaders = hosts, ClientAddress = Lan });

            Assert.Equal(GateReason.RejectedByServer, run.Outcome.Reason);
            Assert.Equal(400, run.Outcome.StatusCode);
            Assert.Equal(GateStepKind.Request, run.Steps[^1].Kind);
            Assert.DoesNotContain(run.Steps, s => s.Kind == GateStepKind.HostMatch);
        }

        // One valid Host: on its way, whatever the case.
        Assert.Equal(GateReason.Forwarded, GateSimulator.Run(snapshot, new GateProbe { Host = "x", HostHeaders = ["APP.test:80"], Path = "/api", ClientAddress = Lan }).Outcome.Reason);
    }

    [Fact]
    public void Http2UsesTheAuthorityAndIgnoresHostHeaders()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });

        // Two Host headers, one that isn't a host, none: on HTTP/2 the authority is the host, so none of it matters.
        foreach (string[] hosts in (string[][])[[], ["a", "b"], ["a@b"]])
        {
            GateSimulation run = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "narrow.test", HostHeaders = hosts, Path = "/api", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 });

            Assert.Equal(GateReason.Forwarded, run.Outcome.Reason);
            Assert.Equal(TestIds.Host("narrow"), Step(run, GateStepKind.HostMatch).Host);
        }

        // An invalid authority is still a 400.
        Assert.Equal(GateReason.RejectedByServer, GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "a@b", ServerName = "narrow.test", Path = "/", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 }).Outcome.Reason);
    }

    [Fact]
    public void ThePathIsDecodedAndNormalisedLikeKestrelBeforeTheGateSeesIt()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());
        AcmeChallengeStore challenges = new();
        challenges.Publish("tok", "tok.key");

        // A dot-segment path reaches the token the real server would give the gate.
        GateSimulation answered = GateSimulator.Run(
            snapshot, new GateProbe { Host = "nobody.test", Path = "/x/../.well-known/./acme-challenge/tok", ClientAddress = Outsider }, challenges);
        Assert.Equal(GateReason.AcmeChallenge, answered.Outcome.Reason);
        Assert.Equal("/.well-known/acme-challenge/tok", Fact(Step(answered, GateStepKind.Request), "decodedPath"));
        Assert.Equal("tok", Fact(Step(answered, GateStepKind.AcmeChallenge), "token"));

        // Escapes first, then dot segments: %2E%2E is "..".
        GateSimulation escaped = GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", Path = "/blocked/%2E%2E/api/x", ClientAddress = Lan });
        Assert.Equal("/api/x", Fact(Step(escaped, GateStepKind.RouteMatch), "matchedPath"));

        // Bytes that aren't UTF-8 stay as written (the real server answers such a request normally: see the socket tests).
        GateSimulation invalid = GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", Path = "/api/%C3", ClientAddress = Lan });
        Assert.Equal(GateReason.Forwarded, invalid.Outcome.Reason);
        Assert.Equal("/api/%C3", Fact(Step(invalid, GateStepKind.Request), "decodedPath"));
        Assert.Equal("/%FF", GateSimulator.DecodePath("/%FF"));
        Assert.Equal("/\u00e9%C3", GateSimulator.DecodePath("/%C3%A9%C3"));
    }

    [Theory]
    [InlineData("/a/b/c", "/a/b/c")]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/a/b/..", "/a/")]
    [InlineData("/a/b/.", "/a/b/")]
    [InlineData("/../a", "/a")]
    [InlineData("/a/../../b", "/b")]
    [InlineData("/a//b/../c", "/a//c")]
    [InlineData("/.hidden/x", "/.hidden/x")]
    public void DotSegmentsAreRemovedAsRfc3986Says(string path, string expected) =>
        Assert.Equal(expected, GateSimulator.RemoveDotSegments(path));

    [Fact]
    public void TheListenerRejectsAConnectionItsProxyProtocolRulesRefuse()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec());
        ProxyProtocolOptions trusted = new(CidrSet.Parse(["192.0.2.0/24"]));
        ListenerFacts none = new(null, null, Http3: true);
        ListenerFacts required = new(trusted, trusted, Http3: true);

        // No PROXY protocol on the listener: a header is read as a broken request.
        GateSimulation unexpected = GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Lan, ProxyProtocol = true }, listeners: none);
        Assert.Equal(GateReason.ConnectionRejected, unexpected.Outcome.Reason);
        Assert.Equal(GateOutcomeAction.Abort, unexpected.Outcome.Action);
        Assert.Single(unexpected.Steps);
        Assert.Equal(GateStepKind.Connection, unexpected.Steps[0].Kind);

        // Required, and none sent; sent from an untrusted socket; sent from a trusted one.
        Assert.Equal(GateReason.ConnectionRejected, GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Lan }, listeners: required).Outcome.Reason);
        GateSimulation untrusted = GateSimulator.Run(
            snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Lan, ProxyProtocol = true, TransportAddress = IPAddress.Parse("198.51.100.9") }, listeners: required);
        Assert.Equal(GateReason.ConnectionRejected, untrusted.Outcome.Reason);
        Assert.Contains("198.51.100.9", untrusted.Steps[0].Summary, StringComparison.Ordinal);
        GateSimulation fine = GateSimulator.Run(
            snapshot, new GateProbe { Host = "narrow.test", Path = "/api", ClientAddress = Lan, ProxyProtocol = true, TransportAddress = IPAddress.Parse("192.0.2.7") }, listeners: required);
        Assert.Equal(GateReason.Forwarded, fine.Outcome.Reason);
        Assert.Equal(GateStepKind.Connection, fine.Steps[0].Kind);
        Assert.Equal(GateStepVerdict.Passed, fine.Steps[0].Verdict);

        // The two listeners have their own rules: HTTPS only.
        ListenerFacts httpsOnly = new(null, trusted, Http3: true);
        Assert.Equal(GateReason.Forwarded, GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", Path = "/api", ClientAddress = Lan }, listeners: httpsOnly).Outcome.Reason);
        Assert.Equal(GateReason.ConnectionRejected, GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "narrow.test", ServerName = "x.test", ClientAddress = Lan }, listeners: httpsOnly).Outcome.Reason);

        // QUIC has no PROXY protocol, and may not exist.
        Assert.Equal(GateReason.ConnectionRejected, GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "narrow.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http3, ProxyProtocol = true }, listeners: none).Outcome.Reason);
        Assert.Equal(GateReason.ConnectionRejected, GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "narrow.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http3 }, listeners: new ListenerFacts(null, null, Http3: false)).Outcome.Reason);
    }

    [Fact]
    public void TheAdmissionIsTheConnectionMiddlewaresOwnDecision()
    {
        ProxyProtocolOptions options = new(CidrSet.Parse(["192.0.2.0/24"]));
        IpKey trusted = IpKey.Parse("192.0.2.1");
        IpKey other = IpKey.Parse("198.51.100.1");

        Assert.Equal(ConnectionAdmission.Accepted, ProxyProtocolAdmission.Check(null, other, headerSent: false));
        Assert.Equal(ConnectionAdmission.HeaderUnexpected, ProxyProtocolAdmission.Check(null, other, headerSent: true));
        Assert.Equal(ConnectionAdmission.UntrustedSource, ProxyProtocolAdmission.Check(options, other, headerSent: true));
        Assert.Equal(ConnectionAdmission.UntrustedSource, ProxyProtocolAdmission.Check(options, other, headerSent: false));
        Assert.Equal(ConnectionAdmission.HeaderMissing, ProxyProtocolAdmission.Check(options, trusted, headerSent: false));
        Assert.Equal(ConnectionAdmission.Accepted, ProxyProtocolAdmission.Check(options, trusted, headerSent: true));
    }

    [Fact]
    public void PoliciesAreToldTheyAreSimulatedAndProductionNever()
    {
        SpyPolicy spy = new();
        RuntimeSnapshot compiled = Bound(Model(new DefaultsSpec()));
        CompiledRoute route = new() { Id = TestIds.Route("spy-root"), MatchKind = RouteMatchKind.Prefix, Path = "/", Slot = 0, Policies = [spy] };
        CompiledHost host = new() { Index = 0, Id = TestIds.Host("spy"), Kind = HostKind.Proxy, Routes = [route], Policies = [spy] };
        HostTable.Builder table = new();
        Assert.True(table.TryAdd("spy.test", host, out _));
        RuntimeSnapshot snapshot = new()
        {
            Generation = 1,
            Revision = default,
            Hosts = table.Build(),
            HostsByIndex = [host],
            Yarp = compiled.Yarp,
            RouteIdBySlot = [route.Id],
            YarpEndpoints = [new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, "spy")],
            Default = compiled.Default,
        };

        GateSimulation simulated = GateSimulator.Run(snapshot, new GateProbe { Host = "spy.test", ClientAddress = Lan });
        Assert.Equal(GateReason.Forwarded, simulated.Outcome.Reason);
        Assert.Equal([true, true], spy.Seen);

        spy.Seen.Clear();
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        Assert.Equal(GateAction.Forward, gate.Decide(TestContexts.Create("spy.test", remote: Lan)).Action);
        Assert.Equal([false, false], spy.Seen);
        store.Dispose();
    }

    [Fact]
    public void TheTraceSaysWhetherHstsWentOutAndWhyNot()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });

        GateSimulation sent = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "narrow.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 });
        GateStep hsts = Step(sent, GateStepKind.Hsts);
        Assert.Equal(GateStepVerdict.Passed, hsts.Verdict);
        Assert.Equal(TestIds.Host("narrow"), hsts.Host);
        Assert.Equal("max-age=31536000", Fact(hsts, "value"));
        Assert.Equal("yes", Fact(hsts, "configured"));

        GateSimulation plain = GateSimulator.Run(snapshot, new GateProbe { Host = "narrow.test", ClientAddress = Lan });
        GateStep not = Step(plain, GateStepKind.Hsts);
        Assert.Equal(GateStepVerdict.Skipped, not.Verdict);
        Assert.Contains("plain HTTP", not.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(not.Facts, f => f.Name == "value");

        GateSimulation off = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "app.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 });
        Assert.Contains("off", Step(off, GateStepKind.Hsts).Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no", Fact(Step(off, GateStepKind.Hsts), "configured"));
    }

    [Fact]
    public void FactsUseWireNamesNotInternalOnes()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared"), TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8"] } });

        GateSimulation run = GateSimulator.Run(snapshot, new GateProbe { Https = true, Host = "x.app.test", Path = "/api", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http2 });

        string[] values = [.. run.Steps.SelectMany(s => s.Facts).Select(f => f.Value), .. run.Steps.Select(s => s.Summary)];
        foreach (string leaked in (string[])["HostMatchKind", "ClientIpOutcome", "SslProtocols", "Tls12, Tls13", "Tls13", "Tls12", "Prefix", "Wildcard", "Generic", "Http2And", "Paduraksa."])
        {
            Assert.DoesNotContain(values, v => v.Contains(leaked, StringComparison.Ordinal));
        }

        Assert.Equal("generic", Fact(Step(run, GateStepKind.ClientAddress), "trustedProxyMode"));
        Assert.Equal("tls1.3", Fact(Step(run, GateStepKind.TlsHandshake), "tlsVersion"));
        Assert.Equal("ipAllowList", Fact(Step(run, GateStepKind.HostPolicy), "policy"));
        Assert.Equal("prefix", Fact(Step(run, GateStepKind.RouteMatch), "match"));
        Assert.Equal("http2AndHttp11", Fact(Step(run, GateStepKind.TlsPolicy), "tcpAlpn"));
        Assert.Equal("tls1.2", Fact(Step(run, GateStepKind.TlsPolicy), "minimumVersion"));
    }

    private sealed class SpyPolicy : IRequestPolicy
    {
        public List<bool> Seen { get; } = [];

        public PolicyDecision Evaluate(in RequestView request)
        {
            Seen.Add(request.IsSimulation);
            return PolicyDecision.Continue;
        }
    }

    [Fact]
    public void HttpThreeNeedsTls()
    {
        Assert.Throws<ArgumentException>(() => GateSimulator.Run(Snapshot(new DefaultsSpec()), new GateProbe { Host = "app.test", ClientAddress = Lan, HttpVersion = ProbeHttpVersion.Http3 }));
    }

    [Fact]
    public void ThePathIsDecodedLikeKestrelDecodesIt()
    {
        Assert.Equal("/a b/c", GateSimulator.DecodePath("/a%20b/c"));
        Assert.Equal("/é", GateSimulator.DecodePath("/%C3%A9"));
        Assert.Equal("/a%2Fb/%5Cc", GateSimulator.DecodePath("/a%2Fb/%5Cc"));
        Assert.Equal("/100%", GateSimulator.DecodePath("/100%"));
        Assert.Equal("/%zz", GateSimulator.DecodePath("/%zz"));
    }

    [Fact]
    public void TheNoOpTracerKeepsTheGateAllocationFree()
    {
        RuntimeSnapshot snapshot = Snapshot(new DefaultsSpec { Certificate = TestIds.Certificate("shared") });
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        DefaultHttpContext forward = TestContexts.Create("app.test", "/api/x", remote: Lan);
        DefaultHttpContext redirect = TestContexts.Create("secure.test", "/", remote: Outsider);

        AllocAssert.Zero((snapshot, addresses, forward), static s => _ = GateMiddleware.Decide(s.forward, s.snapshot, s.addresses, null, default(NoGateTracer)), "forwarding with the no-op tracer");
        AllocAssert.Zero((snapshot, addresses, redirect), static s => _ = GateMiddleware.Decide(s.redirect, s.snapshot, s.addresses, null, default(NoGateTracer)), "redirecting with the no-op tracer");
    }

    private RuntimeSnapshot Snapshot(DefaultsSpec defaults) => Bound(
        Model(defaults),
        options: new SnapshotCompilerOptions { Certificates = new Single(_pki.Leaf("shared", "*.test", "*.app.test", "app.test")), PublicHttpsPort = 8443 });

    private static GateSimulation Run(RuntimeSnapshot snapshot, string host) =>
        GateSimulator.Run(snapshot, new GateProbe { Host = host, ClientAddress = Lan });

    private static GateStep Step(GateSimulation run, GateStepKind kind) => Assert.Single(run.Steps, s => s.Kind == kind);

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;

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
                    Route("app-blocked", "web", "/blocked") with { Policies = [new DenyPolicySpec(451, "legal\n")] },
                    Route("app-drop", "web", "/drop") with { Policies = [new DropPolicySpec()] },
                    Route("app-root", "web"),
                ],
            },
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true },
            ProxyHost("narrow", "narrow.test", Route("narrow-api", "api", "/api")) with { Hsts = new HostHstsSpec { Enabled = true } },
            new HostSpec { Id = TestIds.Host("old"), Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 } },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } },
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with { Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 } },
            ProxyHost("h1", "h1.test", Route("h1-root", "web")) with { Tls = new HostTlsSpec { TcpAlpn = TcpAlpn.Http11Only } },
            ProxyHost("noh3", "noh3.test", Route("noh3-root", "web")) with { Tls = new HostTlsSpec { Http3 = false } },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")) with { Https = false },
            ProxyHost("ipv6", "[::1]", Route("ipv6-root", "web")),
            ProxyHost("lax", "lax.test", Route("lax-root", "web")),
        ],
    };

    private sealed class Single(ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = certificate;
            return true;
        }
    }
}
