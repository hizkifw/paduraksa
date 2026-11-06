using System.Net;
using System.Text;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// The integrated M1 edge through the whole process: per-SNI certificates from the snapshot, the redirect to the public
/// port, PROXY protocol, trusted-proxy client IPs, clean upstream headers and <c>Alt-Svc</c>.
/// </summary>
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
        Upstreams = [new UpstreamSpec { Key = "echo", Destinations = [new("echo-1", Upstream.Address)] }],
        Hosts =
        [
            new HostSpec { Key = "app", Names = ["app.test"], Routes = [new RouteSpec { Key = "app-root", Upstream = "echo" }] },
            .. more,
        ],
    };

    private static async Task<PaduraksaHost> StartAsync(Dictionary<string, string?>? settings = null)
    {
        PaduraksaHost host = TestHost.Create(settings: settings);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task PublishAsync(PaduraksaHost host, ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        PublishResult result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model, options), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static async Task<(HttpResponseMessage Response, Dictionary<string, string> Upstream)> SendAsync(
        PaduraksaHost host, string scheme, string hostHeader, Action<HttpRequestMessage>? configure = null, string path = "/", Version? version = null)
    {
        using HttpClient client = EdgeClient.Create(out _);
        using HttpRequestMessage request = EdgeClient.Request(host, scheme, hostHeader, path, version);
        configure?.Invoke(request);
        HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, EdgeUpstream.Parse(body));
    }

    [Fact]
    public async Task ServesEachHostItsCertificateAndSwapsThemOnPublish()
    {
        using TestCertificates certificates = new();
        await using PaduraksaHost host = await StartAsync();
        host.Shared.Certificates.Set("a", certificates.Create("a.test v1", "a.test"));
        host.Shared.Certificates.Set("b", certificates.Create("b.test", "b.test", "*.b.test"));
        host.Shared.Certificates.Set("fallback", certificates.Create("fallback", "fallback.test"));
        ProxyConfigModel model = App(
            new HostSpec { Key = "a", Names = ["a.test"], Certificate = "a", Kind = Paduraksa.Proxy.Snapshot.HostKind.Respond, Respond = new RespondSpec { Body = "a" } },
            new HostSpec { Key = "b", Names = ["b.test", "*.b.test"], Certificate = "b", Kind = Paduraksa.Proxy.Snapshot.HostKind.Respond, Respond = new RespondSpec { Body = "b" } });
        model = model with { Defaults = new DefaultsSpec { Certificate = "fallback" } };
        await PublishAsync(host, model);

        Assert.Equal("a.test v1", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Equal("b.test", await EdgeClient.ServedSubjectAsync(host, "x.b.test"));
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, "app.test")); // a host without its own certificate
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, "unknown.test"));
        Assert.Equal("fallback", await EdgeClient.ServedSubjectAsync(host, serverName: null)); // IP literal: no SNI

        // The certificate behind "a" is renewed: the next publish serves it to new handshakes, no listener restart.
        host.Shared.Certificates.Set("a", certificates.Create("a.test v2", "a.test"));
        Assert.Equal("a.test v1", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        await PublishAsync(host, model with { Revision = "renewed" });
        Assert.Equal("a.test v2", await EdgeClient.ServedSubjectAsync(host, "a.test"));
        Assert.Equal(0, host.Shared.PublicListeners.Selector.RejectedUnknownServerNameTcp);
    }

    [Fact]
    public async Task UnknownNamesAreRejectedWithoutADefaultCertificate()
    {
        using TestCertificates certificates = new();
        await using PaduraksaHost host = await StartAsync();
        host.Shared.Certificates.Set("b", certificates.Create("b.test", "b.test"));

        // Compiled directly: the shared Compile helper would add the placeholder as the default certificate.
        CompileResult bare = SnapshotCompiler.Compile(
            App(new HostSpec { Key = "b", Names = ["b.test"], Certificate = "b", Kind = Paduraksa.Proxy.Snapshot.HostKind.Respond, Respond = new RespondSpec { Body = "b" } }),
            host.Shared.PublicListeners.CompilerOptions);
        Assert.True((await host.Shared.SnapshotPublisher.PublishAsync(bare.Snapshot!, TestContext.Current.CancellationToken)).Succeeded);

        Assert.Equal("b.test", await EdgeClient.ServedSubjectAsync(host, "b.test"));
        await Assert.ThrowsAsync<HttpRequestException>(() => EdgeClient.ServedSubjectAsync(host, "unknown.test"));
        await Assert.ThrowsAsync<HttpRequestException>(() => EdgeClient.ServedSubjectAsync(host, "app.test")); // no certificate, no default
        Assert.Equal(2, host.Shared.PublicListeners.Selector.RejectedUnknownServerNameTcp);
    }

    [Fact]
    public async Task PlainHttpIsRedirectedToThePublicHttpsPort()
    {
        await using PaduraksaHost host = await StartAsync(new() { ["Paduraksa:Listen:PublicHttpsPort"] = "8443" });
        await PublishAsync(host, App(new HostSpec { Key = "secure", Names = ["secure.test"], ForceHttps = true, Routes = [new RouteSpec { Key = "secure-root", Upstream = "echo" }] }));

        (HttpResponseMessage redirect, _) = await SendAsync(host, "http", "secure.test", path: "/a%20b?c=d");
        using (redirect)
        {
            Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
            Assert.Equal("https://secure.test:8443/a%20b?c=d", redirect.Headers.Location!.OriginalString);
        }

        (HttpResponseMessage secure, Dictionary<string, string> seen) = await SendAsync(host, "https", "secure.test", path: "/a%20b?c=d", version: HttpVersion.Version20);
        using (secure)
        {
            Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
            Assert.Equal("/a%20b?c=d", seen["target"]);
        }
    }

    [Fact]
    public async Task ProxyProtocolOnTheHttpListenerSetsTheClientIp()
    {
        await using PaduraksaHost host = await StartAsync(new()
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

        string response = await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes(
            "PROXY TCP4 203.0.113.7 192.0.2.1 50000 80\r\nGET /pp HTTP/1.1\r\nHost: app.test\r\nX-Forwarded-For: 1.2.3.4\r\nConnection: close\r\n\r\n"));

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal); // the allow-list saw the PROXY protocol client
        Dictionary<string, string> seen = EdgeUpstream.Parse(response);
        Assert.Equal("203.0.113.7", seen["X-Forwarded-For"]);
        Assert.Equal("203.0.113.7", seen["X-Real-IP"]);

        // The header is mandatory on this listener: a plain request is dropped unanswered.
        Assert.Equal(string.Empty, await EdgeClient.RawHttpAsync(host, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: app.test\r\nConnection: close\r\n\r\n")));
        ProxyProtocolCounters counters = host.Shared.PublicListeners.HttpProxyProtocol!.Counters;
        Assert.Equal(1, counters.Accepted);
        Assert.Equal(1, counters.Invalid);
        Assert.Null(host.Shared.PublicListeners.HttpsProxyProtocol);
    }

    [Fact]
    public async Task ProxyProtocolOnTheHttpsListenerIsReadBeforeTls()
    {
        await using PaduraksaHost host = await StartAsync(new()
        {
            ["Paduraksa:Listen:HttpsProxyProtocol:Enabled"] = "true",
            ["Paduraksa:Listen:HttpsProxyProtocol:TrustedSources:0"] = "127.0.0.0/8",
            ["Paduraksa:Listen:HttpsProxyProtocol:TrustedSources:1"] = "::1",
        });
        await PublishAsync(host, App() with
        {
            Hosts = [App().Hosts[0] with { Policies = [new IpAllowListPolicySpec(["198.51.100.0/24"])] }],
        });

        // The header arrives in clear ahead of the ClientHello; TLS and HTTP follow on the same connection.
        string response = await EdgeClient.RawHttpsAsync(host, Encoding.ASCII.GetBytes("PROXY TCP4 198.51.100.20 192.0.2.1 40404 443\r\n"), "app.test", "/pp");

        Assert.StartsWith("HTTP/1.1 200", response, StringComparison.Ordinal); // the allow-list saw the PROXY protocol client
        Dictionary<string, string> seen = EdgeUpstream.Parse(response);
        Assert.Equal("198.51.100.20", seen["X-Forwarded-For"]);
        Assert.Equal("https", seen["X-Forwarded-Proto"]);

        // Mandatory on this listener: TLS straight away is dropped, and the plain HTTP listener is unaffected.
        Assert.Equal(string.Empty, await EdgeClient.RawHttpsAsync(host, [], "app.test"));
        ProxyProtocolCounters counters = host.Shared.PublicListeners.HttpsProxyProtocol!.Counters;
        Assert.Equal(1, counters.Accepted);
        Assert.Equal(1, counters.Invalid);
        Assert.Null(host.Shared.PublicListeners.HttpProxyProtocol);
    }

    [Fact]
    public async Task CloudflareProxyModeTrustsOnlyTheEdge()
    {
        await using PaduraksaHost host = await StartAsync();
        ProxyConfigModel model = App(new HostSpec
        {
            Key = "members",
            Names = ["members.test"],
            Policies = [new IpAllowListPolicySpec(["198.51.100.0/24"])],
            Routes = [new RouteSpec { Key = "members-root", Upstream = "echo" }],
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
        (HttpResponseMessage trusted, Dictionary<string, string> seen) = await SendAsync(host, "https", "members.test", ViaCloudflare, version: HttpVersion.Version20);
        using (trusted)
        {
            Assert.Equal(HttpStatusCode.OK, trusted.StatusCode); // the allow-list judged 198.51.100.7, not 127.0.0.1
            Assert.Equal("198.51.100.7", seen["X-Forwarded-For"]);
            Assert.Equal("198.51.100.7", seen["CF-Connecting-IP"]);
            Assert.Equal("NL", seen["CF-IPCountry"]);
        }

        // The baked-in ranges don't contain loopback: the same headers are now a spoof, ignored and stripped.
        await PublishAsync(host, model);
        (HttpResponseMessage blocked, _) = await SendAsync(host, "https", "members.test", ViaCloudflare, version: HttpVersion.Version20);
        using (blocked)
        {
            Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        }

        (HttpResponseMessage spoofed, Dictionary<string, string> clean) = await SendAsync(host, "https", "app.test", ViaCloudflare, version: HttpVersion.Version20);
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

    [Fact]
    public async Task GenericModeTakesTheClientFromTheForwardedChain()
    {
        await using PaduraksaHost host = await StartAsync();
        await PublishAsync(host, App() with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8", "::1"] } },
        });

        (HttpResponseMessage response, Dictionary<string, string> seen) = await SendAsync(
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
        await using PaduraksaHost host = await StartAsync();
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

        (HttpResponseMessage response, Dictionary<string, string> seen) = await SendAsync(host, "https", "app.test", Spoof, "/a%2Fb", HttpVersion.Version20);
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
        await using PaduraksaHost host = await StartAsync(new() { ["Paduraksa:Listen:PublicHttpsPort"] = "8443" });
        ProxyConfigModel model = App(
            new HostSpec { Key = "tcp", Names = ["tcp.test"], Tls = new HostTlsSpec { Http3 = false }, Routes = [new RouteSpec { Key = "tcp-root", Upstream = "echo" }] },
            new HostSpec { Key = "soon", Names = ["soon.test"], Kind = Paduraksa.Proxy.Snapshot.HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "soon" } });
        // No libmsquic here (or H3 off): tell the compiler the H3 listener exists, as it does in production.
        await PublishAsync(host, model, host.Shared.PublicListeners.CompilerOptions with { AdvertiseHttp3 = true });

        foreach ((string scheme, string name, string? expected) in new (string, string, string?)[]
        {
            ("https", "app.test", "h3=\":8443\""), // forwarded: ours replaces the upstream's
            ("https", "soon.test", "h3=\":8443\""), // answered by the gate
            ("https", "tcp.test", null), // opted out of H3
            ("https", "nobody.test", null), // the default host
            ("http", "app.test", null),
        })
        {
            (HttpResponseMessage response, _) = await SendAsync(host, scheme, name, version: scheme == "https" ? HttpVersion.Version20 : HttpVersion.Version11);
            using (response)
            {
                // SocketsHttpHandler parses Alt-Svc for H3 discovery and drops the default "ma=86400".
                string? altSvc = response.Headers.TryGetValues("Alt-Svc", out IEnumerable<string>? values) ? string.Join(", ", values) : null;
                Assert.True(expected == altSvc, $"{scheme}://{name}: expected {expected ?? "none"}, got {altSvc ?? "none"}");
            }
        }

        // Without the H3 listener (the default here), nothing is advertised.
        Assert.False(host.Shared.PublicListeners.Http3);
        await PublishAsync(host, model);
        (HttpResponseMessage plain, _) = await SendAsync(host, "https", "app.test", version: HttpVersion.Version20);
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
        Microsoft.Extensions.Options.OptionsValidationException ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
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
