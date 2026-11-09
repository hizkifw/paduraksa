using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The config pages in a real browser against a real <c>PaduraksaHost</c> (PLAN §12): a proxy host made in its drawer with a new upstream
/// stacked on it (Escape closing only the top drawer), both reviewed and applied as one revision, the host and upstream in their lists, a
/// simulated request that forwards to it, an edit undone from the toast, the history showing every revision, addresses that open a drawer,
/// and a phone width with no sideways scroll. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots of each
/// page and drawer are kept when <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
public sealed class ConfigPagesEndToEndTests
{
    private const string HostName = "app.home.test";
    private const string ExtraName = "www.home.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateAHostWithANewUpstream_SimulateIt_EditAndUndo_AndSeeTheHistory()
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

        // Empty to start with; the hosts page doesn't send anyone to the upstreams first any more.
        await OpenFromNavigationAsync(page, "Upstreams");
        await Expect(page.GetByText("No upstreams yet")).ToBeVisibleAsync();
        await OpenFromNavigationAsync(page, "Hosts");
        await Expect(page.GetByText("No hosts yet")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "hosts-empty");

        // A new host, in its drawer (with an address of its own).
        await page.Locator("[data-action=add-host]").ClickAsync();
        ILocator hostDrawer = page.Locator(".pdk-sheet [data-drawer=host]");
        await Expect(hostDrawer).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page).ToHaveURLAsync(new Regex("/hosts/new$"));
        await ExpectDrawerTitleAsync(page, "New host");
        await FillAsync(page, "name-0", HostName);

        // "New upstream..." stacks the upstream's drawer on the host's; Escape closes only the top one.
        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(2);
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=trail]")).ToContainTextAsync("New host");
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);
        await ExpectDrawerTitleAsync(page, "New host");
        await Expect(page.Locator("input[data-field='name-0']")).ToHaveValueAsync(HostName);

        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='name']")).ToHaveValueAsync("app");
        await FillAsync(page, "destination-0", "http://127.0.0.1:18080");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-stack-upstream", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-action=stage]").ClickAsync();

        // Back in the host's drawer with the new upstream picked, and marked as going along with the host.
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);
        await Expect(page.Locator("input[data-field='route-upstream-0'], [data-field='route-upstream-0'] input").First).ToHaveValueAsync("app");
        await Expect(page.Locator("[data-staged=app]")).ToContainTextAsync("http://127.0.0.1:18080");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-host", fullPage: false);

        // The staged upstream opens again, as it was given, to be changed before anything is applied.
        await page.Locator("[data-staged=app] [data-action=edit-staged]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "app");
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='destination-0']")).ToHaveValueAsync("http://127.0.0.1:18080");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-staged-edit", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);

        // One review for both, one revision.
        await ReviewAndApplyAsync(page, "review-host-and-upstream");
        await Expect(page).ToHaveURLAsync(new Regex("/hosts$"));
        ILocator row = page.Locator(".pdk-hosts-table [data-host='app.home.test']");
        await Expect(row).ToContainTextAsync("app");
        await Expect(row).ToContainTextAsync("http://127.0.0.1:18080");
        await Expect(page.Locator(".mud-snackbar", new() { HasText = "Live as #1" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "hosts");

        await OpenFromNavigationAsync(page, "Upstreams");
        await Expect(page.Locator("[data-upstream=app]")).ToContainTextAsync("http://127.0.0.1:18080");
        await Expect(page.Locator("[data-upstream=app] [data-label='Used by']")).ToContainTextAsync(HostName);
        await PlaywrightBrowser.ScreenshotAsync(page, "upstreams");
        await page.Locator("[data-upstream=app] .pdk-row-link").ClickAsync();
        await ExpectDrawerTitleAsync(page, "app");
        await Expect(page.Locator(".pdk-sheet [data-section=usage]")).ToContainTextAsync(HostName);
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-upstream", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page).ToHaveURLAsync(new Regex("/upstreams$"));

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
        await ExpectDrawerTitleAsync(page, HostName);
        await Expect(page).ToHaveURLAsync(new Regex("/hosts/host_"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add a hostname" }).ClickAsync();
        await FillAsync(page, "name-1", ExtraName);
        await page.Locator(".pdk-sheet [data-action=review]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] [data-review=apply]")).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(ExtraName);
        await PlaywrightBrowser.ScreenshotAsync(page, "review-edit", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-review=apply]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-hosts-table")).ToContainTextAsync(ExtraName);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Undo" })).ToHaveCountAsync(1);
        await page.Locator(".mud-snackbar", new() { HasText = "Live as #2" }).GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
        await Expect(page.GetByText(new Regex("^Undone: #3"))).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-hosts-table")).Not.ToContainTextAsync(ExtraName, new() { Timeout = 15_000 });

        // The switch in the list goes through review too: switching the host off is reviewed, then cancelled.
        await page.Locator("[data-host='app.home.test'] .pdk-dt-status input[type=checkbox]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] [data-review=apply]")).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await ExpectDrawerTitleAsync(page, $"Switch off {HostName}");
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);

        // "New certificate..." in the host's drawer stacks the certificate flow: issued from the local CA for the host's names, it comes
        // back selected (it is in the vault at once; binding it to the host is the host's change, reviewed as usual).
        await page.GetByRole(AriaRole.Link, new() { Name = HostName }).ClickAsync();
        await ExpectDrawerTitleAsync(page, HostName);
        await ChooseAsync(page, "certificate", "New certificate...");
        await ExpectDrawerTitleAsync(page, "New certificate");
        // The host's picker keeps what it had while the certificate is made.
        await Expect(page.Locator(".pdk-sheet[data-level='0'] input[data-field='certificate']")).ToHaveValueAsync("Automatic: one that covers the hostnames");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-drawer=certificate-new]")).ToContainTextAsync($"For {HostName}");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-stack-certificate", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-method=localcaleaf]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] .pdk-local-names textarea")).ToHaveValueAsync(HostName);
        await page.Locator(".pdk-sheet[data-top] .pdk-local-submit").ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='certificate'], .pdk-sheet[data-top] [data-field='certificate'] input").First)
            .ToHaveValueAsync(new Regex($"^{Regex.Escape(HostName)} · until "));
        await ReviewAndApplyAsync(page, "review-certificate");
        await Expect(page.Locator("[data-host='app.home.test'] [data-chip=certificate]")).ToContainTextAsync("Valid", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "hosts-with-certificate");

        // The history has all four revisions, the newest first and live, with its diff.
        await OpenFromNavigationAsync(page, "History");
        await ExpectHeadingAsync(page, "History");
        await Expect(page.Locator(".pdk-timeline-item")).ToHaveCountAsync(4);
        await Expect(page.Locator(".pdk-timeline-item").First).ToContainTextAsync("Live");
        await Expect(page.Locator(".pdk-timeline-item[data-seq='3']")).ToContainTextAsync("Undo #2");
        await page.Locator(".pdk-timeline-item[data-seq='3'] a").ClickAsync();
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(ExtraName);
        await PlaywrightBrowser.ScreenshotAsync(page, "history");
        await page.Locator(".pdk-timeline-item[data-seq='1'] a").ClickAsync();
        await Expect(page.Locator(".pdk-diff-card")).ToContainTextAsync("Revision #1");
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync(HostName);
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync("127.0.0.1:18080");

        // A host ID that isn't one opens a drawer that says so; an address the dashboard has no page at is the 404.
        await page.GotoAsync("/hosts/not-a-host-id");
        await ExpectDrawerTitleAsync(page, "Host not found");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-not-found", fullPage: false);
        IResponse? missing = await page.GotoAsync("/no-such-page");
        Assert.Equal(404, missing?.Status);
        await ExpectHeadingAsync(page, "Page not found");
        await PlaywrightBrowser.ScreenshotAsync(page, "not-found");

        // Phone width: the lists read as cards, a drawer takes the whole width, and nothing scrolls sideways.
        await page.SetViewportSizeAsync(390, 844);
        foreach ((string path, string shot) in new[] { ("/hosts", "hosts-phone"), ("/upstreams", "upstreams-phone") })
        {
            await page.GotoAsync(path);
            await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
            await Expect(page.Locator(".pdk-dt-row").Nth(1)).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await ExpectNoSidewaysScrollAsync(page, path);
            await PlaywrightBrowser.ScreenshotAsync(page, shot);
        }

        // From the list, inside the app, so the browser's back stays in it.
        await page.GotoAsync("/hosts");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await page.Locator("[data-action=add-host]").First.ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=host]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await FillAsync(page, "name-0", "phone.home.test");
        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await ExpectInsideWindowAsync(page, page.Locator(".pdk-sheet[data-top] [data-action=stage]"));
        await ExpectNoSidewaysScrollAsync(page, "/hosts/new with a stacked upstream");
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-stack-phone", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-drawer-action=back]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(1);
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-host-phone", fullPage: false);

        // The browser's back button with unsaved changes asks too, and "Keep editing" stays where it was.
        await page.GoBackAsync();
        await Expect(page.Locator("[data-drawer=discard]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-drawer=discard]")).ToContainTextAsync("Leave and discard your changes?");
        await Expect(page).ToHaveURLAsync(new Regex("/hosts/new$"));
        await page.Locator("[data-drawer=keep]").ClickAsync();
        await Expect(page.Locator("[data-drawer=discard]")).ToHaveCountAsync(0);
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='name-0']")).ToHaveValueAsync("phone.home.test");

        // The host form holds a change now: leaving asks first.
        await page.Locator(".pdk-sheet[data-top] [data-drawer-action=close]").ClickAsync();
        await Expect(page.Locator("[data-drawer=discard]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "drawer-discard-phone", fullPage: false);
        await page.Locator("[data-drawer=discard-confirm]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);

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
        // Setup lands on the overview.
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
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

    /// <summary>The top drawer's title.</summary>
    private static Task ExpectDrawerTitleAsync(IPage page, string title) =>
        Expect(page.Locator(".pdk-sheet[data-top] .pdk-drawer-title")).ToHaveTextAsync(title, new() { Timeout = 15_000 });

    /// <summary>Types into a form field of the top drawer (or the page) by its <c>data-field</c> name (MudBlazor puts the attribute on the input).</summary>
    private static async Task FillAsync(IPage page, string field, string value)
    {
        ILocator scope = await page.Locator(".pdk-sheet[data-top]").CountAsync() > 0 ? page.Locator(".pdk-sheet[data-top]") : page.Locator("body");
        ILocator input = scope.Locator($"input[data-field='{field}'], textarea[data-field='{field}'], [data-field='{field}'] input").First;
        await input.FillAsync(value);
        await input.BlurAsync();
    }

    /// <summary>Opens a select in the top drawer by its <c>data-field</c> name and picks the item with <paramref name="item"/> in it.</summary>
    private static async Task ChooseAsync(IPage page, string field, string item)
    {
        await page.Locator($".pdk-sheet[data-top] input[data-field='{field}'], .pdk-sheet[data-top] [data-field='{field}'] input").First.ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = item }).ClickAsync();
    }

    /// <summary>Opens the review drawer from the top drawer, waits for the dry run, and applies.</summary>
    private static async Task ReviewAndApplyAsync(IPage page, string screenshot)
    {
        await page.Locator(".pdk-sheet[data-top] [data-action=review]").ClickAsync();
        ILocator apply = page.Locator(".pdk-sheet[data-top] [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff-group").First).ToBeVisibleAsync();
        await ExpectInsideWindowAsync(page, apply);
        await PlaywrightBrowser.ScreenshotAsync(page, screenshot, fullPage: false);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    /// <summary>The element is inside the window once a drawer that just opened has slid in (its animation takes 200 ms).</summary>
    private static async Task ExpectInsideWindowAsync(IPage page, ILocator element)
    {
        await page.WaitForTimeoutAsync(300);
        LocatorBoundingBoxResult? box = await element.BoundingBoxAsync();
        Assert.NotNull(box);
        int width = page.ViewportSize!.Width;
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"The element spans {box.X} to {box.X + box.Width} in a {width} px window.");
    }

    private static async Task ExpectNoSidewaysScrollAsync(IPage page, string where) =>
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"{where} scrolls sideways at 390 px");
}
