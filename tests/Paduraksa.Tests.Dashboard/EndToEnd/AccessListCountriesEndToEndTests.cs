using System.Text.RegularExpressions;


using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The access-list drawer's countries in a real browser: a country found by name and picked from the popover, chips for what is picked, the
/// popover closing when the person clicks away, and a phone width with no sideways scroll (the search and the chips' rules are in the component tests). Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>);
/// screenshots of the drawer at both widths are kept when <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class AccessListCountriesEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDrawerFindsCountriesByNameAndKeepsThemAsChips_AtDesktopAndPhoneWidth()
    {
        await using var session = await BrowserSession.StartAsync(BrowserSession.Desktop(width: 1280));
        var (host, page, problems) = session;

        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();

        await page.GotoAsync("/access-lists/new");
        var drawer = page.Locator(".pdk-sheet [data-drawer=access-list]");
        await Expect(drawer).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var allowed = drawer.Locator("[data-field=allowed-countries]");
        var denied = drawer.Locator("[data-field=denied-countries]");
        await allowed.Locator("input").ClickAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "access-list-countries-search", fullPage: false);
        await allowed.Locator("input").FillAsync("germ");
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Germany (DE)" }).ClickAsync();
        await Expect(allowed.Locator("[data-country=DE]")).ToContainTextAsync("Germany (DE)");

        await denied.Locator("input").ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Tor exit nodes" }).ClickAsync();
        await Expect(denied.Locator("[data-country=T1]")).ToContainTextAsync("Tor exit nodes");

        await drawer.Locator(".pdk-section-title", new() { HasText = "Countries" }).ClickAsync();
        await Expect(denied.Locator("input")).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(page.Locator(".mud-popover-open")).ToHaveCountAsync(0);
        await PlaywrightBrowser.SettleAsync(page);

        await drawer.Locator("[data-section=countries]").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "access-list-countries-desktop", fullPage: false);

        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await drawer.Locator("[data-section=countries]").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "access-list-countries-phone", fullPage: false);
        Assert.False(
            await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"),
            "The page scrolls sideways at a phone width.");
        var section = (await drawer.Locator("[data-section=countries]").BoundingBoxAsync())!;
        Assert.True(section.X >= 0 && section.X + section.Width <= 390 + 1, $"The countries section ({section.X}..{section.X + section.Width}) should fit the phone's width.");

        Assert.Empty(problems);
    }
}
