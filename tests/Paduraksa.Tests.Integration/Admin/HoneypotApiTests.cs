using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Integration.Proxy;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Admin.AdminApiHarness;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The honeypot's API over the admin port: who may read it, what its queries take, and that a visitor's requests through the real gate, trap and recording
/// are the sessions and requests it reads back.
/// </summary>
public sealed partial class HoneypotApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private const string Prefix = "/archive-k7f3/";
    private const string TrapHost = "honeypot-api.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATokenNeedsTheMetricsScopeAndAnonymousIsRefused()
    {
        var api = shared.Api;
        var allowed = await IssueAsync(api, Permission.MetricsRead);
        var refused = await IssueAsync(api, Permission.ConfigRead);

        foreach (var path in new[] { "/api/v1/honeypot/summary", "/api/v1/honeypot/sessions", $"/api/v1/honeypot/sessions/{HoneypotSessionId.New()}", $"/api/v1/honeypot/sessions/{HoneypotSessionId.New()}/requests" })
        {
            using var ok = await Bearer(api, path, allowed);
            using var forbidden = await Bearer(api, path, refused);
            using var anonymous = await api.GetAsync(path, null);

            // A session that doesn't exist is a 404 to a caller who may ask, and a 403 to one who may not.
            Assert.Equal(path.Contains("/sessions/", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task EveryRoleMayRead()
    {
        foreach (var role in new[] { "owner", "admin", "operator", "viewer", "auditor" })
        {
            using var response = await shared.Api.GetAsync("/api/v1/honeypot/summary", role);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task TheSummaryIsJsonForEveryRangeAndForOneHost()
    {
        var api = shared.Api;
        var host = HostId.New();

        foreach (var range in new[] { "hour", "day", "week", "month" })
        {
            using var response = await api.GetAsync($"/api/v1/honeypot/summary?range={range}");
            var summary = await ReadAsync(response, AdminJsonContext.Default.HoneypotSummary, Ct);
            Assert.Null(summary.Host);
            Assert.Equal(13, summary.Behaviour.Links.Count);
        }

        using var one = await api.GetAsync($"/api/v1/honeypot/summary?range=week&host={host}");
        var forOne = await ReadAsync(one, AdminJsonContext.Default.HoneypotSummary, Ct);
        using var defaults = await api.GetAsync("/api/v1/honeypot/summary");
        var byDefault = await ReadAsync(defaults, AdminJsonContext.Default.HoneypotSummary, Ct);

        Assert.Equal(host, forOne.Host);
        Assert.Equal(0, forOne.Counts.Sessions);
        Assert.Equal(Contracts.Metrics.MetricsRange.Hour, byDefault.Range);
    }

    [Theory]
    [InlineData("/api/v1/honeypot/summary?range=fortnight")]
    [InlineData("/api/v1/honeypot/summary?range=Hour")]
    [InlineData("/api/v1/honeypot/summary?host=nope")]
    [InlineData("/api/v1/honeypot/summary?host=ups_0000000000000000000000")]
    [InlineData("/api/v1/honeypot/sessions?range=1")]
    [InlineData("/api/v1/honeypot/sessions?host=unmatched")]
    [InlineData("/api/v1/honeypot/sessions?limit=0")]
    [InlineData("/api/v1/honeypot/sessions?limit=201")]
    [InlineData("/api/v1/honeypot/sessions?cursor=!!!")]
    [InlineData("/api/v1/honeypot/sessions?cursor=aG9zdF9ub3BlMDAwMDAwMDAwMDAwMDA")]
    public async Task ABadQueryIsAProblem(string path)
    {
        using var response = await shared.Api.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await ProblemAsync(response, Ct)).Status);
    }

    [Fact]
    public async Task AQueryLongerThanAFilterCanBeIsAProblem()
    {
        using var response = await shared.Api.GetAsync("/api/v1/honeypot/sessions?q=" + new string('a', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASessionThatIsNotThereIsA404AndAMalformedIdIsNoRoute()
    {
        var api = shared.Api;

        using var missing = await api.GetAsync($"/api/v1/honeypot/sessions/{HoneypotSessionId.New()}");
        using var malformed = await api.GetAsync("/api/v1/honeypot/sessions/nope");
        using var wrongType = await api.GetAsync($"/api/v1/honeypot/sessions/{HostId.New()}");
        using var noRequests = await api.GetAsync($"/api/v1/honeypot/sessions/{HoneypotSessionId.New()}/requests");
        using var badCursor = await api.GetAsync($"/api/v1/honeypot/sessions/{HoneypotSessionId.New()}/requests?cursor=!!!");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(404, (await ProblemAsync(missing, Ct)).Status);
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noRequests.StatusCode);
        Assert.Equal(404, (await ProblemAsync(noRequests, Ct)).Status);
        // The session is looked up after the cursor is read, so a bad cursor is a 400 even for a session that isn't there.
        Assert.Equal(HttpStatusCode.BadRequest, badCursor.StatusCode);
    }

    [Fact]
    public async Task AVisitorsRequestsThroughTheRealTrapAreTheSessionTheApiReads()
    {
        var api = shared.Api;
        await PublishTrapAsync(powShare: 100);
        var agent = "HoneypotApiTest-" + HostId.New();
        var endpoint = TestHost.Endpoint(api.Host, HostRole.Public, "http");
        using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });

        // robots.txt first, then the index, a link from it, and a URL nothing links to.
        using (var robots = await client.SendAsync(Get(endpoint, "/robots.txt", agent), Ct))
        {
            Assert.Contains("Disallow", await robots.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        string index;
        using (var response = await client.SendAsync(Get(endpoint, Prefix, agent), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            index = await response.Content.ReadAsStringAsync(Ct);
        }

        var post = PostLink().Match(index).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(post), index);
        using (var followed = await client.SendAsync(Get(endpoint, post, agent, referer: "https://search.example/results"), Ct))
        {
            // A client that doesn't ask for HTML gets the challenge as a refusal or as its page: the trap's posts are behind it (powShare 100).
            Assert.True(followed.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden, followed.StatusCode.ToString());
        }

        using (var guessed = await client.SendAsync(Get(endpoint, Prefix + "no-such-page/", agent), Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);
        }

        await api.Host.Shared.HoneypotStore.WritePendingAsync(Ct);

        using var list = await api.GetAsync("/api/v1/honeypot/sessions?q=" + Uri.EscapeDataString(agent));
        var page = await ReadAsync(list, AdminJsonContext.Default.PageHoneypotSession, Ct);
        var session = Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal(("127.0.0.1", 3, 1, 1), (session.ClientIp, session.Requests, session.GuessedUrls, session.ChallengesShown));
        Assert.Equal(agent, session.UserAgent);
        Assert.True(session.ReadRobotsFirst);
        Assert.Equal(2, session.Pages);
        Assert.False(string.IsNullOrEmpty(session.Canary));
        Assert.Equal("none", session.BotKind);

        using var detail = await api.GetAsync($"/api/v1/honeypot/sessions/{session.Id}");
        var single = await ReadAsync(detail, AdminJsonContext.Default.HoneypotSession, Ct);
        // Lists compare by reference in a record, so the links are compared by value apart.
        Assert.Equal(session with { Links = [] }, single with { Links = [] });
        Assert.Equal(session.Links, single.Links);
        using var requests = await api.GetAsync($"/api/v1/honeypot/sessions/{session.Id}/requests");
        var read = await ReadAsync(requests, AdminJsonContext.Default.PageHoneypotRequest, Ct);
        Assert.Null(read.NextCursor);
        Assert.Equal(["index", "post", "notFound"], read.Items.Select(static r => r.PageKind));
        Assert.Equal(["none", "shown", "none"], read.Items.Select(static r => r.ProofOfWork));
        Assert.Equal([200, 404], new[] { read.Items[0].Status, read.Items[2].Status });
        Assert.All(read.Items, static r => Assert.Equal("robots", r.Entry));
        Assert.Equal("https://search.example/results", read.Items[1].Referer);
        Assert.Equal([Prefix, post, Prefix + "no-such-page/"], read.Items.Select(static r => r.Path));
        Assert.All(read.Items, static r => Assert.False(r.PathTruncated));
        Assert.Equal("direct", read.Items[0].LinkKind);
        Assert.Equal("HTTP/1.1", read.Items[0].Protocol);

        using var summary = await api.GetAsync("/api/v1/honeypot/summary?range=hour");
        var counts = await ReadAsync(summary, AdminJsonContext.Default.HoneypotSummary, Ct);
        Assert.True(counts.Counts.Sessions >= 1);
        Assert.Contains(counts.TopUserAgents, agentRow => agentRow.UserAgent == agent);
        var topPath = Assert.Single(counts.TopGuessedPaths, path => path.Path == Prefix + "no-such-page/");
        Assert.True(topPath.Requests >= 1);
        Assert.True(counts.Behaviour.ChallengesShown >= 1);
        Assert.True(counts.Behaviour.ReadRobotsFirst >= 1);
    }

    [Fact]
    public async Task SessionsPageNewestFirstAndRequestsPageOldestFirstThroughTheApi()
    {
        var api = shared.Api;
        await PublishTrapAsync(powShare: 0);
        var tag = HostId.New().ToString();
        var endpoint = TestHost.Endpoint(api.Host, HostRole.Public, "http");
        using HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false });
        for (var i = 0; i < 3; i++)
        {
            // One session a user agent, and the first has more than a page of requests.
            for (var request = 0; request < (i == 0 ? 3 : 1); request++)
            {
                using var response = await client.SendAsync(Get(endpoint, Prefix + $"page/{request + 2}/", $"Paging-{tag}-{i}"), Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        await api.Host.Shared.HoneypotStore.WritePendingAsync(Ct);

        using var first = await api.GetAsync($"/api/v1/honeypot/sessions?q=Paging-{tag}&limit=2");
        var one = await ReadAsync(first, AdminJsonContext.Default.PageHoneypotSession, Ct);
        using var second = await api.GetAsync($"/api/v1/honeypot/sessions?q=Paging-{tag}&limit=2&cursor={Uri.EscapeDataString(one.NextCursor!)}");
        var two = await ReadAsync(second, AdminJsonContext.Default.PageHoneypotSession, Ct);

        Assert.Equal([$"Paging-{tag}-2", $"Paging-{tag}-1"], one.Items.Select(static s => s.UserAgent));
        Assert.Equal([$"Paging-{tag}-0"], two.Items.Select(static s => s.UserAgent));
        Assert.Null(two.NextCursor);

        var longest = two.Items[0];
        using var firstRequests = await api.GetAsync($"/api/v1/honeypot/sessions/{longest.Id}/requests?limit=2");
        var early = await ReadAsync(firstRequests, AdminJsonContext.Default.PageHoneypotRequest, Ct);
        using var lastRequests = await api.GetAsync($"/api/v1/honeypot/sessions/{longest.Id}/requests?limit=2&cursor={Uri.EscapeDataString(early.NextCursor!)}");
        var late = await ReadAsync(lastRequests, AdminJsonContext.Default.PageHoneypotRequest, Ct);

        Assert.Equal(2, early.Items.Count);
        Assert.Single(late.Items);
        Assert.Null(late.NextCursor);
        Assert.True(early.Items[0].Time <= early.Items[1].Time && early.Items[1].Time <= late.Items[0].Time);
    }

    private async Task PublishTrapAsync(int powShare)
    {
        var host = shared.Api.Host;
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("honeypot-api"), Destinations = [new(TestIds.Destination("honeypot-api-0"), "http://127.0.0.1:1")] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("honeypot-api"),
                    Names = [TrapHost],
                    Robots = new HostRobotsSpec { Mode = RobotsMode.DisallowAll },
                    Honeypot = new HostHoneypotSpec { Enabled = true, Path = Prefix, PowShare = powShare, PowDifficulty = 8 },
                    Routes = [new RouteSpec { Id = TestIds.Route("honeypot-api-root"), Upstream = TestIds.Upstream("honeypot-api") }],
                },
            ],
        };
        var published = await host.Shared.SnapshotPublisher.PublishAsync(PublicProxyTests.Compile(host, model), Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
    }

    private static HttpRequestMessage Get(Uri endpoint, string path, string agent, string? referer = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, path));
        request.Headers.Host = TrapHost;
        request.Headers.TryAddWithoutValidation("User-Agent", agent);
        if (referer is not null)
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        return request;
    }

    [GeneratedRegex("href=\"(/archive-k7f3/[^\"]*(?:post|20\\d\\d)[^\"]*)\"")]
    private static partial Regex PostLink();

    private static async Task<string> IssueAsync(AdminApiHarness api, Permission scope)
    {
        var issued = await api.Host.Shared.ApiTokens.IssueAsync(
            new CommandOrigin(Actor.ForUser(api.UserOf("viewer")), Channel.Api, IPAddress.Loopback),
            new IssueApiTokenRequest("honeypot-reader", [scope]),
            Ct);
        return issued.Issued?.Token ?? throw new InvalidOperationException($"No token: {issued.Status} {issued.Problem}");
    }

    private static async Task<HttpResponseMessage> Bearer(AdminApiHarness api, string path, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(api.BaseAddress, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.Client.SendAsync(request, Ct);
    }
}
