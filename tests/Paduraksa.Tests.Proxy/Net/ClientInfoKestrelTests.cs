using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Paduraksa.Proxy.Net;
using static Paduraksa.Tests.Proxy.Net.ProxyProtocolTestData;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>
/// Real Kestrel on loopback: PROXY protocol v1/v2 through <see cref="ClientInfoListenOptionsExtensions.UseClientInfo"/>
/// reaches <c>HttpContext.Features</c> on HTTP/1.1 and HTTP/2 (cleartext, prior knowledge).
/// </summary>
public sealed class ClientInfoKestrelTests : IAsyncLifetime
{
    private IHost? _host;
    private readonly Dictionary<string, Uri> _listeners = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
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
                })
                .Configure(app => app.Run(static context =>
                {
                    var info = context.Features.Get<IClientInfoFeature>()?.ClientInfo;
                    return context.Response.WriteAsync(
                        info is null ? "none" : $"{info.Address}|{info.Port}|{info.Source}|{info.TransportAddress}|{context.Request.Protocol}",
                        context.RequestAborted);
                })))
            .Build();
        await _host.StartAsync(Ct);

        var addresses = _host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        Assert.Equal(4, addresses.Length);
        string[] names = ["pp-h1", "pp-h2", "pp-untrusted", "socket"];
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
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(_listeners["pp-untrusted"], Ct));
    }

    [Fact]
    public async Task WithoutProxyProtocolTheSocketPeerIsUsed()
    {
        using var client = Client([], HttpVersion.Version11);
        var body = await client.GetStringAsync(_listeners["socket"], Ct);
        Assert.StartsWith("127.0.0.1|", body, StringComparison.Ordinal);
        Assert.Contains("|Socket|", body, StringComparison.Ordinal);
    }

    /// <summary>An HttpClient whose every new connection starts with <paramref name="preamble"/>.</summary>
    private static HttpClient Client(byte[] preamble, Version version)
    {
        var handler = new SocketsHttpHandler
        {
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
