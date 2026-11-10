using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The managed <c>robots.txt</c> through the real gate: served from the host's compiled bytes after the host chain and
/// the HTTPS redirect, before a fixed answer and route matching, and only for <c>GET</c> and <c>HEAD</c>.
/// </summary>
public sealed class RobotsTxtGateTests : IDisposable
{
    private static readonly RateLimitPolicyId Tight = TestIds.RateLimitPolicy("tight");
    private static readonly IPAddress Lan = IPAddress.Parse("10.0.0.5");

    private static readonly ProxyConfigModel Config = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        RateLimitPolicies = [new RateLimitPolicySpec { Id = Tight, Name = "tight", Parameters = new RateLimitParameters(1, 1) }],
        Hosts =
        [
            ProxyHost("robots", "robots.test", Route("robots-root", "web")) with { Robots = DisallowAll },
            ProxyHost("ai-robots", "ai-robots.test", Route("ai-root", "web")) with { Robots = new HostRobotsSpec { Mode = RobotsMode.BlockAiCrawlers } },
            ProxyHost("custom-robots", "custom-robots.test", Route("custom-root", "web")) with
            {
                Robots = new HostRobotsSpec { Mode = RobotsMode.Custom, CustomText = "User-agent: *\r\nDisallow: /private\r\n" },
            },
            ProxyHost("pass-robots", "pass-robots.test", Route("pass-root", "web")),
            ProxyHost("secure-robots", "secure-robots.test", Route("secure-root", "web")) with { ForceHttps = true, Robots = DisallowAll },
            ProxyHost("locked-robots", "locked-robots.test", Route("locked-root", "web")) with
            {
                Policies = [new IpAllowListPolicySpec(["10.0.0.5"])],
                Robots = DisallowAll,
            },
            ProxyHost("limited-robots", "limited-robots.test", Route("limited-root", "web") with { RateLimit = Tight }) with { Robots = DisallowAll },
            ProxyHost("pow-robots", "pow-robots.test", Route("pow-root", "web")) with
            {
                ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 10 },
                Robots = DisallowAll,
            },
            new HostSpec
            {
                Id = TestIds.Host("old-robots"),
                Names = ["old-robots.test"],
                Kind = HostKind.Redirect,
                Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 },
                Robots = DisallowAll,
            },
            new HostSpec
            {
                Id = TestIds.Host("soon-robots"),
                Names = ["soon-robots.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                Robots = DisallowAll,
            },
        ],
    };

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public RobotsTxtGateTests()
    {
        _store.Publish(Bound(Config, options: Port8443));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    private static HostRobotsSpec DisallowAll => new() { Mode = RobotsMode.DisallowAll };

    [Fact]
    public void ServesEachModeFromItsPrecomputedBytes()
    {
        var disallowAll = Decide("robots.test").Response!;
        Assert.Equal(200, disallowAll.StatusCode);
        Assert.Equal(RobotsText.DisallowAll, Text(disallowAll));
        Assert.Equal("text/plain; charset=utf-8", disallowAll.ContentType.ToString());
        Assert.Equal("max-age=3600", disallowAll.CacheControl.ToString());

        var ai = Decide("ai-robots.test").Response!;
        Assert.Equal(RobotsText.BlockAiCrawlers, Text(ai));
        Assert.Contains("User-agent: GPTBot\n", Text(ai), StringComparison.Ordinal);

        // The custom text is normalised at compile time and ends with a newline.
        var custom = Decide("custom-robots.test").Response!;
        Assert.Equal("User-agent: *\nDisallow: /private\n", Text(custom));
    }

    [Theory]
    [InlineData("robots.test")]
    [InlineData("ai-robots.test")]
    [InlineData("custom-robots.test")]
    public void ServesGETAndHEADForEveryManagedMode(string host)
    {
        foreach (var method in (string[])["GET", "HEAD"])
        {
            var decision = _gate.Decide(TestContexts.Create(host, "/robots.txt", method));
            Assert.Equal(GateAction.Respond, decision.Action);
            Assert.Equal(200, decision.Response!.StatusCode);
            Assert.NotEmpty(decision.Response.Body);
        }
    }

    [Fact]
    public void AnswersAtTheCanonicalPathAndWithAQueryString()
    {
        Assert.Equal(200, Decide("robots.test", "//robots.txt").Response!.StatusCode);
        Assert.Equal(200, Decide("robots.test", "/robots.txt;v=1").Response!.StatusCode);

        DefaultHttpContext withQuery = TestContexts.Create("robots.test", "/robots.txt");
        withQuery.Request.QueryString = new QueryString("?x=1");
        Assert.Equal(200, _gate.Decide(withQuery).Response!.StatusCode);

        // Only the exact file, not a path under it: that is an ordinary request for the route.
        Assert.Equal(GateAction.Forward, Decide("robots.test", "/robots.txt/deep").Action);
    }

    [Fact]
    public void PassthroughAndOtherMethodsGoOnToTheRoute()
    {
        Assert.Equal(GateAction.Forward, Decide("pass-robots.test").Action);

        var post = TestContexts.Create("robots.test", "/robots.txt", method: "POST");
        Assert.Equal(GateAction.Forward, _gate.Decide(post).Action);
        Assert.NotNull(post.GetEndpoint());
    }

    [Fact]
    public void TheHostAllowListStillRefusesBeforeTheFile()
    {
        var outsider = TestContexts.Create("locked-robots.test", "/robots.txt");
        var refused = _gate.Decide(outsider);
        Assert.Equal(GateAction.Respond, refused.Action);
        Assert.Equal(StatusCodes.Status403Forbidden, refused.Response!.StatusCode);

        Assert.Equal(200, _gate.Decide(TestContexts.Create("locked-robots.test", "/robots.txt", remote: Lan)).Response!.StatusCode);
    }

    [Fact]
    public void TheHttpsRedirectComesFirst()
    {
        var plain = _gate.Decide(TestContexts.Create("secure-robots.test", "/robots.txt"));
        Assert.Equal(301, plain.Response!.StatusCode);
        // The Location is built when the response is written, from the target the compiler precomputed.
        Assert.NotNull(plain.Response.Redirect);

        Assert.Equal(200, _gate.Decide(TestContexts.Create("secure-robots.test", "/robots.txt", scheme: "https")).Response!.StatusCode);
    }

    [Fact]
    public void RedirectAndRespondHostsCanCarryAFileToo()
    {
        Assert.Equal(200, Decide("old-robots.test").Response!.StatusCode);
        Assert.Equal(200, Decide("soon-robots.test").Response!.StatusCode);

        Assert.Equal(308, Decide("old-robots.test", "/other").Response!.StatusCode);
        Assert.Equal(503, Decide("soon-robots.test", "/other").Response!.StatusCode);
    }

    [Fact]
    public void TheRouteRateLimitNeverSeesTheFile()
    {
        Assert.Equal(GateAction.Forward, Decide("limited-robots.test", "/other").Action);
        Assert.Equal(429, Decide("limited-robots.test", "/other").Response!.StatusCode);

        // The bucket is empty, yet the file is still served: the request never entered the route chain.
        Assert.Equal(200, Decide("limited-robots.test").Response!.StatusCode);
    }

    [Fact]
    public void TheBrowserCheckNeverSeesTheFile()
    {
        // The policy would answer 503 without a key; the managed file is answered before the route chain exists.
        var decision = Decide("pow-robots.test");
        Assert.Equal(200, decision.Response!.StatusCode);
        Assert.Equal(RobotsText.DisallowAll, Text(decision.Response));
    }

    [Fact]
    public void AServedFileAllocatesNothing()
    {
        var context = TestContexts.Create("robots.test", "/robots.txt");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate-managed robots.txt");
    }

    private GateDecision Decide(string host, string path = "/robots.txt") => _gate.Decide(TestContexts.Create(host, path));

    private static string Text(PrecomputedResponse response) => System.Text.Encoding.UTF8.GetString(response.Body);
}
