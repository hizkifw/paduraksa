using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

using Paduraksa.Server.Hosting;

using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The honeypot from the card to the table in a real browser against a real <c>PaduraksaHost</c>: a host gets a trap through its settings, a
/// crawler (a plain client with a user agent made of markup) reads the index, follows a link and guesses a URL, and the session shows in
/// Analytics, Security, Honeypot with its requests in the drawer. The table is read from the stored rows, which the recording writes every
/// five seconds, so the test waits for them. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots are
/// kept when <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed partial class HoneypotEndToEndTests
{
    private const string Agent = "<b>E2E</b>Bot/1.0 (+https://example.test/bot)";
    private const string Top = ".pdk-sheet[data-top]";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ATrapTurnedOnInTheCardRecordsACrawlersSessionAndTheSectionShowsItWithItsRequests()
    {
        await using var session = await BrowserSession.StartAsync(BrowserSession.Desktop(width: 1366));
        var (host, page, problems) = session;
        await using var upstream = await LoopbackUpstream.StartAsync(static context => context.Response.WriteAsync("the upstream page"), cancellationToken: Ct);
        await SetUpAsync(page, host);

        // Nothing has a trap yet: the section is a line that leads to the hosts.
        await page.GotoAsync("/analytics?tab=security");
        await Expect(page.Locator("[data-section=honeypot-empty] a[data-link=hosts]")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "honeypot-empty-1366");

        // A host with the trap on, through the card of its settings, and no share behind the challenge so every page is served.
        await OpenFromNavigationAsync(page, "Hosts");
        await page.Locator("[data-action=add-host]").First.ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await FillAsync(page, "name-0", "127.0.0.1");
        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await FillAsync(page, "destination-0", upstream.Address);
        await page.Locator($"{Top} [data-action=stage]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await ChooseAsync(page, "honeypot-enabled", "On");
        await FillAsync(page, "honeypot-path", "/archive-e2e/");
        await FillAsync(page, "honeypot-pow-share", "0");
        await page.Locator($"{Top} [data-action=review]").ClickAsync();
        var apply = page.Locator($"{Top} [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });

        // The crawler: the index, a post the index links to, and a URL that is no page.
        var address = PublicHttpAddress(host.Host);
        var index = await Get(session, address + "archive-e2e/");
        Assert.Equal(200, index.Status);
        var post = PostLink().Match(await index.TextAsync());
        Assert.True(post.Success, "The index links to a post.");
        Assert.Equal(200, (await Get(session, address + post.Value.TrimStart('/'))).Status);
        Assert.Equal(404, (await Get(session, address + "archive-e2e/wp-login-not-a-page")).Status);

        // The recording writes every five seconds and the section reads every five: the session turns up within about ten.
        await page.GotoAsync("/analytics?tab=security");
        var row = page.Locator("[data-section=honeypot] [data-session]");
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = 40_000 });
        await Expect(page.Locator("[data-section=honeypot-empty]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-tile=honeypot-sessions] [data-part=value]")).ToHaveTextAsync("1", new() { Timeout = 20_000 });
        await Expect(row.Locator("[data-part=address] .pdk-copy-text")).ToHaveTextAsync("127.0.0.1");

        // The user agent is text: the markup it holds made no element.
        await Expect(row.Locator("[data-part=agent]")).ToHaveTextAsync(Agent);
        Assert.Equal(0, await page.Locator("[data-section=honeypot] b").CountAsync());
        Assert.Equal(0, await page.Locator("[data-section=honeypot] a[href*='example.test']").CountAsync());
        await Expect(page.Locator("[data-section=honeypot-paths] [data-guessed='/archive-e2e/wp-login-not-a-page']")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Expect(page.Locator("[data-section=honeypot-behaviour] [data-behaviour=direct]")).ToBeVisibleAsync();
        await ScrollToSectionAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "honeypot-section-1366", fullPage: false);

        // The search narrows the table.
        await page.Locator("input[data-field=honeypot-search]").FillAsync("no such visitor");
        await Expect(row).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await page.Locator("input[data-field=honeypot-search]").FillAsync("E2E");
        await Expect(row).ToHaveCountAsync(1, new() { Timeout = 15_000 });

        // The drawer: the facts and the three requests in order, with their kinds.
        await row.Locator("[data-action=open-session]").ClickAsync();
        await Expect(page.Locator($"{Top} [data-drawer=honeypot-session]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var requests = page.Locator($"{Top} [data-request]");
        await Expect(requests).ToHaveCountAsync(3, new() { Timeout = 15_000 });
        await Expect(requests.Nth(0)).ToContainTextAsync("/archive-e2e/");
        await Expect(requests.Nth(0)).ToContainTextAsync("Index");
        await Expect(requests.Nth(1)).ToContainTextAsync("Post");
        await Expect(requests.Nth(2)).ToContainTextAsync("Not a page");
        await Expect(requests.Nth(2)).ToContainTextAsync("404");
        await Expect(page.Locator($"{Top} [data-part=agent] .pdk-copy-text")).ToHaveTextAsync(Agent);
        await PlaywrightBrowser.ScreenshotAsync(page, "honeypot-drawer-1366", fullPage: false);

        Assert.Empty(problems);
    }

    private static async Task ScrollToSectionAsync(IPage page) =>
        await page.Locator("#pdk-analytics-honeypot").EvaluateAsync("el => el.scrollIntoView({block: 'start'})");

    private static Task<IAPIResponse> Get(BrowserSession session, string url) =>
        session.Context.APIRequest.GetAsync(url, new() { Headers = new Dictionary<string, string> { ["User-Agent"] = Agent, ["Accept"] = "text/html" } });

    private static string PublicHttpAddress(PaduraksaHost host)
    {
        var address = host.GetListenAddresses(HostRole.Public).Single(a => a.StartsWith("http://", StringComparison.Ordinal));
        return new UriBuilder(address) { Host = IPAddress.Loopback.ToString() }.Uri.ToString();
    }

    [GeneratedRegex("/archive-e2e/\\d{4}/\\d{2}/[^\"'<> ]+")]
    private static partial Regex PostLink();
}
