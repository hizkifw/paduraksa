using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;
using static Paduraksa.Tests.Dashboard.EndToEnd.DashboardPage;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The rule sets in a real browser against a real <c>PaduraksaHost</c>: a preset copied to edit and saved, a set written from nothing
/// (a path, methods, a user agent, an action) with an invalid regex shown at its field first, a host picking the set by name in its
/// Access settings, the overview naming it, and the page, the drawer and the picker at a laptop and at a phone width with no sideways
/// scroll. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots are kept when
/// <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class RuleSetsEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CopyAPreset_WriteASet_PickItOnAHost_AndSeeThemOnThePageAtBothWidths()
    {
        await using var session = await BrowserSession.StartAsync(BrowserSession.Desktop(width: 1280));
        var (host, page, problems) = session;
        await SetUpAsync(page, host);

        // Nothing yet: the page says what a set is for, and the presets are there to start from.
        await OpenFromNavigationAsync(page, "Rule sets");
        await Expect(page.GetByText("No rule sets yet")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-preset]")).ToHaveCountAsync(3);
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-empty-1280");

        // Create from a preset: a new set with the preset's rules under a name of its own.
        await page.Locator("[data-preset='Block known scanners'] [data-action=create-from-preset]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New rule set");
        await Expect(page).ToHaveURLAsync(new Regex("/rule-sets/new\\?copy=rs_"));
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field=name]")).ToHaveValueAsync("Block known scanners (custom)");
        await FillAsync(page, "name", "Scanners and admin");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-rule]")).ToHaveCountAsync(1);
        await page.Locator(".pdk-sheet[data-top] [data-action=add-rule]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] [data-rule]")).ToHaveCountAsync(2);

        // The second rule: a regex that can't be built is said at its field, then fixed.
        await ChooseAsync(page, "path-kind-1", "Matches regex");
        await FillAsync(page, "pattern-1", "(unclosed");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-rule='1']")).ToContainTextAsync("can't be used", new() { Timeout = 15_000 });
        await page.Locator(".pdk-sheet[data-top] [data-rule='1']").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-drawer-regex-error-1280", fullPage: false);
        await FillAsync(page, "pattern-1", @"\.(php|asp)$");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-rule='1']")).Not.ToContainTextAsync("can't be used", new() { Timeout = 15_000 });
        await ChooseAsync(page, "methods-1", "POST");
        await page.Locator(".pdk-sheet[data-top] .pdk-drawer-title").ClickAsync();
        await ChooseAsync(page, "action-1", "Drop the connection");
        await page.Locator(".pdk-sheet[data-top] [data-action=add-rule]").ClickAsync();
        await ChooseAsync(page, "path-kind-2", "Starts with");
        await FillAsync(page, "pattern-2", "/admin");
        await page.Locator(".pdk-sheet[data-top] [data-action='move-up-2']").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='pattern-1']")).ToHaveValueAsync("/admin");
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-drawer-1280", fullPage: false);
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectInsideWindowAsync(page, page.Locator(".pdk-sheet[data-top] [data-rule='1'] [data-field='pattern-1']"));
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-drawer-390", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-rule='2']").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-drawer-390-end", fullPage: false);
        await page.SetViewportSizeAsync(1280, 900);
        await ReviewAndApplyAsync(page, "rules-review-1280");

        // The page lists the set with what its rules do, and the presets under it.
        await Expect(page.Locator("[data-rule-set='Scanners and admin']")).ToContainTextAsync("3 rules", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-page-1280");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectNoSidewaysScrollAsync(page, "The rules page");
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-page-390");
        await page.SetViewportSizeAsync(1280, 900);

        // A host picks it by name in its Access settings: the picker lists the set, then the presets, "Built-in" on those.
        await OpenFromNavigationAsync(page, "Hosts");
        await page.Locator("[data-action=add-host]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await FillAsync(page, "name-0", "app.home.test");
        await page.Locator(".pdk-sheet[data-top] input[data-field='rule-set']").ScrollIntoViewIfNeededAsync();
        await page.Locator(".pdk-sheet[data-top] input[data-field='rule-set']").ClickAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Scanners and admin" })).ToBeVisibleAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Block AI crawlers" })).ToContainTextAsync("Built-in");
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-picker-1280", fullPage: false);
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Scanners and admin" }).ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='rule-set']")).ToHaveValueAsync("Scanners and admin");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await page.Locator(".pdk-sheet[data-top] input[data-field='rule-set']").ScrollIntoViewIfNeededAsync();
        await page.Locator(".pdk-sheet[data-top] input[data-field='rule-set']").ClickAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Block AI crawlers" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-picker-390", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".mud-popover-open")).ToHaveCountAsync(0);
        await page.SetViewportSizeAsync(1280, 900);
        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await FillAsync(page, "destination-0", "http://127.0.0.1:18081");
        await page.Locator(".pdk-sheet[data-top] [data-action=stage]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await ReviewAndApplyAsync(page, "rules-host-review-1280");

        // The overview names the set the host applies.
        await OpenFromNavigationAsync(page, "Security overview");
        await Expect(page.Locator("[data-host='app.home.test'] [data-cell=rules]")).ToContainTextAsync("Scanners and admin", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-security-1280");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectNoSidewaysScrollAsync(page, "The security overview");
        await PlaywrightBrowser.ScreenshotAsync(page, "rules-security-390");

        Assert.Empty(problems);
    }

    private static async Task ReviewAndApplyAsync(IPage page, string screenshot)
    {
        await page.Locator(".pdk-sheet[data-top] [data-action=review]").ClickAsync();
        var apply = page.Locator(".pdk-sheet[data-top] [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff-group").First).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, screenshot, fullPage: false);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    private static async Task ExpectInsideWindowAsync(IPage page, ILocator element)
    {
        await PlaywrightBrowser.SettleAsync(page);
        var box = await element.BoundingBoxAsync();
        Assert.NotNull(box);
        var width = page.ViewportSize!.Width;
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"The element spans {box.X} to {box.X + box.Width} in a {width} px window.");
    }

    private static async Task ExpectNoSidewaysScrollAsync(IPage page, string where) =>
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"{where} scrolls sideways at 390 px");
}
