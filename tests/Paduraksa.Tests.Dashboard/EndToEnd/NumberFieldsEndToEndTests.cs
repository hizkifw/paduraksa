using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>A number typed into a field counts as changed the moment focus leaves it by Tab, as a text field's text does. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).</summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class NumberFieldsEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ANumberTypedAndTabbedOut_IsCommitted_WithoutEnterOrSubmit()
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

        // A settings page: typing is real key presses, and nothing but Tab follows.
        await page.GotoAsync("/settings/defaults");
        var perAddress = page.Locator("input[data-field=max-connections-per-ip]");
        await Expect(perAddress).ToBeVisibleAsync();
        await Expect(page.Locator("[data-action=discard]")).ToHaveCountAsync(0);
        await perAddress.ClickAsync();
        await perAddress.PressSequentiallyAsync("50");
        await page.Keyboard.PressAsync("Tab");
        await Expect(page.Locator("[data-action=discard]")).ToBeVisibleAsync();

        // A drawer: the field's value is in the form's model, so building the field again shows it.
        await page.GotoAsync("/upstreams/new");
        await Expect(page.Locator(".pdk-sheet[data-top]")).ToBeVisibleAsync();
        var activeHealth = page.Locator("[data-field=active-health]");
        await activeHealth.ClickAsync();
        var every = page.Locator(".pdk-sheet[data-top] .mud-input-control", new() { HasText = "Every (seconds)" }).Locator("input");
        await every.ClickAsync();
        await page.Keyboard.PressAsync("Control+a");
        await every.PressSequentiallyAsync("45");
        await page.Keyboard.PressAsync("Tab");
        await activeHealth.ClickAsync();
        await activeHealth.ClickAsync();
        await Expect(every).ToHaveValueAsync("45");
    }
}
