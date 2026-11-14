using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The charts' tooltip in a real browser: it is a DOM element that shows as soon as the pointer is over a column, carries the time in the
/// browser's zone and one row per line, stays inside the viewport at both edges, and goes when the pointer does. Skipped without Chromium
/// (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class ChartTooltipEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HoveringAColumnShowsTheTooltipAtOnceInTheBrowsersZoneAndMovingAwayHidesIt()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            TimezoneId = "Asia/Jakarta",
            HasTouch = true,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await AnalyticsEndToEndTests.SetUpAsync(page, host);
        AnalyticsEndToEndTests.Seed(host);

        await page.GotoAsync("/analytics?tab=visitors");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var columns = page.Locator("[data-chart=requests] rect.pdk-chart-hit");
        await Expect(columns.Last).ToBeAttachedAsync(new() { Timeout = 15_000 });
        var tip = page.Locator(".pdk-chart-tip");
        await Expect(tip).ToBeHiddenAsync();

        // Over a column that has data: the time with its zone, then one row per line of the chart (the legend's swatch, name and value).
        await columns.Last.HoverAsync();
        await Expect(tip).ToBeVisibleAsync();
        await Expect(tip.Locator(".pdk-chart-tip-time")).ToHaveTextAsync(new Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC\+7$"));
        var lines = await page.Locator("[data-chart=requests] .pdk-chart-key").AllInnerTextsAsync();
        Assert.NotEmpty(lines);
        await Expect(tip.Locator(".pdk-chart-tip-row")).ToHaveCountAsync(lines.Count);
        await Expect(tip.Locator(".pdk-chart-tip-row .pdk-chart-swatch")).ToHaveCountAsync(lines.Count);
        await Expect(tip.Locator(".pdk-chart-tip-name").First).ToHaveTextAsync(lines[0].Trim());
        await Expect(tip.Locator(".pdk-chart-tip-value").First).ToHaveTextAsync(new Regex(@"^\d"));
        await Expect(columns.Last).ToHaveClassAsync(new Regex("is-active"));
        await PlaywrightBrowser.ScreenshotAsync(page, "chart-tooltip-right", fullPage: false);
        await AssertInsideViewportAsync(page, tip);

        // The first column is at the left edge: the tooltip flips and clamps and never leaves the window.
        await columns.First.HoverAsync();
        await Expect(tip).ToBeVisibleAsync();
        await AssertInsideViewportAsync(page, tip);
        await PlaywrightBrowser.ScreenshotAsync(page, "chart-tooltip-left", fullPage: false);

        // Off the chart it goes, and the column it marked is no longer marked.
        await page.Mouse.MoveAsync(5, 5);
        await Expect(tip).ToBeHiddenAsync();
        await Expect(columns.First).Not.ToHaveClassAsync(new Regex("is-active"));

        // A phone: a tap shows it, a tap elsewhere hides it, and it still fits.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/analytics?tab=visitors");
        var phoneColumns = page.Locator("[data-chart=requests] rect.pdk-chart-hit");
        await Expect(phoneColumns.Last).ToBeAttachedAsync(new() { Timeout = 15_000 });
        await Expect(tip).ToBeHiddenAsync();
        await phoneColumns.Last.ScrollIntoViewIfNeededAsync();
        var box = (await phoneColumns.Last.BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        await Expect(tip).ToBeVisibleAsync();
        await AssertInsideViewportAsync(page, tip);
        await PlaywrightBrowser.ScreenshotAsync(page, "chart-tooltip-phone", fullPage: false);
        await page.Touchscreen.TapAsync(5, 5);
        await Expect(tip).ToBeHiddenAsync();
        Assert.Empty(problems);
    }

    private static async Task AssertInsideViewportAsync(IPage page, ILocator tip)
    {
        var box = (await tip.BoundingBoxAsync())!;
        var viewport = page.ViewportSize!;
        Assert.True(box.X >= 0 && box.Y >= 0 && box.X + box.Width <= viewport.Width && box.Y + box.Height <= viewport.Height,
            $"The tooltip at ({box.X}, {box.Y}) {box.Width}x{box.Height} leaves the {viewport.Width}x{viewport.Height} window.");
    }
}
