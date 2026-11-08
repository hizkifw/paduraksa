using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The config pages in a real browser against a real <c>PaduraksaHost</c> (PLAN §12, M5): an upstream and a proxy host created through the
/// review drawer, the host in the list, a simulated request that forwards to it, an edit undone from the toast, and the history showing
/// every revision. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots of each page are kept when
/// <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
public sealed class ConfigPagesEndToEndTests
{
    private const string HostName = "app.home.test";
    private const string ExtraName = "www.home.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAnUpstreamAndAHost_SimulateIt_EditAndUndo_AndSeeTheHistory()
    {
        (IPlaywright playwright, IBrowser browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using IPlaywright ownedPlaywright = playwright;
        await using IBrowser ownedBrowser = browser;
        await using DashboardHost host = await DashboardHost.StartAsync(Ct);
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        });
        IPage page = await context.NewPageAsync();
        List<string> problems = [];
        page.Console += (_, message) =>
        {
            // The 404 the test asks for on purpose is logged by the browser; anything else in red is a problem.
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase)
                || (message.Type == "error" && !message.Text.Contains("status of 404", StringComparison.Ordinal)))
            {
                problems.Add(message.Text);
            }
        };

        await SetUpAsync(page, host);

        // The empty states lead the way: no hosts yet, so first an upstream.
        await OpenFromNavigationAsync(page, "Hosts");
        await Expect(page.GetByText("No hosts yet")).ToBeVisibleAsync();
        await OpenFromNavigationAsync(page, "Upstreams");
        await Expect(page.GetByText("No upstreams yet")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Add an upstream" }).First.ClickAsync();
        await ExpectHeadingAsync(page, "New upstream");
        await FillAsync(page, "name", "app");
        await FillAsync(page, "destination-0", "http://127.0.0.1:18080");
        await PlaywrightBrowser.ScreenshotAsync(page, "upstream-editor");
        await ReviewAndApplyAsync(page, "review-upstream");
        await ExpectHeadingAsync(page, "Upstreams");
        await Expect(page.Locator("[data-upstream=app]")).ToContainTextAsync("http://127.0.0.1:18080");
        await PlaywrightBrowser.ScreenshotAsync(page, "upstreams");

        // A proxy host for it: the only upstream is picked for the first route already.
        await OpenFromNavigationAsync(page, "Hosts");
        await page.GetByRole(AriaRole.Link, new() { Name = "Add a host" }).First.ClickAsync();
        await ExpectHeadingAsync(page, "New host");
        await FillAsync(page, "name-0", HostName);
        await Expect(page.Locator("input[data-field='route-upstream-0'], [data-field='route-upstream-0'] input").First).ToHaveValueAsync("app");
        await PlaywrightBrowser.ScreenshotAsync(page, "host-editor");
        await ReviewAndApplyAsync(page, "review-host");
        await ExpectHeadingAsync(page, "Hosts");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = HostName })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "hosts");

        // The simulator: a request for the host is forwarded to its upstream.
        await OpenFromNavigationAsync(page, "Simulator");
        await ExpectHeadingAsync(page, "Simulator");
        await FillAsync(page, "url", $"https://{HostName}/movies?page=2");
        await page.Locator("[data-action=simulate]").ClickAsync();
        await Expect(page.Locator(".pdk-outcome")).ToContainTextAsync("Forwarded to app", new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-trace-step").First).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-trace")).ToContainTextAsync("Route match");
        await PlaywrightBrowser.ScreenshotAsync(page, "simulator");

        // Edit the host (a second name), apply, then undo it from the toast.
        await OpenFromNavigationAsync(page, "Hosts");
        await page.GetByRole(AriaRole.Link, new() { Name = HostName }).ClickAsync();
        await ExpectHeadingAsync(page, HostName);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add a name" }).ClickAsync();
        await FillAsync(page, "name-1", ExtraName);
        await page.Locator("[data-action=review]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-review=apply]")).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(ExtraName);
        await PlaywrightBrowser.ScreenshotAsync(page, "review-edit", fullPage: false);
        await page.Locator(".pdk-sheet [data-review=apply]").ClickAsync();
        await ExpectHeadingAsync(page, "Hosts");
        await Expect(page.Locator(".pdk-table")).ToContainTextAsync(ExtraName);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Undo" })).ToHaveCountAsync(1);
        await page.Locator(".mud-snackbar", new() { HasText = "Live as #3" }).GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
        await Expect(page.GetByText(new Regex("^Undone: #4"))).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-table")).Not.ToContainTextAsync(ExtraName, new() { Timeout = 15_000 });

        // The history has all four revisions, the newest first and live, with its diff.
        await OpenFromNavigationAsync(page, "History");
        await ExpectHeadingAsync(page, "History");
        await Expect(page.Locator(".pdk-timeline-item")).ToHaveCountAsync(4);
        await Expect(page.Locator(".pdk-timeline-item").First).ToContainTextAsync("Undo #3");
        await Expect(page.Locator(".pdk-timeline-item").First).ToContainTextAsync("Live");
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(ExtraName);
        await PlaywrightBrowser.ScreenshotAsync(page, "history");
        await page.Locator(".pdk-timeline-item[data-seq='2'] a").ClickAsync();
        await Expect(page.Locator(".pdk-diff-card")).ToContainTextAsync("Revision #2");
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(HostName);

        // An address the dashboard has no page at, and a host ID that isn't one.
        await page.GotoAsync("/hosts/not-a-host-id");
        await ExpectHeadingAsync(page, "Host not found");
        IResponse? missing = await page.GotoAsync("/no-such-page");
        Assert.Equal(404, missing?.Status);
        await ExpectHeadingAsync(page, "Page not found");
        await PlaywrightBrowser.ScreenshotAsync(page, "not-found");

        // Phone width: the list still reads.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/hosts");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = HostName })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "hosts-phone");

        Assert.Empty(problems);
    }

    private static async Task SetUpAsync(IPage page, DashboardHost host)
    {
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(new Regex("/setup$"));
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        await ExpectHeadingAsync(page, "Overview");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    /// <summary>Follows a side-navigation link, and waits for the live page.</summary>
    private static async Task OpenFromNavigationAsync(IPage page, string name)
    {
        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static Task ExpectHeadingAsync(IPage page, string title) =>
        Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });

    /// <summary>Types into a form field by its <c>data-field</c> name (MudBlazor puts the attribute on the input).</summary>
    private static async Task FillAsync(IPage page, string field, string value)
    {
        ILocator input = page.Locator($"input[data-field='{field}'], textarea[data-field='{field}'], [data-field='{field}'] input").First;
        await input.FillAsync(value);
        await input.BlurAsync();
    }

    /// <summary>Opens the review drawer, waits for the dry run, and applies.</summary>
    private static async Task ReviewAndApplyAsync(IPage page, string screenshot)
    {
        await page.Locator("[data-action=review]").ClickAsync();
        ILocator apply = page.Locator(".pdk-sheet [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff-group").First).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, screenshot, fullPage: false);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }
}
