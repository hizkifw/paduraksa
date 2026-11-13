using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The Local CA page in a real browser against a real <c>PaduraksaHost</c>: the root says what it may issue for, a rotation asks first, shows the
/// replaced root beside the new one, and retiring it removes it. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class LocalCaEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRootIsRotatedAfterAWarningTheReplacedOneStaysUntilRetired()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        BrowserNewContextOptions options = new()
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        };
        await using var context = await browser.NewContextAsync(options);
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
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await page.GotoAsync("/certificates/local-ca");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Local CA", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        var names = page.Locator("[data-section=permitted-names]");
        await Expect(names).ToContainTextAsync("Any name", new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-section=retiring-roots]")).ToHaveCountAsync(0);
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-dark");
        var oldName = await page.Locator(".pdk-trust .pdk-facts dd").First.InnerTextAsync();

        // Rotating asks first, and says what it costs.
        await page.ClickAsync("[data-action=rotate-local-ca]");
        await Expect(page.Locator("[data-banner=local-ca-dialog]")).ToContainTextAsync("key is destroyed");
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-rotate-dialog-dark");
        await Expect(page.Locator("[data-field=local-ca-names]")).ToHaveCountAsync(0); // unlimited is the default: no list until asked for
        await page.Locator("[data-field=local-ca-limit]").ClickAsync();
        var list = page.Locator("[data-field=local-ca-names]");
        await Expect(list).ToHaveValueAsync(new Regex("home\\.arpa[\\s\\S]*192\\.168\\.0\\.0/16"));
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-rotate-dialog-limited-dark");
        await list.FillAsync("lab.example.org");
        await page.Locator(".pdk-confirm").ClickAsync();

        var retiring = page.Locator("[data-section=retiring-roots]");
        await Expect(retiring).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(names).ToContainTextAsync("lab.example.org");
        await Expect(names).ToContainTextAsync("localhost"); // the admin certificate's names are kept
        Assert.NotEqual(oldName, await page.Locator(".pdk-trust .pdk-facts dd").First.InnerTextAsync());
        await Expect(retiring.Locator("[data-action=download-retiring-pem]")).ToBeVisibleAsync();
        // The admin listener's certificate was issued again at once, so nothing the replaced root signed is left and it can be retired.
        await Expect(retiring).ToContainTextAsync("nothing it signed is left");
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-rotated-dark");

        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-rotated-phone");
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
        await page.SetViewportSizeAsync(1366, 900);
        await PlaywrightBrowser.ScreenshotAsync(page, "local-ca-rotated-light");

        await retiring.Locator("[data-action=retire-root]").ClickAsync();
        await page.Locator(".pdk-confirm").ClickAsync();
        await Expect(page.Locator("[data-section=retiring-roots]")).ToHaveCountAsync(0, new() { Timeout = 15_000 });

        Assert.Empty(problems);
    }
}
