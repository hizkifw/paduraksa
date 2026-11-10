using System.Net;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

public sealed class PublicEdgeTests : IAsyncLifetime
{
    private static readonly CidrSet LoopbackEdge = CidrSet.Parse(["127.0.0.0/8", "::1"]);

    private EdgeUpstream? _upstream;

    private EdgeUpstream Upstream => _upstream!;

    public async ValueTask InitializeAsync() => _upstream = await EdgeUpstream.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }
    }

    private ProxyConfigModel App(params HostSpec[] more) => new()
    {
        Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] }],
        Hosts =
        [
            new HostSpec { Id = TestIds.Host("app"), Names = ["app.test"], Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("echo") }] },
            .. more,
        ],
    };

    private static async Task<PaduraksaHost> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var host = TestHost.Create(settings: settings);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task PublishAsync(PaduraksaHost host, ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        var result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model, options), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Upstream)> SendAsync(
        PaduraksaHost host, string scheme, string hostHeader, Action<HttpRequestMessage>? configure = null, string path = "/", Version? version = null)
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, scheme, hostHeader, path, version);
        configure?.Invoke(request);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, EdgeUpstream.Parse(body));
    }

    [Fact]
    public async Task ServesEachHostItsCertificateAndSwapsThemOnPublish()
    {
        using TestCertificates certificates = new();
        await using var host = await StartAsync();
        host.Shared.Certificates.Set(TestIds.Certificate("a"), certificates.Create("a.test v1", "a.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("b"), certificates.Create("b.test", "b.test", "*.b.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("fallback"), certificates.Create("fallback", "fallback.test"));
        var model = App(
            new HostSpec { Id = TestIds.Host("a"), Names = ["a.test"], Certificate = TestIds.Certificate("a"), Kind = HostKind.Respond, Respond = new RespondSpec { Body = "a" } },
            new HostSpec { Id = TestIds.Host("b"), Names = ["b.test", "*.b.test"], Certificate = TestIds.Certificate("b"), Kind = HostKind.Respond, Respond = new RespondSpec { Body = "b" } });
        model = model with { Defaults = new DefaultsSpec { Certificate = TestIds.Certificate("fallback") } };
        await PublishAsync(host, model);

        Assert.Equal("a.test v1", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Equal("b.test", await EdgeClient.ServedSubjectAsync(host, "x.b.test"));
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, "app.test")); // a host without its own certificate
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, "unknown.test"));
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, serverName: null)); // IP literal: no SNI

        // The certificate behind "a" is renewed: the next publish serves it to new handshakes, no listener restart.
        host.Shared.Certificates.Set(TestIds.Certificate("a"), certificates.Create("a.test v2", "a.test"));
        Assert.Equal("a.test v1", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        await PublishAsync(host, model with { Revision = TestIds.Revision("renewed") });
        Assert.Equal("a.test v2", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Equal(0, host.Shared.PublicListeners.Selector.Counters.UnknownServerNameTcp);
    }

    [Fact]
    public async Task UnknownNamesAreRejectedWithoutADefaultCertificate()
    {
        using TestCertificates certificates = new();
        await using var host = await StartAsync();
        host.Shared.Certificates.Set(TestIds.Certificate("b"), certificates.Create("b.test", "b.test"));

        // Compiled directly: the shared Compile helper would add the placeholder as the default certificate.
        var bare = SnapshotCompiler.Compile(
            App(new HostSpec { Id = TestIds.Host("b"), Names = ["b.test"], Certificate = TestIds.Certificate("b"), Kind = HostKind.Respond, Respond = new RespondSpec { Body = "b" } }),
            host.Shared.PublicListeners.CompilerOptions with { Certificates = host.Shared.Certificates });
        Assert.True((await host.Shared.SnapshotPublisher.PublishAsync(bare.Snapshot!, TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal("b.test", await EdgeClient.ServedSubjectAsync(host, "b.test"));
        await Assert.ThrowsAsync<HttpRequestException>(() => EdgeClient.ServedSubjectAsync(host, "unknown.test"));
        await Assert.ThrowsAsync<HttpRequestException>(() => EdgeClient.ServedSubjectAsync(host, "app.test")); // no certificate, no default
        Assert.Equal(2, host.Shared.PublicListeners.Selector.Counters.UnknownServerNameTcp);
    }

    [Theory]
    [InlineData(System.Security.Authentication.SslProtocols.Tls12, HttpStatusCode.MisdirectedRequest)]
    [InlineData(System.Security.Authentication.SslProtocols.Tls13, HttpStatusCode.OK)]
    public async Task AHostRequiringTls13RefusesAWeakerConnectionNegotiatedForAnotherName(System.Security.Authentication.SslProtocols client, HttpStatusCode expected)
    {
        await using var host = await StartAsync();
        // No SNI (an IP-literal URL): the default entry (TLS 1.2+) negotiates; the Host names the strict host.
        await PublishAsync(host, App(new HostSpec
        {
            Id = TestIds.Host("strict"),
            Names = ["127.0.0.1"],
            Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
            Routes = [new RouteSpec { Id = TestIds.Route("strict-root"), Upstream = TestIds.Upstream("echo") }],
        }));

        using var http = EdgeClient.Create(out _, client);
        using var response = await http.GetAsync(TestHost.Endpoint(host, HostRole.Public, "https"), TestContext.Current.CancellationToken);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task PlainHttpIsRedirectedToThePublicHttpsPort()
    {
        await using var host = await StartAsync(new() { ["Paduraksa:Listen:PublicHttpsPort"] = "8443" });
        await PublishAsync(host, App(new HostSpec { Id = TestIds.Host("secure"), Names = ["secure.test"], ForceHttps = true, Routes = [new RouteSpec { Id = TestIds.Route("secure-root"), Upstream = TestIds.Upstream("echo") }] }));

        var (redirect, _) = await SendAsync(host, "http", "secure.test", path: "/a%20b?c=d");
        using (redirect)
        {
            Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
            Assert.Equal("https://secure.test:8443/a%20b?c=d", redirect.Headers.Location!.OriginalString);
        }

        var (secure, seen) = await SendAsync(host, "https", "secure.test", path: "/a%20b?c=d", version: HttpVersion.Version20);
        using (secure)
        {
            Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
            Assert.Equal("/a%20b?c=d", seen["target"]);
        }
    }

    [Fact]
    public async Task ProxyProtocolOnTheHttpListenerSetsTheClientIp()
    {
        await using var host = await StartAsync(new()
        {
            ["Paduraksa:Listen:HttpProxyProtocol:Enabled"] = "true",
            ["Paduraksa:Listen:HttpProxyProtocol:TrustedSources:0"] = "127.0.0.0/8",
            ["Paduraksa:Listen:HttpProxyProtocol:TrustedSources:1"] = "::1",
            ["Paduraksa:Listen:HttpProxyProtocol:HeaderTimeout"] = "00:00:02",
        });
        await PublishAsync(host, App() with
        {
            Hosts = [App().Hosts[0] with { Policies = [new IpAllowListPolicySpec(["203.0.113.0/24"])] }],
        });

        // Counted as deltas around each of this test's own connections, like the HTTPS listener's test: another test's stray
        // connection to this host's reused port would otherwise move an absolute count.
        var counters = host.Shared.PublicListeners.HttpProxyProtocol!.Counters;

        var accepted = counters.Accepted;
        var response = await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.7 192.0.2.1 50000 80\r\nGET /pp HTTP/1.1\r\nHost: app.test\r\nX-Forwarded-For: 1.2.3.4\r\nConnection: close\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal); // the allow-list saw the PROXY protocol client
        var seen = EdgeUpstream.Parse(response);
        Assert.Equal("203.0.113.7", seen["X-Forwarded-For"]);
        Assert.Equal("203.0.113.7", seen["X-Real-IP"]);
        Assert.Equal(accepted + 1, counters.Accepted);

        // The header is mandatory on this listener: a plain request is dropped unanswered.
        var invalid = counters.Invalid;
        Assert.Equal(string.Empty, await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n")));
        Assert.Equal(invalid + 1, counters.Invalid);
        Assert.Null(host.Shared.PublicListeners.HttpsProxyProtocol);
    }

    [Fact]
    public async Task ProxyProtocolOnTheHttpsListenerIsReadBeforeTls()
    {
        await using var host = await StartAsync(new()
        {
            ["Paduraksa:Listen:HttpsProxyProtocol:Enabled"] = "true",
            ["Paduraksa:Listen:HttpsProxyProtocol:TrustedSources:0"] = "127.0.0.0/8",
            ["Paduraksa:Listen:HttpsProxyProtocol:TrustedSources:1"] = "::1",
        });
        await PublishAsync(host, App() with
        {
            Hosts = [App().Hosts[0] with { Policies = [new IpAllowListPolicySpec(["198.51.100.0/24"])] }],
        });

        // Counted as deltas around each of this test's own connections: under a parallel run another test can reach this
        // host's reused port, and an absolute count would then depend on it (the invalid count used to read 3, not 1).
        var counters = host.Shared.PublicListeners.HttpsProxyProtocol!.Counters;

        // The header arrives in clear ahead of the ClientHello; TLS and HTTP follow on the same connection.
        var accepted = counters.Accepted;
        var response = await EdgeClient.RawHttpsAsync(host, Encoding.ASCII.GetBytes("PROXY TCP4 198.51.100.20 192.0.2.1 40404 443\r\n"), "app.test", "/pp");

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal); // the allow-list saw the PROXY protocol client
        Assert.Equal(accepted + 1, counters.Accepted);
        var seen = EdgeUpstream.Parse(response);
        Assert.Equal("198.51.100.20", seen["X-Forwarded-For"]);
        Assert.Equal("https", seen["X-Forwarded-Proto"]);

        // Mandatory on this listener: TLS straight away is dropped, and the plain HTTP listener is unaffected.
        var invalid = counters.Invalid;
        Assert.Equal(string.Empty, await EdgeClient.RawHttpsAsync(host, [], "app.test"));
        Assert.Equal(invalid + 1, counters.Invalid);
        Assert.Null(host.Shared.PublicListeners.HttpProxyProtocol);
    }

    [Fact]
    public async Task CloudflareProxyModeTrustsOnlyTheEdge()
    {
        await using var host = await StartAsync();
        var model = App(new HostSpec
        {
            Id = TestIds.Host("members"),
            Names = ["members.test"],
            Policies = [new IpAllowListPolicySpec(["198.51.100.0/24"])],
            Routes = [new RouteSpec { Id = TestIds.Route("members-root"), Upstream = TestIds.Upstream("echo") }],
        }) with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy } },
        };
        static void ViaCloudflare(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.7");
            request.Headers.TryAddWithoutValidation("CF-IPCountry", "NL");
            request.Headers.TryAddWithoutValidation("CF-Ray", "8f00000000000000-AMS");
            request.Headers.TryAddWithoutValidation("Cf-Access-Authenticated-User-Email", "admin@example.com");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4");
        }

        // Loopback stands in for Cloudflare's edge: the peer is trusted and CF-Connecting-IP is the client.
        await PublishAsync(host, model, host.Shared.PublicListeners.CompilerOptions with { CloudflareEdgeRanges = LoopbackEdge });
        var (trusted, seen) = await SendAsync(host, "https", "members.test", ViaCloudflare, version: HttpVersion.Version20);
        using (trusted)
        {
            Assert.Equal(HttpStatusCode.OK, trusted.StatusCode); // the allow-list judged 198.51.100.7, not 127.0.0.1
            Assert.Equal("198.51.100.7", seen["X-Forwarded-For"]);
            Assert.Equal("198.51.100.7", seen["CF-Connecting-IP"]);
            Assert.Equal("NL", seen["CF-IPCountry"]);
        }

        // The baked-in ranges don't contain loopback: the same headers are now a spoof, ignored and stripped.
        await PublishAsync(host, model);
        var (blocked, _) = await SendAsync(host, "https", "members.test", ViaCloudflare, version: HttpVersion.Version20);
        using (blocked)
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }

        var (spoofed, clean) = await SendAsync(host, "https", "app.test", ViaCloudflare, version: HttpVersion.Version20);
        using (spoofed)
        {
            Assert.Equal(HttpStatusCode.OK, spoofed.StatusCode);
            Assert.Equal("127.0.0.1", clean["X-Forwarded-For"]);
            Assert.False(clean.ContainsKey("CF-Connecting-IP"));
            Assert.False(clean.ContainsKey("CF-IPCountry"));
            Assert.False(clean.ContainsKey("CF-Ray"));
            Assert.False(clean.ContainsKey("Cf-Access-Authenticated-User-Email")); // every cf-* header, not just the known ones
        }
    }

    [Theory]
    [InlineData("127.0.0.0/8", HttpStatusCode.OK)] // the peer is cloudflared: the visitor is on HTTPS already
    [InlineData("10.0.0.2", HttpStatusCode.MovedPermanently)] // anyone else: CF-Visitor is a spoof, redirect
    public async Task ATunnelOnPlainHttpDoesNotLoopTheHttpsRedirect(string cloudflared, HttpStatusCode expected)
    {
        await using var host = await StartAsync();
        await PublishAsync(host, App(new HostSpec { Id = TestIds.Host("secure"), Names = ["secure.test"], ForceHttps = true, Routes = [new RouteSpec { Id = TestIds.Route("secure-root"), Upstream = TestIds.Upstream("echo") }] }) with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = [cloudflared, "::1"] } },
        });

        // cloudflared forwards to http://paduraksa:80 with the visitor's scheme in CF-Visitor.
        var (response, seen) = await SendAsync(
            host,
            "http",
            "secure.test",
            static request =>
            {
                request.Headers.TryAddWithoutValidation("CF-Visitor", "{\"scheme\":\"https\"}");
                request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.7");
            });
        using (response)
        {
            Assert.Equal(expected, response.StatusCode);
            if (expected == HttpStatusCode.OK)
            {
                Assert.Equal("https", seen["X-Forwarded-Proto"]); // the same scheme the redirect decision used
                Assert.Equal("198.51.100.7", seen["X-Forwarded-For"]);
            }
            else
            {
                Assert.Equal("https://secure.test/", response.Headers.Location!.OriginalString);
            }
        }
    }

    [Fact]
    public async Task GenericModeTakesTheClientFromTheForwardedChain()
    {
        await using var host = await StartAsync();
        await PublishAsync(host, App() with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8", "::1"] } },
        });

        var (response, seen) = await SendAsync(
            host,
            "http",
            "app.test",
            static request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4, 203.0.113.5, 127.0.0.2"));
        using (response)
        {
            Assert.Equal("203.0.113.5", seen["X-Forwarded-For"]); // right-most untrusted entry, alone
            Assert.Equal("203.0.113.5", seen["X-Real-IP"]);
        }
    }

    [Fact]
    public async Task TheUpstreamGetsCleanForwardingHeaders()
    {
        await using var host = await StartAsync();
        await PublishAsync(host, App());
        static void Spoof(HttpRequestMessage request)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4");
            request.Headers.TryAddWithoutValidation("X-Real-IP", "1.2.3.4");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.test");
            request.Headers.TryAddWithoutValidation("Forwarded", "for=1.2.3.4");
            request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "1.2.3.4");
        }

        var (response, seen) = await SendAsync(host, "https", "app.test", Spoof, "/a%2Fb", HttpVersion.Version20);
        using (response)
        {
            Assert.Equal(HttpVersion.Version20, response.Version);
            Assert.Equal("127.0.0.1", seen["X-Forwarded-For"]); // TCP on [::] reports ::ffff:127.0.0.1
            Assert.Equal("127.0.0.1", seen["X-Real-IP"]);
            Assert.Equal("https", seen["X-Forwarded-Proto"]);
            Assert.Equal("app.test", seen["X-Forwarded-Host"]);
            Assert.Equal("/a%2Fb", seen["target"]);
            Assert.False(seen.ContainsKey("Forwarded"));
            Assert.False(seen.ContainsKey("CF-Connecting-IP"));
        }
    }

    [Fact]
    public async Task AltSvcAdvertisesThePublicPortOnTcpHttps()
    {
        await using var host = await StartAsync(new() { ["Paduraksa:Listen:PublicHttpsPort"] = "8443" });
        var model = App(
            new HostSpec { Id = TestIds.Host("tcp"), Names = ["tcp.test"], Tls = new HostTlsSpec { Http3 = false }, Routes = [new RouteSpec { Id = TestIds.Route("tcp-root"), Upstream = TestIds.Upstream("echo") }] },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } });
        // No libmsquic here (or H3 off): tell the compiler the H3 listener exists, as it does in production.
        await PublishAsync(host, model, host.Shared.PublicListeners.CompilerOptions with { AdvertiseHttp3 = true });

        foreach (var (scheme, name, expected) in new (string, string, string?)[]
        {
            ("https", "app.test", "h3=\":8443\""), // forwarded: ours replaces the upstream's
            ("https", "soon.test", "h3=\":8443\""), // answered by the gate
            ("https", "tcp.test", null), // opted out of H3
            ("https", "nobody.test", null), // the default host
            ("http", "app.test", null),
        })
        {
            var (response, _) = await SendAsync(host, scheme, name, version: scheme == "https" ? HttpVersion.Version20 : HttpVersion.Version11);
            using (response)
            {
                // SocketsHttpHandler parses Alt-Svc for H3 discovery and drops the default "ma=86400".
                var altSvc = response.Headers.TryGetValues("Alt-Svc", out var values) ? string.Join(", ", values) : null;
                Assert.True(expected == altSvc, $"{scheme}://{name}: expected {expected ?? "none"}, got {altSvc ?? "none"}");
            }
        }

        // Without the H3 listener (the default here), nothing is advertised.
        Assert.False(host.Shared.PublicListeners.Http3);
        await PublishAsync(host, model);
        var (plain, _) = await SendAsync(host, "https", "app.test", version: HttpVersion.Version20);
        using (plain)
        {
            Assert.False(plain.Headers.Contains("Alt-Svc"));
        }
    }

    [Theory]
    [InlineData("Paduraksa:Listen:PublicHttpsPort", "0", "PublicHttpsPort")]
    [InlineData("Paduraksa:Listen:Http3", "true", "Http3 needs a fixed Https port")]
    [InlineData("Paduraksa:Listen:HttpProxyProtocol:Enabled", "true", "HttpProxyProtocol:TrustedSources must list")]
    [InlineData("Paduraksa:Listen:HttpsProxyProtocol:TrustedSources:0", "10.0.0.1/8", "isn't an IP address or CIDR")]
    [InlineData("Paduraksa:Listen:HttpsProxyProtocol:HeaderTimeout", "00:05:00", "HeaderTimeout must be positive and at most")]
    public void InvalidListenerOptionsFailValidation(string key, string value, string expected)
    {
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));
        Assert.Contains(ex.Failures, f => f.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultsServeHttp3OnThePublicPort()
    {
        Paduraksa.Server.Configuration.PaduraksaListenOptions defaults = new();

        Assert.True(defaults.Http3);
        Assert.Equal(443, defaults.PublicHttpsPort);
        Assert.False(defaults.HttpProxyProtocol.Enabled);
        Assert.False(defaults.HttpsProxyProtocol.Enabled);
    }

}
