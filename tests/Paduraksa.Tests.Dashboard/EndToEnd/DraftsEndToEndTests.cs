using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// Drafts end to end in a real browser (PLAN §12): an upstream added to a new draft on one page, a host that uses it made on another (the picker offers the
/// upstream marked "Draft"), the bar following the person across pages and a reload, then the whole set reviewed and applied as one revision. Skipped when no
/// Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots are kept when <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class DraftsEndToEndTests
{
    private const string HostName = "drafted.home.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUpstreamInOnePageAndAHostUsingItInAnother_ReviewedAndAppliedAsOneRevision()
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
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);
        await Expect(page.Locator("[data-draft-bar]")).ToHaveCountAsync(0);

        // On the upstreams page: a new upstream, into a new draft (nothing is applied).
        await OpenFromNavigationAsync(page, "Upstreams");
        await page.Locator("[data-action=add-upstream]").First.ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=upstream]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await FillAsync(page, "name", "app");
        await FillAsync(page, "destination-0", "http://127.0.0.1:18080");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-action=add-to-new-draft]")).ToHaveTextAsync("Add to a new draft");
        await page.Locator(".pdk-sheet[data-top] [data-action=add-to-new-draft]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        var bar = page.Locator("[data-draft-bar]");
        await Expect(bar).ToBeVisibleAsync();
        await Expect(bar.Locator("[data-draft-count]")).ToHaveTextAsync("1 change");
        await Expect(page.Locator("[data-upstream=app] [data-chip=draft]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-bar-upstreams");

        // On the hosts page the bar is still there, and a host's form offers the upstream the draft adds, marked Draft.
        await OpenFromNavigationAsync(page, "Hosts");
        await Expect(bar.Locator("[data-draft-count]")).ToHaveTextAsync("1 change");
        await page.Locator("[data-action=add-host]").First.ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=host]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await FillAsync(page, "name-0", HostName);
        await page.Locator(".pdk-sheet[data-top] input[data-field='route-upstream-0'], .pdk-sheet[data-top] [data-field='route-upstream-0'] input").First.ClickAsync();
        var option = page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "app" });
        await Expect(option.Locator("[data-chip=draft]")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-picker", fullPage: false);
        await option.ClickAsync();

        // With a draft open the form's primary action adds to it; reviewing and applying now is the secondary one.
        var primary = page.Locator(".pdk-sheet[data-top] button[type=submit]");
        await Expect(primary).ToHaveAttributeAsync("data-action", "add-to-draft");
        await Expect(primary).ToContainTextAsync("Add to draft");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-action=review]")).ToContainTextAsync("Review and apply now");
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-host-form", fullPage: false);
        await primary.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(bar.Locator("[data-draft-count]")).ToHaveTextAsync("2 changes");
        await Expect(page.Locator($"[data-host='{HostName}'] [data-chip=draft]")).ToBeVisibleAsync();

        // The draft is the server's: a reload and a second tab both see it.
        await page.ReloadAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-draft-bar] [data-draft-count]")).ToHaveTextAsync("2 changes");
        var second = await context.NewPageAsync();
        await second.GotoAsync("/upstreams");
        await Expect(second.Locator("[data-draft-bar] [data-draft-count]")).ToHaveTextAsync("2 changes", new() { Timeout = 15_000 });
        await second.CloseAsync();

        // Nothing is live: the history is empty.
        await OpenFromNavigationAsync(page, "History");
        await Expect(page.Locator(".pdk-timeline-item")).ToHaveCountAsync(0);

        // Review: the draft's two entries, then the whole set as one change.
        await page.Locator("[data-draft-bar] [data-action=review-draft]").ClickAsync();
        var list = page.Locator(".pdk-sheet [data-drawer=draft]");
        await Expect(list.Locator("[data-entry]")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
        await Expect(list).ToContainTextAsync("Add upstream app");
        await Expect(list).ToContainTextAsync($"Add host {HostName}");
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-list", fullPage: false);
        await list.Locator("[data-action=review-changes]").ClickAsync();
        var apply = page.Locator(".pdk-sheet[data-top] [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet[data-top] .pdk-diff-group")).ToHaveCountAsync(2);
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-review", fullPage: false);
        await apply.ClickAsync();

        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-draft-bar]")).ToHaveCountAsync(0);
        await Expect(page.Locator(".mud-snackbar", new() { HasText = "Live as #1" })).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-timeline-item")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-timeline-item")).ToContainTextAsync($"Draft (2 changes): add upstream app, add host {HostName}");
        await OpenFromNavigationAsync(page, "Hosts");
        await Expect(page.Locator($"[data-host='{HostName}']")).ToContainTextAsync("http://127.0.0.1:18080");
        await Expect(page.Locator($"[data-host='{HostName}'] [data-chip=draft]")).ToHaveCountAsync(0);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task TheBar_FitsAPhoneAndTheListShowsDiscardAndRemove()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 390, Height = 800 },
            ColorScheme = ColorScheme.Light,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);

        await page.GotoAsync("/upstreams/new");
        await Expect(page.Locator(".pdk-sheet [data-drawer=upstream]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await FillAsync(page, "name", "phone");
        await FillAsync(page, "destination-0", "http://127.0.0.1:18081");
        await page.Locator(".pdk-sheet[data-top] [data-action=add-to-new-draft]").ClickAsync();
        await Expect(page.Locator("[data-draft-bar]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var barBox = (await page.Locator("[data-draft-bar]").BoundingBoxAsync())!;
        Assert.True(barBox.X >= 0 && barBox.X + barBox.Width <= 390 + 1, $"The bar ({barBox.X}..{barBox.X + barBox.Width}) should fit the window.");
        var scrollWidth = await page.EvaluateAsync<int>("document.documentElement.scrollWidth");
        Assert.True(scrollWidth <= 390, $"The page scrolls sideways ({scrollWidth}px).");
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-bar-phone");

        await page.Locator("[data-draft-bar] [data-action=review-draft]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=draft] [data-entry]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-list-phone", fullPage: false);
        await page.Locator(".pdk-sheet [data-entry] [data-action=remove-entry]").ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=draft] .pdk-empty")).ToBeVisibleAsync();

        // The draft is still open, empty; discarding asks first.
        await page.Locator(".pdk-sheet [data-drawer=draft] [data-action=discard-draft]").ClickAsync();
        await page.Locator(".pdk-confirm").ClickAsync();
        await Expect(page.Locator("[data-draft-bar]")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        Assert.Empty(problems);
    }

    [Fact]
    public async Task AnObjectChangedWhileTheReviewIsOpen_BlocksTheApply_AndTheSheetShowsItInPlace()
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
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);
        UpstreamConfig web = new(UpstreamId.New(), "web", Destinations: [new DestinationConfig(DestinationId.New(), "http://127.0.0.1:18090")]);
        Assert.Equal(ConfigCommandStatus.Committed, (await host.Host.Shared.Config.ExecuteAsync(new UpsertUpstreamCommand(web), CommandOrigin.Cli, Ct)).Status);

        await page.GotoAsync($"/upstreams/{web.Id}");
        await FillAsync(page, "name", "web renamed");
        await page.Locator(".pdk-sheet[data-top] [data-action=add-to-new-draft]").ClickAsync();
        await Expect(page.Locator("[data-draft-bar] [data-draft-count]")).ToHaveTextAsync("1 change", new() { Timeout = 15_000 });
        await page.Locator("[data-draft-bar] [data-action=review-draft]").ClickAsync();
        await page.Locator(".pdk-sheet [data-drawer=draft] [data-action=review-changes]").ClickAsync();
        var apply = page.Locator(".pdk-sheet[data-top] [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });

        // Someone else edits the same upstream while the sheet is open.
        var current = (await host.Host.Shared.Config.GetCurrentAsync(Ct)).Document.Upstreams.Single();
        await host.Host.Shared.Config.ExecuteAsync(new UpsertUpstreamCommand(current with { Name = "web theirs" }), CommandOrigin.Cli, Ct);
        await apply.ClickAsync();

        // Nothing is applied; the sheet stays, says which entry is blocked, and Apply is gone until it is resolved.
        var blocked = page.Locator(".pdk-sheet[data-top] [data-review=blocked]");
        await Expect(blocked).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(blocked).ToContainTextAsync("nothing can be applied");
        await Expect(blocked.Locator("[data-action=reload-entry]")).ToHaveCountAsync(1);
        await PlaywrightBrowser.ScreenshotAsync(page, "draft-blocked-review", fullPage: false);
        Assert.Equal("web theirs", (await host.Host.Shared.Config.GetCurrentAsync(Ct)).Document.Upstreams.Single().Name);
        Assert.Equal(2L, (await host.Host.Shared.Config.GetCurrentAsync(Ct)).Revision!.Seq);
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
        await Expect(page).ToHaveURLAsync(new Regex("/$"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview", Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static async Task OpenFromNavigationAsync(IPage page, string name)
    {
        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = name, Exact = true }).ClickAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static async Task FillAsync(IPage page, string field, string value)
    {
        var scope = await page.Locator(".pdk-sheet[data-top]").CountAsync() > 0 ? page.Locator(".pdk-sheet[data-top]") : page.Locator("body");
        var input = scope.Locator($"input[data-field='{field}'], textarea[data-field='{field}'], [data-field='{field}'] input").First;
        await input.FillAsync(value);
        await input.BlurAsync();
    }
}
