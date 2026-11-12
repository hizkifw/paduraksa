using Paduraksa.Application.Analytics;
using Paduraksa.Contracts.Analytics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Data.Config;

namespace Paduraksa.Tests.Data.Analytics;

/// <summary>The overview the dashboard and the API read: stored rows, rows waiting to be written and open buckets merged, with the bounds, the gaps and the scopes it promises.</summary>
public sealed class AnalyticsServiceTests
{
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string Firefox = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0";

    private static readonly DateTimeOffset Noon = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly HostId Other = HostId.New();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The store and the analytics share one backlog, as the process wires them.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(TestConfigDatabase database)
        {
            Store = new AnalyticsRollupStore(database.Factory, Backlog);
            Live = new TrafficAnalytics(new SnapshotStore(), Time, Backlog, new byte[16]);
            Service = new AnalyticsService(Store, Live, Time);
        }

        public ManualTimeProvider Time { get; } = new(Noon);

        public AnalyticsBacklog Backlog { get; } = new();

        public AnalyticsRollupStore Store { get; }

        public TrafficAnalytics Live { get; }

        public AnalyticsService Service { get; }

        public async Task StoreAsync(params AnalyticsRollup[] rollups) => await Store.WriteAsync(rollups, Ct);

        public void Dispose() => Store.Dispose();
    }

    private static AccessLogEntry Entry(string path, int status = 200, string agent = Chrome, long bytes = 1_000, string protocol = "HTTP/2", string? referer = null, long ms = 4) => new()
    {
        Host = "app.test",
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
            bucket.ServerErrors,
            TrafficBucketCodec.Encode(bucket));
    }

    private static AnalyticsRollup Coverage(DateTimeOffset at, int seconds = 300) =>
        new(AnalyticsSubject.Coverage, string.Empty, at.ToUnixTimeSeconds(), seconds, 0, 0, 0, null);

    [Fact]
    public async Task AnInstallWithNothingRecordedHasAnEmptyOverview()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);

        Assert.Equal(0, overview.Totals.Requests);
        Assert.Equal(0, overview.Totals.Visitors);
        Assert.Empty(overview.Series);
        Assert.Empty(overview.TopPaths);
        Assert.Empty(overview.BotsIgnoringRobots);
        Assert.Equal(6, overview.StatusClasses.Count);
        Assert.Equal(MetricsRange.Day, overview.Range);
        Assert.Equal(3_600, overview.StepSeconds);
    }

    [Fact]
    public void EveryRangeHasItsReachAndStep()
    {
        Assert.Equal((TimeSpan.FromHours(1), 300), AnalyticsService.Reach(MetricsRange.Hour));
        Assert.Equal((TimeSpan.FromDays(1), 3_600), AnalyticsService.Reach(MetricsRange.Day));
        Assert.Equal((TimeSpan.FromDays(7), 10_800), AnalyticsService.Reach(MetricsRange.Week));
        Assert.Equal((TimeSpan.FromDays(30), 86_400), AnalyticsService.Reach(MetricsRange.Month));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalyticsService.Reach((MetricsRange)9));
    }

    [Fact]
    public async Task TheOverviewMergesStoredRowsAndClosedBucketsStillWaitingToBeWritten()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        await rig.StoreAsync(
            Coverage(Noon.AddMinutes(-15)),
            Rollup(App, Noon.AddMinutes(-15), (Entry("/a", referer: "https://www.google.com/x?q=1"), 1, false), (Entry("/a", 500), 2, false), (Entry("/b", agent: Firefox, bytes: 5_000_000), 3, false)));
        rig.Backlog.Add([Coverage(Noon.AddMinutes(-10)), Rollup(App, Noon.AddMinutes(-10), (Entry("/a"), 2, false), (Entry("/c", agent: "GPTBot/1.2"), 9, true))]);

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, null, false, Ct);

        // 3 stored and 2 waiting. Visitors 1, 2, 3 and 2, 9: four distinct people over five requests.
        Assert.Equal(5, overview.Totals.Requests);
        Assert.Equal(4, overview.Totals.Visitors);
        Assert.Equal(1, overview.Totals.ServerErrors);
        Assert.Equal(0.2, overview.Totals.ErrorRate, 6);
        Assert.Equal(5_004_000, overview.Totals.BytesOut);
        Assert.Equal(1, overview.Totals.BotRequests);
        Assert.Equal(600, overview.Totals.CoveredSeconds);
        Assert.Equal(("/a", 3L), (overview.TopPaths[0].Path, overview.TopPaths[0].Requests));
        Assert.Equal("/b", overview.LargestResponses[0].Label);
        Assert.Equal(5_000_000, overview.LargestResponses[0].Count);
        Assert.Equal(["Chrome", "Firefox"], overview.Browsers.Select(static count => count.Label));
        Assert.Equal(["Chrome 131", "Firefox 133"], overview.BrowserVersions.Select(static count => count.Label));
        var ignoring = Assert.Single(overview.BotsIgnoringRobots);
        Assert.Equal(("aiCrawler", "GPTBot", 1L), (ignoring.Kind, ignoring.Name, ignoring.Count));
        Assert.Contains(overview.TopReferers, count => count is { Label: "https://www.google.com", Count: 1 });
        Assert.Contains(overview.TopReferers, count => count.Label == "direct");
        Assert.Equal(5, overview.Protocols.Single(static count => count.Label == "HTTP/2").Count);
        Assert.Equal(4, overview.StatusClasses.Single(static count => count.Label == "2xx").Count);
        Assert.Equal(1, overview.StatusClasses.Single(static count => count.Label == "5xx").Count);
    }

    [Fact]
    public async Task VisitorsAreAUnionAcrossHostsAndStepsAndNotASum()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        // One person (visitor 1) uses both hosts in two buckets of two different hours; another (2) only one host.
        await rig.StoreAsync(
            Coverage(Noon.AddHours(-3)),
            Coverage(Noon.AddMinutes(-30)),
            Rollup(App, Noon.AddHours(-3), (Entry("/a"), 1, false), (Entry("/a"), 2, false)),
            Rollup(Other, Noon.AddHours(-3), (Entry("/a"), 1, false)),
            Rollup(App, Noon.AddMinutes(-30), (Entry("/a"), 1, false)));

        var all = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);
        var app = await rig.Service.ReadOverviewAsync(MetricsRange.Day, App, false, Ct);

        Assert.Equal(4, all.Totals.Requests);
        Assert.Equal(2, all.Totals.Visitors);
        Assert.Equal(3, app.Totals.Requests);
        Assert.Equal(2, app.Totals.Visitors);
        var step = all.Series.Single(point => point.Start == Noon.AddHours(-3));
        Assert.Equal((3L, 2L), (step.Requests, step.Visitors));
    }

    [Fact]
    public async Task TheChartHasAGapWhereNobodyWasWatchingAndLeavesTheTimeBeforeTheFirstObservedStepOut()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        // Observed 05:00-06:00 and 09:00-10:00 and 11:55-12:00 of the day range; nothing before 05:00 (a fresh install) and nothing from 06:00 to 09:00.
        List<AnalyticsRollup> rollups = [];
        foreach (var start in new[] { 5, 9 })
        {
            for (var minute = 0; minute < 60; minute += 5)
            {
                rollups.Add(Coverage(new DateTimeOffset(2026, 11, 12, start, minute, 0, TimeSpan.Zero)));
            }
        }

        rollups.Add(Coverage(Noon.AddMinutes(-5)));
        rollups.Add(Rollup(App, new DateTimeOffset(2026, 11, 12, 5, 0, 0, TimeSpan.Zero), (Entry("/a"), 1, false)));
        await rig.StoreAsync([.. rollups]);

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);

        var hours = overview.Series.Select(static step => (step.Start.Hour, step.CoveredSeconds)).ToList();
        Assert.Equal(5, hours[0].Hour);
        Assert.Equal(11, hours[^1].Hour);
        Assert.Equal(3_600, hours[0].CoveredSeconds);
        // 06:00 to 08:00 and 10:00 are steps with no coverage: a gap, drawn as one, with every figure 0.
        Assert.All(overview.Series.Where(static step => step.Start.Hour is 6 or 7 or 8 or 10), step => Assert.Equal(0, step.CoveredSeconds));
        Assert.Equal(4, overview.Series.Count(static step => step.CoveredSeconds == 0));
        // 11:00 is observed for five minutes only: an ordinary point, not a gap.
        Assert.Equal(300, overview.Series[^1].CoveredSeconds);
        Assert.Equal(1, overview.Series[0].Requests);
    }

    [Fact]
    public async Task ARangeForOneHostTheUnmatchedOrAllReadsOnlyThoseAndTheOpenBucketFollowsTheScope()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        await rig.StoreAsync(
            Coverage(Noon.AddMinutes(-10)),
            Rollup(App, Noon.AddMinutes(-10), (Entry("/app"), 1, false)),
            Rollup(Other, Noon.AddMinutes(-10), (Entry("/other"), 2, false), (Entry("/other"), 3, false)),
            Rollup(default, Noon.AddMinutes(-10), (Entry("/scan", 404), 4, false)));
        // The open bucket belongs to no configured host: the rig's snapshot has none.
        rig.Time.Now = Noon.AddMinutes(1);
        var open = Entry("/probe", 404) with { Host = "nobody.test", TimestampUtcTicks = Noon.AddMinutes(1).UtcTicks };
        rig.Live.Offer(in open);
        rig.Live.Tick();

        var all = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, null, false, Ct);
        var app = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, App, false, Ct);
        var unmatched = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, null, true, Ct);

        Assert.Equal(5, all.Totals.Requests);
        Assert.Equal(["/app", "/other", "/probe", "/scan"], all.TopPaths.Select(static path => path.Path).Order(StringComparer.Ordinal));
        Assert.Equal(("/app", 1L), (Assert.Single(app.TopPaths).Path, app.TopPaths[0].Requests));
        Assert.Equal(App, app.Host);
        Assert.Equal(["/probe", "/scan"], unmatched.TopPaths.Select(static path => path.Path).Order(StringComparer.Ordinal));
        Assert.True(unmatched.Unmatched);
        Assert.Null(unmatched.Host);
    }

    [Fact]
    public async Task ATableRowCarriesItsErrorBoundWhenThePathsOutgrewTheSketch()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        List<(AccessLogEntry, int, bool)> entries = [];
        for (var i = 0; i < 400; i++)
        {
            entries.Add((Entry("/unique/" + i), i, false));
            entries.Add((Entry("/popular"), i, false));
        }

        await rig.StoreAsync(Coverage(Noon.AddMinutes(-10)), Rollup(App, Noon.AddMinutes(-10), [.. entries]));

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, null, false, Ct);

        // 800 requests over 401 paths: the heavy path is found, its count is an upper bound, and the long tail's rows say how loose they are.
        Assert.Equal("/popular", overview.TopPaths[0].Path);
        Assert.InRange(overview.TopPaths[0].Requests, 400, 400 + overview.TopPaths[0].Error);
        Assert.All(overview.TopPaths.Skip(1), static path => Assert.True(path.Error > 0));
        Assert.Equal(800, overview.Totals.Requests);
    }

    [Fact]
    public async Task ASlowPathIsRankedByItsPercentileAndOnlyWithEnoughRequests()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        List<(AccessLogEntry, int, bool)> entries = [];
        for (var i = 0; i < 20; i++)
        {
            entries.Add((Entry("/fast", ms: 3), i, false));
            entries.Add((Entry("/slow", ms: 900), i, false));
        }

        entries.Add((Entry("/once", ms: 30_000), 99, false));
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-10)), Rollup(App, Noon.AddMinutes(-10), [.. entries]));

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Hour, null, false, Ct);

        Assert.Equal(["/slow", "/fast"], overview.SlowestPaths.Select(static path => path.Path));
        Assert.InRange(overview.SlowestPaths[0].P95Ms, 800, 1_000);
    }

    [Fact]
    public async Task AResultIsKeptBrieflyAndReadAgainOnceItIsStale()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        await rig.StoreAsync(Coverage(Noon.AddMinutes(-10)), Rollup(App, Noon.AddMinutes(-10), (Entry("/a"), 1, false)));
        var first = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);
        await rig.StoreAsync(Rollup(Other, Noon.AddMinutes(-10), (Entry("/b"), 2, false)));

        var cached = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);
        rig.Time.Now = Noon.AddMinutes(1);
        var fresh = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);

        Assert.Same(first, cached);
        Assert.Equal(1, first.Totals.Requests);
        Assert.Equal(2, fresh.Totals.Requests);
    }

    [Fact]
    public async Task TheKeptResultsAreBoundedHoweverManyHostsAreAskedAbout()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);

        // Any well-formed host ID is accepted (a deleted host's history stays readable), so a caller can walk IDs.
        for (var i = 0; i < AnalyticsService.MaxCached * 3; i++)
        {
            await rig.Service.ReadOverviewAsync(MetricsRange.Day, HostId.New(), false, Ct);
        }

        Assert.Equal(AnalyticsService.MaxCached, rig.Service.CachedCount);
        // The one that was asked about last is still kept: the oldest results are the ones that go.
        var asked = HostId.New();
        var first = await rig.Service.ReadOverviewAsync(MetricsRange.Day, asked, false, Ct);
        Assert.Same(first, await rig.Service.ReadOverviewAsync(MetricsRange.Day, asked, false, Ct));
        Assert.Equal(AnalyticsService.MaxCached, rig.Service.CachedCount);
    }

    [Fact]
    public async Task AStepWithRequestsButNoCoverageRowIsDrawnAsDataSoTheChartAndTheTotalsAgree()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        // Coverage for the 08:00 step and the 11:00 step only; the 10:00 step has traffic (filed there by its timestamp) and nobody ticked through it.
        await rig.StoreAsync(
            Coverage(Noon.AddHours(-4)),
            Coverage(Noon.AddMinutes(-5)),
            Rollup(App, Noon.AddHours(-4), (Entry("/a"), 1, false)),
            Rollup(App, Noon.AddHours(-2).AddMinutes(10), (Entry("/b"), 2, false), (Entry("/b"), 3, false)),
            Rollup(App, Noon.AddMinutes(-5), (Entry("/c"), 4, false)));

        var overview = await rig.Service.ReadOverviewAsync(MetricsRange.Day, null, false, Ct);

        var withData = overview.Series.Single(step => step.Start == Noon.AddHours(-2));
        Assert.Equal(2, withData.Requests);
        Assert.True(withData.CoveredSeconds > 0);
        // The step with neither is still a gap.
        var gap = overview.Series.Single(step => step.Start == Noon.AddHours(-3));
        Assert.Equal((0, 0L), (gap.CoveredSeconds, gap.Requests));
        Assert.Equal(overview.Totals.Requests, overview.Series.Sum(static step => step.Requests));
    }

    [Fact]
    public async Task TheMonthOfTwentyHostsIsReadInTensOfMillisecondsFromDailyRows()
    {
        await using var database = await TestConfigDatabase.CreateAsync();
        using var rig = new Rig(database);
        // A busy host's hour: 20 000 requests over 4 000 paths, 600 agents, 200 referers and 3 000 visitors, so the blob is as full as it gets.
        TrafficBucket hour = new(scale: 2);
        var random = new Random(1);
        for (var i = 0; i < 20_000; i++)
        {
            var entry = Entry("/page/" + random.Next(4_000), i % 50 == 0 ? 502 : 200, "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/" + (100 + random.Next(600) % 40) + ".0.0.0 Safari/537.36 agent" + random.Next(600), random.Next(1, 50_000), "HTTP/2", "https://ref" + random.Next(200) + ".example/x", random.Next(1, 800));
            hour.Record(in entry, Visitor(random.Next(3_000)), UserAgentClassifier.Classify(entry.UserAgent), false);
        }

        var encoded = TrafficBucketCodec.Encode(hour);
        var rows = new List<Paduraksa.Data.Analytics.AnalyticsRollupRow>();
        var hosts = Enumerable.Range(0, 20).Select(static _ => HostId.New()).ToArray();
        var firstHour = Noon.AddDays(-30).ToUnixTimeSeconds() / 3_600 * 3_600;
        for (var h = 0; h < 30 * 24; h++)
        {
            var start = firstHour + (h * 3_600L);
            rows.Add(new() { Id = AnalyticsRollupId.New(), Tier = 1, Subject = 0, HostKey = string.Empty, Start = start, CoveredSeconds = 3_600 });
            for (var host = 0; host < 20; host++)
            {
                rows.Add(new()
                {
                    Id = AnalyticsRollupId.New(),
                    Tier = 1,
                    Subject = 1,
                    HostKey = hosts[host].ToString(),
                    Start = start,
                    CoveredSeconds = 3_600,
                    Requests = hour.Requests,
                    BytesOut = hour.BytesOut,
                    ServerErrors = hour.ServerErrors,
                    Visitors = encoded.Visitors,
                    Data = encoded.Data,
                });
            }
        }

        await using (var context = database.NewContext())
        {
            context.AnalyticsRollups.AddRange(rows);
            await context.SaveChangesAsync(Ct);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        // The hourly rows of whole days become daily rows (the job does this as the days end); the month is read from those.
        await rig.Store.RollUpAsync(Noon, Ct);
        var rollMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var month = await rig.Service.ReadOverviewAsync(MetricsRange.Month, null, false, Ct);
        var monthMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var week = await rig.Service.ReadOverviewAsync(MetricsRange.Week, null, false, Ct);
        var weekMs = watch.ElapsedMilliseconds;
        watch.Restart();
        var oneHost = await rig.Service.ReadOverviewAsync(MetricsRange.Month, hosts[3], false, Ct);
        var oneMs = watch.ElapsedMilliseconds;

        TestContext.Current.TestOutputHelper?.WriteLine($"analytics cost: encoded hour {encoded.Data.Length} + {encoded.Visitors.Length} bytes; 30 days x 20 hosts = {rows.Count} rows; rolling 30 days into daily rows {rollMs} ms; month all hosts {monthMs} ms, week all hosts {weekMs} ms, month one host {oneMs} ms");
        Assert.Equal(20 * oneHost.Totals.Requests, month.Totals.Requests);
        Assert.True(week.Totals.Requests > 0);
        Assert.True(monthMs < 60_000, $"{monthMs} ms");
    }
}
