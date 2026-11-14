using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The overview's "Finish setting up" checklist: dismissed with a cookie the server reads (so the prerender has it), and a dismissal an
/// earlier build kept in local storage moves to the cookie on the first load. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class SetupChecklistEndToEndTests
{
    private const string Cookie = "paduraksa-checklist";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(IPlaywright, IBrowser, DashboardHost, IBrowserContext, IPage)> StartAsync()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        var host = await DashboardHost.StartAsync(Ct);
        var context = await browser.NewContextAsync(new() { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await AnalyticsEndToEndTests.SetUpAsync(page, host);
        await Expect(page.Locator(".pdk-checklist")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        return (playwright, browser, host, context, page);
    }

    [Fact]
    public async Task DismissingItWritesTheCookieAndTheNextPrerenderLeavesItOut()
    {
        var (playwright, browser, host, context, page) = await StartAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var ownedHost = host;
        await using var ownedContext = context;

        await page.Locator(".pdk-checklist-dismiss").ClickAsync();
        await Expect(page.Locator(".pdk-checklist")).ToHaveCountAsync(0);
        Assert.Contains(await context.CookiesAsync(), cookie => cookie.Name == Cookie && cookie.Value == "dismissed");

        // With no script at all the document is what the server rendered: it has no checklist.
        var plain = await context.NewPageAsync();
        await plain.RouteAsync("**/_framework/blazor.web*", route => route.AbortAsync());
        await plain.GotoAsync("/");
        await Expect(plain.Locator("[data-section=traffic-strip]")).ToBeVisibleAsync();
        await Expect(plain.Locator(".pdk-checklist")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ADismissalKeptInLocalStorageMovesToTheCookieAndTheChecklistDoesNotComeBack()
    {
        var (playwright, browser, host, context, page) = await StartAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var ownedHost = host;
        await using var ownedContext = context;

        await page.EvaluateAsync("() => localStorage.setItem('paduraksa-setup-checklist', 'dismissed')");
        await page.ReloadAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        // Rendered by the server (it had no cookie yet), but hidden before it paints and never shown.
        await Expect(page.Locator(".pdk-checklist")).ToBeHiddenAsync();
        Assert.Contains(await context.CookiesAsync(), cookie => cookie.Name == Cookie && cookie.Value == "dismissed");
        Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('paduraksa-setup-checklist')"));

        await page.ReloadAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-checklist")).ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
    }
}
