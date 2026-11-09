using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// HTTP/3 end to end through the whole process: the UDP listener next to TCP on one port, per-SNI
/// certificates on QUIC, ClientInfo from the QUIC handshake reaching the request, and <c>Alt-Svc</c> with the public
/// port. Needs libmsquic: skipped elsewhere, run in Docker by <c>tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh</c>.
/// </summary>
[Trait("Transport", "Quic")]
public sealed class PublicHttp3Tests
{
    [Fact]
    public async Task ServesHttp3ThroughTheWholeProxy()
    {
        QuicSupport.Require();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using EdgeUpstream upstream = await EdgeUpstream.StartAsync();
        using TestCertificates certificates = new();
        ClientInfoProbe probe = new();
        await using PaduraksaHost host = await StartOnFreePortAsync(probe);
        Assert.True(host.Shared.PublicListeners.Http3);
        Assert.Equal(3, host.GetListenAddresses(HostRole.Public).Count); // http, https (TCP), https (UDP)

        host.Shared.Certificates.Set(TestIds.Certificate("app"), certificates.Create("app.test", "app.test"));
        RuntimeSnapshot snapshot = Compile(host, new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), upstream.Address)] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Certificate = TestIds.Certificate("app"),
                    Hsts = new HostHstsSpec { Enabled = true },
                    Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("echo") }],
                },
                new HostSpec { Id = TestIds.Host("tcp"), Names = ["tcp.test"], Certificate = TestIds.Certificate("app"), Tls = new HostTlsSpec { Http3 = false }, Routes = [new RouteSpec { Id = TestIds.Route("tcp-root"), Upstream = TestIds.Upstream("echo") }] },
                new HostSpec { Id = TestIds.Host("literal"), Names = ["127.0.0.1"], Tls = new HostTlsSpec { Http3 = false }, Routes = [new RouteSpec { Id = TestIds.Route("literal-root"), Upstream = TestIds.Upstream("echo") }] },
            ],
        });
        PublishResult published = await host.Shared.SnapshotPublisher.PublishAsync(snapshot, ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));

        // H2 over TCP advertises H3 on the public port (the 443 default), never the bound port: as if published 443 -> bound.
        using (HttpClient tcp = EdgeClient.Create(out _))
        using (HttpResponseMessage h2 = await tcp.SendAsync(EdgeClient.Request(host, "https", "app.test", "/h2", HttpVersion.Version20), ct))
        {
            Assert.Equal(HttpVersion.Version20, h2.Version);
            Assert.Equal("h3=\":443\"", string.Join(", ", h2.Headers.GetValues("Alt-Svc")));
            Assert.Equal("max-age=31536000", Assert.Single(h2.Headers.GetValues("Strict-Transport-Security")));
        }

        // H3 over QUIC: the host's certificate, forwarded upstream with the client from the QUIC connection.
        using HttpClient quic = EdgeClient.Create(out StrongBox<string?> served);
        using HttpResponseMessage h3 = await quic.SendAsync(EdgeClient.Request(host, "https", "app.test", "/h3?x=1", HttpVersion.Version30), ct);
        Dictionary<string, string> seen = EdgeUpstream.Parse(await h3.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpVersion.Version30, h3.Version);
        Assert.Equal(HttpStatusCode.OK, h3.StatusCode);
        Assert.Equal("app.test", served.Value);
        Assert.False(h3.Headers.Contains("Alt-Svc")); // already on H3
        Assert.Equal("max-age=31536000", Assert.Single(h3.Headers.GetValues("Strict-Transport-Security"))); // HSTS does go on H3
        Assert.Equal("Paduraksa", Assert.Single(h3.Headers.GetValues("Server"))); // and so does the host's Server header
        Assert.Equal("/h3?x=1", seen["target"]);
        Assert.Equal("127.0.0.1", seen["X-Forwarded-For"]);
        Assert.Equal("https", seen["X-Forwarded-Proto"]);

        // The ClientInfo attached in the QUIC handshake callback is what the H3 request sees.
        (string _, ClientInfo? info) = Assert.Single(probe.Seen, s => s.Protocol == "HTTP/3");
        Assert.NotNull(info);
        Assert.Equal(IpKey.Parse("127.0.0.1"), info.Address);
        Assert.Equal(ConnectionAddressSource.Socket, info.Source);

        // A host that opted out of H3 is refused on QUIC but still served on TCP.
        using HttpClient refused = EdgeClient.Create(out _);
        await Assert.ThrowsAsync<HttpRequestException>(() => refused.SendAsync(EdgeClient.Request(host, "https", "tcp.test", "/", HttpVersion.Version30), ct));
        Assert.Equal(1, host.Shared.PublicListeners.Selector.Counters.Http3Disabled);

        // A QUIC connection negotiated for another entry (no SNI: the default certificate, H3 on) can't carry a request
        // for a host that turned H3 off: 421, so the client retries over TCP.
        using HttpClient coalesced = EdgeClient.Create(out _);
        using HttpRequestMessage literal = new(HttpMethod.Get, TestHost.Endpoint(host, HostRole.Public, "https"))
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using HttpResponseMessage misdirected = await coalesced.SendAsync(literal, ct);
        Assert.Equal(HttpVersion.Version30, misdirected.Version);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, misdirected.StatusCode);
    }

    private static async Task<PaduraksaHost> StartOnFreePortAsync(ClientInfoProbe probe)
    {
        for (int attempt = 1; ; attempt++)
        {
            PaduraksaHost host = TestHost.Create(
                settings: new Dictionary<string, string?>
                {
                    ["Paduraksa:Listen:Https"] = QuicSupport.FreeTcpAndUdpPort().ToString(CultureInfo.InvariantCulture),
                    ["Paduraksa:Listen:Http3"] = "true",
                },
                configureServices: (role, services) =>
                {
                    if (role == HostRole.Public)
                    {
                        services.AddSingleton<IStartupFilter>(probe);
                    }
                });
            try
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
                return host;
            }
            catch (Exception error) when (attempt < 5 && IsAddressInUse(error))
            {
                // Lost the race for the port: another process took it between the probe and the bind.
                await host.DisposeAsync();
            }
        }
    }

    private static bool IsAddressInUse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }
}
