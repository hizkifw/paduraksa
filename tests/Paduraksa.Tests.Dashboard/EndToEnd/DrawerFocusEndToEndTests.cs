using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Focus follows the drawer stack: Escape closes the top drawer only and focus goes back to what opened it, also for a drawer that opens by
/// navigating (the router moves focus to the page's heading on the way). Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class DrawerFocusEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Escape_ClosesTheTopDrawerOnly_AndFocusGoesBackToItsOpener()
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
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await page.GotoAsync("/hosts");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();

        var addHost = page.Locator("[data-action=add-host]").First;
        await addHost.FocusAsync();
        await addHost.PressAsync("Enter");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=host]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // A drawer stacked on it, from a list in it: Escape closes that one, and focus is back in the level below, on the list.
        var upstream = page.Locator(".pdk-sheet[data-top] input[data-field='route-upstream-0'], .pdk-sheet[data-top] [data-field='route-upstream-0'] input").First;
        await upstream.ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "New upstream..." }).ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(2);
        // The new drawer has taken focus, so the list's menu is gone: Escape would be the menu's until then.
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='name']")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);
        await Expect(page.Locator(".pdk-sheet[data-level='0']")).ToHaveAttributeAsync("data-top", "true");
        Assert.True(await page.EvaluateAsync<bool>("() => !!document.activeElement.closest('.pdk-sheet[data-level=\"0\"]')"), "Focus left the drawer that is left.");

        // The last one: back to the control that opened it, though opening it navigated.
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);
        await Expect(addHost).ToBeFocusedAsync();
    }
}
