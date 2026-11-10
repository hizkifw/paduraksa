using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The managed <c>robots.txt</c> through a real Kestrel listener and the gate: the exact bytes, the caching headers,
/// <c>HEAD</c> without a body, a passthrough host still reaching its upstream, and a method other than GET/HEAD going on
/// as an ordinary request.
/// </summary>
public sealed class RobotsTxtEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel Config => new()
    {
        Revision = TestIds.Revision("robots-e2e"),
        Upstreams = [Upstream("web", upstream.Address("web"))],
        Hosts =
        [
            ProxyHost("robots", "robots.test", Route("robots-root", "web")) with { Robots = new HostRobotsSpec { Mode = RobotsMode.DisallowAll } },
            ProxyHost("ai", "ai.test", Route("ai-root", "web")) with { Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers } },
            ProxyHost("custom", "custom.test", Route("custom-root", "web")) with
            {
                Robots = new HostRobotsSpec { Mode = RobotsMode.Custom, CustomText = "User-agent: *\r\nDisallow: /private" },
            },
            ProxyHost("pass", "pass.test", Route("pass-root", "web")),
        ],
    };

    [Fact]
    public async Task GETServesTheCompiledFileWithItsCachingHeaders()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(server.Request("robots.test", "/robots.txt?from=test"), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("max-age=3600", response.Headers.CacheControl?.ToString());
        Assert.Equal(RobotsText.DisallowAll, await response.Content.ReadAsStringAsync(ct));

        using var ai = await client.SendAsync(server.Request("ai.test", "/robots.txt"), ct);
        Assert.Equal(RobotsText.BlockAiCrawlers, await ai.Content.ReadAsStringAsync(ct));

        // The stored text keeps its CRLF; what reaches the wire has been normalised, with a trailing newline.
        using var custom = await client.SendAsync(server.Request("custom.test", "/robots.txt"), ct);
        Assert.Equal("User-agent: *\nDisallow: /private\n", await custom.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task HEADSendsTheLengthAndNoBody()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(server.Request("robots.test", "/robots.txt", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RobotsText.DisallowAll.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task APassthroughHostStillReachesItsUpstreamAndOtherMethodsGoOn()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var forwarded = await client.SendAsync(server.Request("pass.test", "/robots.txt"), ct);
        Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
        Assert.Equal("/web/robots.txt", await forwarded.Content.ReadAsStringAsync(ct));

        // A managed host answers only GET and HEAD: a POST is the upstream's request.
        using var post = await client.SendAsync(server.Request("robots.test", "/robots.txt", HttpMethod.Post), ct);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("/web/robots.txt", await post.Content.ReadAsStringAsync(ct));
    }
}
