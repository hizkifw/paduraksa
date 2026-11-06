using System.Collections.Concurrent;
using System.Net;

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
            .Add(["*.wild.test"], _wild, TlsPolicy.Default);
        if (withDefault)
        {
            builder.SetDefault(_default, TlsPolicy.Default);
        }

        return builder.Build();
    }

    private static async Task<TlsTestServer> StartAsync(TlsHandshakeSelector selector, IPAddress? address = null)
    {
        QuicTestSupport.Require();
        var port = QuicTestSupport.FreeTcpAndUdpPort(address ?? IPAddress.Any);
        var http3 = false;
        var server = await TlsTestServer.StartAsync(
            static web => web.UsePublicHttpsTransports(),
            kestrel => http3 = kestrel.ListenPublicHttps(new PublicHttpsListenerOptions { Address = address, Port = port }, selector));
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
    public async Task RejectsUnknownNamesAndHostsWithoutHttp3()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)));
        await using var server = await StartAsync(selector, IPAddress.Loopback);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "unknown.test", HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, null, HttpVersion.Version30));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version30));

        Assert.Equal(2, selector.RejectedUnknownServerNameQuic);
        Assert.Equal(1, selector.RejectedHttp3Disabled);
        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);

        // The opted-out host is still served over TCP, and the QUIC listener is still healthy.
        Assert.Equal("b.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version20)).ServedSubject);
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
}
