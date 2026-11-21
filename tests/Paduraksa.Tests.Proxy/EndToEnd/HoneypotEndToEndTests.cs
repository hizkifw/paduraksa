using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.RuleSets;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The trap through the real host (Kestrel, the gate, the response writer) with a small fake site: what it answers and with which
/// headers, what it leaves to the upstream, the cap on answers at once, the proof-of-work flow with its cookie and both outcomes
/// of a mix-up with the browser check, and what reaches the sink.
/// </summary>
public sealed partial class HoneypotEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private const string Prefix = "/archive-k7f3/";
    private const int Difficulty = 8;

    private static readonly HostHoneypotSpec On = new() { Enabled = true, Path = Prefix, PowDifficulty = Difficulty };

    private ProxyConfigModel Model(params HostSpec[] hosts) => new() { Upstreams = [Upstream("web", upstream.Address("a"))], Hosts = hosts };

    private static HostSpec Host(string label, HostHoneypotSpec? honeypot = null, HostRobotsSpec? robots = null) =>
        ProxyHost(label, label + ".test", Route(label + "-root", "web")) with
        {
            Honeypot = honeypot ?? On,
            Robots = robots ?? new HostRobotsSpec { Mode = RobotsMode.DisallowAll },
        };

    private static Task<ProxyTestServer> StartAsync(ProxyConfigModel model, GateTrapRig rig) =>
        ProxyTestServer.StartAsync(model, new ProxyTestServerOptions { ProofOfWork = rig.ProofOfWork, CompilerOptions = rig.Options });

    [Fact]
    public async Task APageIsAnsweredWithItsHeadersAndHeadGetsTheSameWithoutABody()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(
            Model(Host("trap") with { Headers = new HostHeadersSpec { Response = [new HeaderRuleSpec("X-Test", HeaderOperation.Set, "yes")] } }), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var get = await client.SendAsync(server.Request("trap.test", Prefix + "post-42/"), ct);
        var body = await get.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("text/html; charset=utf-8", get.Content.Headers.ContentType!.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(body), get.Content.Headers.ContentLength);
        Assert.Equal(FakeHoneypotSite.Modified, get.Content.Headers.LastModified);
        Assert.Equal("noindex, nofollow", get.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("yes", get.Headers.GetValues("X-Test").Single());
        Assert.Contains("Post 42 canary=canary-", body, StringComparison.Ordinal);

        using var head = await client.SendAsync(server.Request("trap.test", Prefix + "post-42/", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(get.Content.Headers.LastModified, head.Content.Headers.LastModified);
        Assert.Equal("noindex, nofollow", head.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task APageThatIsNotThereIsA404WithTheSameHeadersAndNoIndexCanBeSwitchedOff()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap"), Host("open", On with { NoIndex = false })), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var missing = await client.SendAsync(server.Request("trap.test", Prefix + "nothing/"), ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("NotFound", await missing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("noindex, nofollow", missing.Headers.GetValues("X-Robots-Tag").Single());

        using var open = await client.SendAsync(server.Request("open.test", Prefix), ct);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.False(open.Headers.Contains("X-Robots-Tag"));
    }

    [Fact]
    public async Task AnythingOutsideThePrefixGoesToTheUpstreamAndOtherMethodsInsideAreRefused()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        foreach (var path in new[] { "/archive-k7f3x/", "/archive-k7f3", "/elsewhere" })
        {
            using var near = await client.SendAsync(server.Request("trap.test", path), ct);
            Assert.Equal(HttpStatusCode.OK, near.StatusCode);
            Assert.DoesNotContain("canary=", await near.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = server.Request("trap.test", Prefix, method);
            using var refused = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, refused.StatusCode);
            Assert.Equal(["GET", "HEAD"], refused.Content.Headers.Allow.Order());
        }

        Assert.Equal(3, rig.Sink.Hits.Count);
        Assert.All(rig.Sink.Hits, hit => Assert.Equal(405, hit.Status));
    }

    [Fact]
    public async Task TheSinkSeesWhatTheRequestShowedOfItsClient()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(
            Model(Host("trap"), Host("loose", robots: new HostRobotsSpec { Mode = RobotsMode.Passthrough })), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var request = server.Request("trap.test", Prefix + "hidden-3/");
        request.Headers.UserAgent.ParseAdd("Crawler/2.0");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        request.Headers.Referrer = new Uri("https://search.example/q?x=1");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var hit = Assert.Single(rig.Sink.Hits);
        Assert.Equal(TestIds.Host("trap"), hit.Host);
        Assert.Equal(IpKey.Parse("127.0.0.1"), hit.ClientIp);
        Assert.Equal("Crawler/2.0", hit.UserAgent);
        Assert.Equal(HoneypotEntry.Robots, hit.Entry);
        Assert.Equal((HoneypotPageKind.Post, HoneypotLinkKind.Hidden, 3UL, 1UL), (hit.PageKind, hit.LinkKind, hit.PageId, hit.ParentId));
        Assert.Equal(200, hit.Status);
        Assert.Equal((HoneypotPowOutcome.None, 0), (hit.Pow, hit.SolveMilliseconds));
        Assert.Equal("HTTP/1.1", hit.Protocol);
        Assert.Equal(System.Security.Authentication.SslProtocols.None, hit.TlsVersion);
        Assert.Equal("text/html,*/*;q=0.8", hit.Accept);
        Assert.Equal("de", hit.AcceptLanguage);
        Assert.Equal(("none", "navigate", "document", "?1"), (hit.SecFetchSite, hit.SecFetchMode, hit.SecFetchDest, hit.SecFetchUser));
        Assert.Equal("https://search.example/q?x=1", hit.Referer);
        Assert.Contains("canary=" + hit.Canary, body, StringComparison.Ordinal);
        Assert.InRange((DateTimeOffset.UtcNow - hit.Time).TotalSeconds, -1, 30);

        // Without a Disallow line nothing told the crawler to stay out.
        using var loose = await client.SendAsync(server.Request("loose.test", Prefix), ct);
        Assert.Equal(HoneypotEntry.Prefix, rig.Sink.Hits[^1].Entry);
        Assert.Null(rig.Sink.Hits[^1].Referer);
        Assert.Equal(2, rig.Sink.Hits.Count);
        Assert.Equal(2, rig.Honeypot.Stats.Served);
    }

    [Fact]
    public async Task AHitCarriesTheCanonicalPathWithoutTheQueryAndCutAtTheLimit()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var plain = await client.SendAsync(server.Request("trap.test", Prefix + "nothing/?utm=1&x=%2F"), ct);
        // A redundant spelling is the path the gate matched on: dot segments and doubled slashes are folded.
        using var spelled = await client.SendAsync(server.Request("trap.test", Prefix + "a//b/../c/"), ct);
        using var verify = await client.SendAsync(server.Request("trap.test", Prefix + "verify"), ct);
        var long512 = new string('x', 600);
        using var cut = await client.SendAsync(server.Request("trap.test", Prefix + long512), ct);

        Assert.Equal([Prefix + "nothing/", Prefix + "a/c/", Prefix + "verify"], rig.Sink.Hits.Take(3).Select(static h => h.Path));
        Assert.All(rig.Sink.Hits.Take(3), static h => Assert.False(h.PathTruncated));
        var last = rig.Sink.Hits[^1];
        Assert.Equal(HoneypotLimits.RecordedPathLength, last.Path.Length);
        Assert.True(last.PathTruncated);
        Assert.StartsWith(Prefix + "xxx", last.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARulesLandingIsRecordedUnderThePathThatWasProbed()
    {
        var rig = new GateTrapRig();
        var set = TestIds.RuleSet("scanners");
        var model = Model(Host("rules") with { Policies = [new RuleSetPolicySpec(set)] }) with
        {
            RuleSets = [new RuleSetSpec { Id = set, Name = "scanners", Rules = [RuleSpecs.Rule(RuleAction.Honeypot, agents: ["sqlmap"])] }],
        };
        await using var server = await StartAsync(model, rig);
        using var client = ProxyTestServer.CreateClient();

        using var request = server.Request("rules.test", "/wp-login.php?redirect_to=%2F");
        request.Headers.UserAgent.ParseAdd("sqlmap/1.7");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var hit = Assert.Single(rig.Sink.Hits);
        Assert.Equal((HoneypotEntry.Rule, HoneypotPageKind.Landing, "/wp-login.php"), (hit.Entry, hit.PageKind, hit.Path));
    }

    [Fact]
    public async Task RobotsFetchesOnATrapHostReachTheSink()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap"), Host("plain", HostHoneypotSpec.Off)), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var trap = server.Request("trap.test", "/robots.txt");
        trap.Headers.UserAgent.ParseAdd("Crawler/2.0");
        using var served = await client.SendAsync(trap, ct);
        using var plain = await client.SendAsync(server.Request("plain.test", "/robots.txt"), ct);

        var text = await served.Content.ReadAsStringAsync(ct);
        Assert.Contains("Disallow: " + Prefix, text, StringComparison.Ordinal);
        var (host, client4, userAgent) = Assert.Single(rig.Sink.RobotsFetches);
        Assert.Equal((TestIds.Host("trap"), IpKey.Parse("127.0.0.1"), "Crawler/2.0"), (host, client4, userAgent));
    }

    [Fact]
    public async Task AnswersOverTheCapAreShedWithRetryAfterAndCounted()
    {
        var rig = new GateTrapRig(maxConcurrent: 1);
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        rig.Factory.OnRender = target =>
        {
            if (target.PageId == 7)
            {
                entered.Release();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var first = client.SendAsync(server.Request("trap.test", Prefix + "post-7/"), ct);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10), ct));

        using var shed = await client.SendAsync(server.Request("trap.test", Prefix + "post-8/"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, shed.StatusCode);
        Assert.Equal("5", shed.Headers.GetValues("Retry-After").Single());
        Assert.Equal(1, rig.Honeypot.Stats.Shed);
        Assert.Empty(rig.Sink.Hits);

        release.Release();
        using var done = await first;
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        // The place was given back: the next one is answered.
        using var after = await client.SendAsync(server.Request("trap.test", Prefix + "post-8/"), ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(2, rig.Sink.Hits.Count);
        Assert.Equal(1, rig.Honeypot.Stats.Shed);
    }

    [Fact]
    public async Task ARulesHoneypotActionServesTheLandingForAnyMethodAndRecordsTheRuleAsTheEntry()
    {
        var rig = new GateTrapRig();
        var set = TestIds.RuleSet("scanners");
        var model = Model(Host("trap") with { Policies = [new RuleSetPolicySpec(set)] }) with
        {
            RuleSets = [new RuleSetSpec { Id = set, Name = "scanners", Rules = [new RuleSpec { Action = RuleAction.Honeypot, UserAgentContains = ["sqlmap"] }] }],
        };
        await using var server = await StartAsync(model, rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var get = server.Request("trap.test", "/wp-login.php?x=1");
        get.Headers.UserAgent.ParseAdd("sqlmap/1.7");
        using var landing = await client.SendAsync(get, ct);
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        Assert.Contains("Landing 5 canary=", await landing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal("noindex, nofollow", landing.Headers.GetValues("X-Robots-Tag").Single());

        using var post = server.Request("trap.test", "/xmlrpc.php", HttpMethod.Post);
        post.Headers.UserAgent.ParseAdd("sqlmap/1.7");
        post.Content = new StringContent("<methodCall/>");
        using var posted = await client.SendAsync(post, ct);
        Assert.Equal(HttpStatusCode.OK, posted.StatusCode);

        using var head = server.Request("trap.test", "/", HttpMethod.Head);
        head.Headers.UserAgent.ParseAdd("sqlmap/1.7");
        using var headed = await client.SendAsync(head, ct);
        Assert.Equal(HttpStatusCode.OK, headed.StatusCode);
        Assert.Empty(await headed.Content.ReadAsByteArrayAsync(ct));

        Assert.Equal(3, rig.Sink.Hits.Count);
        Assert.All(rig.Sink.Hits, hit => Assert.Equal((HoneypotEntry.Rule, HoneypotPageKind.Landing, 200), (hit.Entry, hit.PageKind, hit.Status)));

        // Anyone else is an ordinary request.
        using var other = await client.SendAsync(server.Request("trap.test", "/wp-login.php"), ct);
        Assert.DoesNotContain("canary=", await other.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(3, rig.Sink.Hits.Count);
    }

    [Fact]
    public async Task WithoutTheKeyAConfiguredTrapAnswersTheUnavailablePageAndRecordsNoHit()
    {
        var rig = new GateTrapRig(withKey: false);
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(server.Request("trap.test", Prefix + "post-1/"), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("The browser check is temporarily unavailable.\n", await response.Content.ReadAsStringAsync(ct));
        Assert.Empty(rig.Sink.Hits);
    }

    [Fact]
    public async Task ThePowFlowShowsTheChallengeTakesTheSolutionSetsTheCookieAndPasses()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // A browser is shown the page's challenge; the form posts to a path under the prefix.
        using var shown = await client.SendAsync(Browser(server, "trap.test", Prefix + "secure-9/"), ct);
        var page = await shown.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Contains("action=\"" + Prefix + "verify\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"return\" name=\"return\" type=\"hidden\" value=\"" + Prefix + "secure-9/\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"difficulty\" type=\"hidden\" value=\"" + Difficulty + "\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("canary=", page, StringComparison.Ordinal);
        var shownHit = Assert.Single(rig.Sink.Hits);
        Assert.Equal((HoneypotPageKind.Post, 9UL, 200, HoneypotPowOutcome.Shown), (shownHit.PageKind, shownHit.PageId, shownHit.Status, shownHit.Pow));

        // Something that isn't a browser gets the browser check's 403, and still counts as shown.
        using var plain = await client.SendAsync(server.Request("trap.test", Prefix + "secure-9/"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, plain.StatusCode);
        Assert.Equal((403, HoneypotPowOutcome.Shown), (rig.Sink.Hits[^1].Status, rig.Sink.Hits[^1].Pow));

        // The solution earns a cookie scoped to the prefix with a name that says nothing, and sends the visitor back to the page.
        var challenge = ChallengeOf(page);
        using var verify = await client.SendAsync(Verify(server, "trap.test", challenge, Solve(challenge), Prefix + "secure-9/"), ct);
        Assert.Equal(HttpStatusCode.SeeOther, verify.StatusCode);
        Assert.Equal(Prefix + "secure-9/", verify.Headers.Location!.OriginalString);
        var setCookie = Assert.Single(verify.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("reader_token=", setCookie, StringComparison.Ordinal);
        Assert.Contains("; Path=" + Prefix + "; HttpOnly; SameSite=Lax; Max-Age=86400", setCookie, StringComparison.Ordinal);
        var solved = rig.Sink.Hits[^1];
        Assert.Equal((HoneypotPageKind.Post, 9UL, 303, HoneypotPowOutcome.Solved), (solved.PageKind, solved.PageId, solved.Status, solved.Pow));
        Assert.InRange(solved.SolveMilliseconds, 0, 10_000);
        var cookie = setCookie[..setCookie.IndexOf(';')];

        // With it the page is served, as passed; a cookie that isn't a token is a challenge again.
        using var request = Browser(server, "trap.test", Prefix + "secure-9/");
        request.Headers.Add("Cookie", cookie);
        using var passed = await client.SendAsync(request, ct);
        Assert.Contains("Post 9 canary=", await passed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Equal(HoneypotPowOutcome.Passed, rig.Sink.Hits[^1].Pow);

        using var forged = Browser(server, "trap.test", Prefix + "secure-9/");
        forged.Headers.Add("Cookie", cookie[..^2] + "AA");
        using var again = await client.SendAsync(forged, ct);
        Assert.Contains("name=\"challenge\"", await again.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // A page that isn't behind a challenge needs no cookie.
        using var open = await client.SendAsync(Browser(server, "trap.test", Prefix + "post-1/"), ct);
        Assert.Equal(HoneypotPowOutcome.None, rig.Sink.Hits[^1].Pow);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
    }

    [Fact]
    public async Task AWrongSolutionIsRefusedAndAReturnOutsideThePrefixIsReplacedByIt()
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var shown = await client.SendAsync(Browser(server, "trap.test", Prefix + "secure-9/"), ct);
        var challenge = ChallengeOf(await shown.Content.ReadAsStringAsync(ct));

        using var wrong = await client.SendAsync(Verify(server, "trap.test", challenge, "1" + Solve(challenge) + "9", Prefix), ct);
        using var forged = await client.SendAsync(Verify(server, "trap.test", challenge[..^1] + (challenge[^1] == 'A' ? 'B' : 'A'), Solve(challenge), Prefix), ct);
        Assert.True(wrong.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.SeeOther);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.False(forged.Headers.Contains("Set-Cookie"));

        using var outside = await client.SendAsync(Verify(server, "trap.test", challenge, Solve(challenge), "/elsewhere/"), ct);
        Assert.Equal(HttpStatusCode.SeeOther, outside.StatusCode);
        Assert.Equal(Prefix, outside.Headers.Location!.OriginalString);
        Assert.Equal(HoneypotPowOutcome.Solved, rig.Sink.Hits[^1].Pow);
        Assert.Equal(HoneypotPageKind.Index, rig.Sink.Hits[^1].PageKind);

        // A GET on the verify path is a URL that is no page.
        using var get = await client.SendAsync(server.Request("trap.test", Prefix + "verify"), ct);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task ATrapTokenPassesNothingOnTheRealSiteAndTheRealTokenPassesNothingInTheTrap()
    {
        var rig = new GateTrapRig();
        var both = Host("both") with { ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = Difficulty, TokenLifetimeHours = 24 } };
        await using var server = await StartAsync(Model(both), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // The trap's cookie, from the trap's flow.
        using var trapPage = await client.SendAsync(Browser(server, "both.test", Prefix + "secure-9/"), ct);
        var trapChallenge = ChallengeOf(await trapPage.Content.ReadAsStringAsync(ct));
        using var trapVerify = await client.SendAsync(Verify(server, "both.test", trapChallenge, Solve(trapChallenge), Prefix + "secure-9/"), ct);
        var trapCookie = Assert.Single(trapVerify.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var trapToken = trapCookie["reader_token=".Length..];

        // The real gate's cookie, from the real flow.
        using var realPage = await client.SendAsync(Browser(server, "both.test", "/real"), ct);
        var realChallenge = ChallengeOf(await realPage.Content.ReadAsStringAsync(ct));
        using var realVerify = await client.SendAsync(Verify(server, "both.test", realChallenge, Solve(realChallenge), "/real", "/.well-known/paduraksa/pow"), ct);
        Assert.Equal(HttpStatusCode.SeeOther, realVerify.StatusCode);
        var realCookie = Assert.Single(realVerify.Headers.GetValues("Set-Cookie")).Split(';')[0];
        var realToken = realCookie["paduraksa_pow=".Length..];

        // Each one passes its own flow...
        using var realOk = Browser(server, "both.test", "/real");
        realOk.Headers.Add("Cookie", realCookie);
        using var realOkResponse = await client.SendAsync(realOk, ct);
        Assert.DoesNotContain("name=\"challenge\"", await realOkResponse.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // ...and neither passes the other's, whatever name it is sent under.
        foreach (var (path, header) in new[]
        {
            ("/real", "paduraksa_pow=" + trapToken),
            (Prefix + "secure-9/", "reader_token=" + realToken),
            (Prefix + "secure-9/", "paduraksa_pow=" + realToken),
        })
        {
            using var request = Browser(server, "both.test", path);
            request.Headers.Add("Cookie", header);
            using var response = await client.SendAsync(request, ct);
            Assert.Contains("name=\"challenge\"", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("GET", "post-1/", null)]
    [InlineData("HEAD", "post-1/", null)]
    [InlineData("GET", "", "text/html")]
    [InlineData("GET", "nothing/", null)]
    [InlineData("GET", "image.svg", null)]
    [InlineData("GET", "secure-9/", "text/html")]
    [InlineData("GET", "secure-9/", "application/json")]
    [InlineData("GET", "secure-9/", null)]
    [InlineData("POST", "post-1/", null)]
    [InlineData("PUT", "", null)]
    [InlineData("POST", "verify", null)]
    public async Task TheSimulatorSaysWhatTheTrapAnswers(string method, string path, string? accept)
    {
        var rig = new GateTrapRig();
        await using var server = await StartAsync(Model(Host("trap")), rig);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        using var request = server.Request("trap.test", Prefix + path, new HttpMethod(method));
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        if (method == "POST")
        {
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>());
        }

        using var real = await client.SendAsync(request, ct);
        var simulated = Paduraksa.Proxy.Simulation.GateSimulator.Run(
            server.Store.Current,
            new Paduraksa.Proxy.Simulation.GateProbe
            {
                Host = "trap.test",
                Path = Prefix + path,
                Method = method,
                ClientAddress = IPAddress.Loopback,
                Headers = accept is null ? [] : [new Paduraksa.Proxy.Simulation.ProbeHeader("Accept", accept)],
            });

        Assert.Equal((int)real.StatusCode, simulated.Outcome.StatusCode);
        Assert.DoesNotContain(rig.Sink.Hits, hit => hit.Status != (int)real.StatusCode);
    }

    private static HttpRequestMessage Browser(ProxyTestServer server, string host, string path)
    {
        var request = server.Request(host, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return request;
    }

    private static HttpRequestMessage Verify(ProxyTestServer server, string host, string challenge, string solution, string returnTarget, string path = Prefix + "verify")
    {
        var request = server.Request(host, path, HttpMethod.Post);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["challenge"] = challenge,
            ["solution"] = solution,
            ["return"] = returnTarget,
        });
        return request;
    }

    private static string ChallengeOf(string page) => ChallengePattern().Match(page).Groups[1].Value;

    [GeneratedRegex("id=\"challenge\" name=\"challenge\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex ChallengePattern();

    private static string Solve(string challenge)
    {
        var hash = new byte[32];
        for (var n = 0; ; n++)
        {
            var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ = SHA256.HashData(Encoding.ASCII.GetBytes(string.Concat(challenge, ":", text)), hash);
            if (ProofOfWorkBits(hash) >= Difficulty)
            {
                return text;
            }
        }
    }

    private static int ProofOfWorkBits(byte[] hash)
    {
        var bits = 0;
        foreach (var value in hash)
        {
            if (value == 0)
            {
                bits += 8;
                continue;
            }

            return bits + System.Numerics.BitOperations.LeadingZeroCount((uint)value) - 24;
        }

        return bits;
    }
}
