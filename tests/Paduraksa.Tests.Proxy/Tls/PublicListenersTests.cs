using System.Net;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>The listener layout on TCP. Runs everywhere; the QUIC half is in <see cref="KestrelQuicTlsTests"/>.</summary>
public sealed class PublicListenersTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly CertificateTable _table;

    public PublicListenersTests()
    {
        _table = new CertificateTableBuilder()
            .Add(["a.test"], _pki.Leaf("a.test", "a.test"), TlsPolicy.Default)
            .Build();
    }

    public void Dispose() => _pki.Dispose();

    [Theory]
    [InlineData(false)] // explicit loopback
    [InlineData(true)] // null = any (dual-mode [::])
    public async Task TcpListenerServesH1AndH2WithoutKestrelAltSvc(bool anyAddress)
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(_table));
        var http3 = true;
        var address = anyAddress ? null : IPAddress.Loopback;
        var port = 0;
        await using var server = await QuicTestSupport.StartOnFreePortAsync(address, free => TlsTestServer.StartAsync(
            static web => web.UsePublicHttpsTransports(),
            kestrel => http3 = kestrel.ListenPublicHttps(
                new PublicHttpsListenerOptions { Address = address, Port = port = free, EnableHttp3 = false },
                selector)));

        Assert.False(http3);
        Assert.Equal(port, server.Port); // a fixed public port (not 0), as in production
        foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20 })
        {
            var result = await TlsProbe.GetAsync(_pki, port, "a.test", version);
            Assert.Equal(version, result.Version);
            Assert.Equal("a.test", result.ServedSubject);
            Assert.Null(result.AltSvc);
        }
    }

    [Fact]
    public async Task TcpConnectionMiddlewareRunsBeforeTls()
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(_table));
        var gate = new CountingGate();
        await using var server = await TlsTestServer.StartAsync(
            static web => web.UsePublicHttpsTransports(),
            kestrel => kestrel.ListenPublicHttps(
                new PublicHttpsListenerOptions
                {
                    Address = IPAddress.Loopback,
                    Port = 0,
                    EnableHttp3 = false,
                    ConfigureTcpConnection = listen => listen.Use(gate.Middleware),
                },
                selector));

        Assert.Equal("a.test", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20)).ServedSubject);

        gate.Deny = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "unknown.test", HttpVersion.Version20));

        Assert.Equal(2, gate.Seen);
        Assert.Equal(0, selector.Counters.UnknownServerNameTcp); // aborted before the handshake callback ran
    }

    [Fact]
    public async Task TestServersRetryWhenThePortIsTakenBetweenProbeAndBind()
    {
        var attempts = 0;
        await using var server = await QuicTestSupport.StartOnFreePortAsync(IPAddress.Loopback, port =>
        {
            if (++attempts == 1)
            {
                throw new IOException("Failed to bind.", new AddressInUseException("Address already in use."));
            }

            return TlsTestServer.StartAsync(static _ => { }, kestrel => kestrel.ListenPublicHttp(IPAddress.Loopback, port));
        });

        Assert.Equal(2, attempts);
        Assert.NotEqual(0, server.Port);
    }

    [Fact]
    public async Task PlainHttpListenerServesHttp11()
    {
        var port = 0;
        await using var server = await TlsTestServer.StartAsync(
            static _ => { },
            kestrel => kestrel.ListenPublicHttp(IPAddress.Loopback, port));
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/"), TestContext.Current.CancellationToken);

        Assert.Equal("HTTP/1.1", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidPortsAreRejected(int port)
    {
        var selector = new TlsHandshakeSelector(new CertificateTableSource(_table));

        Assert.Throws<ArgumentOutOfRangeException>(() => new KestrelServerOptions().ListenPublicHttps(new PublicHttpsListenerOptions { Port = port }, selector));
    }

    private sealed class CountingGate
    {
        private int _seen;

        public volatile bool Deny;

        public int Seen => Volatile.Read(ref _seen);

        public ConnectionDelegate Middleware(ConnectionDelegate next) => connection =>
        {
            Interlocked.Increment(ref _seen);
            if (Deny)
            {
                connection.Abort();
                return Task.CompletedTask;
            }

            return next(connection);
        };
    }
}
