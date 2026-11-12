using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Paduraksa.Proxy.Logging;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The live tail in a real browser against a real <c>PaduraksaHost</c>: entries offered to the hub appear on the page over the circuit, a
/// filter narrows them, a row opens its full entry with the request ID and the simulator link, the simulator starts from that link, and the
/// page fits a phone. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed class LiveTailEndToEndTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessLogEntry Entry(string path, int status, string host = "app.example.test", string method = "GET", string? query = null) => new()
    {
        TimestampUtcTicks = DateTime.UtcNow.Ticks,
        ClientAddress = "198.51.100.7",
        Host = host,
        Method = method,
        Path = path,
        Query = query,
        Protocol = "HTTP/2",
        StatusCode = status,
        ResponseBytes = 2048,
        TotalTicks = 124_000,
        UpstreamTicks = 98_000,
        UpstreamAddress = "http://10.0.0.2:8080/",
        RequestId = "0k3m9x2p-" + path.Length.ToString("D14", System.Globalization.CultureInfo.InvariantCulture),
        UserAgent = "Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0",
    };

    [Fact]
    public async Task RequestsStreamInFilterOpenAndLeadToTheSimulator()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = host.Admin.ToString(),
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 },
            ColorScheme = ColorScheme.Dark,
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        await SetUpAsync(page, host);
        var hub = host.Host.Shared.AccessLogTail;
        hub.Offer(Entry("/welcome", 200));

        await page.GotoAsync("/logs/live");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-nav-link[aria-current=page]")).ToHaveTextAsync(new Regex("Live tail"));

        // The host's public listener is live, so a stray request to it (a port prober on a developer machine) can be in the table
        // too: the test looks at its own client address.
        await page.FillAsync("input[data-field=client]", "198.51.100.");
        await Expect(page.Locator("[data-seq]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });

        hub.Offer(Entry("/cart", 502, method: "POST", query: "?item=7&token=hunter2"));
        hub.Offer(Entry("/about", 404));
        await Expect(page.Locator("[data-seq]")).ToHaveCountAsync(3, new() { Timeout = 15_000 });
        Assert.DoesNotContain("hunter2", await page.ContentAsync(), StringComparison.Ordinal);
        await PlaywrightBrowser.ScreenshotAsync(page, "live-tail", fullPage: false);

        // The status filter reaches the hub; pausing keeps the view.
        await page.FillAsync("input[data-field=status]", "5xx");
        await Expect(page.Locator("[data-seq]")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await page.ClickAsync("[data-action=pause]");
        hub.Offer(Entry("/while-paused", 503));
        await Expect(page.Locator("[data-part=waiting]")).ToContainTextAsync("1 waiting", new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-seq]")).ToHaveCountAsync(1);
        await page.ClickAsync("[data-action=follow]");
        await Expect(page.Locator("[data-seq]")).ToHaveCountAsync(2, new() { Timeout = 15_000 });

        // A row opens its entry: the request ID is copyable, the simulator link carries the request.
        await page.Locator("[data-seq]").Last.ClickAsync();
        await Expect(page.Locator(".pdk-drawer [data-secret=request-id]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "live-tail-entry", fullPage: false);
        await page.ClickAsync(".pdk-drawer [data-action=simulate]");
        await Expect(page).ToHaveURLAsync(new Regex("/simulator\\?"));
        await Expect(page.Locator("input[data-field=url]")).ToHaveValueAsync("https://app.example.test/cart?item=7&token=REDACTED", new() { Timeout = 15_000 });
        await Expect(page.Locator("input[data-field=client-ip]")).ToHaveValueAsync("198.51.100.7");

        // Phone width: the tail fits without scrolling sideways.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/logs/live");
        await Expect(page.Locator("[data-seq]").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "/logs/live scrolls sideways at 390 px");
        await PlaywrightBrowser.ScreenshotAsync(page, "live-tail-phone");
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
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync();
    }
}
