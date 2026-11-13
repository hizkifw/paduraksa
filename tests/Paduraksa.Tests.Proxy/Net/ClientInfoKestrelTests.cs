using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Shared.ProxyProtocolTestData;

namespace Paduraksa.Tests.Proxy.Net;

public sealed class ClientInfoKestrelTests : IAsyncLifetime
{
    // Policies for the /alloc endpoint: the PROXY-protocol source 203.0.113.42 is the trusted proxy.
    private static readonly TrustedProxyPolicy CloudflareAtTestNet = TrustedProxyPolicy.CloudflareProxy(CidrSet.Parse(["203.0.113.0/24"]));
    private static readonly TrustedProxyPolicy GenericAtTestNet = TrustedProxyPolicy.Generic(CidrSet.Parse(["203.0.113.0/24"]));

    private IHost? _host;
    private X509Certificate2? _certificate;
    private readonly Dictionary<string, Uri> _listeners = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _certificate = CreateSelfSignedCertificate();
        var certificate = _certificate;
        var trustedLoopback = CidrSet.Parse(["127.0.0.0/8", "::1"]);
        var trustedElsewhere = CidrSet.Parse(["10.0.0.0/8"]);
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, 0, l => l.UseClientInfo(new ProxyProtocolOptions(trustedLoopback)).Protocols = HttpProtocols.Http1);
                    kestrel.Listen(IPAddress.Loopback, 0, l => l.UseClientInfo(new ProxyProtocolOptions(trustedLoopback)).Protocols = HttpProtocols.Http2);
                    kestrel.Listen(IPAddress.Loopback, 0, l => l.UseClientInfo(new ProxyProtocolOptions(trustedElsewhere)).Protocols = HttpProtocols.Http1);
                    kestrel.Listen(IPAddress.Loopback, 0, l => l.UseClientInfo().Protocols = HttpProtocols.Http1);
                    kestrel.Listen(IPAddress.Loopback, 0, l =>
                    {
                        // PROXY protocol first, TLS second: the header arrives in clear ahead of the ClientHello.
                        l.Protocols = HttpProtocols.Http1AndHttp2;
                        l.UseClientInfo(new ProxyProtocolOptions(trustedLoopback));
                        l.UseHttps(certificate);
                    });
                })
                .Configure(app => app.Run(static context =>
                {
                    var info = context.Features.Get<IClientInfoFeature>()?.ClientInfo;
                    if (info is not null && context.Request.Path == "/alloc")
                    {
                        return context.Response.WriteAsync(MeasureResolve(info, context.Request.Headers), context.RequestAborted);
                    }

                    return context.Response.WriteAsync(
                        info is null ? "none" : $"{info.Address}|{info.Port}|{info.Source}|{info.TransportAddress}|{context.Request.Protocol}",
                        context.RequestAborted);
                })))
            .Build();
        await _host.StartAsync(Ct);

        var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        Assert.Equal(5, addresses.Length);
        string[] names = ["pp-h1", "pp-h2", "pp-untrusted", "socket", "pp-tls"];
        for (var i = 0; i < names.Length; i++)
        {
            _listeners[names[i]] = new Uri(addresses[i]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        _certificate?.Dispose();
    }

    /// <summary>A throwaway self-signed server certificate for the TLS listener (the client accepts any certificate).</summary>
    private static X509Certificate2 CreateSelfSignedCertificate()
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback TLS listener; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning restore RS0030
    }

    /// <summary>Runs <see cref="AllocAssert"/> over the resolver with Kestrel's real request headers (H1: per-connection
    /// <c>HttpRequestHeaders</c>; H2: per-stream), on the request thread, synchronously.</summary>
    private static string MeasureResolve(ClientInfo info, IHeaderDictionary headers)
    {
        // Settled: a lone counter jump under parallel load isn't an allocation (see AllocAssert.MeasureSettled).
        var cloudflare = AllocAssert.MeasureSettled((info, headers), static s => _ = ClientIpResolver.Resolve(s.info, s.headers, CloudflareAtTestNet), warmup: 200, iterations: 2_000);
        var generic = AllocAssert.MeasureSettled((info, headers), static s => _ = ClientIpResolver.Resolve(s.info, s.headers, GenericAtTestNet), warmup: 200, iterations: 2_000);
        var cf = ClientIpResolver.Resolve(info, headers, CloudflareAtTestNet);
        var xff = ClientIpResolver.Resolve(info, headers, GenericAtTestNet);
        return $"{cloudflare.TotalBytes}|{generic.TotalBytes}|{cf.Address}|{cf.Source}|{xff.Address}|{xff.Source}";
    }

    [Theory]
    [InlineData("pp-h1", "1.1")]
    [InlineData("pp-h2", "2.0")]
    public async Task ResolverIsAllocationFreeOverKestrelHeaders(string listener, string version)
    {
        using var client = Client(V2(source: "203.0.113.42"), Version.Parse(version));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_listeners[listener], "/alloc"))
        {
            Version = client.DefaultRequestVersion,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Add(CloudflareHeaders.ConnectingIp, "2001:db8::77");
        request.Headers.Add("X-Forwarded-For", "198.51.100.7, 203.0.113.9");
        using var response = await client.SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal("0|0|2001:db8::77|CfConnectingIp|198.51.100.7|ForwardingHeader", body);
    }

    [Theory]
    [InlineData(1, "1.1", "HTTP/1.1")]
    [InlineData(2, "1.1", "HTTP/1.1")]
    [InlineData(1, "2.0", "HTTP/2")]
    [InlineData(2, "2.0", "HTTP/2")]
    public async Task ProxyProtocolHeaderBeforeTlsHandshake(int ppVersion, string version, string protocol)
    {
        var header = ppVersion == 1
            ? V1("PROXY TCP4 198.51.100.20 192.0.2.1 40404 443\r\n")
            : V2(source: "198.51.100.20", sourcePort: 40404);
        using var client = Client(header, Version.Parse(version));
        var uri = new UriBuilder(_listeners["pp-tls"]) { Scheme = Uri.UriSchemeHttps }.Uri;
        for (var i = 0; i < 2; i++)
        {
            var body = await client.GetStringAsync(uri, Ct);
            Assert.Equal($"198.51.100.20|40404|ProxyProtocol|127.0.0.1|{protocol}", body);
        }
    }

    [Fact]
    public async Task TlsListenerWithoutProxyHeaderIsDropped()
    {
        using var client = Client([], HttpVersion.Version11);
        var uri = new UriBuilder(_listeners["pp-tls"]) { Scheme = Uri.UriSchemeHttps }.Uri;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(uri, Ct));
    }

    /// <summary>The header is read once per connection: a second PROXY line later on is just a bad HTTP request.</summary>
    [Fact]
    public async Task SecondProxyLineOnTheConnectionIsABadRequest()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(IPAddress.Loopback, _listeners["pp-h1"].Port, Ct);
        var bytes = Concat(
            V1("PROXY TCP4 203.0.113.7 192.0.2.1 50123 80\r\n"),
            V1("GET / HTTP/1.1\r\nHost: test\r\n\r\n"),
            V1("PROXY TCP4 198.51.100.66 192.0.2.1 50124 80\r\n"),
            V1("GET / HTTP/1.1\r\nHost: test\r\n\r\n"));
        await socket.SendAsync(bytes, SocketFlags.None, Ct);

        // Kestrel answers the first request, then 400 and closes on the bogus request line.
        var received = new List<byte>();
        var buffer = new byte[4096];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int read;
        while ((read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token)) > 0)
        {
            received.AddRange(buffer.AsSpan(0, read));
        }

        var text = System.Text.Encoding.ASCII.GetString([.. received]);
        Assert.StartsWith("HTTP/1.1 200 ", text, StringComparison.Ordinal);
        Assert.Contains("203.0.113.7|50123|ProxyProtocol|", text, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 400 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("198.51.100.66", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "pp-h1", "HTTP/1.1")]
    [InlineData(2, "pp-h1", "HTTP/1.1")]
    [InlineData(1, "pp-h2", "HTTP/2")]
    [InlineData(2, "pp-h2", "HTTP/2")]
    public async Task ProxyProtocolClientInfoReachesTheRequest(int ppVersion, string listener, string protocol)
    {
        var header = ppVersion == 1
            ? V1("PROXY TCP6 2001:db8::42 2001:db8::1 61000 443\r\n")
            : V2(family: 1, source: "203.0.113.42", destination: "192.0.2.1", sourcePort: 61000, tlvBytes: 32);
        var expectedClient = ppVersion == 1 ? "2001:db8::42" : "203.0.113.42";

        using var client = Client(header, protocol == "HTTP/2" ? HttpVersion.Version20 : HttpVersion.Version11);
        // Two requests on one connection: the header is read once and the feature is per connection.
        for (var i = 0; i < 2; i++)
        {
            var body = await client.GetStringAsync(_listeners[listener], Ct);
            Assert.Equal($"{expectedClient}|61000|ProxyProtocol|127.0.0.1|{protocol}", body);
        }
    }

    [Fact]
    public async Task LocalCommandFallsBackToTheSocketPeer()
    {
        using var client = Client(V2(command: 0, family: 0, transport: 0), HttpVersion.Version11);
        var body = await client.GetStringAsync(_listeners["pp-h1"], Ct);
        Assert.StartsWith("127.0.0.1|", body, StringComparison.Ordinal);
        Assert.Contains("|ProxyProtocolUnspecified|", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingHeaderIsDropped()
    {
        using var client = Client([], HttpVersion.Version11);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(_listeners["pp-h1"], Ct));
    }

    [Fact]
    public async Task UntrustedSourceIsDropped()
    {
        using var client = Client(V2(), HttpVersion.Version11);

        // The server aborts an untrusted peer before reading anything, so depending on timing the client fails
        // either while writing the PROXY preamble in ConnectCallback (raw SocketException) or on the HTTP exchange.
        var error = await Assert.ThrowsAnyAsync<Exception>(() => client.GetStringAsync(_listeners["pp-untrusted"], Ct));
        Assert.True(
            error is HttpRequestException or SocketException or IOException,
            $"Expected a connection failure, got {error.GetType()}: {error.Message}");
    }

    [Fact]
    public async Task WithoutProxyProtocolTheSocketPeerIsUsed()
    {
        using var client = Client([], HttpVersion.Version11);
        var body = await client.GetStringAsync(_listeners["socket"], Ct);
        Assert.StartsWith("127.0.0.1|", body, StringComparison.Ordinal);
        Assert.Contains("|Socket|", body, StringComparison.Ordinal);
    }

    /// <summary>An HttpClient whose every new connection starts with <paramref name="preamble"/> (sent in clear,
    /// before any TLS handshake).</summary>
    private HttpClient Client(byte[] preamble, Version version)
    {
        var pinnedHash = _certificate?.GetCertHashString();
        var handler = new SocketsHttpHandler
        {
            // Pin the TLS listener's throwaway self-signed certificate.
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && pinnedHash is not null && certificate.GetCertHashString() == pinnedHash,
            },
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                    var stream = new NetworkStream(socket, ownsSocket: true);
                    await stream.WriteAsync(preamble, cancellationToken);
                    return stream;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler)
        {
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };
    }
}
