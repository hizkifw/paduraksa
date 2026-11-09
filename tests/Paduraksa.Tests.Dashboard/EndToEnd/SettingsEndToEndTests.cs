using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The settings, account and audit pages in a real browser against a real <c>PaduraksaHost</c> (PLAN §12, M5): a Viewer created from the
/// users page sees no edit actions in a second browser; an API token made on the tokens page answers <c>/api/v1/auth/me</c>; a global
/// default changed through review and apply; the audit page shows all of it and the chain is intact; My account enrols TOTP and ends the
/// Owner's other session, whose page goes to the sign-in page. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed class SettingsEndToEndTests
{
    private const string ViewerName = "viewer";
    private const string ViewerPassword = "a viewer's long password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Users_Tokens_Defaults_Audit_AndMyAccount()
    {
        (IPlaywright playwright, IBrowser browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using IPlaywright ownedPlaywright = playwright;
        await using IBrowser ownedBrowser = browser;
        await using DashboardHost host = await DashboardHost.StartAsync(Ct);
        BrowserNewContextOptions options = new()
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        };
        await using IBrowserContext ownerContext = await browser.NewContextAsync(options);
        IPage page = await ownerContext.NewPageAsync();
        List<string> problems = PlaywrightBrowser.CollectProblems(page);

        await SetUpAsync(page, host);

        // A second browser signed in as the Owner too: its session is ended from My account at the end.
        await using IBrowserContext otherContext = await browser.NewContextAsync(options);
        IPage other = await otherContext.NewPageAsync();
        await SignInAsync(other, DashboardHost.OwnerName, DashboardHost.OwnerPassword);

        // Users and roles, from the side navigation: add a Viewer.
        await OpenFromNavigationAsync(page, "Users and roles");
        await ExpectHeadingAsync(page, "Users and roles");
        await page.ClickAsync("[data-action=add-user]");
        await page.FillAsync("input[data-field=username]", ViewerName);
        await page.FillAsync("input[data-field=display-name]", "Vera Viewer");
        await page.FillAsync("input[data-field=password]", ViewerPassword);
        await Expect(page.Locator("[data-role=viewer] input")).ToBeCheckedAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "user-add", fullPage: false);
        await page.ClickAsync("[data-action=save-user]");
        await Expect(page.Locator("[data-user=viewer]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "users");

        // The Viewer, in a browser of their own, is offered no change.
        await using (IBrowserContext viewerContext = await browser.NewContextAsync(options))
        {
            IPage viewer = await viewerContext.NewPageAsync();
            await SignInAsync(viewer, ViewerName, ViewerPassword);
            await viewer.GotoAsync("/hosts");
            await ExpectHeadingAsync(viewer, "Hosts");
            await Expect(viewer.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
            await Expect(viewer.GetByText("No hosts yet")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Expect(viewer.GetByRole(AriaRole.Link, new() { Name = "Add a host" })).ToHaveCountAsync(0);
            await viewer.GotoAsync("/settings/defaults");
            await Expect(viewer.Locator("[data-banner=read-only]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Expect(viewer.Locator("[data-action=review]")).ToHaveCountAsync(0);
            await viewer.GotoAsync("/settings");
            await ExpectHeadingAsync(viewer, "Global defaults");
            await Expect(viewer.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = "Users and roles" })).ToHaveCountAsync(0);
            await viewer.GotoAsync("/settings/users");
            await Expect(viewer.GetByText("Your role can't see the users")).ToBeVisibleAsync(new() { Timeout = 15_000 });
            await Expect(viewer.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = "Audit" })).ToHaveCountAsync(0);
            await PlaywrightBrowser.ScreenshotAsync(viewer, "viewer-users");
        }

        // An API token, shown once, that works against the API.
        await page.GotoAsync("/settings/tokens");
        await ExpectHeadingAsync(page, "API tokens");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=add-token]");
        await page.FillAsync("input[data-field=token-name]", "e2e script");
        await page.ClickAsync("[data-preset=read]");
        await PlaywrightBrowser.ScreenshotAsync(page, "token-create", fullPage: false);
        await page.ClickAsync("[data-action=issue]");
        ILocator secret = page.Locator("[data-secret=token] code");
        await Expect(secret).ToBeVisibleAsync(new() { Timeout = 15_000 });
        string token = (await secret.TextContentAsync())!.Trim();
        Assert.StartsWith("pdk_", token, StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "token-created", fullPage: false);
        await page.ClickAsync("[data-action=done]");
        await Expect(page.Locator("[data-secret=token]")).ToHaveCountAsync(0);
        Assert.DoesNotContain(token, await page.ContentAsync(), StringComparison.Ordinal);
        await Expect(page.Locator("[data-token='e2e script']")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "tokens");

        await using (IAPIRequestContext api = await playwright.APIRequest.NewContextAsync(new() { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true }))
        {
            IAPIResponse me = await api.GetAsync("api/v1/auth/me", new() { Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" } });
            Assert.True(me.Ok, $"auth/me answered {me.Status}");
            using JsonDocument caller = JsonDocument.Parse(await me.TextAsync());
            Assert.Equal("token", caller.RootElement.GetProperty("authKind").GetString());
            Assert.Equal(DashboardHost.OwnerName, caller.RootElement.GetProperty("user").GetProperty("username").GetString());
        }

        // A global default, changed through review and apply; the drawer's actions stay inside the window.
        await page.GotoAsync("/settings/defaults");
        await ExpectHeadingAsync(page, "Global defaults");
        // The form is the circuit's render (the heading is prerendered), so it can only appear once the circuit is live:
        // a reconnect under load re-renders it, and waiting on the form alone would race that.
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-form=defaults]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.GetByText("421 Misdirected Request").ClickAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "defaults");
        await page.ClickAsync("[data-action=review]");
        ILocator apply = page.Locator(".pdk-sheet [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff")).ToContainTextAsync("421 Misdirected Request");
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectInsideWindowAsync(page, apply);
        await PlaywrightBrowser.ScreenshotAsync(page, "review-defaults", fullPage: false);
        await page.SetViewportSizeAsync(390, 844);
        await ExpectInsideWindowAsync(page, apply);
        await PlaywrightBrowser.ScreenshotAsync(page, "review-defaults-phone", fullPage: false);
        await page.SetViewportSizeAsync(1366, 900);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Expect(page.Locator(".mud-snackbar", new() { HasText = "Saved the global defaults" })).ToBeVisibleAsync();

        // The audit page shows all of it, and the chain verifies.
        await OpenFromNavigationAsync(page, "Audit");
        await ExpectHeadingAsync(page, "Audit");
        await Expect(page.Locator("[data-action='user.create']")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-action='token.issue']")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-action='config.settings.update']")).ToBeVisibleAsync();
        await page.ClickAsync("[data-action=verify]");
        await Expect(page.Locator("[data-section=chain]")).ToHaveAttributeAsync("data-chain", "intact", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "audit");
        await page.Locator("[data-action='token.issue'] button").ClickAsync();
        await Expect(page.Locator(".pdk-sheet [data-drawer=audit-event] pre")).ToContainTextAsync("e2e script");
        Assert.DoesNotContain(token, await page.ContentAsync(), StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "audit-event", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0);

        // My account: two-step verification, then the other session ended.
        await page.ClickAsync(".pdk-user-chip");
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "My account" }).ClickAsync();
        await ExpectHeadingAsync(page, "My account");
        await Expect(page.Locator("[data-session]")).ToHaveCountAsync(2, new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "account");
        await page.ClickAsync("[data-action=enrol]");
        await page.FillAsync(".pdk-totp-password input", DashboardHost.OwnerPassword);
        await page.ClickAsync(".pdk-totp-begin");
        await Expect(page.Locator("svg.pdk-qr")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await page.Locator(".pdk-totp-manual summary").ClickAsync();
        string key = (await page.Locator(".pdk-totp-secret").TextContentAsync())!.Replace(" ", string.Empty, StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "account-totp-qr", fullPage: false);
        await page.FillAsync(".pdk-totp-code input", TotpOracle.CodeFor($"otpauth://totp/x?secret={key}"));
        await page.ClickAsync(".pdk-totp-confirm");
        await Expect(page.Locator(".pdk-recovery-codes li")).ToHaveCountAsync(10, new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "account-recovery-codes", fullPage: false);
        await page.Locator(".pdk-totp-saved input").CheckAsync();
        await page.ClickAsync(".pdk-totp-finish");
        await Expect(page.Locator(".pdk-recovery-codes")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-card=two-factor]")).ToHaveAttributeAsync("data-state", "on", new() { Timeout = 15_000 });

        await page.ClickAsync("[data-current=false] [data-action=revoke-session]");
        await page.ClickAsync(".pdk-confirm");
        await Expect(page.Locator("[data-session]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(other).ToHaveURLAsync(new Regex("/signin\\?.*notice=sessionEnded"), new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "account-done");

        // Phone width: the pages fit without scrolling sideways.
        await page.SetViewportSizeAsync(390, 844);
        foreach ((string path, string shot) in new[] { ("/settings/users", "users-phone"), ("/settings/tokens", "tokens-phone"), ("/settings/defaults", "defaults-phone"), ("/audit", "audit-phone"), ("/account", "account-phone") })
        {
            await page.GotoAsync(path);
            await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
            await PlaywrightBrowser.SettleAsync(page);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"{path} scrolls sideways at 390 px");
            await PlaywrightBrowser.ScreenshotAsync(page, shot);
        }

        Assert.Empty(problems);
    }

    [Fact]
    public async Task TheLogsPageListsAFileReadsItsLastLinesAndDownloadsIt()
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
            AcceptDownloads = true,
        });
        IPage page = await context.NewPageAsync();
        await SetUpAsync(page, host);

        // A line in the error log, as the process itself would have written it.
        string directory = host.Host.Options.Logs.ResolvedDirectory;
        Directory.CreateDirectory(directory);
        await File.AppendAllTextAsync(Path.Combine(directory, "error.log"), "warn: the browser test's own line\n", Ct);

        await OpenFromNavigationAsync(page, "Logs");
        await ExpectHeadingAsync(page, "Logs");
        await Expect(page.Locator("[data-file=\"error.log\"]")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The last lines open in a drawer, and the file downloads through the authenticated endpoint.
        await page.ClickAsync("[data-file=\"error.log\"]");
        await Expect(page.Locator(".pdk-drawer [data-section=tail]")).ToContainTextAsync("the browser test's own line", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "logs", fullPage: false);

        IDownload download = await page.RunAndWaitForDownloadAsync(() => page.ClickAsync(".pdk-drawer [data-action=download]"));
        Assert.Equal("error.log", download.SuggestedFilename);

        // Phone width: the log page fits without scrolling sideways.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/logs");
        await Expect(page.Locator("[data-file=\"error.log\"]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "/logs scrolls sideways at 390 px");
        await PlaywrightBrowser.ScreenshotAsync(page, "logs-phone");
    }

    private static async Task ExpectInsideWindowAsync(IPage page, ILocator element)
    {
        LocatorBoundingBoxResult? box = await element.BoundingBoxAsync();
        Assert.NotNull(box);
        int width = page.ViewportSize!.Width;
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"The element spans {box.X} to {box.X + box.Width} in a {width} px window.");
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

    private static async Task SignInAsync(IPage page, string username, string password)
    {
        await page.GotoAsync("/signin");
        await page.FillAsync("input[name=username]", username);
        await page.FillAsync("input[name=password]", password);
        await page.ClickAsync("button[type=submit]");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    private static async Task OpenFromNavigationAsync(IPage page, string name)
    {
        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static Task ExpectHeadingAsync(IPage page, string title) =>
        Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync(new() { Timeout = 15_000 });
}
