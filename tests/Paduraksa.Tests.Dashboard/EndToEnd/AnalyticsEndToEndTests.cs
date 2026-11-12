using System.Text.RegularExpressions;

using Microsoft.Playwright;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The analytics page in a real browser against a real <c>PaduraksaHost</c>: its entry under Insight, the tiles, the charts and a
/// gap, the tables with their links to the live tail, the range switch, and a page that fits a phone. Skipped without Chromium (see
/// <see cref="PlaywrightBrowser"/>).
/// </summary>
public sealed class AnalyticsEndToEndTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string Safari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";
    private const string Googlebot = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessLogEntry Entry(string path, int status = 200, string agent = Chrome, long bytes = 4_096, string protocol = "HTTP/2", string? referer = null, long ms = 12) => new()
    {
        Host = "app.example.test",
        Path = path,
        Method = "GET",
        Protocol = protocol,
        StatusCode = status,
        ResponseBytes = bytes,
        UserAgent = agent,
        Referer = referer,
        TotalTicks = ms * TimeSpan.TicksPerMillisecond,
    };

    private static ulong Visitor(int i)
    {
        var z = (ulong)i + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static AnalyticsRollup HostBucket(HostId host, DateTimeOffset at, params (AccessLogEntry Entry, int Visitor, bool Ignored)[] entries)
    {
        TrafficBucket bucket = new();
        foreach (var (entry, visitor, ignored) in entries)
        {
            var copy = entry;
            bucket.Record(in copy, Visitor(visitor), UserAgentClassifier.Classify(entry.UserAgent), ignored);
        }

        return new AnalyticsRollup(
            host.IsEmpty ? AnalyticsSubject.Unmatched : AnalyticsSubject.Host,
            host.IsEmpty ? string.Empty : host.ToString(),
            at.ToUnixTimeSeconds(),
            300,
            bucket.Requests,
            bucket.BytesOut,
            bucket.ServerErrors,
            TrafficBucketCodec.Encode(bucket));
    }

    /// <summary>
    /// Closed buckets for the last ten hours of two hosts and the requests no host owns, with a stretch nobody observed in the middle, put where
    /// the analytics service reads them (the backlog the drain closes buckets into).
    /// </summary>
    private static void Seed(DashboardHost host)
    {
        var app = HostId.New();
        var blog = HostId.New();
        var top = DateTimeOffset.UtcNow.AddMinutes(-10);
        top = top.AddTicks(-(top.Ticks % (TimeSpan.TicksPerMinute * 5)));
        List<AnalyticsRollup> rollups = [];
        for (var bucket = 0; bucket < 120; bucket++)
        {
            var at = top.AddMinutes(-5 * bucket);
            // Nothing was running for two hours and ten minutes, three to five hours back: whole hourly steps without coverage.
            if (bucket is >= 40 and < 66)
            {
                continue;
            }

            rollups.Add(new AnalyticsRollup(AnalyticsSubject.Coverage, string.Empty, at.ToUnixTimeSeconds(), 300, 0, 0, 0, null));
            var busy = 4 + (bucket % 7);
            List<(AccessLogEntry, int, bool)> entries = [];
            for (var i = 0; i < busy; i++)
            {
                entries.Add((Entry("/", referer: i % 3 == 0 ? "https://www.google.com/search?q=paduraksa" : null), (bucket * 20) + i, false));
                entries.Add((Entry("/products/" + (i % 5), agent: i % 2 == 0 ? Chrome : Safari, bytes: 18_000), (bucket * 20) + i, false));
            }

            entries.Add((Entry("/api/checkout", 502, ms: 900), (bucket * 20) + 1, false));
            entries.Add((Entry("/missing", 404), (bucket * 20) + 2, false));
            entries.Add((Entry("/media/video.mp4", bytes: 48_000_000, ms: 600), (bucket * 20) + 3, false));
            entries.Add((Entry("/page", agent: Googlebot), 9_000, false));
            entries.Add((Entry("/private", agent: "GPTBot/1.2"), 9_001, true));
            rollups.Add(HostBucket(app, at, [.. entries]));
            rollups.Add(HostBucket(blog, at, (Entry("/post/hello", protocol: "HTTP/1.1", referer: "https://news.example.org/x"), 7_000 + bucket, false)));
            rollups.Add(HostBucket(default, at, (Entry("/wp-login.php", 404, agent: "python-requests/2.32.3"), 8_000 + bucket, false)));
        }

        host.Host.Shared.AnalyticsBacklog.Add(rollups);
    }

    [Fact]
    public async Task ThePageShowsTheTrafficAndItsPathsLeadToTheLiveTail()
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
        Seed(host);

        await page.GotoAsync("/analytics");
        await Expect(page.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(page.Locator(".pdk-nav-link[aria-current=page]")).ToHaveTextAsync(new Regex("Analytics"));
        await Expect(page.Locator("[data-tile]")).ToHaveCountAsync(4, new() { Timeout = 15_000 });
        await Expect(page.Locator("[data-chart]")).ToHaveCountAsync(3);
        // The stretch nobody observed is shaded, not drawn as zero.
        await Expect(page.Locator("[data-chart=requests] rect.pdk-chart-gap")).ToHaveCountAsync(1);
        await PlaywrightBrowser.ScreenshotAsync(page, "analytics-day", fullPage: true);

        // The tables carry the paths, the bots and the one who ignored robots.txt, and a path links to the live tail filtered to it.
        await Expect(page.Locator("[data-section=top-paths] tbody tr").First).ToBeVisibleAsync();
        await Expect(page.Locator("[data-section=ignoring-robots] tbody tr")).ToHaveCountAsync(1);
        await Expect(page.Locator("[data-section=ignoring-robots] tbody tr")).ToContainTextAsync("GPTBot");
        await Expect(page.Locator("[data-section=largest] tbody tr").First).ToContainTextAsync("/media/video.mp4");

        await page.Locator("[data-field=range] [data-value=week]").ClickAsync();
        await Expect(page.Locator("[data-field=range] [data-value=week]")).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(page.Locator("[data-tile]")).ToHaveCountAsync(4, new() { Timeout = 15_000 });

        await page.Locator("[data-section=top-paths] tbody tr a", new() { HasTextString = "/products/0" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new Regex("/logs/live\\?path=%2Fproducts%2F0"));
        await Expect(page.Locator("input[data-field=path]")).ToHaveValueAsync("/products/0", new() { Timeout = 15_000 });

        // Phone width: the page fits without scrolling sideways.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync("/analytics");
        await Expect(page.Locator("[data-tile]")).ToHaveCountAsync(4, new() { Timeout = 15_000 });
        await PlaywrightBrowser.ScreenshotAsync(page, "analytics-phone");
        Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), "/analytics scrolls sideways at 390 px");
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
