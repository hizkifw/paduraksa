using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The access-list drawer's countries in a real browser: a search that finds a country by name, Tor and unknown offered first, chips for
/// what is picked, and a phone width with no sideways scroll. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>);
/// screenshots of the drawer at both widths are kept when <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
public sealed class AccessListCountriesEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDrawerFindsCountriesByNameAndKeepsThemAsChips_AtDesktopAndPhoneWidth()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);

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
        await Expect(drawer.Locator("[data-banner=no-country-source]")).ToContainTextAsync("every visitor's country is unknown");

        var allowed = drawer.Locator("[data-field=allowed-countries]");
        var denied = drawer.Locator("[data-field=denied-countries]");
        await allowed.Locator("input").ClickAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item").First).ToContainTextAsync("Tor exit nodes");
        await Expect(page.Locator(".mud-popover-open .mud-list-item").Nth(1)).ToContainTextAsync("Unknown country");
        await PlaywrightBrowser.ScreenshotAsync(page, "access-list-countries-search", fullPage: false);
        await allowed.Locator("input").FillAsync("germ");
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Germany (DE)" }).ClickAsync();
        await Expect(allowed.Locator("[data-country=DE]")).ToContainTextAsync("Germany (DE)");

        await denied.Locator("input").ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Tor exit nodes" }).ClickAsync();
        await Expect(denied.Locator("[data-country=T1]")).ToContainTextAsync("Tor exit nodes");

        // Germany is allowed, so the denied side no longer offers it.
        await denied.Locator("input").FillAsync("germ");
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Germany" })).ToHaveCountAsync(0);
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
