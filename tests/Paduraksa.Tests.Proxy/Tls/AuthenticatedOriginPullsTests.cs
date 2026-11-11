using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// Authenticated Origin Pulls: a host that requires it asks the handshake for Cloudflare's client certificate and takes only one
/// its CA signed, and the gate refuses a request that reaches the host on a connection without one. A test CA stands in for
/// Cloudflare's, handed to the compiler through <c>SnapshotCompilerOptions.OriginPullCa</c>.
/// </summary>
public sealed class AuthenticatedOriginPullsTests : IDisposable
{
    private static readonly HostCloudflareSpec OriginPulls = new() { AuthenticatedOriginPulls = true };
    private static readonly TrustedProxySpec CloudflareProxy = new() { Mode = TrustedProxyMode.CloudflareProxy };

    private readonly TestPki _pki = TestPki.Create();
    private readonly TestOriginPullCa _cloudflare = TestOriginPullCa.Create("Stand-in for Cloudflare");
    private readonly TestOriginPullCa _other = TestOriginPullCa.Create("Another authority");
    private readonly SnapshotStore _store = new();
    private readonly CertificateId _certificateId = TestIds.Certificate("shared");

    public void Dispose()
    {
        _store.Dispose();
        _cloudflare.Dispose();
        _other.Dispose();
        _pki.Dispose();
    }

