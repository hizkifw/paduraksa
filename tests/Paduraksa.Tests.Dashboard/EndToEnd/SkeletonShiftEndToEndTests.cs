using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// A statistics skeleton is the box its loaded component takes, so nothing moves when the figures arrive. Each page is measured twice in a
/// real browser: held at its skeleton (the script that starts the circuit is blocked, so the prerendered document is what stays on screen,
/// with no sleeps and no hook in the host) and loaded. A skeleton marks itself with <c>data-pending</c> where the loaded component has
/// <c>data-section</c> or <c>data-tile</c>, and the two are paired by name. Skipped without Chromium (see <see cref="PlaywrightBrowser"/>).
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class SkeletonShiftEndToEndTests
{
    private const double Tolerance = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One element in both states. A table or a list is as long as its data, so it only has to never be shorter than its skeleton.</summary>
    private sealed record Pair(string Pending, string Loaded, bool Exact = true);

    /// <summary>Where an element sits below an anchor, which must not change: the column below a skeleton doesn't move when it is replaced.</summary>
    private sealed record Below(string Anchor, string Pending, string Loaded);

    private static Pair Tile(string name) => new($"[data-pending={name}]", $"[data-tile={name}]");

    private static Pair Section(string name, bool exact = true) => new($"[data-pending={name}]", $"[data-section={name}]", exact);

    private static Pair Same(string selector) => new(selector, selector);

    [Fact]
    public async Task TheOverviewStripKeepsItsBoxAndWhatIsBelowItStaysPut()
    {
        var strip = "[data-section=traffic-strip]";
        await VerifyAsync("/", "overview",
            [Same(strip), Tile("requests"), Tile("errors"), Tile("refused"), Tile("latency"), Same(".pdk-stats")],
            [new Below(strip, ".pdk-stats", ".pdk-stats"), new Below(strip, "[data-section=hosts]", "[data-section=hosts]")]);
    }

    [Fact]
    public async Task TheTrafficTabKeepsItsBox()
    {
        await VerifyAsync("/analytics?tab=traffic", "traffic",
            [Same("section[data-section=traffic]"), Section("requests-chart"), Section("latency-chart"), Section("top-hosts", exact: false), Section("upstream-errors", exact: false)],
            [new Below(".pdk-analytics-tabs", "section.pdk-traffic", "section.pdk-traffic")]);
    }

    [Fact]
    public async Task TheVisitorsTabKeepsItsBox()
    {
        await VerifyAsync("/analytics?tab=visitors", "visitors",
            [
                Same("[data-part=tiles]"), Tile("requests"), Tile("bandwidth"), Tile("visitors"), Tile("errors"),
                Section("requests-chart"), Section("bandwidth-chart"), Section("visitors-chart"),
                Section("status", exact: false), Section("methods", exact: false), Section("protocols", exact: false),
                Section("top-paths", exact: false), Section("referers", exact: false), Section("slowest", exact: false), Section("largest", exact: false),
                Section("browsers", exact: false), Section("systems", exact: false), Section("devices", exact: false),
                Section("bot-kinds", exact: false), Section("top-bots", exact: false), Section("ignoring-robots", exact: false), Section("user-agents", exact: false),
            ],
            [new Below(".pdk-analytics-tabs", "[data-part=tiles]", "[data-part=tiles]"), new Below(".pdk-analytics-tabs", "[data-section=content]", "[data-section=content]")],
            seed: true);
    }

    [Fact]
    public async Task TheSecurityTabKeepsItsBox()
    {
        // Not the tab's whole height: a card that exists only when something was not served comes and goes with the counters.
        await VerifyAsync("/analytics?tab=security", "security",
            [Section("refusals-chart"), Section("proof-of-work"), Section("rate-limits"), Section("access-lists"), Section("connections"), Section("sign-in")],
            [new Below(".pdk-analytics-tabs", "section.pdk-security-stats", "section.pdk-security-stats")]);
    }

    [Fact]
    public async Task TheSecurityOverviewKeepsItsRefusalsLineAndTheTableBelowIt()
    {
        await VerifyAsync("/security", "security-overview",
            [new Pair("[data-pending=refused]", "[data-part=refused]")],
            [new Below(".pdk-page-header", "[data-pending=hosts]", "[data-section=hosts]")]);
    }

    private static async Task VerifyAsync(string path, string name, Pair[] pairs, Below[] below, bool seed = false)
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var context = await browser.NewContextAsync(new() { BaseURL = host.Admin.ToString(), IgnoreHTTPSErrors = true });
        var setup = await context.NewPageAsync();
        await AnalyticsEndToEndTests.SetUpAsync(setup, host);
        if (seed)
        {
            AnalyticsEndToEndTests.Seed(host);
        }

        await setup.CloseAsync();

        List<string> failures = [];
        foreach (var width in new[] { 1366, 390 })
        {
            foreach (var scheme in new[] { ColorScheme.Dark, ColorScheme.Light })
            {
                var label = $"{name}-{width}-{scheme.ToString().ToLowerInvariant()}";
                var held = await OpenAsync(context, path, width, scheme, hold: true);
                await Expect(held.Locator("[data-pending]").First).ToBeVisibleAsync(new() { Timeout = 15_000 });
                var skeleton = await MeasureAsync(held, pairs.Select(p => p.Pending), below);
                await PlaywrightBrowser.ScreenshotAsync(held, $"skeleton-{label}");
                await held.CloseAsync();

                var live = await OpenAsync(context, path, width, scheme, hold: false);
                await Expect(live.Locator(".pdk-shell[data-circuit=live]")).ToBeVisibleAsync(new() { Timeout = 15_000 });
                await Expect(live.Locator("[data-pending]")).ToHaveCountAsync(0, new() { Timeout = 20_000 });
                var loaded = await MeasureAsync(live, pairs.Select(p => p.Loaded), below);
                await PlaywrightBrowser.ScreenshotAsync(live, $"loaded-{label}");
                await live.CloseAsync();

                foreach (var pair in pairs)
                {
                    var before = skeleton.Boxes[pair.Pending];
                    var after = loaded.Boxes[pair.Loaded];
                    if (before is null || after is null)
                    {
                        failures.Add($"{label}: {pair.Pending} / {pair.Loaded} is missing in the {(before is null ? "skeleton" : "loaded")} state");
                        continue;
                    }

                    var short_ = after[3] < before[3] - Tolerance;
                    var different = Math.Abs(after[3] - before[3]) > Tolerance;
                    if (pair.Exact ? different : short_)
                    {
                        failures.Add($"{label}: {pair.Loaded} is {after[3]:0.#} px tall, its skeleton was {before[3]:0.#}");
                    }

                    if (Math.Abs(after[2] - before[2]) > Tolerance)
                    {
                        failures.Add($"{label}: {pair.Loaded} is {after[2]:0.#} px wide, its skeleton was {before[2]:0.#}");
                    }
                }

                foreach (var item in below)
                {
                    var before = skeleton.Offsets[item.Pending];
                    var after = loaded.Offsets[item.Loaded];
                    if (before is null || after is null || Math.Abs(before.Value - after.Value) > Tolerance)
                    {
                        failures.Add($"{label}: {item.Loaded} sits {after:0.#} px below {item.Anchor} loaded, {before:0.#} px in the skeleton");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, "The page moved when it loaded:\n" + string.Join('\n', failures));
    }

    private static async Task<IPage> OpenAsync(IBrowserContext context, string path, int width, ColorScheme scheme, bool hold)
    {
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(width, 900);
        await page.EmulateMediaAsync(new() { ColorScheme = scheme, ReducedMotion = ReducedMotion.Reduce });
        if (hold)
        {
            // Without the script there is no circuit, so the prerendered document is what stays on screen.
            await page.RouteAsync("**/_framework/blazor.web*", route => route.AbortAsync());
        }

        await page.GotoAsync(path);
        return page;
    }

    private sealed record Measured(Dictionary<string, double[]?> Boxes, Dictionary<string, double?> Offsets);

    private static async Task<Measured> MeasureAsync(IPage page, IEnumerable<string> selectors, Below[] below)
    {
        await page.EvaluateAsync("() => document.fonts.ready");
        Dictionary<string, double[]?> boxes = [];
        foreach (var selector in selectors.Distinct())
        {
            boxes[selector] = await BoxAsync(page, selector);
        }

        Dictionary<string, double?> offsets = [];
        foreach (var item in below)
        {
            var anchor = await BoxAsync(page, item.Anchor);
            var box = await BoxAsync(page, item.Pending) ?? await BoxAsync(page, item.Loaded);
            offsets[item.Pending] = offsets[item.Loaded] = anchor is null || box is null ? null : box[1] - anchor[1];
        }

        return new Measured(boxes, offsets);
    }

    private static Task<double[]?> BoxAsync(IPage page, string selector) => page.EvaluateAsync<double[]?>(
        "s => { const e = document.querySelector(s); if (!e) { return null; } const r = e.getBoundingClientRect(); return [r.left + scrollX, r.top + scrollY, r.width, r.height]; }",
        selector);
}
