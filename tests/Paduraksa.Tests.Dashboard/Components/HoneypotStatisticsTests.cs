using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.HoneypotArea;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The honeypot section of the Security tab: the figures, what the sessions did, the user agents and the guessed paths, the sessions with their
/// search and paging, and a session's drawer with its timeline. What a visitor sends is text, whatever it holds.
/// </summary>
public sealed class HoneypotStatisticsTests
{
    private static readonly DateTimeOffset Now = FakeDashboardHoneypot.Now;
    private static readonly HostId Trapped = HostId.New();

    private static readonly HostConfig TrappedHost = new(Trapped, ["blog.test"], Settings: new HostSettings { Honeypot = new HoneypotSettings { Enabled = true } });

    private static (DashboardTestContext Context, FakeDashboardConfig Config) Start(bool trap = true, TimeProvider? time = null)
    {
        DashboardTestContext context = new();
        var config = ConfigPages.SignIn(context, Role.Viewer, time);
        config.Document = new ConfigDocument(Hosts: trap ? [TrappedHost] : [new HostConfig(HostId.New(), ["plain.test"])]);
        context.Render<MudPopoverProvider>();
        return (context, config);
    }

    private static IRenderedComponent<HoneypotStatistics> Render(DashboardTestContext context, MetricsRange range = MetricsRange.Hour) =>
        context.Render<HoneypotStatistics>(parameters => parameters
            .Add(p => p.Range, range)
            .Add(p => p.PollInterval, TimeSpan.FromSeconds(5)));

    private static HoneypotSummary Summary() => new(
        Now,
        MetricsRange.Hour,
        new HoneypotCounts(Sessions: 4, Requests: 1_234, Pages: 900, GuessedUrls: 30, Shed: 2, Dropped: 0),
        new HoneypotBehaviour(
            [new HoneypotLinkCount("direct", 4), new HoneypotLinkCount("normal", 3), new HoneypotLinkCount("hidden", 1), new HoneypotLinkCount("script", 0)],
            RanJs: 1, FetchedImages: 0, ChallengesShown: 2, ChallengesSolved: 1, ChallengesPassed: 1, ReadRobotsFirst: 0),
        [
            new HoneypotUserAgent("GPTBot/1.1", "aiCrawler", "GPTBot", 2, 800),
            new HoneypotUserAgent("<b>Mozilla</b><img src=x onerror=alert(1)>", "none", string.Empty, 1, 300),
            new HoneypotUserAgent(string.Empty, "none", string.Empty, 1, 134),
        ],
        [
            new HoneypotPathCount("/wp-login.php", false, 20, Now.AddMinutes(-2)),
            new HoneypotPathCount("/" + new string('a', 500), true, 3, Now.AddHours(-1)),
        ]);

