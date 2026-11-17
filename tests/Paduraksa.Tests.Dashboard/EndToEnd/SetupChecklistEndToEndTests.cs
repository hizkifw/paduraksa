using System.Text.RegularExpressions;


using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

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

    private static async Task<BrowserSession> StartAsync()
    {
        var session = await BrowserSession.StartAsync(new());
        await SetUpAsync(session.Page, session.Host);
        await Expect(session.Page.Locator(".pdk-checklist")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        return session;
    }

    [Fact]
    public async Task DismissingItWritesTheCookieAndTheNextPrerenderLeavesItOut()
    {
        await using var session = await StartAsync();
        var (_, page) = session;

        await page.Locator(".pdk-checklist-dismiss").ClickAsync();
        await Expect(page.Locator(".pdk-checklist")).ToHaveCountAsync(0);
        Assert.Contains(await session.Context.CookiesAsync(), cookie => cookie.Name == Cookie && cookie.Value == "dismissed");

        // With no script at all the document is what the server rendered: it has no checklist.
        var plain = await session.Context.NewPageAsync();
        await plain.RouteAsync("**/_framework/blazor.web*", route => route.AbortAsync());
        await plain.GotoAsync("/");
        await Expect(plain.Locator("[data-section=traffic-strip]")).ToBeVisibleAsync();
        await Expect(plain.Locator(".pdk-checklist")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task ADismissalKeptInLocalStorageMovesToTheCookieAndTheChecklistDoesNotComeBack()
    {
        await using var session = await StartAsync();
        var (_, page) = session;

        await page.EvaluateAsync("() => localStorage.setItem('paduraksa-setup-checklist', 'dismissed')");
        await page.ReloadAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        // Rendered by the server (it had no cookie yet), but hidden before it paints and never shown.
        await Expect(page.Locator(".pdk-checklist")).ToBeHiddenAsync();
        Assert.Contains(await session.Context.CookiesAsync(), cookie => cookie.Name == Cookie && cookie.Value == "dismissed");
        Assert.Null(await page.EvaluateAsync<string?>("() => localStorage.getItem('paduraksa-setup-checklist')"));

        await page.ReloadAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-checklist")).ToHaveCountAsync(0);
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
    }
}
