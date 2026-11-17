
using Paduraksa.Dashboard;

using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// A page that loads its data once the circuit is live shows a skeleton while it is prerendered. One that loaded during the prerender showed
/// its buttons before the circuit had a handler for them, and a click made then did nothing (the Notifications page lost one, which was
/// found by a test that waited for a drawer that never opened). Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class PrerenderEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(DashboardRoutes.Certificates)]
    [InlineData(DashboardRoutes.LocalCa)]
    [InlineData(DashboardRoutes.DnsProviders)]
    [InlineData(DashboardRoutes.AcmeAccounts)]
    [InlineData(SettingsRoutes.Notifications)]
    [InlineData(DashboardRoutes.Logs)]
    public async Task APrerenderedPageHasNothingToClickUntilTheCircuitIsLive(string path)
    {
        await using var session = await BrowserSession.StartAsync(new());
        var (host, page) = session;
        await SetUpAsync(page, host);
        await PlaywrightBrowser.WatchPrerenderAsync(page);

        await page.GotoAsync(path);
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        // Once live, the page has loaded and has its buttons: the same selector finds what the prerender must not have had.
        await Expect(page.Locator(".pdk-page button:not([disabled]), .pdk-page [data-action]:not(a[href])").First).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var (seen, controls) = await PlaywrightBrowser.PrerenderedControlsAsync(page);
        Assert.True(seen, "the prerendered document was never seen; this test would pass on anything");
        Assert.True(controls.Count == 0, $"{path} showed these before the circuit was live:\n" + string.Join('\n', controls));
    }
}
