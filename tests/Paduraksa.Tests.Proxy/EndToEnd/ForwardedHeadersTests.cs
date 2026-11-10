using System.Net;
using Microsoft.Extensions.Hosting;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>What the upstream sees: clean forwarding headers from the resolved client, never the client's own.</summary>
public sealed class ForwardedHeadersTests(HeaderEchoUpstream upstream) : IClassFixture<HeaderEchoUpstream>
{
    private static readonly SnapshotCompilerOptions LoopbackEdge = new() { CloudflareEdgeRanges = CidrSet.Parse(["127.0.0.0/8"]) };

    private ProxyConfigModel Model(TrustedProxySpec proxies) => new()
    {
        Upstreams = [Upstream("echo", upstream.Address)],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "echo"))],
        Defaults = new DefaultsSpec { TrustedProxies = proxies },
    };

    /// <summary>Everything a client could try to smuggle in.</summary>
    private static void Spoof(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ["1.2.3.4", "5.6.7.8"]);
        request.Headers.TryAddWithoutValidation("X-Real-IP", "1.2.3.4");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.test");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/admin");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Port", "8443");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=1.2.3.4;proto=https");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.7");
        request.Headers.TryAddWithoutValidation("True-Client-IP", "198.51.100.8");
        request.Headers.TryAddWithoutValidation("CF-IPCountry", "NL");
        request.Headers.TryAddWithoutValidation("CF-Ray", "8f00000000000000-AMS");
        request.Headers.TryAddWithoutValidation("CF-Visitor", "{\"scheme\":\"https\"}");
        request.Headers.TryAddWithoutValidation("Cf-Access-Authenticated-User-Email", "admin@example.com");
        request.Headers.TryAddWithoutValidation("Cf-Access-Jwt-Assertion", "eyJhbGciOiJSUzI1NiJ9.forged.sig");
        request.Headers.TryAddWithoutValidation("cf-ipcity", "Amsterdam");
    }

    private static readonly string[] AnyCloudflare = [.. CloudflareHeaders.StripWhenUntrusted, "Cf-Access-Authenticated-User-Email", "Cf-Access-Jwt-Assertion", "cf-ipcity"];

    [Fact]
    public async Task WithoutTrustedProxiesEveryClientHeaderIsReplacedOrRemoved()
    {
        var seen = await SendAsync(Model(TrustedProxySpec.Off), Spoof, "/a%2Fb?q=1");

        Assert.Equal("127.0.0.1", seen["X-Forwarded-For"]); // one address, the IPv4-mapped peer as dotted quad
        Assert.Equal("127.0.0.1", seen["X-Real-IP"]);
        Assert.Equal("http", seen["X-Forwarded-Proto"]); // an untrusted client can't claim https
        Assert.Equal("app.test", seen["X-Forwarded-Host"]);
        // The client's Host, not the destination's address: name-based upstreams, absolute URLs and trusted-domain checks
        // depend on it.
        Assert.Equal("app.test", seen["Host"]);
        Assert.Equal("/a%2Fb?q=1", seen["target"]); // the raw-target forwarding is untouched
        foreach (var removed in (string[])["Forwarded", "X-Forwarded-Prefix", "X-Forwarded-Port", .. AnyCloudflare])
        {
            Assert.False(seen.ContainsKey(removed), removed);
        }
    }

    [Fact]
    public async Task AClientsTraceparentReachesTheUpstreamUnchanged()
    {
        // The proxy traces nothing, so it neither starts a span of its own nor rewrites the client's.
        const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        var seen = await SendAsync(Model(TrustedProxySpec.Off), static request => request.Headers.TryAddWithoutValidation("traceparent", Traceparent), "/");

        Assert.Equal(Traceparent, seen["traceparent"]);
    }

    [Fact]
    public async Task ATrustedCloudflareEdgePassesTheClientAndItsHeaders()
    {
        var seen = await SendAsync(Model(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy }), Spoof, "/");

        Assert.Equal("198.51.100.7", seen["X-Forwarded-For"]);
        Assert.Equal("198.51.100.7", seen["X-Real-IP"]);
        Assert.Equal("https", seen["X-Forwarded-Proto"]); // the edge terminated the client's TLS
        Assert.Equal("app.test", seen["X-Forwarded-Host"]);
        Assert.Equal("198.51.100.7", seen["CF-Connecting-IP"]);
        Assert.Equal("NL", seen["CF-IPCountry"]);
        Assert.Equal("8f00000000000000-AMS", seen["CF-Ray"]);
        Assert.Equal("admin@example.com", seen["Cf-Access-Authenticated-User-Email"]); // genuine from a trusted edge
        Assert.False(seen.ContainsKey("Forwarded"));
    }

    [Fact]
    public async Task AnUntrustedPeerInCloudflareModeCannotSpoofCloudflare()
    {
        // Loopback isn't in the baked-in edge ranges, so here the client's CF-* headers are fake.
        var seen = await SendAsync(Model(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy }), Spoof, "/", SnapshotCompilerOptions.Default);

        Assert.Equal("127.0.0.1", seen["X-Forwarded-For"]);
        Assert.Equal("http", seen["X-Forwarded-Proto"]);
        foreach (var removed in AnyCloudflare)
        {
            Assert.False(seen.ContainsKey(removed), removed);
        }
    }

    [Fact]
    public async Task GenericProxiesYieldOneCleanAddressAndNoCloudflareHeaders()
    {
        var seen = await SendAsync(
            Model(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8"] }),
            static request =>
            {
                Spoof(request);
                request.Headers.Remove("X-Forwarded-For");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "1.2.3.4, 203.0.113.5, 127.0.0.2");
            },
            "/");

        // Right-most untrusted entry, sent alone: the chain isn't appended to.
        Assert.Equal("203.0.113.5", seen["X-Forwarded-For"]);
        Assert.Equal("203.0.113.5", seen["X-Real-IP"]);
        // The proxy's X-Forwarded-Proto isn't trusted by default (it may be the client's): our own scheme.
        Assert.Equal("http", seen["X-Forwarded-Proto"]);
        // A trusted nginx passes client headers through, so they are as untrusted as the client.
        foreach (var removed in AnyCloudflare)
        {
            Assert.False(seen.ContainsKey(removed), removed);
        }
    }

    [Fact]
    public async Task GenericProxiesCanBeTrustedForTheScheme()
    {
        var seen = await SendAsync(
            Model(new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8"], TrustForwardedProto = true }),
            static request => request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "HTTPS"),
            "/");

        Assert.Equal("https", seen["X-Forwarded-Proto"]);
    }

    [Fact]
    public async Task CloudflareFlexibleIsForwardedAsHttp()
    {
        // CF-Visitor beats the edge's X-Forwarded-Proto: the visitor's own scheme.
        var seen = await SendAsync(
            Model(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareProxy }),
            static request =>
            {
                request.Headers.TryAddWithoutValidation("CF-Visitor", "{\"scheme\":\"http\"}");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            },
            "/");

        Assert.Equal("http", seen["X-Forwarded-Proto"]);
    }

    [Fact]
    public void FindingCloudflareHeadersAllocatesNothing()
    {
        using HttpRequestMessage request = new();
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
        request.Headers.TryAddWithoutValidation("User-Agent", "test");
        request.Headers.TryAddWithoutValidation("X-Custom", "1");
        Assert.Null(ForwardedHeadersTransform.FindCloudflareHeader(request.Headers));
        AllocAssert.Zero(request.Headers, static h => _ = ForwardedHeadersTransform.FindCloudflareHeader(h), "FindCloudflareHeader (none present)");

        request.Headers.TryAddWithoutValidation("cf-RAY", "x");
        Assert.Equal("cf-RAY", ForwardedHeadersTransform.FindCloudflareHeader(request.Headers));
        AllocAssert.Zero(request.Headers, static h => _ = ForwardedHeadersTransform.FindCloudflareHeader(h), "FindCloudflareHeader (one present)");
    }

    [Fact]
    public async Task ATunnelClientOnTheLanCannotForgeCloudflareAccess()
    {
        // Tunnel mode trusts cloudflared at 10.0.0.2 only; this client connects directly from loopback.
        var seen = await SendAsync(Model(new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["10.0.0.2"] }), Spoof, "/");

        Assert.False(seen.ContainsKey("Cf-Access-Authenticated-User-Email"));
        Assert.False(seen.ContainsKey("Cf-Access-Jwt-Assertion"));
        Assert.Equal("127.0.0.1", seen["X-Forwarded-For"]);
    }

    private static async Task<Dictionary<string, string>> SendAsync(ProxyConfigModel model, Action<HttpRequestMessage> configure, string pathAndQuery, SnapshotCompilerOptions? options = null)
    {
        await using var server = await ProxyTestServer.StartAsync(model, new() { CompilerOptions = options ?? LoopbackEdge, ClientInfo = true });
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", pathAndQuery);
        configure(request);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return HeaderEchoUpstream.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}

/// <summary>The <see cref="HeaderEchoUpstreamHost"/> upstream as a class fixture.</summary>
public sealed class HeaderEchoUpstream : IAsyncLifetime
{
    private IHost? _host;

    public string Address { get; private set; } = string.Empty;

    public static Dictionary<string, string> Parse(string body) => HeaderEchoUpstreamHost.Parse(body);

    public async ValueTask InitializeAsync() =>
        (_host, Address) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
