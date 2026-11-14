using Microsoft.Playwright;

using Paduraksa.Contracts.Config;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Settings, Server, in a real browser: a retention typed, reviewed (the sheet says what a lower value deletes) and applied, and the server
/// taking it with no restart. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class ServerSettingsEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARetentionIsLoweredThroughTheReviewDrawerAndTakesEffectLive()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/");
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();

        await page.GotoAsync("/settings/server");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var retention = page.Locator("input[data-field=server-metricsRetention]");
        await Expect(retention).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await retention.ClickAsync();
        await retention.PressSequentiallyAsync("7");
        await page.Keyboard.PressAsync("Tab");
        await page.ClickAsync("[data-action=review]");

        var apply = page.Locator(".pdk-sheet [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet")).ToContainTextAsync("goes down from 90 days to 7 days");
        await Expect(page.Locator(".pdk-sheet")).ToContainTextAsync("can't be recovered");
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(page.Locator(".mud-snackbar", new() { HasText = "Saved the server settings" })).ToBeVisibleAsync();

        Assert.Equal(TimeSpan.FromDays(7), ServerSettingCatalog.MetricsRetention.Resolve(host.Host.Shared.ServerSettings.Current));
        Assert.Equal(ServerSettingSource.Config, host.Host.Shared.ServerSettings.SourceOf(ServerSettingCatalog.MetricsRetention));
    }
}
