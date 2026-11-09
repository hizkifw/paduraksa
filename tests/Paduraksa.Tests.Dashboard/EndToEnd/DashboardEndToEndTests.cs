using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The dashboard in a real browser against a real <c>PaduraksaHost</c> (PLAN §12, M5): first-run setup through the setup page, sign-in with
/// a password and a TOTP code, the overview, a revoked session ending the open page, sign-in with a recovery code, and sign-out. Skipped
/// when no Chromium is installed (see <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed partial class DashboardEndToEndTests
{
    private const string SessionCookie = "__Host-paduraksa-session";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SetUp_SignInWithTotp_SeeTheOverview_LoseTheSessionToARevocation_AndSignOut()
    {
        (IPlaywright playwright, IBrowser browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using IPlaywright ownedPlaywright = playwright;
        await using IBrowser ownedBrowser = browser;
        await using DashboardHost host = await DashboardHost.StartAsync(Ct);
        await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true, // the admin's certificate comes from the process's own local CA
            ViewportSize = new ViewportSize { Width = 1366, Height = 860 },
            ColorScheme = ColorScheme.Dark,
        });
        IPage page = await context.NewPageAsync();
        List<string> policyViolations = [];
        page.Console += (_, message) =>
        {
            if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
            {
                policyViolations.Add(message.Text);
            }
        };
        List<string> sockets = [];
        page.WebSocket += (_, socket) => sockets.Add(socket.Url);

        // First run: any page leads to the setup page, which creates the Owner and signs them in.
        await page.GotoAsync("/");
        await Expect(page).ToHaveURLAsync(EndsWith("/setup"));
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Welcome to Paduraksa" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "setup");
        await page.FillAsync("input[name=setupToken]", await host.SetupTokenAsync(Ct));
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=displayName]", "Ada Lovelace");
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.FillAsync("input[name=confirmPassword]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
        // Setup lands on the overview (FirstRunEndToEndTests follows its checklist).
        await Expect(page).ToHaveURLAsync(EndsWith("/"));
        await ExpectOverviewAsync(page);

        // A second factor for the Owner, through the API with the browser's own session.
        (string provisioningUri, string recoveryCode) = await EnrolTotpAsync(context);

        // Sign out from the user menu.
        await SignOutAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "signin");

        // Sign in: the password, then the authenticator's code.
        await SignInWithPasswordAsync(page);
        await Expect(page).ToHaveURLAsync(EndsWith("/signin/verify"));
        await PlaywrightBrowser.ScreenshotAsync(page, "verify");
        // The next step's code: the confirmation just used this step's, and a code works once (the window allows one step ahead).
        await page.FillAsync("input[name=code]", TotpOracle.CodeFor(provisioningUri, stepOffset: 1));
        await page.ClickAsync("button[type=submit]");
        await ExpectOverviewAsync(page);
        await Expect(page.Locator(".pdk-user-chip")).ToContainTextAsync("Ada Lovelace");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Audit" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "home");

        // The page is live: the circuit runs over a WebSocket to the hub.
        Assert.Contains(sockets, url => url.Contains("/_blazor", StringComparison.Ordinal));

        // The light palette, and a phone-sized window (the navigation folds away).
        await page.GetByRole(AriaRole.Button, new() { Name = "Switch to the light theme" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Switch to the dark theme" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "home-light");
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        await ExpectOverviewAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "home-phone");
        await page.SetViewportSizeAsync(1366, 860);

        // Revoking the session elsewhere ends the open page within the re-check interval (one second here).
        await RevokeOwnSessionAsync(context);
        await Expect(page).ToHaveURLAsync(new Regex("/signin\\?.*notice=sessionEnded"), new() { Timeout = 15_000 });
        await Expect(page.GetByText("Your session ended")).ToBeVisibleAsync();

        // Back in with a recovery code instead of the authenticator, then out again.
        await SignInWithPasswordAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = "Use a recovery code instead" }).ClickAsync();
        await page.FillAsync("input[name=recoveryCode]", recoveryCode);
        await page.ClickAsync("button[type=submit]");
        await ExpectOverviewAsync(page);
        await SignOutAsync(page);
        Assert.DoesNotContain(await context.CookiesAsync(), cookie => cookie.Name == SessionCookie);

        Assert.Empty(policyViolations);
    }

    /// <summary>The overview, interactive: the server's render has been taken over by the live circuit.</summary>
    private static async Task ExpectOverviewAsync(IPage page)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Overview", Level = 1 })).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-stat").First).ToBeVisibleAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static async Task SignInWithPasswordAsync(IPage page)
    {
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await page.FillAsync("input[name=username]", DashboardHost.OwnerName);
        await page.FillAsync("input[name=password]", DashboardHost.OwnerPassword);
        await page.ClickAsync("button[type=submit]");
    }

    private static async Task SignOutAsync(IPage page)
    {
        await page.ClickAsync(".pdk-user-chip");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(EndsWith("/signin?notice=signedOut"));
        await Expect(page.GetByText("You have signed out.")).ToBeVisibleAsync();
    }

    /// <summary>Starts and confirms a TOTP enrolment as the signed-in browser; returns the provisioning URI and one recovery code.</summary>
    private static async Task<(string ProvisioningUri, string RecoveryCode)> EnrolTotpAsync(IBrowserContext context)
    {
        using JsonDocument started = await PostJsonAsync(context, "api/v1/me/totp/enrollment", new { password = DashboardHost.OwnerPassword });
        string uri = started.RootElement.GetProperty("provisioningUri").GetString()!;
        using JsonDocument confirmed = await PostJsonAsync(context, "api/v1/me/totp/enrollment/confirm", new { code = TotpOracle.CodeFor(uri) });
        return (uri, confirmed.RootElement.GetProperty("codes")[0].GetString()!);
    }

    /// <summary>Ends the browser's own session through the API, as another tab or another admin would.</summary>
    private static async Task RevokeOwnSessionAsync(IBrowserContext context)
    {
        IAPIResponse me = await context.APIRequest.GetAsync("api/v1/auth/me", new() { Headers = SameOrigin });
        Assert.True(me.Ok, $"auth/me answered {me.Status}");
        using JsonDocument caller = JsonDocument.Parse(await me.TextAsync());
        string session = caller.RootElement.GetProperty("sessionId").GetString()!;
        IAPIResponse revoked = await context.APIRequest.DeleteAsync($"api/v1/sessions/{session}", new() { Headers = SameOrigin });
        Assert.True(revoked.Ok, $"revoking the session answered {revoked.Status}");
    }

    private static async Task<JsonDocument> PostJsonAsync(IBrowserContext context, string path, object body)
    {
        IAPIResponse response = await context.APIRequest.PostAsync(path, new() { DataObject = body, Headers = SameOrigin });
        string text = await response.TextAsync();
        Assert.True(response.Ok, $"{path} answered {response.Status}: {text}");
        return JsonDocument.Parse(text);
    }

    private static Dictionary<string, string> SameOrigin { get; } = new() { ["Sec-Fetch-Site"] = "same-origin" };

    private static Regex EndsWith(string pathAndQuery) => new(Regex.Escape(pathAndQuery) + "$");
}
