using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.Playwright;

using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Shared;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The auth providers in a real browser against a real <c>PaduraksaHost</c> and a fake forward-auth endpoint: a provider written from nothing (an address
/// with credentials refused at its field first, then the Authelia preset), a host picking it by name and a route turning sign-in off, then a real request
/// to the public listener (refused with the provider's 401, let through with the provider's identity once the endpoint allows it, and the sign-in route
/// open), the overview naming the provider with what it decided, and the page, the drawer and the picker at a laptop and at a phone width, in dark and in
/// light, with no sideways scroll. Skipped when no Chromium is installed (see <see cref="PlaywrightBrowser"/>); screenshots are kept when
/// <see cref="PlaywrightBrowser.ScreenshotVariable"/> names a directory.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class AuthProvidersEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WriteAProvider_PickItOnAHost_AskItForARealRequest_AndSeeItOnThePagesAtBothWidthsInBothThemes()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var auth = await FakeAuthServer.StartAsync(Ct);
        await using var upstream = await FakeAuthServer.StartAsync(Ct);
        auth.Handler = static context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        upstream.Handler = static context =>
            context.Response.WriteAsync($"upstream saw Remote-User={context.Request.Headers["Remote-User"]} Remote-Groups={context.Request.Headers["Remote-Groups"]}");
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
        await SetUpAsync(page, host);

        // Nothing yet: the page says what a provider is for.
        await OpenFromNavigationAsync(page, "Sign-in");
        await Expect(page.GetByText("No auth providers yet")).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-empty-1280");

        // A provider: an address with credentials is refused at its field, then the Authelia preset and a bare address.
        await page.Locator("[data-action=add-auth-provider]").First.ClickAsync();
        await ExpectDrawerTitleAsync(page, "New auth provider");
        await Expect(page).ToHaveURLAsync(new Regex("/sign-in/new$"));
        await FillAsync(page, "name", "Authelia");
        await FillAsync(page, "url", "http://user:secret@authelia:9091");
        await Expect(page.Locator(".pdk-sheet[data-top]")).ToContainTextAsync("can't carry a user name or password", new() { Timeout = 15_000 });
        await ChooseAsync(page, "preset", "Authelia");
        await FillAsync(page, "url", auth.Address);
        await Expect(page.Locator(".pdk-sheet[data-top]")).Not.ToContainTextAsync("can't carry a user name or password", new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-sheet[data-top]")).ToContainTextAsync("/api/authz/forward-auth");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-section=response-headers-default]")).ToContainTextAsync("Remote-User");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-drawer-1280", fullPage: false);
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectInsideWindowAsync(page, page.Locator(".pdk-sheet[data-top] input[data-field='url']"));
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-drawer-390", fullPage: false);
        await page.Locator(".pdk-sheet[data-top] [data-field='sign-in-parameter']").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-drawer-390-end", fullPage: false);
        await page.SetViewportSizeAsync(1280, 900);
        await ReviewAndApplyAsync(page, "sign-in-review-1280");

        // The page lists it with its preset and endpoint.
        await Expect(page.Locator("[data-auth-provider='Authelia']")).ToContainTextAsync("Authelia at http://127.0.0.1", new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-page-1280");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectNoSidewaysScrollAsync(page, "The sign-in page");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-page-390");
        await page.SetViewportSizeAsync(1280, 900);

        // A host picks it by name; a second route, the one that serves the provider's own pages, turns sign-in off.
        await OpenFromNavigationAsync(page, "Hosts");
        await page.Locator("[data-action=add-host]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await FillAsync(page, "name-0", "app.home.test");
        await page.Locator(".pdk-sheet[data-top] input[data-field='auth-provider']").ScrollIntoViewIfNeededAsync();
        await page.Locator(".pdk-sheet[data-top] input[data-field='auth-provider']").ClickAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Authelia" })).ToContainTextAsync("Authelia at http://127.0.0.1");
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "New auth provider..." })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-picker-1280", fullPage: false);
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "Authelia" }).First.ClickAsync();
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='auth-provider']")).ToHaveValueAsync("Authelia");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await page.Locator(".pdk-sheet[data-top] input[data-field='auth-provider']").ScrollIntoViewIfNeededAsync();
        await page.Locator(".pdk-sheet[data-top] input[data-field='auth-provider']").ClickAsync();
        await Expect(page.Locator(".mud-popover-open .mud-list-item", new() { HasText = "anyone may reach it" })).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-picker-390", fullPage: false);
        await page.Keyboard.PressAsync("Escape");
        await Expect(page.Locator(".mud-popover-open")).ToHaveCountAsync(0);
        await page.SetViewportSizeAsync(1280, 900);
        await ChooseAsync(page, "route-upstream-0", "New upstream...");
        await ExpectDrawerTitleAsync(page, "New upstream");
        await FillAsync(page, "destination-0", upstream.Address);
        await page.Locator(".pdk-sheet[data-top] [data-action=stage]").ClickAsync();
        await ExpectDrawerTitleAsync(page, "New host");
        await page.Locator(".pdk-sheet[data-top]").GetByRole(AriaRole.Button, new() { Name = "Add a route" }).ClickAsync();
        await FillAsync(page, "route-path-1", "/oauth2");
        await page.Locator(".pdk-sheet[data-top] [data-action='route-menu-1']").ClickAsync();
        await page.Locator(".mud-popover-open [data-action=route-settings]").ClickAsync();
        await ChooseAsync(page, "route-auth-provider-1", "anyone may reach it");
        await Expect(page.Locator(".pdk-sheet[data-top] input[data-field='route-auth-provider-1']")).ToHaveValueAsync("No sign-in");
        await page.Locator(".pdk-sheet[data-top] [data-section='route-settings-1']").ScrollIntoViewIfNeededAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-host-route-1280", fullPage: false);
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await page.Locator(".pdk-sheet[data-top] [data-section='route-settings-1']").ScrollIntoViewIfNeededAsync();
        await ExpectInsideWindowAsync(page, page.Locator(".pdk-sheet[data-top] input[data-field='route-auth-provider-1']"));
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-host-route-390", fullPage: false);
        await page.SetViewportSizeAsync(1280, 900);
        await ReviewAndApplyAsync(page, "sign-in-host-review-1280");

        // A real request to the public listener: the endpoint is asked (the original request in X-Forwarded-*) and its 401 is the answer.
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(20) };
        var address = PublicHttpAddress(host.Host);
        using (var refused = await SendAsync(client, address, "/docs?x=1"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        var call = Assert.Single(auth.Calls);
        Assert.Equal("app.home.test", call.Header("X-Forwarded-Host"));
        Assert.Equal("/docs?x=1", call.Header("X-Forwarded-Uri"));
        Assert.Equal(0, upstream.CallCount);

        // Once the endpoint allows the session, the upstream sees the provider's identity and never the client's forged one.
        auth.Handler = static context =>
        {
            context.Response.Headers["Remote-User"] = "ada";
            context.Response.Headers["Remote-Groups"] = "admins";
            return Task.CompletedTask;
        };
        using (var allowed = await SendAsync(client, address, "/docs", request =>
        {
            request.Headers.TryAddWithoutValidation("Cookie", "authelia_session=abc");
            request.Headers.TryAddWithoutValidation("Remote-User", "root");
        }))
        {
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            Assert.Equal("upstream saw Remote-User=ada Remote-Groups=admins", await allowed.Content.ReadAsStringAsync(Ct));
        }

        // The route that turned sign-in off is open and asks nothing.
        var calls = auth.CallCount;
        using (var open = await SendAsync(client, address, "/oauth2/callback"))
        {
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }

        Assert.Equal(calls, auth.CallCount);

        // The overview names the provider the host asks and what it decided.
        await OpenFromNavigationAsync(page, "Security overview");
        await Expect(page.Locator("[data-host='app.home.test'] [data-cell=sign-in]")).ToContainTextAsync("Authelia", new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-host='app.home.test'] [data-cell=sign-in]")).ToContainTextAsync("1 allowed, 1 refused", new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-host='app.home.test'] [data-cell=sign-in]")).ToContainTextAsync("Off on 1 route");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-security-1280");
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await ExpectNoSidewaysScrollAsync(page, "The security overview");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-security-390");

        // The providers' summed counters are a card of the security statistics on Analytics, where the page's link leads.
        await page.Locator("[data-part=refused] a").ClickAsync();
        await Expect(page.Locator("[data-section=sign-in] [data-reason=sign-in-allowed]")).ToHaveTextAsync("1", new() { Timeout = 15_000 });
        await page.SetViewportSizeAsync(1280, 900);
        await OpenFromNavigationAsync(page, "Security overview");
        await Expect(page.Locator("[data-host='app.home.test'] [data-cell=sign-in]")).ToContainTextAsync("Authelia", new() { Timeout = 15_000 });
        await page.SetViewportSizeAsync(390, 844);

        // Light: the same pages in the other theme.
        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Light });
        await PlaywrightBrowser.SettleAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-security-390-light");
        await page.SetViewportSizeAsync(1280, 900);
        await PlaywrightBrowser.SettleAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-security-1280-light");
        await OpenFromNavigationAsync(page, "Sign-in");
        await Expect(page.Locator("[data-auth-provider='Authelia']")).ToContainTextAsync("1 host");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-page-1280-light");
        await page.Locator("[data-auth-provider='Authelia'] a.pdk-row-link").ClickAsync();
        await ExpectDrawerTitleAsync(page, "Authelia");
        await Expect(page.Locator(".pdk-sheet[data-top] [data-section=usage-list]")).ToContainTextAsync("app.home.test");
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-drawer-1280-light", fullPage: false);
        await page.SetViewportSizeAsync(390, 844);
        await PlaywrightBrowser.SettleAsync(page);
        await PlaywrightBrowser.ScreenshotAsync(page, "sign-in-drawer-390-light", fullPage: false);

        Assert.Empty(problems);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string address, string path, Action<HttpRequestMessage>? configure = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(new Uri(address), path));
        request.Headers.Host = "app.home.test";
        configure?.Invoke(request);
        return await client.SendAsync(request, Ct);
    }

    private static string PublicHttpAddress(PaduraksaHost host)
    {
        var address = host.GetListenAddresses(HostRole.Public).Single(a => a.StartsWith("http://", StringComparison.Ordinal));
        return new UriBuilder(address) { Host = IPAddress.Loopback.ToString() }.Uri.ToString();
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
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static async Task OpenFromNavigationAsync(IPage page, string name)
    {
        await page.Locator(".pdk-nav").GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }

    private static Task ExpectDrawerTitleAsync(IPage page, string title) =>
        Expect(page.Locator(".pdk-sheet[data-top] .pdk-drawer-title")).ToHaveTextAsync(title, new() { Timeout = 15_000 });

    private static async Task FillAsync(IPage page, string field, string value)
    {
        var input = page.Locator($".pdk-sheet[data-top] input[data-field='{field}'], .pdk-sheet[data-top] textarea[data-field='{field}'], .pdk-sheet[data-top] [data-field='{field}'] input").First;
        await input.FillAsync(value);
        await input.BlurAsync();
    }

    private static async Task ChooseAsync(IPage page, string field, string item)
    {
        await page.Locator($".pdk-sheet[data-top] [data-field='{field}']").First.ClickAsync();
        await page.Locator(".mud-popover-open .mud-list-item", new() { HasText = item }).First.ClickAsync();
    }

    private static async Task ReviewAndApplyAsync(IPage page, string screenshot)
    {
        await page.Locator(".pdk-sheet[data-top] [data-action=review]").ClickAsync();
        var apply = page.Locator(".pdk-sheet[data-top] [data-review=apply]");
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-diff-group").First).ToBeVisibleAsync();
        await PlaywrightBrowser.ScreenshotAsync(page, screenshot, fullPage: false);
        await apply.ClickAsync();
        await Expect(page.Locator(".pdk-sheet")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    private static async Task ExpectInsideWindowAsync(IPage page, ILocator element)
    {
        await PlaywrightBrowser.SettleAsync(page);
        var box = await element.BoundingBoxAsync();
        Assert.NotNull(box);
        var width = page.ViewportSize!.Width;
        Assert.True(box.X >= 0 && box.X + box.Width <= width, $"The element spans {box.X} to {box.X + box.Width} in a {width} px window.");
    }

    private static async Task ExpectNoSidewaysScrollAsync(IPage page, string where) =>
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"{where} scrolls sideways at 390 px");
}
