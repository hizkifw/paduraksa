using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>What the browser tests do on the dashboard's pages more than once: first-run setup, the navigation, and a drawer's fields.</summary>
internal static class DashboardPage
{
    private const string TopDrawer = ".pdk-sheet[data-top]";

    /// <summary>
    /// First-run setup through the form, ending on the overview with its circuit live. <paramref name="displayName"/> is filled in
    /// when there is one.
    /// </summary>
    public static async Task SetUpAsync(IPage page, DashboardHost host, string? displayName = "Ada Lovelace")
    {
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(TestContext.Current.CancellationToken));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        if (displayName is not null)
        {
            await page.FillAsync("input[name=displayName]", displayName);
        }

        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        // Setup lands on the overview.
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
        await ExpectHeadingAsync(page, "Overview");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    public static async Task OpenFromNavigationAsync(IPage page, string name)
    {
        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = name, Exact = true }).ClickAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    public static Task ExpectHeadingAsync(IPage page, string title) =>
        Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });

    /// <summary>The top drawer's title.</summary>
    public static Task ExpectDrawerTitleAsync(IPage page, string title) =>
        Expect(page.Locator($"{TopDrawer} .pdk-drawer-title")).ToHaveTextAsync(title, new() { Timeout = 15_000 });

    /// <summary>Fills a field of the top drawer (or of the page, with none open) and leaves it, so the form has taken the value.</summary>
    public static async Task FillAsync(IPage page, string field, string value)
    {
        var scope = await page.Locator(TopDrawer).CountAsync() > 0 ? page.Locator(TopDrawer) : page.Locator("body");
        var input = scope.Locator($"input[data-field='{field}'], textarea[data-field='{field}'], [data-field='{field}'] input").First;
        await input.FillAsync(value);
        await input.BlurAsync();
    }

    /// <summary>Opens a select of the top drawer and picks the item with <paramref name="item"/> in its text.</summary>
    public static async Task ChooseAsync(IPage page, string field, string item)
    {
        await page.Locator($"{TopDrawer} [data-field='{field}']").First.ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = item }).First.ClickAsync();
    }
}
