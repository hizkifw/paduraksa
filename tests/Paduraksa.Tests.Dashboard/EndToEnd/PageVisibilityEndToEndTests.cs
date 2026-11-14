using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The browser-tab visibility helper the polling pages use: each owner has its own listener, so a page that is leaving can't take down the one
/// that has just begun to watch. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class PageVisibilityEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADetachTakesDownOnlyItsOwnersListener()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new() { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var page = await context.NewPageAsync();
        await AnalyticsEndToEndTests.SetUpAsync(page, host);
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();

        // Two stand-ins for the circuit's object reference, each counting what it is told; the browser tab is shown, so a report is only
        // sent on a visibilitychange.
        var counts = await page.EvaluateAsync<int[]>("""
            () => {
                const counts = [0, 0];
                const fake = index => ({ invokeMethodAsync: () => { counts[index]++; return Promise.resolve(); } });
                const watch = paduraksa.pageVisibility;
                watch.watch("first", fake(0));
                watch.watch("second", fake(1));
                document.dispatchEvent(new Event("visibilitychange"));
                const both = [...counts];

                // The first is leaving after the second began: only the first stops.
                watch.detach("first");
                document.dispatchEvent(new Event("visibilitychange"));
                const afterDetach = [...counts];

                watch.detach("second");
                document.dispatchEvent(new Event("visibilitychange"));
                return [...both, ...afterDetach, ...counts];
            }
            """);

        Assert.Equal([1, 1, 1, 2, 1, 2], counts);
    }
}
