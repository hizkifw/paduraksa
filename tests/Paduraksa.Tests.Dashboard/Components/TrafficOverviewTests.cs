using Bunit;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Metrics;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Data;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Storage.Sqlite;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class TrafficOverviewTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly HostId App = HostId.New();
    private static readonly HostId Blog = HostId.New();
    private static readonly UpstreamId Web = UpstreamId.New();
    private static readonly UpstreamId Media = UpstreamId.New();

    [Fact]
    public async Task TheTilesShowThePaceOfTheLastHalfMinute()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 270, status: 200, micros: 2_000, upstreamMicros: 1_500);
        rig.Record(App, 30, status: 502, micros: 4_000, upstreamMicros: 3_500, error: 10);
        rig.TickAt(5);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Equal("60/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);
        Assert.Equal("10%", page.Find("[data-tile=errors] [data-part=value]").TextContent);
        Assert.Equal("0/s", page.Find("[data-tile=refused] [data-part=value]").TextContent);
        Assert.EndsWith(" ms", page.Find("[data-tile=latency] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Contains("p95", page.Find("[data-tile=latency] [data-part=detail]").TextContent, StringComparison.Ordinal);
        Assert.EndsWith(" ms", page.Find("[data-tile=upstream-latency] [data-part=value]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOverviewLinksToTheAnalytics()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickAt(2);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Equal("/analytics", page.Find("[data-link=analytics]").GetAttribute("href"));
    }

    [Fact]
    public async Task ThereAreNoChartsUntilTheRangeHasTwoPoints_ThenBothAreDrawn()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickAt(2);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20)));

        Assert.Empty(page.FindAll("[data-chart]"));
        Assert.Contains("No traffic in this range yet", page.Find("[data-section=requests-chart]").TextContent, StringComparison.Ordinal);

        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickEverySecondTo(22);

        // The next poll reads the ring again.
        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 2);
        Assert.NotEmpty(page.Find("[data-chart=requests] polyline.pdk-chart-line").GetAttribute("points")!);
        // p50, p95 and p99; nothing was forwarded upstream, so the upstream line has nothing to draw.
        Assert.Equal(3, page.FindAll("[data-chart=latency] polyline").Count);
        Assert.NotEmpty(page.FindAll("[data-chart=requests] rect.pdk-chart-hit title"));
    }

    [Fact]
    public async Task ATimeTheServerWasNotRunningIsAGapInBothChartsAndAQuietOneIsNot()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 30, status: 200, micros: 2_000);
        rig.TickEverySecondTo(25);
        // The sampler is silent for a hundred seconds (the process was down), then goes on.
        rig.TickAt(125);
        rig.Record(App, 30, status: 200, micros: 2_000);
        rig.TickEverySecondTo(150);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Equal(2, page.FindAll("[data-chart=requests] polyline.pdk-chart-a").Count);
        Assert.Equal(2, page.FindAll("[data-chart=requests] polyline.pdk-chart-err").Count);
        Assert.Equal(2, page.FindAll("[data-chart=latency] polyline.pdk-chart-b").Count);
        Assert.Single(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        Assert.Contains(page.FindAll("[data-chart=requests] rect.pdk-chart-hit title"), title => title.TextContent.Contains("No data: Paduraksa was not running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStallInWhichRequestsWereServedIsNotAGapInEitherChart()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickEverySecondTo(5);
        rig.Record(App, 300, status: 200, micros: 2_000);
        // The sampler is silent for 25 s, but the proxy answered 300 requests in it: the server was running.
        rig.TickAt(30);
        rig.TickEverySecondTo(60);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Single(page.FindAll("[data-chart=requests] polyline.pdk-chart-a"));
        Assert.Empty(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        Assert.DoesNotContain(page.FindAll("[data-chart=requests] rect.pdk-chart-hit title"), title => title.TextContent.Contains("No data", StringComparison.Ordinal));
        Assert.Single(page.FindAll("[data-chart=latency] polyline.pdk-chart-b"));
    }

    [Fact]
    public async Task AQuietStretchTheServerWasUpForKeepsOneLineAtZeroAndAnEmptyLatencyLine()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickEverySecondTo(95);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Single(page.FindAll("[data-chart=requests] polyline.pdk-chart-a"));
        Assert.Empty(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        // Nothing was timed, so there is no latency to draw: the lines are empty, not zeros.
        Assert.Empty(page.FindAll("[data-chart=latency] polyline"));
    }

    [Fact]
    public async Task TheBusiestHostsAreLinkedAndTheRequestsNoHostOwnedAreNotLinkedAtAll()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 100, status: 200, micros: 1_000);
        rig.Record(App, 20, status: 500, micros: 1_000);
        rig.Record(Blog, 400, status: 200, micros: 1_000);
        rig.RecordUnmatched(7);
        rig.TickAt(5);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        var rows = page.FindAll("[data-section=top-hosts] tbody tr");
        Assert.Equal(["blog.test", "app.test", "No host matched"], rows.Select(row => row.GetAttribute("data-host")));
        Assert.Equal(DashboardRoutes.Host(Blog), page.Find("[data-host='blog.test'] a").GetAttribute("href"));
        Assert.Empty(page.FindAll("[data-host='No host matched'] a"));
        Assert.Contains("17%", page.Find("[data-host='app.test']").TextContent, StringComparison.Ordinal);
        Assert.Equal("pdk-traffic-bad", page.FindAll("[data-host='app.test'] td")[1].ClassName);
    }

    [Fact]
    public async Task UpstreamErrorsAreNamedFromTheConfigAndWorstFirst()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Config.Document = new ConfigDocument(Upstreams: [new UpstreamConfig(Web, "web"), new UpstreamConfig(Media, "media")]);
        rig.Publish();
        rig.Record(App, 50, status: 200, micros: 1_000, upstream: Web, upstreamMicros: 800);
        rig.Record(Blog, 40, status: 200, micros: 1_000, upstream: Media, upstreamMicros: 800);
        rig.Record(Blog, 5, status: 502, micros: 1_000, upstream: Media, upstreamMicros: 800, error: 10);
        rig.TickAt(5);

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        var rows = page.FindAll("[data-section=upstream-errors] tbody tr");
        Assert.Equal([Media.ToString(), Web.ToString()], rows.Select(row => row.GetAttribute("data-upstream")));
        Assert.Equal("media", rows[0].QuerySelector("th a")!.TextContent);
        Assert.Equal(DashboardRoutes.Upstream(Media), rows[0].QuerySelector("th a")!.GetAttribute("href"));
        Assert.Equal("5", rows[0].QuerySelectorAll("td")[1].TextContent);
        Assert.Equal("0", rows[1].QuerySelectorAll("td")[1].TextContent);
        Assert.DoesNotContain("pdk-traffic-bad", rows[1].QuerySelectorAll("td")[1].ClassName ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyRangeSaysSoInsteadOfDrawingNothing()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();

        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        Assert.Contains("No requests in this range", page.Find("[data-section=top-hosts]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Nothing was forwarded in this range", page.Find("[data-section=upstream-errors]").TextContent, StringComparison.Ordinal);
        Assert.Equal("0/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);
        Assert.Equal("-", page.Find("[data-tile=latency] [data-part=value]").TextContent);
    }

    [Fact]
    public async Task ALongerRangeReadsTheRollupsAndTheButtonShowsWhichIsChosen()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        await rig.UseDatabaseAsync();
        for (var i = 0; i < 12; i++)
        {
            await rig.StoreMinuteAsync(MetricRollupSubject.Total, string.Empty, Start.AddMinutes(-60 + (i * 5)), requests: 600);
        }

        await rig.StoreMinuteAsync(MetricRollupSubject.Host, App.ToString(), Start.AddMinutes(-10), requests: 900);
        await rig.StoreMinuteAsync(MetricRollupSubject.Upstream, Web.ToString(), Start.AddMinutes(-10), requests: 900, errors: 9);
        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));
        Assert.Equal("true", page.Find("[data-range=hour]").GetAttribute("aria-pressed"));

        await page.Find("[data-range=day]").ClickAsync(new());

        await page.WaitForStateAsync(() => page.Find("[data-range=day]").GetAttribute("aria-pressed") == "true" && page.FindAll("[data-chart]").Count == 2);
        Assert.Equal("false", page.Find("[data-range=hour]").GetAttribute("aria-pressed"));
        Assert.Equal("app.test", page.Find("[data-section=top-hosts] tbody tr").GetAttribute("data-host"));
        Assert.Equal(Web.ToString(), page.Find("[data-section=upstream-errors] tbody tr").GetAttribute("data-upstream"));
        Assert.Contains("MMM", TrafficFigures.TimeFormat(MetricsRange.Week), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARangeThatCannotBeReadShowsTheAlertAndTryAgainReadsAgain()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        var page = context.Render<TrafficOverview>(parameters => parameters.Add(p => p.PollInterval, TimeSpan.FromHours(1)));
        // No database is open, so the rollup store fails.
        await page.Find("[data-range=week]").ClickAsync(new());

        await page.WaitForStateAsync(() => page.FindAll("[data-part=failed]").Count == 1);
        Assert.Contains("couldn't be read", page.Find("[data-part=failed]").TextContent, StringComparison.Ordinal);

        await rig.UseDatabaseAsync();
        await page.Find("[data-part=failed] button").ClickAsync(new());

        await page.WaitForStateAsync(() => page.FindAll("[data-part=failed]").Count == 0);
        Assert.NotEmpty(page.FindAll("[data-part=tiles]"));
    }

    [Fact]
    public async Task TheHomePageShowsTheTrafficPanel()
    {
        await using Rig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        context.ComponentFactories.AddStub<Paduraksa.Dashboard.Setup.SetupChecklist>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll("[data-section=traffic]").Count == 1);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.9, 1)]
    [InlineData(1, 1)]
    [InlineData(1.1, 2)]
    [InlineData(2.2, 2.5)]
    [InlineData(3, 5)]
    [InlineData(7, 10)]
    [InlineData(31, 50)]
    [InlineData(480, 500)]
    public void TheChartScaleTopsOutAtARoundNumber(double peak, double expected) => Assert.Equal(expected, TimeSeriesChart.Nice(peak));

    [Theory]
    [InlineData(0, "0/s")]
    [InlineData(0.04, "<0.1/s")]
    [InlineData(4.24, "4.2/s")]
    [InlineData(340.4, "340/s")]
    [InlineData(1_240, "1.2k/s")]
    [InlineData(12_400, "12k/s")]
    public void RatesAreShortWithTheirUnit(double rate, string expected) => Assert.Equal(expected, TrafficFigures.PerSecond(rate));

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.0004, "<0.1%")]
    [InlineData(0.0123, "1.2%")]
    [InlineData(0.5, "50%")]
    public void SharesAreWrittenAsPercentages(double share, string expected) => Assert.Equal(expected, TrafficFigures.Percent(share));

    [Theory]
    [InlineData(0, "-")]
    [InlineData(0.4, "0.4 ms")]
    [InlineData(4.4, "4.4 ms")]
    [InlineData(44, "44 ms")]
    [InlineData(1_300, "1.3 s")]
    public void LatenciesAreWrittenInTheUnitThatReadsBest(double ms, string expected) => Assert.Equal(expected, TrafficFigures.Millis(ms));

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        // The test only moves time forward, so the wall clock is also the monotonic one.
        public override long GetTimestamp() => Now.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>The metrics read side, whole: a registry the test records into, a sampler it ticks, a snapshot, and (when asked) a real rollup database.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ManualClock _clock = new(Start);
        private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-metrics-").FullName;
        private ServiceProvider? _database;
        private readonly SwitchableFactory _factory = new();
        private RuntimeSnapshot? _snapshot;

        public Rig()
        {
            Registry = new MetricsRegistry(processors: 2, timeProvider: _clock);
            Backlog = new MetricRollupBacklog();
            Sampler = new MetricSampler(Registry, _clock, Backlog);
            Store = new SnapshotStore();
        }

        public MetricsRegistry Registry { get; }

        public MetricRollupBacklog Backlog { get; }

        public MetricSampler Sampler { get; }

        public SnapshotStore Store { get; }

        public FakeDashboardConfig Config { get; private set; } = null!;

        /// <summary>Signs an operator in and registers the service; the page's clock is this rig's.</summary>
        public DashboardTestContext SignIn()
        {
            var context = new DashboardTestContext();
            // Before the sign-in: its setup builds the service provider, after which nothing can be registered.
            context.Services.AddSingleton(new MetricsService(Registry, Store, Sampler, new MetricRollupStore(_factory), ProcessCounters.None, _clock));
            Config = ConfigPages.SignIn(context, Role.Operator, _clock);
            return context;
        }

        public void Publish()
        {
            var result = SnapshotCompiler.Compile(
                new ProxyConfigModel
                {
                    Upstreams =
                    [
                        new UpstreamSpec { Id = Web, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:1")] },
                        new UpstreamSpec { Id = Media, Destinations = [new DestinationSpec(DestinationId.New(), "http://127.0.0.1:2")] },
                    ],
                    Hosts =
                    [
                        new HostSpec { Id = App, Names = ["app.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Web }] },
                        new HostSpec { Id = Blog, Names = ["blog.test"], Https = false, Routes = [new RouteSpec { Id = RouteId.New(), Upstream = Media }] },
                    ],
                },
                new SnapshotCompilerOptions { Metrics = Registry });
            Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
            var snapshot = result.Snapshot!;
            _snapshot = snapshot.Bind(1, [.. snapshot.RouteIdBySlot.Select(_ => new Endpoint(null, null, "route"))]);
            Registry.Publish(_snapshot.MetricRegistrations);
            Store.Publish(_snapshot);
        }

        public void Record(HostId host, int count, int status, long micros, UpstreamId? upstream = null, long upstreamMicros = -1, int error = 0)
        {
            var compiled = _snapshot!.HostsByIndex.Single(h => h.Id == host);
            var route = compiled.Routes[0];
            for (var i = 0; i < count; i++)
            {
                Registry.RecordRequest(new RequestMetrics
                {
                    Host = compiled.MetricSlot,
                    Route = route.MetricSlot,
                    Upstream = upstream is null || route.Upstream is null ? MetricSlot.None : route.Upstream.Slot,
                    Status = status,
                    Outcome = GateOutcome.Forwarded,
                    TotalMicros = micros,
                    UpstreamMicros = upstreamMicros,
                    ForwarderError = error,
                });
            }
        }

        public void RecordUnmatched(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Registry.RecordRequest(new RequestMetrics { Host = MetricsRegistry.UnmatchedHost, Status = 404, Outcome = GateOutcome.UnknownHost, TotalMicros = 100 });
            }
        }

        public void TickAt(int seconds)
        {
            _clock.Now = Start.AddSeconds(seconds);
            Sampler.Tick();
            _last = seconds;
        }

        /// <summary>Samples on every second after the last tick, as the service does: a longer silence is a stall the sampler doesn't claim to have observed.</summary>
        public void TickEverySecondTo(int seconds)
        {
            for (var second = _last + 1; second <= seconds; second++)
            {
                TickAt(second);
            }
        }

        private int _last;

        public async Task UseDatabaseAsync()
        {
            _database = new ServiceCollection().AddPaduraksaDbContextFactory<ConfigDbContext>(Path.Combine(_directory, ConfigDatabase.FileName)).BuildServiceProvider();
            var factory = _database.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>>();
            await ConfigDatabase.InitializeAsync(factory, new Int64OrderedIdGenerator(TimeProvider.System), Xunit.TestContext.Current.CancellationToken);
            _factory.Inner = factory;
        }

        public Task StoreMinuteAsync(MetricRollupSubject subject, string id, DateTimeOffset at, long requests, long errors = 0) =>
            new MetricRollupStore(_factory).WriteAsync(
                [new MetricRollup(subject, id, new MetricPoint { Start = at.ToUnixTimeSeconds() / 60 * 60, Width = 60, CoveredSeconds = 60, Requests = requests, Status2xx = requests - errors, Status5xx = errors, Errors = errors, TotalP50 = 1_000, TotalP95 = 2_000, TotalP99 = 3_000, UpstreamTimed = requests, UpstreamP95 = 1_000 })],
                Xunit.TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
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

    /// <summary>The database the test has opened, or - until it has - one that fails every time, as a database that is down does.</summary>
    private sealed class SwitchableFactory : Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>
    {
        public Microsoft.EntityFrameworkCore.IDbContextFactory<ConfigDbContext>? Inner { get; set; }

        public ConfigDbContext CreateDbContext() => Inner?.CreateDbContext() ?? throw new InvalidOperationException("The database is down.");
    }
}