    private ProxyConfigModel Model(TrustedProxySpec? proxies = null) => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Defaults = new DefaultsSpec { Certificate = _certificateId, TrustedProxies = proxies ?? CloudflareProxy },
        Hosts =
        [
            ProxyHost("aop", "aop.test", Route("aop-root", "web")) with { Cloudflare = OriginPulls },
            ProxyHost("aop2", "aop2.test", Route("aop2-root", "web")) with { Cloudflare = OriginPulls },
            ProxyHost("plain", "plain.test", Route("plain-root", "web")),
        ],
    };

    private RuntimeSnapshot Snapshot(TrustedProxySpec? proxies = null, bool http3 = false)
    {
        var shared = _pki.Leaf("shared", "*.test");
        return Bound(
            Model(proxies),
            options: new SnapshotCompilerOptions
            {
                Certificates = new Single(_certificateId, shared),
                OriginPullCa = _cloudflare.Roots,
                AdvertiseHttp3 = http3,
            });
    }

    [Fact]
    public void AnOriginPullsHostGetsItsOwnTlsEntryThatRequiresTheCertificateAndLeavesHttp3()
    {
        var snapshot = Snapshot(http3: true);
        var table = snapshot.Certificates;
        var aop = table.Find("aop.test")!;
        var aop2 = table.Find("aop2.test")!;
        var plain = table.Find("plain.test")!;

        // The same certificate, a different policy: the flag is part of the deduplication key, so the two never share options.
        Assert.Same(plain.Certificate.Context, aop.Certificate.Context);
        Assert.NotSame(plain, aop);
        Assert.NotSame(plain.TcpOptions, aop.TcpOptions);
        Assert.False(plain.TcpOptions.ClientCertificateRequired);
        Assert.True(aop.TcpOptions.ClientCertificateRequired);
        Assert.Equal(X509ChainTrustMode.CustomRootTrust, aop.TcpOptions.CertificateChainPolicy!.TrustMode);
        Assert.True(aop.TcpOptions.CertificateChainPolicy.DisableCertificateDownloads);
        Assert.Equal(X509RevocationMode.NoCheck, aop.TcpOptions.CertificateChainPolicy.RevocationMode);
        Assert.Single(aop.TcpOptions.CertificateChainPolicy.CustomTrustStore);

        // A resumed session may carry no certificate, so this entry does a full handshake each time; the open host's entry
        // keeps resumption.
        Assert.False(aop.TcpOptions.AllowTlsResume);
        Assert.True(plain.TcpOptions.AllowTlsResume);

        // Two such hosts on one certificate share one entry, one options object.
        Assert.Same(aop, aop2);

        // No HTTP/3 for it: no QUIC options, so the QUIC handshake for its name is refused, not served by anything else.
        Assert.Null(aop.QuicOptions);
        Assert.NotNull(plain.QuicOptions);
        var quic = TlsHandshakeSelector.Select(table, "aop.test", quic: true);
        Assert.Equal(TlsRejectionReason.Http3Disabled, quic.Rejection);
        Assert.Null(TlsHandshakeSelector.Select(table, "plain.test", quic: true).Rejection);
    }

    [Fact]
    public void NoAltSvcIsOfferedForAnOriginPullsHostAndTheTraceSaysWhy()
    {
        var snapshot = Snapshot(http3: true);

        var aop = snapshot.Hosts.Find("aop.test")!.ResponseHeaders.Single(o => o.Name == "Alt-Svc");
        var plain = snapshot.Hosts.Find("plain.test")!.ResponseHeaders.Single(o => o.Name == "Alt-Svc");

        Assert.True(aop.Value.Count == 0);
        Assert.Contains("Authenticated Origin Pulls", aop.Reason, StringComparison.Ordinal);
        Assert.Contains("HTTP/3", aop.Reason, StringComparison.Ordinal);
        Assert.True(plain.Value.Count > 0);
    }

    [Theory]
    [InlineData(TrustedProxyMode.Off)]
    [InlineData(TrustedProxyMode.Generic)]
    public void TheSettingIsInertUnderAModeWithoutCloudflare(TrustedProxyMode mode)
    {
        var snapshot = Snapshot(new TrustedProxySpec { Mode = mode, Proxies = mode == TrustedProxyMode.Generic ? ["10.0.0.0/8"] : [] });

        Assert.False(snapshot.Hosts.Find("aop.test")!.AuthenticatedOriginPulls);
        Assert.False(snapshot.Certificates.Find("aop.test")!.TcpOptions.ClientCertificateRequired);
        Assert.NotNull(snapshot.Certificates.Find("aop.test")!.QuicOptions);
    }

    [Fact]
    public void TheShippedCaIsCloudflaresOriginPullAuthority()
    {
        var roots = OriginPullCa.Default;

        var ca = Assert.Single(roots);
        Assert.Equal("origin-pull.cloudflare.net", ca.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Contains("OU=Origin Pull", ca.Subject, StringComparison.Ordinal);
        Assert.Equal(ca.Subject, ca.Issuer);
        Assert.Equal(new DateTime(2029, 11, 1, 17, 0, 0, DateTimeKind.Utc), ca.NotAfter.ToUniversalTime());
        Assert.Equal("9A1AC2B4BE15F9F27EEE20A734CBA4E9898F61001B3BD7C84B69B56A3E25A2B9", ca.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.Same(roots, OriginPullCa.Default);

        // Without an override a compiled snapshot trusts exactly this one.
        var compiled = Bound(
            Model(),
            options: new SnapshotCompilerOptions { Certificates = new Single(_certificateId, _pki.Leaf("shared", "*.test")) });
        Assert.Same(ca, Assert.Single(compiled.Certificates.Find("aop.test")!.TcpOptions.CertificateChainPolicy!.CustomTrustStore));
    }

    [Fact]
    public async Task OnlyAClientCertificateTheCaSignedGetsThroughTheHandshake()
    {
        _store.Publish(Snapshot(http3: true));
        await using var server = await StartAsync(new GateMiddleware(_store, new TrustedProxyClientAddressSource()));
        var valid = _cloudflare.IssueClient("cloudflare edge");
        var expired = _cloudflare.IssueClient("old edge", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
        var foreign = _other.IssueClient("someone else");
        var selfSigned = _pki.ClientCertificate("self-signed");

        var ok = await TlsProbe.GetAsync(_pki, server.Port, "aop.test", HttpVersion.Version11, configureSsl: Present(valid));
        Assert.Equal("ok", ok.Body);

        // On the wire: the open host advertises HTTP/3 and the origin-pulls host doesn't.
        Assert.Null(ok.AltSvc);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "aop.test", HttpVersion.Version11));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "aop.test", HttpVersion.Version11, configureSsl: Present(foreign)));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "aop.test", HttpVersion.Version11, configureSsl: Present(expired)));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "aop.test", HttpVersion.Version11, configureSsl: Present(selfSigned)));

        // A host without the setting never asks, on the same certificate, and offers HTTP/3.
        var plain = await TlsProbe.GetAsync(_pki, server.Port, "plain.test", HttpVersion.Version11);
        Assert.Equal("ok", plain.Body);
        Assert.Equal("h3=\":443\"", plain.AltSvc);
    }

    [Fact]
    public async Task AChainBuildNeverFollowsAnAiaUrl()
    {
        _store.Publish(Snapshot());
        await using var server = await StartAsync(new GateMiddleware(_store, new TrustedProxyClientAddressSource()));
        await using var aia = new HitCounter();
        var chasing = _pki.ClientCertificateWithAia("aia", aia.Url);

        // An offline context so the client itself never chases the URL: any hit is the server's chain build.
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(
            _pki, server.Port, "aop.test", HttpVersion.Version11,
            configureSsl: options => options.ClientCertificateContext = SslStreamCertificateContext.Create(chasing, null, offline: true)));

        Assert.Equal(0, aia.Hits);
    }

    [Fact]
    public async Task ARequestOnAConnectionThatNeverAskedForTheCertificateIsRefusedByTheGate()
    {
        _store.Publish(Snapshot());
        await using var server = await StartAsync(new GateMiddleware(_store, new TrustedProxyClientAddressSource()));
        var valid = _cloudflare.IssueClient("cloudflare edge");

        // SNI names the open host (no certificate asked), the request names the origin-pulls host on the same connection:
        // what a coalescing client does. The handshake can't have checked it, so the gate does, whatever the client holds.
        // 421, so the client retries on a connection of its own, whose handshake does ask.
        Assert.Equal("HTTP/1.1 421", await StatusAsync(server.Port, sni: "plain.test", host: "aop.test", valid));

        // No SNI at all rides the default certificate's entry, which never asks either.
        Assert.Equal("HTTP/1.1 421", await StatusAsync(server.Port, sni: string.Empty, host: "aop2.test", valid));
        Assert.Equal("HTTP/1.1 404", await StatusAsync(server.Port, sni: string.Empty, host: "nobody.test", valid));

        // The right name on its own connection does ask, and the same client gets through.
        Assert.Equal("HTTP/1.1 200", await StatusAsync(server.Port, sni: "aop.test", host: "aop.test", valid));
    }

    [Fact]
    public async Task ASecondConnectionOfTheSameClientStillPresentsAndValidatesItsCertificate()
    {
        // The client keeps a TLS session cache between connections; the host's entry allows no resumption, so each one runs a
        // full handshake with the certificate and the gate never meets a resumed session without it.
        _store.Publish(Snapshot());
        await using var server = await StartAsync(new GateMiddleware(_store, new TrustedProxyClientAddressSource()));
        var valid = _cloudflare.IssueClient("cloudflare edge");

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("HTTP/1.1 200", await StatusAsync(server.Port, sni: "aop.test", host: "aop.test", valid));
        }
    }

    /// <summary>One HTTP/1.1 request over a raw TLS stream whose SNI and Host differ; returns the status line's first two words.</summary>
    private static async Task<string> StatusAsync(int port, string sni, string host, X509Certificate2 clientCertificate)
    {
        using System.Net.Sockets.TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using SslStream tls = new(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                ClientCertificateContext = SslStreamCertificateContext.Create(clientCertificate, null, offline: true),
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
            },
            TestContext.Current.CancellationToken);
        await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
        using StreamReader reader = new(tls, System.Text.Encoding.ASCII);
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken) ?? string.Empty;
        return string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));
    }

    [Fact]
    public void PlainHttpTheSniAndTheCertificateFeatureDecideTheGatesSecondLine()
    {
        var snapshot = Snapshot();
        _store.Publish(snapshot);
        var gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
        var edge = IPAddress.Parse("173.245.48.1");

        DefaultHttpContext Tls(string host, string sni, bool certificate)
        {
            var context = Testing.TestContexts.Create(host, scheme: "https", remote: edge);
            context.Features.Set<Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature>(new FakeTlsHandshake(sni));
            if (certificate)
            {
                context.Features.Set<Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature>(new FakeTlsConnection(_cloudflare.Certificate));
            }

            return context;
        }

        // Plain HTTP can't carry a certificate: 403, never a retry that would end the same way.
        var plain = gate.Decide(Testing.TestContexts.Create("aop.test", remote: edge));
        Assert.Equal(403, plain.Response!.StatusCode);
        Assert.Equal("forbidden\n", System.Text.Encoding.UTF8.GetString(plain.Response.Body));

        // A TLS connection whose SNI doesn't select the host's entry (another name, none at all, a name of an open host that
        // shares the certificate): 421.
        foreach (var sni in new[] { "plain.test", string.Empty, "nobody.test" })
        {
            var refused = gate.Decide(Tls("aop.test", sni, certificate: false));
            Assert.Equal(421, refused.Response!.StatusCode);
            Assert.Same(snapshot.Misdirected, refused.Response);
        }

        // SNI naming the host (or another origin-pulls host on the same entry) but no verified certificate: 403.
        Assert.Equal(403, gate.Decide(Tls("aop.test", "aop.test", certificate: false)).Response!.StatusCode);
        Assert.Equal(403, gate.Decide(Tls("aop.test", "aop2.test", certificate: false)).Response!.StatusCode);

        // A verified certificate goes on, whatever else; an open host is untouched.
        Assert.Equal(GateAction.Forward, gate.Decide(Tls("aop.test", "aop.test", certificate: true)).Action);
        Assert.Equal(GateAction.Forward, gate.Decide(Testing.TestContexts.Create("plain.test", remote: edge)).Action);
    }

    [Fact]
    public void RefusedAndAllowedDecisionsAllocateNothing()
    {
        _store.Publish(Snapshot());
        var gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
        var refused = Testing.TestContexts.Create("aop.test", scheme: "https", remote: IPAddress.Parse("173.245.48.1"));
        var allowed = Testing.TestContexts.Create("aop.test", scheme: "https", remote: IPAddress.Parse("173.245.48.1"));
        allowed.Features.Set<Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature>(new FakeTlsConnection(_cloudflare.Certificate));

        Testing.AllocAssert.Zero((gate, refused), static s => _ = s.gate.Decide(s.refused), "an Authenticated Origin Pulls refusal");
        Testing.AllocAssert.Zero((gate, allowed), static s => _ = s.gate.Decide(s.allowed), "an Authenticated Origin Pulls pass");
    }

    [Fact]
    public void TheSimulatorReachesTheVerdictsTheRealServerGaveAbove()
    {
        var snapshot = Snapshot(http3: true);
        var edge = IPAddress.Parse("173.245.48.1");
        GateProbe Probe(string host, ProbeArrival arrival, bool https = true, string? sni = null, ProbeHttpVersion version = ProbeHttpVersion.Http11, bool? certificate = null) => new()
        {
            Host = host,
            Https = https,
            ClientAddress = edge,
            PeerAddress = edge,
            Arrival = arrival,
            ServerName = sni,
            HttpVersion = version,
            ClientCertificate = certificate,
        };

        // Cloudflare presents its certificate: through the handshake and the gate.
        Assert.Equal(GateOutcomeAction.Forward, GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy)).Outcome.Action);

        // Nothing presented (a direct client, a tunnel): the handshake fails.
        foreach (var arrival in new[] { ProbeArrival.Direct, ProbeArrival.CloudflareTunnel })
        {
            var run = GateSimulator.Run(snapshot, Probe("aop.test", arrival));
            Assert.Equal((GateOutcomeAction.Abort, GateReason.HandshakeRejected), (run.Outcome.Action, run.Outcome.Reason));
            Assert.Contains("requires Cloudflare's client certificate", Assert.Single(run.Steps, s => s.Kind == GateStepKind.TlsHandshake).Summary, StringComparison.Ordinal);
        }

        Assert.Equal(GateReason.HandshakeRejected, GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy, certificate: false)).Outcome.Reason);

        // The setting asks nothing of a host without it.
        Assert.Equal(GateOutcomeAction.Forward, GateSimulator.Run(snapshot, Probe("plain.test", ProbeArrival.Direct)).Outcome.Action);

        // A coalesced connection (SNI of the open host) reaches the gate without a certificate: 421, so the client retries. Plain
        // HTTP can't carry one: 403.
        var coalesced = GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy, sni: "plain.test"));
        Assert.Equal((GateOutcomeAction.Respond, GateReason.MisdirectedConnection, 421), (coalesced.Outcome.Action, coalesced.Outcome.Reason, coalesced.Outcome.StatusCode));
        var coalescedStep = Assert.Single(coalesced.Steps, s => s.Kind == GateStepKind.ClientCertificate);
        Assert.Equal(GateStepVerdict.Stopped, coalescedStep.Verdict);
        Assert.Contains("421", coalescedStep.Summary, StringComparison.Ordinal);
        var plainHttp = GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy, https: false));
        Assert.Equal((GateOutcomeAction.Respond, GateReason.ClientCertificate, 403), (plainHttp.Outcome.Action, plainHttp.Outcome.Reason, plainHttp.Outcome.StatusCode));

        var passed = GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy));
        Assert.Equal(GateStepVerdict.Passed, Assert.Single(passed.Steps, s => s.Kind == GateStepKind.ClientCertificate).Verdict);

        // HTTP/3: the QUIC handshake for the name is refused, and the response headers step says no Alt-Svc and why.
        var quic = GateSimulator.Run(snapshot, Probe("aop.test", ProbeArrival.CloudflareProxy, version: ProbeHttpVersion.Http3), listeners: new ListenerFacts(null, null, Http3: true));
        Assert.Equal((GateOutcomeAction.Abort, GateReason.HandshakeRejected), (quic.Outcome.Action, quic.Outcome.Reason));
        var headers = Assert.Single(passed.Steps, s => s.Kind == GateStepKind.ResponseHeaders);
        Assert.Contains("Authenticated Origin Pulls", Assert.Single(headers.Facts, f => f.Name == "Alt-Svc").Value, StringComparison.Ordinal);
    }

    private static Action<SslClientAuthenticationOptions> Present(X509Certificate2 certificate) =>
        options => options.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;

    private Task<TlsTestServer> StartAsync(GateMiddleware gate)
    {
        TlsHandshakeSelector selector = new(_store);
        return TlsTestServer.StartAsync(
            static web => web.UseKestrelHttpsConfiguration(),
            kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(selector.CreateTcpCallbackOptions());
            }),
            context => gate.InvokeAsync(context, static c => c.Response.WriteAsync("ok")));
    }

#pragma warning disable SYSLIB0058 // The interface still carries the members SslStream obsoleted; nothing reads them.
    private sealed class FakeTlsHandshake(string hostName) : Microsoft.AspNetCore.Connections.Features.ITlsHandshakeFeature
    {
        public SslProtocols Protocol => SslProtocols.Tls13;

        public TlsCipherSuite? NegotiatedCipherSuite => null;

        public string HostName { get; } = hostName;

        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.None;

        public int CipherStrength => 0;

        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.None;

        public int HashStrength => 0;

        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;

        public int KeyExchangeStrength => 0;
    }
#pragma warning restore SYSLIB0058

    private sealed class FakeTlsConnection(X509Certificate2 certificate) : Microsoft.AspNetCore.Http.Features.ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate;

        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) => Task.FromResult(ClientCertificate);
    }

    private sealed class Single(CertificateId id, ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId candidate, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = candidate == id ? certificate : null;
            return resolved is not null;
        }
    }
}
