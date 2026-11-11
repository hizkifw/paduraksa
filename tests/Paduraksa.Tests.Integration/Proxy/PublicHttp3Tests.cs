using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;
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
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await EdgeUpstream.StartAsync();
        using TestCertificates certificates = new();
        ClientInfoProbe probe = new();
        await using var host = await StartOnFreePortAsync(probe);
        Assert.True(host.Shared.PublicListeners.Http3);
        Assert.Equal(3, host.GetListenAddresses(HostRole.Public).Count); // http, https (TCP), https (UDP)

        host.Shared.Certificates.Set(TestIds.Certificate("app"), certificates.Create("app.test", "app.test"));
        var snapshot = Compile(host, new ProxyConfigModel
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
        var published = await host.Shared.SnapshotPublisher.PublishAsync(snapshot, ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));

        // H2 over TCP advertises H3 on the public port (the 443 default), never the bound port: as if published 443 -> bound.
        using (var tcp = EdgeClient.Create(out _))
        using (var h2 = await tcp.SendAsync(EdgeClient.Request(host, "https", "app.test", "/h2", HttpVersion.Version20), ct))
        {
            Assert.Equal(HttpVersion.Version20, h2.Version);
            Assert.Equal("h3=\":443\"", string.Join(", ", h2.Headers.GetValues("Alt-Svc")));
            Assert.Equal("max-age=31536000", Assert.Single(h2.Headers.GetValues("Strict-Transport-Security")));
        }

        // H3 over QUIC: the host's certificate, forwarded upstream with the client from the QUIC connection.
        using var quic = EdgeClient.Create(out var served);
        using var h3 = await quic.SendAsync(EdgeClient.Request(host, "https", "app.test", "/h3?x=1", HttpVersion.Version30), ct);
        var seen = EdgeUpstream.Parse(await h3.Content.ReadAsStringAsync(ct));
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
        var (_, info) = Assert.Single(probe.Seen, s => s.Protocol == "HTTP/3");
        Assert.NotNull(info);
        Assert.Equal(IpKey.Parse("127.0.0.1"), info.Address);
        Assert.Equal(ConnectionAddressSource.Socket, info.Source);

        // A host that opted out of H3 is refused on QUIC but still served on TCP.
        using var refused = EdgeClient.Create(out _);
        await Assert.ThrowsAsync<HttpRequestException>(() => refused.SendAsync(EdgeClient.Request(host, "https", "tcp.test", "/", HttpVersion.Version30), ct));
        Assert.Equal(1, host.Shared.PublicListeners.Selector.Counters.Http3Disabled);

        // A QUIC connection negotiated for another entry (no SNI: the default certificate, H3 on) can't carry a request
        // for a host that turned H3 off: 421, so the client retries over TCP.
        using var coalesced = EdgeClient.Create(out _);
        using HttpRequestMessage literal = new(HttpMethod.Get, TestHost.Endpoint(host, HostRole.Public, "https"))
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var misdirected = await coalesced.SendAsync(literal, ct);
        Assert.Equal(HttpVersion.Version30, misdirected.Version);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, misdirected.StatusCode);
    }

    [Fact]
    public async Task TheProxysOwnHtmlPagesKeepTheirBodyOverHttp3()
    {
        // A page is written into the body pipe after the response starts. Over HTTP/3 a body left unflushed when the request
        // ends was dropped: the 404 and 502 pages, and the proof-of-work challenge, reached Safari with no bytes at all.
        QuicSupport.Require();
        var ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        await using var host = await StartOnFreePortAsync(new ClientInfoProbe());
        host.Shared.Certificates.Set(TestIds.Certificate("app"), certificates.Create("app.test", "app.test"));
        using (TcpListener closed = new(IPAddress.Loopback, 0))
        {
            closed.Start();
            var port = ((IPEndPoint)closed.LocalEndpoint).Port;
            closed.Stop();
            var snapshot = Compile(host, new ProxyConfigModel
            {
                Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("down"), Destinations = [new(TestIds.Destination("down-1"), $"http://127.0.0.1:{port}")] }],
                Hosts =
                [
                    new HostSpec
                    {
                        Id = TestIds.Host("app"),
                        Names = ["app.test"],
                        Certificate = TestIds.Certificate("app"),
                        Routes = [new RouteSpec { Id = TestIds.Route("app-down"), Path = "/down", Upstream = TestIds.Upstream("down") }],
                    },
                ],
            });
            var published = await host.Shared.SnapshotPublisher.PublishAsync(snapshot, ct);
            Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        }

        using var quic = EdgeClient.Create(out _);
        foreach (var (path, status, heading) in new[] { ("/nothing-here", HttpStatusCode.NotFound, "404 Not Found"), ("/down", HttpStatusCode.BadGateway, "502 Bad Gateway") })
        {
            using var request = EdgeClient.Request(host, "https", "app.test", path, HttpVersion.Version30);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await quic.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.Equal(HttpVersion.Version30, response.Version);
            Assert.Equal(status, response.StatusCode);
            Assert.Contains($"<h1>{heading}</h1>", body, StringComparison.Ordinal);
            Assert.EndsWith("</html>\n", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnOperatorsErrorPagesAndMaintenanceAreWrittenWholeOverHttp3()
    {
        // HTTP/3 drops what is left in the body pipe when the request ends, so a page that isn't flushed arrives without its body
        // (Safari got the built-in pages that way); the custom pages go through the same writer, and this run pins them on QUIC.
        QuicSupport.Require();
        var ct = TestContext.Current.CancellationToken;
        using TestCertificates certificates = new();
        await using var host = await StartOnFreePortAsync(new ClientInfoProbe());
        host.Shared.Certificates.Set(TestIds.Certificate("app"), certificates.Create("app.test", "app.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("down"), certificates.Create("down.test", "down.test"));
        var snapshot = Compile(host, new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("none"), Destinations = [new(TestIds.Destination("none-1"), "http://127.0.0.1:1/")] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Certificate = TestIds.Certificate("app"),
                    Routes = [new RouteSpec { Id = TestIds.Route("app-only"), Path = "/only", Upstream = TestIds.Upstream("none") }],
                    ErrorPages = new HostErrorPagesSpec
                    {
                        Pages = new Dictionary<int, ErrorPageSpec> { [404] = new() { Heading = "{status} on {host}", Text = "{requestId}", Source = ErrorPageSource.Host } },
                    },
                },
                new HostSpec
                {
                    Id = TestIds.Host("down"),
                    Names = ["down.test"],
                    Certificate = TestIds.Certificate("down"),
                    Routes = [new RouteSpec { Id = TestIds.Route("down-root"), Upstream = TestIds.Upstream("none") }],
                    Maintenance = new HostMaintenanceSpec { Enabled = true, RetryAfterSeconds = 60 },
                },
            ],
        });
        var published = await host.Shared.SnapshotPublisher.PublishAsync(snapshot, ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));

        using var quic = EdgeClient.Create(out _);
        foreach (var (name, status, expected) in new (string, HttpStatusCode, string)[]
        {
            ("app.test", HttpStatusCode.NotFound, "<h1>404 on app.test</h1>"),
            ("down.test", HttpStatusCode.ServiceUnavailable, "<h1>Down for maintenance</h1>"),
        })
        {
            using var request = EdgeClient.Request(host, "https", name, "/nothing", HttpVersion.Version30);
            request.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var response = await quic.SendAsync(request, ct);
            Assert.Equal(HttpVersion.Version30, response.Version);
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(expected, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RequestLimitsHoldOverHttp3()
    {
        // The gate's refusals and Kestrel's cut-off of a streaming body are the same code on every protocol; QUIC is the one this
        // machine usually can't run, so the Docker run pins it: a body under the limit forwards, a declared one over it is
        // refused before the upstream sees it, and one with no length is cut off at the limit.
        QuicSupport.Require();
        var ct = TestContext.Current.CancellationToken;
        const long Limit = 64 * 1024;
        await using var upstream = await BodyCountingUpstream.StartAsync(ct);
        using TestCertificates certificates = new();
        await using var host = await StartOnFreePortAsync(new ClientInfoProbe());
        host.Shared.Certificates.Set(TestIds.Certificate("app"), certificates.Create("app.test", "app.test"));
        var snapshot = Compile(host, new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("body"), Destinations = [new(TestIds.Destination("body-1"), upstream.Address)] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("app"),
                    Names = ["app.test"],
                    Certificate = TestIds.Certificate("app"),
                    RequestLimits = new HostRequestLimitsSpec { MaxBodySize = Limit, MaxUrlLength = 1024, MaxHeaderCount = 20 },
                    Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("body") }],
                },
            ],
        });
        var published = await host.Shared.SnapshotPublisher.PublishAsync(snapshot, ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));

        HttpRequestMessage Upload(long length, bool declared)
        {
            var request = EdgeClient.Request(host, "https", "app.test", "/", HttpVersion.Version30);
            request.Method = HttpMethod.Post;
            request.Content = new StreamContent(new ZeroStream(length));
            if (declared)
            {
                request.Content.Headers.ContentLength = length;
            }

            return request;
        }

        using var quic = EdgeClient.Create(out _);
        using (var within = await quic.SendAsync(Upload(40 * 1024, declared: true), ct))
        {
            Assert.Equal(HttpVersion.Version30, within.Version);
            Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        }

        Assert.Equal(40 * 1024, upstream.Bytes);

        using (var declared = await quic.SendAsync(Upload(Limit + 1, declared: true), ct))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
            Assert.Equal("Paduraksa", Assert.Single(declared.Headers.GetValues("Server")));
        }

        Assert.Equal(40 * 1024, upstream.Bytes);
        Assert.Equal(1, upstream.Targets.Count(target => target.StartsWith("POST ", StringComparison.Ordinal)));

        // No length: the server cuts the body off at the limit, with the answer or a reset stream.
        try
        {
            using var streamed = await quic.SendAsync(Upload(4 * Limit, declared: false), ct);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
        }
        catch (HttpRequestException)
        {
        }

        Assert.True(upstream.Bytes <= 40 * 1024 + Limit, $"the upstream received {upstream.Bytes} bytes");

        using (var url = await quic.SendAsync(EdgeClient.Request(host, "https", "app.test", "/" + new string('a', 1100), HttpVersion.Version30), ct))
        {
            Assert.Equal(HttpStatusCode.RequestUriTooLong, url.StatusCode);
        }

        using var headers = EdgeClient.Request(host, "https", "app.test", "/", HttpVersion.Version30);
        for (var i = 0; i < 30; i++)
        {
            headers.Headers.TryAddWithoutValidation($"X-Pad-{i}", "v");
        }

        using var many = await quic.SendAsync(headers, ct);
        Assert.Equal(HttpStatusCode.RequestHeaderFieldsTooLarge, many.StatusCode);

        // One 20 KiB field (a cookie) is under the host's limit of header bytes, and the transport takes it.
        using var cookie = EdgeClient.Request(host, "https", "app.test", "/", HttpVersion.Version30);
        cookie.Headers.TryAddWithoutValidation("Cookie", "c=" + new string('c', 20 * 1024));
        using var cookieResponse = await quic.SendAsync(cookie, ct);
        Assert.Equal(HttpStatusCode.OK, cookieResponse.StatusCode);
    }

    private static async Task<PaduraksaHost> StartOnFreePortAsync(ClientInfoProbe probe)
    {
        for (var attempt = 1; ; attempt++)
        {
            var host = TestHost.Create(
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
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is AddressInUseException or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }
}
