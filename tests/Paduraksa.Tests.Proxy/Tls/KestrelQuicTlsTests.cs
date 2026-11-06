using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// The full public layout (TCP H1/H2 + UDP H3 on one port) on a real Kestrel with QUIC. Skipped without libmsquic;
/// <c>run-quic-tests.sh</c> runs these in Docker.
/// </summary>
[Trait("Transport", "Quic")]
public sealed class KestrelQuicTlsTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly ServerCertificate _a;
    private readonly ServerCertificate _b;
    private readonly ServerCertificate _wild;
    private readonly ServerCertificate _default;

    public KestrelQuicTlsTests()
    {
        _a = _pki.Leaf("a.test v1", "a.test");
        _b = _pki.Leaf("b.test v1", "b.test");
        _wild = _pki.Leaf("*.wild.test v1", "*.wild.test");
        _default = _pki.Leaf("default.test", "default.test");
    }

    public void Dispose() => _pki.Dispose();

    private CertificateTable Table(bool withDefault = true)
    {
        var builder = new CertificateTableBuilder()
            .Add(["a.test"], _a, TlsPolicy.Default)
            .Add(["b.test"], _b, new TlsPolicy { Http3Enabled = false })
            .Add(["*.wild.test"], _wild, TlsPolicy.Default)
            .Add(["tcp.wild.test"], _b, new TlsPolicy { Http3Enabled = false }); // H3 off under an H3-enabled wildcard
        if (withDefault)
        {
            builder.SetDefault(_default, TlsPolicy.Default);
        }

        return builder.Build();
    }

    private static async Task<TlsTestServer> StartAsync(TlsHandshakeSelector selector, IPAddress? address = null, RequestDelegate? handler = null)
    {
        QuicTestSupport.Require();
        var port = QuicTestSupport.FreeTcpAndUdpPort(address ?? IPAddress.Any);
        var http3 = false;
        var server = await TlsTestServer.StartAsync(
            static web => web.UsePublicHttpsTransports(),
            kestrel => http3 = kestrel.ListenPublicHttps(new PublicHttpsListenerOptions { Address = address, Port = port }, selector),
            handler);
        Assert.True(http3);
        Assert.Equal(port, server.Port);
        return server;
    }

    [Fact]
    public void PortZeroIsRejectedWithHttp3()
    {
        QuicTestSupport.Require();
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table()));

        // Validated before any listener is added (TCP and UDP must share one port).
        Assert.Throws<ArgumentException>(() => new KestrelServerOptions().ListenPublicHttps(new PublicHttpsListenerOptions { Port = 0 }, selector));
    }

    [Fact]
    public async Task ServesEachNameItsCertificateOverHttp3WithTheIntermediate()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table()));
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        (string? Sni, string Expected)[] cases =
        [
            ("a.test", "a.test v1"),
            ("x.wild.test", "*.wild.test v1"),
            ("unknown.test", "default.test"),
            (null, "default.test"),
        ];
        foreach (var (sni, expected) in cases)
        {
            foreach (var version in new[] { HttpVersion.Version30, HttpVersion.Version20 })
            {
                var result = await TlsProbe.GetAsync(_pki, server.Port, sni, version);

                Assert.Equal(expected, result.ServedSubject);
                Assert.Equal(version, result.Version);
                Assert.Equal(version.Major == 3 ? "HTTP/3" : "HTTP/2", result.Body);
                Assert.Equal(2, result.Received.Count);
                Assert.Contains(_pki.Intermediate.Thumbprint, result.Received);
                Assert.True(result.ChainTrusted);
                Assert.Null(result.AltSvc); // Kestrel's (bound-port) Alt-Svc is off on both listeners
            }
        }
    }

    [Fact]
    public async Task IdnNamesMatchOnBothTransports()
    {
        // QUIC passes the punycode SNI through; TCP (SslStream) decodes it to Unicode. Both must hit the same entry.
        var table = new CertificateTableBuilder()
            .Add(["xn--bcher-kva.test"], _pki.Leaf("idn exact", "xn--bcher-kva.test"), TlsPolicy.Default)
            .Add(["*.xn--caf-dma.test"], _pki.Leaf("idn wildcard", "*.xn--caf-dma.test"), TlsPolicy.Default)
            .Build(); // no default: a miss is a rejection, not a silent fallback
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table));
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        foreach (var version in new[] { HttpVersion.Version30, HttpVersion.Version20 })
        {
            Assert.Equal("idn exact", (await TlsProbe.GetAsync(_pki, server.Port, "xn--bcher-kva.test", version)).ServedSubject);
            Assert.Equal("idn wildcard", (await TlsProbe.GetAsync(_pki, server.Port, "x.xn--caf-dma.test", version)).ServedSubject);
        }

        Assert.Equal(0, selector.RejectedUnknownServerNameQuic);
        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);
    }

    [Fact]
    public async Task RequiredClientCertificateIsEnforcedOverQuicWithoutDownloads()
    {
        await using var aia = new HitCounter();
        var trusted = _pki.ClientCertificateWithAia("origin-pull", aia.Url);
        var other = _pki.ClientCertificateWithAia("someone-else", aia.Url);
        var thumbprint = trusted.Thumbprint;
        var policy = new TlsPolicy
        {
            ClientCertificate = new ClientCertificatePolicy
            {
                Validator = (_, certificate, _, _) => certificate is X509Certificate2 c && c.Thumbprint == thumbprint,
            },
        };
        var table = new CertificateTableBuilder()
            .Add(["locked.test"], _a, policy)
            .Add(["open.test"], _b, TlsPolicy.Default)
            .Build();
        await using var server = await StartAsync(
            new TlsHandshakeSelector(new CertificateTableSource(table)),
            IPAddress.Loopback,
            static context => context.Response.WriteAsync(context.Connection.ClientCertificate?.Thumbprint ?? "none"));
        // Offline contexts so the client itself never chases the AIA URL.
        static Action<SslClientAuthenticationOptions> Present(X509Certificate2 certificate) =>
            options => options.ClientCertificateContext = SslStreamCertificateContext.Create(certificate, null, offline: true);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version30, configureSsl: Present(other)));
        Assert.Equal(thumbprint, (await TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version30, configureSsl: Present(trusted))).Body);
        Assert.Equal(0, aia.Hits);
    }

    /// <summary>
    /// Pins the release behaviour the <see cref="QuicConnectionGate"/> contract documents: <c>ConnectionClosed</c> fires
    /// for a completed connection, but not when the handshake fails after the gate accepted. If the second case starts
    /// failing, Kestrel now signals those closes: simplify the contract.
    /// </summary>
    [Theory]
    [InlineData(false)] // handshake completes, the client goes away
    [InlineData(true)] // handshake fails *after* the gate accepted (client certificate missing)
    public async Task ConnectionClosedReleasesWhatTheGateAcquired(bool failAfterGate)
    {
        var slots = new SlotCounter();
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _a, TlsPolicy.Default)
            .Add(["locked.test"], _b, new TlsPolicy { ClientCertificate = new ClientCertificatePolicy() })
            .Build();
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table), slots.Gate);
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        if (failAfterGate)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version30));
        }
        else
        {
            Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);
        }

        Assert.Equal(1, slots.Acquired);
        var deadline = DateTime.UtcNow.AddSeconds(failAfterGate ? 3 : 15);
        while (slots.Released < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // Known gap (see QuicConnectionGate): no ConnectionClosed after a handshake that failed past the gate.
        Assert.Equal(failAfterGate ? 0 : 1, slots.Released);
    }

    [Fact]
    public async Task RejectsUnknownNamesAndHostsWithoutHttp3()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)));
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "unknown.test", HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, null, HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "tcp.wild.test", HttpVersion.Version30));

        Assert.Equal(2, selector.RejectedUnknownServerNameQuic);
        Assert.Equal(2, selector.RejectedHttp3Disabled);
        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);

        // The opted-out host is still served over TCP, and the QUIC listener is still healthy.
        Assert.Equal("b.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version20)).ServedSubject);
        Assert.Equal("b.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "tcp.wild.test", HttpVersion.Version20)).ServedSubject);
        Assert.Equal("*.wild.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "x.wild.test", HttpVersion.Version30)).ServedSubject);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);
    }

    [Fact]
    public async Task HotSwapAffectsNewQuicConnections()
    {
        var source = new CertificateTableSource(Table());
        await using var server = await StartAsync(new TlsHandshakeSelector(source), IPAddress.Loopback);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);

        source.Swap(new CertificateTableBuilder()
            .Add(["a.test"], _pki.Leaf("a.test v2", "a.test"), TlsPolicy.Default)
            .SetDefault(_default, TlsPolicy.Default)
            .Build());

        Assert.Equal("a.test v2", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);
        Assert.Equal("default.test", (await TlsProbe.GetAsync(_pki, server.Port, "x.wild.test", HttpVersion.Version30)).ServedSubject);
    }

    [Fact]
    public async Task QuicGateSeesTheRemoteEndPointAndCanDeny()
    {
        var seen = new ConcurrentQueue<(IPEndPoint? Remote, bool HasConnection)>();
        var deny = true;
        var selector = new TlsHandshakeSelector(
            new CertificateTableSource(Table()),
            (remote, connection) =>
            {
                seen.Enqueue((remote, connection is not null));
                return !Volatile.Read(ref deny);
            });
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30));
        Assert.Equal(1, selector.RejectedConnectionDenied);
        // TCP isn't gated by the QUIC gate (its stage A is ListenOptions.Use).
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20)).ServedSubject);

        Volatile.Write(ref deny, false);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);

        Assert.Equal(2, seen.Count);
        foreach (var (remote, hasConnection) in seen)
        {
            Assert.NotNull(remote);
            Assert.Equal(IPAddress.Loopback, remote.Address);
            Assert.NotEqual(0, remote.Port);
            Assert.True(hasConnection);
        }
    }

    [Fact]
    public async Task AnyAddressServesIPv4ClientsOnBothTransports()
    {
        var seen = new ConcurrentQueue<IPAddress?>();
        var selector = new TlsHandshakeSelector(
            new CertificateTableSource(Table()),
            (remote, _) =>
            {
                seen.Enqueue(remote?.Address);
                return true;
            });
        await using var server = await StartAsync(selector, address: null);

        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).ServedSubject);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20)).ServedSubject);

        // On a dual-mode listener QUIC may report IPv4 plain or mapped (spike S1 saw plain): the gate normalises.
        var address = Assert.Single(seen);
        Assert.NotNull(address);
        Assert.Equal(IPAddress.Loopback, address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    }

    [Fact]
    public async Task AltSvcAdvertisesThePublicPortOnTcpOnly()
    {
        var value = new AltSvcHeader(443, http3Listening: true).Value;
        await using var server = await StartAsync(
            new TlsHandshakeSelector(new CertificateTableSource(Table())),
            IPAddress.Loopback,
            context =>
            {
                AltSvcHeader.Apply(context, value);
                return context.Response.WriteAsync(context.Request.Protocol);
            });

        Assert.Equal("h3=\":443\"", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20)).AltSvc); // parsed: default ma dropped
        Assert.Null((await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version30)).AltSvc);
    }

    /// <summary>A stand-in per-IP cap: acquires in the gate, releases on <c>ConnectionClosed</c>.</summary>
    private sealed class SlotCounter
    {
        private int _acquired;
        private int _released;

        public int Acquired => Volatile.Read(ref _acquired);

        public int Released => Volatile.Read(ref _released);

        public bool Gate(IPEndPoint? remote, Microsoft.AspNetCore.Connections.ConnectionContext? connection)
        {
            Assert.NotNull(connection);
            Interlocked.Increment(ref _acquired);
            connection.ConnectionClosed.Register(static state => Interlocked.Increment(ref ((SlotCounter)state!)._released), this);
            return true;
        }
    }
}
