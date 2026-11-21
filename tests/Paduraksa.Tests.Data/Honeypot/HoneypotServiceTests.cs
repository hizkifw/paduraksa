using Paduraksa.Application.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Honeypot;
using static Paduraksa.Tests.Data.Honeypot.HoneypotRig;

namespace Paduraksa.Tests.Data.Honeypot;

/// <summary>The honeypot's reads: the summary's arithmetic, the list's filters and paging, and a session's requests.</summary>
public sealed class HoneypotServiceTests
{
    private static readonly HostId Other = HostId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Second(int seconds) => seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Three visitors on one host and one on another, each doing something different.</summary>
    private static async Task<HoneypotRig> BehavingAsync()
    {
        var rig = await HoneypotRig.CreateAsync();
        // A crawler that read robots.txt, then followed a hidden link, ran the script, fetched an image and got through the challenge.
        rig.Recorder.RobotsFetched(Host, System.Net.IPAddress.Parse("198.51.100.20") is var ip ? Paduraksa.ProxyProtocol.IpKey.FromIPAddress(ip) : default, "GPTBot/1.2");
        await rig.RecordAsync(
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", page: HoneypotPageKind.Index, link: HoneypotLinkKind.Direct, id: 0),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", link: HoneypotLinkKind.Hidden, id: 5, after: TimeSpan.FromSeconds(1)),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", link: HoneypotLinkKind.Normal, id: 6, pow: HoneypotPowOutcome.Shown, after: TimeSpan.FromSeconds(2)),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", link: HoneypotLinkKind.Normal, id: 6, pow: HoneypotPowOutcome.Solved, status: 303, after: TimeSpan.FromSeconds(3)),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", link: HoneypotLinkKind.Normal, id: 6, pow: HoneypotPowOutcome.Passed, after: TimeSpan.FromSeconds(4)),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", page: HoneypotPageKind.Beacon, link: HoneypotLinkKind.Beacon, id: 7, after: TimeSpan.FromSeconds(5)),
            Hit(client: "198.51.100.20", agent: "GPTBot/1.2", page: HoneypotPageKind.Image, link: HoneypotLinkKind.Image, id: 8, after: TimeSpan.FromSeconds(6)),
            Hit(client: "192.0.2.7", agent: "curl/8.5.0", page: HoneypotPageKind.NotFound, link: HoneypotLinkKind.Direct, id: 1, status: 404, canary: string.Empty),
            Hit(client: "192.0.2.7", agent: "curl/8.5.0", page: HoneypotPageKind.NotFound, link: HoneypotLinkKind.Direct, id: 2, status: 404, canary: string.Empty, after: TimeSpan.FromSeconds(1)),
            Hit(client: "192.0.2.8", agent: "curl/8.5.0", link: HoneypotLinkKind.Comment, id: 3),
            Hit(client: "192.0.2.9", agent: "Mozilla/5.0 Firefox/130.0", host: Other, link: HoneypotLinkKind.Normal, id: 4));
        rig.Time.Now = Start.AddMinutes(1);
        return rig;
    }

    [Fact]
    public async Task TheSummaryCountsSessionsRequestsAndWhatTheVisitorsDid()
    {
        await using var rig = await BehavingAsync();
        rig.Stats.CountShed();
        rig.Stats.CountDropped();
        rig.Stats.CountDropped();

        var summary = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, null, Ct);

        Assert.Equal(MetricsRange.Hour, summary.Range);
        Assert.Null(summary.Host);
        Assert.Equal(new Contracts.Honeypot.HoneypotCounts(4, 11, 6, 2, 1, 2), summary.Counts);
        var behaviour = summary.Behaviour;
        Assert.Equal(1, behaviour.RanJs);
        Assert.Equal(1, behaviour.FetchedImages);
        Assert.Equal(1, behaviour.ChallengesShown);
        Assert.Equal(1, behaviour.ChallengesSolved);
        Assert.Equal(1, behaviour.ChallengesPassed);
        Assert.Equal(1, behaviour.ReadRobotsFirst);
        long Followed(string kind) => behaviour.Links.Single(link => link.Kind == kind).Count;
        // Every kind is listed, and each counts sessions, not requests.
        Assert.Equal(13, behaviour.Links.Count);
        Assert.Equal(1, Followed("hidden"));
        Assert.Equal(1, Followed("comment"));
        Assert.Equal(2, Followed("direct"));
        Assert.Equal(2, Followed("normal"));
        Assert.Equal(0, Followed("sponsored"));
        Assert.Contains(behaviour.Links, static link => link.Kind == "nofollow");
        Assert.Contains(behaviour.Links, static link => link.Kind == "noindexPage");
    }

    [Fact]
    public async Task TheSummaryListsTheBusiestUserAgentsWithTheirBotClass()
    {
        await using var rig = await BehavingAsync();

        var summary = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, null, Ct);

        Assert.Equal(["GPTBot/1.2", "curl/8.5.0", "Mozilla/5.0 Firefox/130.0"], summary.TopUserAgents.Select(static agent => agent.UserAgent));
        var gpt = summary.TopUserAgents[0];
        Assert.Equal(("aiCrawler", "GPTBot", 1L, 7L), (gpt.BotKind, gpt.BotName, gpt.Sessions, gpt.Requests));
        var curl = summary.TopUserAgents[1];
        Assert.Equal((2L, 3L), (curl.Sessions, curl.Requests));
        Assert.Equal("none", summary.TopUserAgents[2].BotKind);
        Assert.Equal(string.Empty, summary.TopUserAgents[2].BotName);
    }

    [Fact]
    public async Task TheSummaryListsTheMostAskedForPathsThatAreNoPageWithTheirLatestRequest()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        static HoneypotHit Guess(string path, int seconds, string client = "192.0.2.7", HostId? host = null, HoneypotPageKind page = HoneypotPageKind.NotFound) =>
            Hit(client: client, host: host, page: page, link: HoneypotLinkKind.Direct, status: page == HoneypotPageKind.NotFound ? 404 : 200, path: path, after: TimeSpan.FromSeconds(seconds));
        await rig.RecordAsync(
            Guess("/archive-k7f3/x/", 1),
            Guess("/archive-k7f3/x/", 5, client: "192.0.2.8"),
            // A rule's landing page is the probed path.
            Guess("/wp-login.php", 3, page: HoneypotPageKind.Landing),
            Guess("/wp-login.php", 9, client: "192.0.2.8", page: HoneypotPageKind.Landing),
            Guess("/wp-login.php", 2, client: "192.0.2.9", host: Other, page: HoneypotPageKind.Landing),
            Guess("/archive-k7f3/once/", 20),
            // A page the trap does serve is not a guess.
            Hit(path: "/archive-k7f3/post-1/", after: TimeSpan.FromSeconds(4)));
        rig.Time.Now = Start.AddMinutes(1);

        var all = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, null, Ct);
        var mine = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, Host, Ct);

        // Most requests first; a tie goes to the later one.
        Assert.Equal([("/wp-login.php", 3L, Start.AddSeconds(9)), ("/archive-k7f3/x/", 2L, Start.AddSeconds(5)), ("/archive-k7f3/once/", 1L, Start.AddSeconds(20))], all.TopGuessedPaths.Select(static p => (p.Path, p.Requests, p.LastSeen)));
        Assert.Equal([("/wp-login.php", 2L), ("/archive-k7f3/x/", 2L), ("/archive-k7f3/once/", 1L)], mine.TopGuessedPaths.Select(static p => (p.Path, p.Requests)));
        Assert.All(all.TopGuessedPaths, static p => Assert.False(p.PathTruncated));
    }

    [Fact]
    public async Task TheGuessedPathsAreOfTheRangeByRequestTimeAndAtMostTenWithATruncatedOneMarked()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        var hits = Enumerable.Range(0, 12).Select(i => Hit(agent: "a" + i, page: HoneypotPageKind.NotFound, link: HoneypotLinkKind.Direct, status: 404, path: "/p" + i, after: TimeSpan.FromSeconds(i))).ToList();
        hits.Add(Hit(agent: "a0", page: HoneypotPageKind.NotFound, status: 404, path: "/long", pathTruncated: true, after: TimeSpan.FromSeconds(30)));
        for (var i = 0; i < 3; i++)
        {
            hits.Add(Hit(agent: "ancient" + i, page: HoneypotPageKind.NotFound, status: 404, path: "/old", after: TimeSpan.FromHours(-3)));
        }

        await rig.RecordAsync([.. hits]);
        rig.Time.Now = Start.AddMinutes(1);

        var hour = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, null, Ct);
        var day = await rig.Service.ReadSummaryAsync(MetricsRange.Day, null, Ct);

        Assert.Equal(10, hour.TopGuessedPaths.Count);
        Assert.DoesNotContain(hour.TopGuessedPaths, static p => p.Path == "/old");
        Assert.Contains(day.TopGuessedPaths, static p => p.Path == "/old");
        Assert.Contains(hour.TopGuessedPaths, static p => p is { Path: "/long", PathTruncated: true });
    }

    [Fact]
    public async Task TheSummaryOfOneHostLeavesTheOthersOut()
    {
        await using var rig = await BehavingAsync();

        var summary = await rig.Service.ReadSummaryAsync(MetricsRange.Hour, Other, Ct);

        Assert.Equal(Other, summary.Host);
        Assert.Equal(1, summary.Counts.Sessions);
        Assert.Equal(1, summary.Counts.Requests);
        Assert.Equal("Mozilla/5.0 Firefox/130.0", Assert.Single(summary.TopUserAgents).UserAgent);
    }

    [Fact]
    public async Task TheSummaryOfNothingIsZeroesAndEveryLinkKind()
    {
        await using var rig = await HoneypotRig.CreateAsync();

        var summary = await rig.Service.ReadSummaryAsync(MetricsRange.Month, null, Ct);

        Assert.Equal(new Contracts.Honeypot.HoneypotCounts(0, 0, 0, 0, 0, 0), summary.Counts);
        Assert.Equal(13, summary.Behaviour.Links.Count);
        Assert.All(summary.Behaviour.Links, static link => Assert.Equal(0, link.Count));
        Assert.Empty(summary.TopUserAgents);
        Assert.Empty(summary.TopGuessedPaths);
    }

    [Fact]
    public async Task ARangeCoversSessionsWhoseLatestRequestIsInIt()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        await rig.RecordAsync(
            Hit(agent: "yesterday", after: TimeSpan.FromHours(-30)),
            Hit(agent: "last-week", after: TimeSpan.FromDays(-3)),
            Hit(agent: "recent", after: TimeSpan.FromMinutes(-30)),
            Hit(agent: "ancient", after: TimeSpan.FromDays(-20)));
        rig.Time.Now = Start;

        async Task<string[]> Agents(MetricsRange range) =>
            [.. (await rig.Service.ReadSummaryAsync(range, null, Ct)).TopUserAgents.Select(static agent => agent.UserAgent).Order()];

        Assert.Equal(["recent"], await Agents(MetricsRange.Hour));
        Assert.Equal(["recent"], await Agents(MetricsRange.Day));
        Assert.Equal(["last-week", "recent", "yesterday"], await Agents(MetricsRange.Week));
        Assert.Equal(["ancient", "last-week", "recent", "yesterday"], await Agents(MetricsRange.Month));
    }

    [Fact]
    public async Task TheListIsNewestFirstAndPagesByCursor()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await rig.RecordAsync(Hit(agent: "agent-" + Second(i), after: TimeSpan.FromSeconds(i)));
        }

        var first = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour), 2, null, Ct);
        var second = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour), 2, first.Next, Ct);
        var third = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour), 2, second.Next, Ct);

        Assert.Equal(["agent-4", "agent-3"], first.Items.Select(static s => s.UserAgent));
        Assert.Equal(["agent-2", "agent-1"], second.Items.Select(static s => s.UserAgent));
        Assert.Equal(["agent-0"], third.Items.Select(static s => s.UserAgent));
        Assert.NotNull(first.Next);
        Assert.NotNull(second.Next);
        Assert.Null(third.Next);
    }

    [Fact]
    public async Task AQueryMatchesAnAddressExactlyOrByPrefixAUserAgentSubstringOrACanary()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        await rig.RecordAsync(
            Hit(client: "203.0.113.9", agent: "Alpha/1", canary: "amber falcon"),
            Hit(client: "203.0.113.90", agent: "Beta/2 (compatible; Scrapy)", canary: "quiet harbor"),
            Hit(client: "2001:db8::1", agent: "Gamma/3", canary: "silver pine"),
            Hit(client: "198.51.100.1", agent: "100%_real", canary: "red kite"));

        async Task<string[]> Find(string query) =>
            [.. (await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour, Query: query), 50, null, Ct)).Items.Select(static s => s.UserAgent).Order()];

        // A prefix of an address reaches both; the whole address only the exact one that isn't a prefix of the other.
        Assert.Equal(["Alpha/1", "Beta/2 (compatible; Scrapy)"], await Find("203.0.113."));
        Assert.Equal(["Alpha/1", "Beta/2 (compatible; Scrapy)"], await Find("203.0.113.9"));
        Assert.Equal(["Gamma/3"], await Find("2001:db8::1"));
        Assert.Equal(["Gamma/3"], await Find("2001:DB8:0:0:0:0:0:1"));
        Assert.Equal(["Beta/2 (compatible; Scrapy)"], await Find("scrapy"));
        Assert.Equal(["Alpha/1"], await Find("amber falcon"));
        Assert.Equal(["Gamma/3"], await Find("silver"));
        // LIKE's own characters are text here.
        Assert.Equal(["100%_real"], await Find("100%_"));
        Assert.Equal(["100%_real"], await Find("%"));
        Assert.Equal(["100%_real"], await Find("_"));
        Assert.Empty(await Find("nothing like this"));
    }

    [Fact]
    public async Task TheListFiltersByHostAndRange()
    {
        await using var rig = await BehavingAsync();

        var mine = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour, Host), 50, null, Ct);
        var theirs = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour, Other), 50, null, Ct);
        rig.Time.Now = Start.AddHours(3);
        var later = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour), 50, null, Ct);

        Assert.Equal(3, mine.Items.Count);
        Assert.All(mine.Items, static s => Assert.Equal(Host, s.Host));
        Assert.Equal("Mozilla/5.0 Firefox/130.0", Assert.Single(theirs.Items).UserAgent);
        Assert.Empty(later.Items);
    }

    [Fact]
    public async Task ASessionReadsBackAsContractsWithItsBotAndLinkCounts()
    {
        await using var rig = await BehavingAsync();

        var page = await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour, Query: "198.51.100.20"), 5, null, Ct);

        var session = Assert.Single(page.Items);
        Assert.Equal(("198.51.100.20", "aiCrawler", "GPTBot", "amber falcon"), (session.ClientIp, session.BotKind, session.BotName, session.Canary));
        Assert.True(session.ReadRobotsFirst);
        Assert.Equal(Start, session.StartedAt);
        Assert.Equal(Start.AddSeconds(6), session.LastSeen);
        Assert.Equal((7, 4, 0, 1, 1, 1, 1, 1), (session.Requests, session.Pages, session.GuessedUrls, session.Images, session.JsRuns, session.ChallengesShown, session.ChallengesSolved, session.ChallengesPassed));
        Assert.Equal(1, session.MaxDepth);
        // Only the kinds followed, and the verify request followed none.
        Assert.Equal([("direct", 1L), ("normal", 2L), ("hidden", 1L), ("beacon", 1L), ("image", 1L)], session.Links.Select(static link => (link.Kind, link.Count)));
    }

    [Fact]
    public async Task ASessionIsReadByItsIDAndItsRequestsOldestFirstAPageAtATime()
    {
        await using var rig = await BehavingAsync();
        var session = (await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour, Query: "198.51.100.20"), 5, null, Ct)).Items.Single();

        var read = await rig.Service.ReadSessionAsync(session.Id, Ct);
        var first = await rig.Service.ListRequestsAsync(session.Id, 4, null, Ct);
        var second = await rig.Service.ListRequestsAsync(session.Id, 4, first!.Next, Ct);

        // Lists compare by reference in a record, so the links are compared by value apart.
        Assert.Equal(session with { Links = [] }, read! with { Links = [] });
        Assert.Equal(session.Links, read.Links);
        Assert.Equal(["index", "post", "post", "post"], first.Items.Select(static r => r.PageKind));
        Assert.Equal(["none", "none", "shown", "solved"], first.Items.Select(static r => r.ProofOfWork));
        Assert.NotNull(first.Next);
        Assert.Equal(["post", "beacon", "image"], second!.Items.Select(static r => r.PageKind));
        Assert.Null(second.Next);
        var solved = first.Items[3];
        Assert.Equal((303, 3_000, "HTTP/2", "tls13"), (solved.Status, solved.SolveMilliseconds, solved.Protocol, solved.TlsVersion));
        Assert.Equal(("robots", "normal", 6L, 0L), (solved.Entry, solved.LinkKind, solved.PageKey, solved.ParentKey));
        Assert.Equal(("text/html", "en-US", "none", "navigate", "document", "?1", "https://example.test/"), (solved.Accept, solved.AcceptLanguage, solved.SecFetchSite, solved.SecFetchMode, solved.SecFetchDest, solved.SecFetchUser, solved.Referer));
        Assert.Equal(Start.AddSeconds(3), solved.Time);
        Assert.Equal(("/archive-k7f3/", false), (solved.Path, solved.PathTruncated));
    }

    [Fact]
    public async Task ASessionNobodyKeptHasNoRequestsEither()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        var id = HoneypotSessionId.New();

        Assert.Null(await rig.Service.ReadSessionAsync(id, Ct));
        Assert.Null(await rig.Service.ListRequestsAsync(id, 10, null, Ct));
    }

    [Fact]
    public async Task ASessionWhoseRequestsWerePrunedHasAnEmptyPage()
    {
        await using var rig = await HoneypotRig.CreateAsync();
        await rig.RecordAsync(Hit(), Hit(id: 2, after: TimeSpan.FromSeconds(1)));
        var session = (await rig.Service.ListSessionsAsync(new HoneypotSessionFilter(MetricsRange.Hour), 5, null, Ct)).Items.Single();
        await rig.Store.PruneAsync(Start, new HoneypotRetention(TimeSpan.FromDays(30), MaxSessions: 100, MaxRequests: 0), Ct);

        var page = await rig.Service.ListRequestsAsync(session.Id, 10, null, Ct);

        Assert.Empty(page!.Items);
        Assert.Null(page.Next);
        Assert.NotNull(await rig.Service.ReadSessionAsync(session.Id, Ct));
    }
}