    [Fact]
    public async Task TheFiguresTheBehaviourTheAgentsAndThePathsAreDrawnFromTheSummary()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-tile=honeypot-sessions]").Count == 1);

        Assert.Contains("4", page.Find("[data-tile=honeypot-sessions] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1,234", page.Find("[data-tile=honeypot-requests] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Contains("900 pages", page.Find("[data-tile=honeypot-requests] [data-part=detail]").TextContent, StringComparison.Ordinal);
        Assert.Contains("30", page.Find("[data-tile=honeypot-guessed] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2", page.Find("[data-tile=honeypot-shed] [data-part=value]").TextContent, StringComparison.Ordinal);

        // A behaviour no session showed is left out; the rest say how many of the sessions did it.
        var behaviours = page.FindAll("[data-behaviour]").Select(row => row.GetAttribute("data-behaviour")).ToList();
        Assert.Equal(["direct", "normal", "hidden", "js", "challenge-shown", "challenge-solved", "challenge-passed"], behaviours);
        Assert.Contains("100%", page.Find("[data-behaviour=direct]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Hidden link", page.Find("[data-behaviour=hidden]").TextContent, StringComparison.Ordinal);
        Assert.Contains("25%", page.Find("[data-behaviour=hidden]").TextContent, StringComparison.Ordinal);

        var gpt = page.Find("[data-agent='GPTBot/1.1']");
        Assert.Contains("GPTBot, AI crawler", gpt.TextContent, StringComparison.Ordinal);
        Assert.Contains("800", gpt.TextContent, StringComparison.Ordinal);
        Assert.Contains("(none)", page.Find("[data-agent='']").TextContent, StringComparison.Ordinal);

        var guessed = page.FindAll("[data-guessed]");
        Assert.Equal(2, guessed.Count);
        Assert.Contains("/wp-login.php", guessed[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("20", guessed[0].TextContent, StringComparison.Ordinal);
        Assert.EndsWith("...", guessed[1].QuerySelector("[data-part=text]")!.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Cut at 512 characters: /aaa", guessed[1].QuerySelector("th")!.GetAttribute("title"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatAVisitorSentIsTextAndNeverMarkupOrALink()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        var hostile = FakeDashboardHoneypot.Session(agent: "<script>alert(1)</script>", canary: "<i>amber</i> falcon") with { ClientIp = "2001:db8::1" };
        context.Honeypot.Sessions.Add(hostile);
        context.Honeypot.Summary = Summary() with { TopGuessedPaths = [new HoneypotPathCount("/\"><a href=\"http://evil.test\">x</a>", false, 1, Now)] };

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        // The text is in the page as text: no element was made from it, and nothing in the section links to what a visitor sent.
        Assert.Empty(page.FindAll("script"));
        Assert.Empty(page.FindAll("img[src=x]"));
        Assert.Empty(page.FindAll("a[href*='evil.test']"));
        Assert.Contains("<script>alert(1)</script>", page.Find("[data-session] [data-part=agent]").TextContent, StringComparison.Ordinal);
        Assert.Equal("<i>amber</i> falcon", page.Find("[data-session] [data-part=canary] .pdk-copy-text").TextContent);
        Assert.Equal("<b>Mozilla</b><img src=x onerror=alert(1)>", page.Find("[data-section=honeypot-agents] tbody tr:nth-child(2) [data-part=text]").TextContent);
        Assert.Empty(page.FindAll("[data-section=honeypot-agents] b"));
        Assert.All(page.FindAll("a"), link => Assert.DoesNotContain("http", link.GetAttribute("href") ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionRowShowsItsFactsAndTheAddressAndCanaryCanBeCopied()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        var session = FakeDashboardHoneypot.Session(ip: "203.0.113.9", agent: "GPTBot/1.1", botKind: "aiCrawler", botName: "GPTBot", pages: 7, robotsFirst: true) with
        {
            JsRuns = 2, Images = 4, ChallengesShown = 3, ChallengesSolved = 1, ChallengesPassed = 2, MaxDepth = 5,
        };
        context.Honeypot.Sessions.Add(session);

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        var row = page.Find($"[data-session='{session.Id}']");
        Assert.Equal("203.0.113.9", row.QuerySelector("[data-part=address] .pdk-copy-text")!.TextContent);
        Assert.NotNull(row.QuerySelector("[data-part=address] [data-action=copy-address]"));
        Assert.Equal("amber falcon", row.QuerySelector("[data-part=canary] .pdk-copy-text")!.TextContent);
        Assert.NotNull(row.QuerySelector("[data-part=canary] [data-action=copy-canary]"));
        Assert.Contains("GPTBot, AI crawler", row.QuerySelector("[data-part=bot]")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("GPTBot/1.1", row.QuerySelector("[data-part=agent]")!.TextContent);
        Assert.Equal("7, depth 5", row.QuerySelector("[data-part=pages]")!.TextContent.Trim());
        var did = row.QuerySelectorAll("[data-part=did] .pdk-tag").Select(tag => tag.TextContent.Trim()).ToList();
        Assert.Equal(["robots.txt", "JS", "Images", "PoW solved"], did);
        Assert.Equal("Shown 3, solved 1, passed 2", row.QuerySelectorAll("[data-part=did] .pdk-tag")[3].GetAttribute("title"));
    }

    [Fact]
    public async Task ASessionWithoutACanaryOrAUserAgentSaysSoPlainly()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session(agent: string.Empty, canary: string.Empty));

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        Assert.Equal("(none)", page.Find("[data-session] [data-part=agent]").TextContent);
        Assert.Empty(page.FindAll("[data-session] [data-part=canary]"));
    }

    [Fact]
    public async Task TheSearchAsksForSessionsMatchingItAndAClearedSearchAsksForAll()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session(ip: "203.0.113.9", agent: "Alpha/1"));
        context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session(ip: "198.51.100.7", agent: "Beta/1"));
        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 2);

        await Search(page, "  198.51  ");
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        Assert.Equal("198.51", context.Honeypot.Lists[^1].Filter.Query);
        Assert.Contains("198.51.100.7", page.Find("[data-session]").TextContent, StringComparison.Ordinal);

        await Search(page, "nothing like it");
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 0 && page.Markup.Contains("No session matches", StringComparison.Ordinal));

        await Search(page, string.Empty);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 2);
        Assert.Null(context.Honeypot.Lists[^1].Filter.Query);
    }

    [Fact]
    public async Task MoreSessionsAreLoadedOnRequest_AndANewerOneAppearsWithoutDroppingThem()
    {
        ManualTimeProvider time = new(Now);
        var (context, _) = Start(time: time);
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        for (var i = 0; i < HoneypotText.PageSize + 5; i++)
        {
            context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session(ip: $"203.0.113.{i}"));
        }

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == HoneypotText.PageSize);
        Assert.Single(page.FindAll("[data-action=more-sessions]"));

        await page.Find("[data-action=more-sessions]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == HoneypotText.PageSize + 5);
        Assert.Empty(page.FindAll("[data-action=more-sessions]"));
        Assert.NotNull(context.Honeypot.Lists[^1].After);

        // A session that began since is read with the newest page, and the older ones a person loaded stay.
        context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session(ip: "192.0.2.1"));
        time.Advance(TimeSpan.FromSeconds(5));

        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == HoneypotText.PageSize + 6);
        var addresses = page.FindAll("[data-session] [data-part=address] .pdk-copy-text").Select(code => code.TextContent).ToList();
        Assert.Equal("192.0.2.1", addresses[0]);
        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("203.0.113.0", addresses);
    }

    [Fact]
    public async Task ARowOpensTheSessionWithItsFactsAndItsRequestsInOrderWithTheGapSinceThePreviousOne()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        var session = FakeDashboardHoneypot.Session(host: Trapped, agent: "<em>Crawler</em>/2", botKind: "library", botName: "curl") with { Canary = "quiet harbour" };
        context.Honeypot.Sessions.Add(session);
        context.Honeypot.Requests[session.Id] =
        [
            FakeDashboardHoneypot.Request(Now.AddMinutes(-20), "/archive-k7f3/"),
            FakeDashboardHoneypot.Request(Now.AddMinutes(-20).AddMilliseconds(1_500), "/archive-k7f3/2024/07/a-b/", pageKind: "post", linkKind: "hidden", pow: "shown", status: 200),
            FakeDashboardHoneypot.Request(Now.AddMinutes(-19), "/wp-login.php", pageKind: "landing", linkKind: "direct", status: 200),
        ];
        var drawers = context.Render<DrawerHost>();
        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        await page.Find("[data-session] [data-action=open-session]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-request]").Count == 3);

        var facts = drawers.Find("[data-part=facts]");
        Assert.Equal("203.0.113.9", facts.QuerySelector("[data-part=address] .pdk-copy-text")!.TextContent);
        Assert.Equal("<em>Crawler</em>/2", facts.QuerySelector("[data-part=agent] .pdk-copy-text")!.TextContent);
        Assert.Equal("quiet harbour", facts.QuerySelector("[data-part=canary] .pdk-copy-text")!.TextContent);
        Assert.Contains("blog.test", facts.TextContent, StringComparison.Ordinal);
        Assert.Contains("curl, HTTP library", drawers.Find("[data-drawer=honeypot-session]").TextContent, StringComparison.Ordinal);
        Assert.Empty(drawers.FindAll("[data-drawer=honeypot-session] em"));

        var rows = drawers.FindAll("[data-request]");
        Assert.Contains("/archive-k7f3/2024/07/a-b/", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Hidden link", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Shown", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Equal("+1.5 s", rows[1].QuerySelector("[data-part=gap]")!.TextContent);
        Assert.Contains("Landing", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Equal("+58.5 s", rows[2].QuerySelector("[data-part=gap]")!.TextContent);
        Assert.Null(rows[0].QuerySelector("[data-part=gap]"));
        Assert.Empty(drawers.FindAll("[data-action=more-requests]"));
    }

    [Fact]
    public async Task TheTimelineLoadsMoreRequestsAWholePageAtATime_AndMarksACutPath()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        var session = FakeDashboardHoneypot.Session(host: Trapped);
        context.Honeypot.Sessions.Add(session);
        List<HoneypotRequest> requests = [];
        for (var i = 0; i < HoneypotText.PageSize + 3; i++)
        {
            requests.Add(FakeDashboardHoneypot.Request(Now.AddSeconds(i), "/p" + i));
        }

        requests[2] = requests[2] with { Path = "/" + new string('x', 511), PathTruncated = true };
        context.Honeypot.Requests[session.Id] = requests;
        var drawers = context.Render<DrawerHost>();
        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);
        await page.Find("[data-action=open-session]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-request]").Count == HoneypotText.PageSize);
        Assert.EndsWith("...", drawers.FindAll("[data-request] [data-part=path]")[2].TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Cut at 512 characters: /xxx", drawers.FindAll("[data-request]")[2].QuerySelector("td.pdk-honeypot-text")!.GetAttribute("title"), StringComparison.Ordinal);

        await drawers.Find("[data-action=more-requests]").ClickAsync(new());
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-request]").Count == HoneypotText.PageSize + 3);

        Assert.Empty(drawers.FindAll("[data-action=more-requests]"));
        Assert.Equal(requests[HoneypotText.PageSize - 1].Id, context.Honeypot.RequestReads[^1].After);
        Assert.Equal("+1 s", drawers.FindAll("[data-request]")[HoneypotText.PageSize].QuerySelector("[data-part=gap]")!.TextContent);
    }

    [Fact]
    public async Task ASessionPastRetentionSaysSo()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        var session = FakeDashboardHoneypot.Session(host: Trapped);
        context.Honeypot.Sessions.Add(session);
        var drawers = context.Render<DrawerHost>();
        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        // The recording pruned it between the list and the click.
        context.Honeypot.Sessions.Clear();
        await page.Find("[data-action=open-session]").ClickAsync(new());

        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-part=gone]").Count == 1);
    }

    [Fact]
    public async Task WithNoTrapAnywhereAndNothingRecordedTheSectionIsOneLineWithALinkToTheHosts()
    {
        var (context, _) = Start(trap: false);
        await using var _1 = context;

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=honeypot-empty]").Count == 1);

        Assert.Equal("/hosts", page.Find("[data-section=honeypot-empty] a[data-link=hosts]").GetAttribute("href"));
        Assert.Empty(page.FindAll("[data-tile]"));
        Assert.Empty(page.FindAll("[data-section=honeypot-sessions-card]"));
    }

    [Fact]
    public async Task SessionsRecordedBeforeTheTrapWasTurnedOffAreStillShown()
    {
        var (context, _) = Start(trap: false);
        await using var _1 = context;
        context.Honeypot.Summary = Summary();
        context.Honeypot.Sessions.Add(FakeDashboardHoneypot.Session());

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-session]").Count == 1);

        Assert.Empty(page.FindAll("[data-section=honeypot-empty]"));
    }

    [Fact]
    public async Task ATrapWithNoSessionsYetShowsTheEmptyFigures()
    {
        var (context, _) = Start();
        await using var _1 = context;

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-tile=honeypot-sessions]").Count == 1);

        Assert.Empty(page.FindAll("[data-section=honeypot-empty]"));
        Assert.Contains("No session in this range", page.Markup, StringComparison.Ordinal);
        Assert.Contains("No session did anything yet", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARangeThatCantBeReadSaysSoAndRetries()
    {
        var (context, _) = Start();
        await using var _1 = context;
        context.Honeypot.Down = true;

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-part=honeypot-failed]").Count == 1);
        context.Honeypot.Down = false;
        context.Honeypot.Summary = Summary();
        await page.Find("[data-part=honeypot-failed] button").ClickAsync(new());

        await page.WaitForStateAsync(() => page.FindAll("[data-tile=honeypot-sessions]").Count == 1);
    }

    private static Task Search(IRenderedComponent<HoneypotStatistics> page, string text) =>
        page.InvokeAsync(() => page.FindComponent<MudTextField<string>>().Instance.ValueChanged.InvokeAsync(text));
}
