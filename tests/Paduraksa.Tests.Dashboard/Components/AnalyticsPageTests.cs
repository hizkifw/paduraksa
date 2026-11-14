using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Application.Analytics;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Layout;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Data;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The analytics page: the tiles, the charts and their gaps, the breakdowns, the tables with their links and marks, the clients and bots, and its range and host switches.</summary>
public sealed class AnalyticsPageTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string Safari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly HostId Blog = HostId.New();

    private static AccessLogEntry Entry(string path, int status = 200, string agent = Chrome, long bytes = 2_000, string protocol = "HTTP/2", string? referer = null, string method = "GET", long ms = 4, long bytesIn = -1) => new()
    {
        Host = "app.test",
        Path = path,
        Method = method,
        Protocol = protocol,
        StatusCode = status,
        ResponseBytes = bytes,
        RequestBytes = bytesIn,
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

    private static AnalyticsRollup Rollup(HostId host, DateTimeOffset at, params (AccessLogEntry Entry, int Visitor, bool Ignored)[] entries)
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
            bucket.BytesIn,
            bucket.ServerErrors,
            TrafficBucketCodec.Encode(bucket));
    }

    private static AnalyticsRollup Coverage(DateTimeOffset at, int seconds = 300) =>
        new(AnalyticsSubject.Coverage, string.Empty, at.ToUnixTimeSeconds(), seconds, 0, 0, 0, 0, null);

    /// <summary>The database the test has opened, or - until it has - one that fails every time, as a database that is down does.</summary>
    private sealed class SwitchableFactory : IDbContextFactory<ConfigDbContext>
    {
        public IDbContextFactory<ConfigDbContext>? Inner { get; set; }

        public ConfigDbContext CreateDbContext() => Inner?.CreateDbContext() ?? throw new InvalidOperationException("The database is down.");
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-analytics-").FullName;
        private readonly SwitchableFactory _factory = new();
        private ServiceProvider? _database;

        public Rig()
        {
            Store = new AnalyticsRollupStore(_factory, Backlog);
            Live = new TrafficAnalytics(new SnapshotStore(), Time, Backlog, new byte[16]);
            Service = new AnalyticsService(Store, Live, Time);
            Security = new SecurityRig(Time);
        }

        /// <summary>The page's security section reads this; its database stays down unless a test opens it.</summary>
        public SecurityRig Security { get; }

        public ManualClock Time { get; } = new(Noon);

        public AnalyticsBacklog Backlog { get; } = new();

        public AnalyticsRollupStore Store { get; }

        public TrafficAnalytics Live { get; }

        public AnalyticsService Service { get; }

        public FakeDashboardConfig Config { get; private set; } = null!;

        public DashboardTestContext SignIn(Role role = Role.Operator)
        {
            var context = new DashboardTestContext();
            context.Services.AddSingleton(Service);
            Security.Register(context.Services);
            Config = ConfigPages.SignIn(context, role, Time);
            Config.Document = new ConfigDocument(Hosts:
            [
                new HostConfig(App, ["app.test"]),
                new HostConfig(Blog, ["blog.test", "www.blog.test"]),
            ]);
            context.Render<MudPopoverProvider>();
            return context;
        }

        public async Task UseDatabaseAsync()
        {
            _database = new ServiceCollection().AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(_directory, ConfigDatabase.FileName)).BuildServiceProvider();
            var factory = _database.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await ConfigDatabase.InitializeAsync(factory, new Int64OrderedIdGenerator(TimeProvider.System), Xunit.TestContext.Current.CancellationToken);
            _factory.Inner = factory;
        }

        public Task StoreAsync(params AnalyticsRollup[] rollups) => Store.WriteAsync(rollups, Xunit.TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            Store.Dispose();
            await Security.DisposeAsync();
            if (_database is not null)
            {
                await _database.DisposeAsync();
            }

            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory a still-open SQLite file keeps; the OS cleans it.
            }
        }
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private long _timestamp = start.UtcTicks;

        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by)
        {
            Now += by;
            _timestamp += by.Ticks;
        }

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>The page opened at an address (the visitors tab, unless a test says otherwise); only the tab being tested polls.</summary>
    private static IRenderedComponent<Analytics> Render(DashboardTestContext context, string query = "?tab=visitors")
    {
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(DashboardRoutes.Analytics + query);
        return context.Render<Analytics>(parameters => parameters
            .Add(p => p.PollInterval, TimeSpan.FromHours(1))
            .Add(p => p.TrafficPollInterval, TimeSpan.FromHours(1)));
    }

    /// <summary>Two hours of five-minute buckets for the app and the blog, with traffic of every kind the page tells apart.</summary>
    private static async Task FillAsync(Rig rig)
    {
        List<AnalyticsRollup> rollups = [];
        for (var bucket = 0; bucket < 12; bucket++)
        {
            var at = Noon.AddHours(-2).AddMinutes(5 * bucket);
            rollups.Add(Coverage(at));
            rollups.Add(Rollup(
                App,
                at,
                (Entry("/", referer: "https://www.google.com/search?q=x"), bucket * 10, false),
                (Entry("/", agent: Safari), (bucket * 10) + 1, false),
                (Entry("/api/items", 200, bytes: 500, bytesIn: 250_000), (bucket * 10) + 2, false),
                (Entry("/missing", 404), (bucket * 10) + 3, false),
                (Entry("/broken", 502, ms: 800), (bucket * 10) + 4, false),
                (Entry("/download/big.iso", bytes: 900_000_000, ms: 300), (bucket * 10) + 5, false),
                (Entry("/page", agent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"), 5_000, false),
                (Entry("/page2", agent: "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)"), 5_000, false),
                (Entry("/secret", agent: "GPTBot/1.2"), 5_001, true)));
            rollups.Add(Rollup(Blog, at, (Entry("/post/1", protocol: "HTTP/1.1"), 9_000 + bucket, false)));
        }

        await rig.StoreAsync([.. rollups]);
    }

    [Fact]
    public async Task AnInstallWithNothingRecordedSaysSoAndShowsNoTilesOrTables()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=empty]").Count == 1);

        Assert.Contains("No requests in this range", page.Markup, StringComparison.Ordinal);
        Assert.Contains("A host with its access log off is never counted here.", page.Markup, StringComparison.Ordinal);
        Assert.Empty(page.FindAll("[data-tile]"));
        Assert.Empty(page.FindAll("table"));
    }

    [Fact]
    public async Task TheTilesShowRequestsBandwidthVisitorsAndTheErrorRate()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-tile]").Count == 4);

        // 12 buckets of 10 requests (the app's 9 and the blog's 1), three of them from bots.
        Assert.Equal("120", page.Find("[data-tile=requests] [data-part=value]").TextContent);
        Assert.Equal("30% from bots", page.Find("[data-tile=requests] [data-part=detail]").TextContent);
        Assert.Equal("11 GB", page.Find("[data-tile=bandwidth] [data-part=value]").TextContent);
        // Twelve uploads of 250 kB.
        Assert.Equal("sent, 3.0 MB received", page.Find("[data-tile=bandwidth] [data-part=detail]").TextContent);
        Assert.DoesNotContain("≈", page.Find("[data-tile=visitors] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Equal("±1.6%", page.Find("[data-tile=visitors] [data-part=detail]").TextContent);
        // One 502 in ten.
        Assert.Equal("10%", page.Find("[data-tile=errors] [data-part=value]").TextContent);
    }

    [Fact]
    public async Task ThreeChartsAreDrawnOverTheRangeAndAGapIsShadedNotZeroed()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        // Observed 10:00-10:30, then nothing for an hour and a half, then 12:00-12:30 and 11:30-11:55: the day's hourly steps break at 11:00.
        List<AnalyticsRollup> rollups = [];
        foreach (var minutes in new[] { -120, -115, -110, -105, -100, -95, -30, -25, -20, -15, -10, -5 })
        {
            rollups.Add(Coverage(Noon.AddMinutes(minutes)));
            rollups.Add(Rollup(App, Noon.AddMinutes(minutes), (Entry("/"), minutes, false)));
        }

        await rig.StoreAsync([.. rollups]);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 3);

        Assert.Equal(["requests", "bandwidth", "visitors"], page.FindAll("[data-chart]").Select(chart => chart.GetAttribute("data-chart")));
        // 10:00 and 11:00 steps: 10:00 observed, 11:00 only its last half. The gap is the 10:30-11:30 stretch inside the 11:00 step's coverage? No: one gap step (none here) - the line is one run.
        Assert.NotEmpty(page.Find("[data-chart=requests] polyline.pdk-chart-a").GetAttribute("points")!);
        Assert.Contains("Requests", page.Find("[data-chart=requests] figcaption").TextContent, StringComparison.Ordinal);
        Assert.Contains("Bytes sent", page.Find("[data-chart=bandwidth] figcaption").TextContent, StringComparison.Ordinal);
        // Nothing was uploaded, so there is no line of zeros for it.
        Assert.DoesNotContain("Bytes received", page.Find("[data-chart=bandwidth] figcaption").TextContent, StringComparison.Ordinal);
        Assert.Contains("Visitors", page.Find("[data-chart=visitors] figcaption").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimeTheServerWasNotRunningIsAGapInTheCharts()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        List<AnalyticsRollup> rollups = [];
        // 08:00 to 09:00 observed with traffic, 09:00 to 11:00 not at all, 11:00 to 12:00 observed with traffic.
        for (var minutes = -240; minutes < -180; minutes += 5)
        {
            rollups.Add(Coverage(Noon.AddMinutes(minutes)));
            rollups.Add(Rollup(App, Noon.AddMinutes(minutes), (Entry("/"), minutes, false)));
        }

        for (var minutes = -60; minutes < -5; minutes += 5)
        {
            rollups.Add(Coverage(Noon.AddMinutes(minutes)));
            rollups.Add(Rollup(App, Noon.AddMinutes(minutes), (Entry("/"), minutes, false)));
        }

        await rig.StoreAsync([.. rollups]);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 3);

        Assert.Equal(2, page.FindAll("[data-chart=requests] polyline.pdk-chart-a").Count);
        Assert.Single(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        Assert.Contains(page.FindAll("[data-chart=requests] rect.pdk-chart-hit"), hit => hit.GetAttribute("data-note") == "No data: Paduraksa was not running");
    }

    [Fact]
    public async Task UploadsAddABytesReceivedLineToTheBandwidthChart()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        List<AnalyticsRollup> rollups = [];
        // Two hours, so the day's hourly steps make a line.
        for (var minutes = -120; minutes < -5; minutes += 5)
        {
            rollups.Add(Coverage(Noon.AddMinutes(minutes)));
            rollups.Add(Rollup(App, Noon.AddMinutes(minutes), (Entry("/upload", method: "POST", bytesIn: 40_000), minutes, false)));
        }

        await rig.StoreAsync([.. rollups]);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 3);

        Assert.Contains("Bytes received", page.Find("[data-chart=bandwidth] figcaption").TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(page.Find("[data-chart=bandwidth] polyline.pdk-chart-a").GetAttribute("points")!);
    }

    [Fact]
    public async Task TheBreakdownsShowStatusClassesTopCodesMethodsAndVersionsWithTheirShares()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-share]").Count > 0);

        var classes = page.FindAll("[data-share=status-classes] li").ToDictionary(row => row.GetAttribute("data-label")!, row => row.QuerySelector(".pdk-share-count")!.TextContent);
        // Twelve buckets, a 404 and a 502 in each.
        Assert.Equal("12", classes["4xx"]);
        Assert.Equal("12", classes["5xx"]);
        Assert.False(classes.ContainsKey("1xx"));
        Assert.False(classes.ContainsKey("none"));
        Assert.Contains("pdk-share-ok", page.Find("[data-share=status-classes] [data-label='2xx'] .pdk-share-fill").ClassName, StringComparison.Ordinal);
        Assert.Contains("pdk-share-warn", page.Find("[data-share=status-classes] [data-label='4xx'] .pdk-share-fill").ClassName, StringComparison.Ordinal);
        Assert.Contains("pdk-share-bad", page.Find("[data-share=status-classes] [data-label='5xx'] .pdk-share-fill").ClassName, StringComparison.Ordinal);
        Assert.Equal("10%", page.Find("[data-share=status-classes] [data-label='5xx'] .pdk-share-pct").TextContent);
        Assert.Equal(["200", "404", "502"], page.FindAll("[data-share=status-codes] li").Select(row => row.GetAttribute("data-label")));
        Assert.Equal(["GET"], page.FindAll("[data-share=methods] li").Select(row => row.GetAttribute("data-label")));
        Assert.Equal(["HTTP/1.1", "HTTP/2"], page.FindAll("[data-share=protocols] li").Select(row => row.GetAttribute("data-label")));
    }

    [Fact]
    public async Task ADroppedConnectionIsLabelledNoResponseAndNotZero()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-20)), Rollup(App, Noon.AddMinutes(-20), (Entry("/"), 1, false), (Entry("/", 0), 2, false)));
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-share=status-classes] li").Count > 0);

        Assert.Equal("No response", page.Find("[data-share=status-classes] [data-label=none] .pdk-share-label").TextContent);
        Assert.Equal("No response", page.Find("[data-share=status-codes] [data-label=none] .pdk-share-label").TextContent);
        Assert.DoesNotContain("data-label=\"0\"", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTablesListPathsWithLinksToTheLiveTailAndMarkTheApproximateCounts()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        // More paths than a bucket's table holds, so the long tail's counts are marked approximate.
        List<(AccessLogEntry, int, bool)> entries = [];
        for (var i = 0; i < 300; i++)
        {
            entries.Add((Entry("/long/tail/" + i), i, false));
        }

        for (var i = 0; i < 200; i++)
        {
            entries.Add((Entry("/popular", ms: 700), i, false));
        }

        await rig.StoreAsync(Coverage(Noon.AddMinutes(-20)), Rollup(App, Noon.AddMinutes(-20), [.. entries]));
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=top-paths] tbody tr").Count > 0);

        var first = page.Find("[data-section=top-paths] tbody tr");
        Assert.Equal("/popular", first.GetAttribute("data-path"));
        Assert.Equal("/logs/live?path=%2Fpopular", first.QuerySelector("a")!.GetAttribute("href"));
        // 500 paths in a table of 64: the count is an upper bound and says how far it may be above the truth.
        var count = first.QuerySelectorAll("td")[0];
        Assert.StartsWith("≈ 2", count.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Between 200 and ", count.GetAttribute("title"), StringComparison.Ordinal);
        Assert.Equal("/popular", page.Find("[data-section=slowest] tbody tr").GetAttribute("data-path"));
        Assert.Contains("ms", page.Find("[data-section=slowest] tbody tr td").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLargestResponsesReferersAndUserAgentsAreTables()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=largest] tbody tr").Count > 0);

        var largest = page.Find("[data-section=largest] tbody tr");
        Assert.Equal("/download/big.iso", largest.GetAttribute("data-path"));
        Assert.Contains("GB", largest.QuerySelector("td")!.TextContent, StringComparison.Ordinal);
        Assert.Equal(["direct", "https://www.google.com"], page.FindAll("[data-section=referers] tbody tr").Select(row => row.GetAttribute("data-referer")).Order());
        var agents = page.FindAll("[data-section=user-agents] tbody tr").Select(row => row.GetAttribute("data-agent")).ToList();
        Assert.Contains(Chrome, agents);
        Assert.Contains(Safari, agents);
    }

    [Fact]
    public async Task ClientsAreBrowsersSystemsDevicesAndBotsWithTheOnesIgnoringRobotsTxt()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-share=browsers] li").Count > 0);

        Assert.Equal(["Chrome", "Safari"], page.FindAll("[data-share=browsers] li").Select(row => row.GetAttribute("data-label")));
        Assert.Equal(["Windows", "iOS"], page.FindAll("[data-share=systems] li").Select(row => row.GetAttribute("data-label")));
        Assert.Equal(["Desktop", "Mobile", "Bot"], page.FindAll("[data-share=devices] li .pdk-share-label").Select(label => label.TextContent));
        Assert.Equal(["Search engine", "AI crawler"], page.FindAll("[data-share=bot-kinds] li .pdk-share-label").Select(label => label.TextContent));
        Assert.Equal(["Googlebot", "GPTBot"], page.FindAll("[data-section=top-bots] tbody tr").Select(row => row.GetAttribute("data-bot")));
        var ignoring = Assert.Single(page.FindAll("[data-section=ignoring-robots] tbody tr"));
        Assert.Equal("GPTBot", ignoring.GetAttribute("data-bot"));
        Assert.Contains("AI crawler", ignoring.TextContent, StringComparison.Ordinal);
        Assert.Equal("12", ignoring.QuerySelectorAll("td")[1].TextContent);
    }

    [Fact]
    public async Task WhenNoBotIgnoredARobotsTxtTheTableSaysWhichHostsAreChecked()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-20)), Rollup(App, Noon.AddMinutes(-20), (Entry("/"), 1, false)));
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=ignoring-robots]").Count == 1);

        var card = page.Find("[data-section=ignoring-robots]").TextContent;
        Assert.Contains("No bot fetched what a robots.txt disallows", card, StringComparison.Ordinal);
        Assert.Contains("Only hosts that serve a robots.txt", card, StringComparison.Ordinal);
        Assert.Contains("No bot made a request", page.Find("[data-section=bot-kinds]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRangeSwitchReadsThatRangeAndTheHostPickerOnlyThatHost()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();
        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-tile]").Count == 4);
        Assert.Equal("day", page.Find("[aria-checked=true]").GetAttribute("data-value"));

        // Nothing happened in the last hour that is observed: the range changes what is read.
        await page.Find("[data-field=range] [data-value=hour]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindAll("[data-section=empty]").Count == 1);
        Assert.Equal("hour", page.Find("[aria-checked=true]").GetAttribute("data-value"));
        await page.Find("[data-field=range] [data-value=week]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindAll("[data-tile]").Count == 4);
        Assert.Equal("120", page.Find("[data-tile=requests] [data-part=value]").TextContent);

        var picker = page.FindComponent<MudSelect<string>>();
        await page.InvokeAsync(() => picker.Instance.ValueChanged.InvokeAsync(Blog.ToString()));
        await page.WaitForStateAsync(() => page.Find("[data-tile=requests] [data-part=value]").TextContent == "12");
        Assert.Equal(["/post/1"], page.FindAll("[data-section=top-paths] tbody tr").Select(row => row.GetAttribute("data-path")));
        // The blog's first name is what the live tail knows it by.
        Assert.Equal("/logs/live?path=%2Fpost%2F1&host=blog.test", page.Find("[data-section=top-paths] tbody tr a").GetAttribute("href"));
    }

    [Fact]
    public async Task TheRangeAndTheHostPickerOnlyReachTheTabsThatUseThem()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await using var context = rig.SignIn();

        var page = Render(context, "?tab=traffic");
        Assert.Empty(page.FindAll("[data-field=host]"));

        await page.Find("[data-field=tab] [data-value=visitors]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindAll("[data-field=host]").Count == 1);

        await page.Find("[data-field=tab] [data-value=security]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindAll("[data-field=host]").Count == 0);
    }

    [Fact]
    public async Task WithNothingInTheAddressItIsTheTrafficTabOverTheDay()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await using var context = rig.SignIn();

        var page = Render(context, string.Empty);
        await page.WaitForStateAsync(() => page.FindComponents<TrafficStatistics>().Count == 1);

        Assert.Equal(AnalyticsTab.Traffic, Chosen<AnalyticsTab>(page, "tab"));
        Assert.Equal(MetricsRange.Day, Chosen<MetricsRange>(page, "range"));
        Assert.Equal(MetricsRange.Day, page.FindComponent<TrafficStatistics>().Instance.Range);
        Assert.Empty(page.FindComponents<VisitorsStatistics>());
        Assert.Empty(page.FindComponents<SecurityStatistics>());
    }

    [Theory]
    [InlineData("?tab=traffic&range=1h", AnalyticsTab.Traffic, MetricsRange.Hour)]
    [InlineData("?tab=visitors&range=7d", AnalyticsTab.Visitors, MetricsRange.Week)]
    [InlineData("?range=30d&tab=security", AnalyticsTab.Security, MetricsRange.Month)]
    [InlineData("?tab=nonsense&range=forever", AnalyticsTab.Traffic, MetricsRange.Day)]
    [InlineData("?tab=SECURITY&range=24H", AnalyticsTab.Security, MetricsRange.Day)]
    public async Task TheTabAndTheRangeAreReadFromTheAddressAndOnlyTheOpenTabIsMounted(string query, AnalyticsTab tab, MetricsRange range)
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await using var context = rig.SignIn();

        var page = Render(context, query);
        await page.WaitForStateAsync(() => page.FindComponents<TrafficStatistics>().Count + page.FindComponents<VisitorsStatistics>().Count + page.FindComponents<SecurityStatistics>().Count == 1);

        Assert.Equal(tab, Chosen<AnalyticsTab>(page, "tab"));
        Assert.Equal(range, Chosen<MetricsRange>(page, "range"));
        Assert.Equal(tab == AnalyticsTab.Traffic ? 1 : 0, page.FindComponents<TrafficStatistics>().Count);
        Assert.Equal(tab == AnalyticsTab.Visitors ? 1 : 0, page.FindComponents<VisitorsStatistics>().Count);
        Assert.Equal(tab == AnalyticsTab.Security ? 1 : 0, page.FindComponents<SecurityStatistics>().Count);
    }

    [Fact]
    public async Task ChoosingATabOrARangeWritesItToTheAddressAndKeepsTheOther()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await using var context = rig.SignIn();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var page = Render(context, "?tab=traffic&range=7d");

        await page.Find("[data-field=tab] [data-value=visitors]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindComponents<VisitorsStatistics>().Count == 1);
        Assert.EndsWith("/analytics?tab=visitors&range=7d", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal(MetricsRange.Week, page.FindComponent<VisitorsStatistics>().Instance.Range);

        await page.Find("[data-field=range] [data-value=hour]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindComponent<VisitorsStatistics>().Instance.Range == MetricsRange.Hour);
        Assert.EndsWith("/analytics?tab=visitors&range=1h", navigation.Uri, StringComparison.Ordinal);

        await page.Find("[data-field=tab] [data-value=security]").ClickAsync(new());
        await page.WaitForStateAsync(() => page.FindComponents<SecurityStatistics>().Count == 1);
        Assert.EndsWith("/analytics?tab=security&range=1h", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal(MetricsRange.Hour, page.FindComponent<SecurityStatistics>().Instance.Range);
        Assert.Empty(page.FindComponents<VisitorsStatistics>());
    }

    [Theory]
    [InlineData(AnalyticsTab.Traffic, MetricsRange.Hour, "/analytics?tab=traffic&range=1h")]
    [InlineData(AnalyticsTab.Visitors, MetricsRange.Day, "/analytics?tab=visitors&range=24h")]
    [InlineData(AnalyticsTab.Security, MetricsRange.Week, "/analytics?tab=security&range=7d")]
    [InlineData(AnalyticsTab.Security, MetricsRange.Month, "/analytics?tab=security&range=30d")]
    public void TheAddressOfAViewReadsBackAsTheSameView(AnalyticsTab tab, MetricsRange range, string href)
    {
        Assert.Equal(href, AnalyticsView.Href(tab, range));
        Assert.Equal(tab, AnalyticsView.ParseTab(AnalyticsView.Token(tab)));
        Assert.Equal(range, AnalyticsView.ParseRange(AnalyticsView.Token(range)));
    }

    [Fact]
    public async Task OnlyTheOpenTabReadsAndAHiddenBrowserTabPausesIt()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await FillAsync(rig);
        await using var context = rig.SignIn();
        var page = Render(context, "?tab=visitors");
        await page.WaitForStateAsync(() => page.FindAll("[data-tile]").Count == 4);
        Assert.True(page.FindComponent<VisitorsStatistics>().Instance.Visible);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(false));
        Assert.False(page.FindComponent<VisitorsStatistics>().Instance.Visible);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(true));
        Assert.True(page.FindComponent<VisitorsStatistics>().Instance.Visible);
    }

    private static T Chosen<T>(IRenderedComponent<Analytics> page, string field) =>
        page.FindComponents<SegmentedControl<T>>().Single(control => control.Find("[data-field]").GetAttribute("data-field") == field).Instance.Value!;

    [Fact]
    public async Task APathThatWasCutAtItsCapIsNotALink()
    {
        await using Rig rig = new();
        await rig.UseDatabaseAsync();
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-20)), Rollup(App, Noon.AddMinutes(-20), (Entry("/" + new string('a', 400)), 1, false)));
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-section=top-paths] tbody tr").Count > 0);

        Assert.Empty(page.FindAll("[data-section=top-paths] tbody tr a"));
        Assert.EndsWith("...", page.Find("[data-section=top-paths] tbody tr th").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADatabaseThatIsDownIsAnAlertWithTryAgainAndRecovers()
    {
        await using Rig rig = new();
        await using var context = rig.SignIn();

        var page = Render(context);
        await page.WaitForStateAsync(() => page.FindAll("[data-part=failed]").Count == 1);
        Assert.Contains("The analytics couldn't be read", page.Find("[data-part=failed]").TextContent, StringComparison.Ordinal);

        await rig.UseDatabaseAsync();
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-20)), Rollup(App, Noon.AddMinutes(-20), (Entry("/"), 1, false)));
        await page.Find("[data-part=failed] button").ClickAsync(new());

        await page.WaitForStateAsync(() => page.FindAll("[data-tile]").Count == 4);
        Assert.Empty(page.FindAll("[data-part=failed]"));
    }

    [Fact]
    public void AnalyticsIsAnEntryUnderInsightForEveryRoleThatReadsTheMetrics()
    {
        var entry = Assert.Single(DashboardNavigation.Entries, static entry => entry.Href == DashboardRoutes.Analytics);

        Assert.Equal((DashboardNavigation.InsightGroup, Permission.MetricsRead, true), (entry.Group, entry.Requires, entry.Available));
        Assert.Same(entry, DashboardNavigation.ActiveFor("/analytics", DashboardNavigation.Entries));
        foreach (var role in Enum.GetValues<Role>())
        {
            Assert.Contains(entry, DashboardNavigation.VisibleTo(permission => RolePermissions.Of([role]).Contains(permission)));
        }
    }
}
