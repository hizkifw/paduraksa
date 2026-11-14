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

public sealed class TrafficStatisticsTests
{
    private static readonly DateTimeOffset Start = MetricsRig.Start;
    private static readonly HostId App = MetricsRig.App;
    private static readonly HostId Blog = MetricsRig.Blog;
    private static readonly UpstreamId Web = MetricsRig.Web;
    private static readonly UpstreamId Media = MetricsRig.Media;

    private static IRenderedComponent<TrafficStatistics> Render(DashboardTestContext context, MetricsRange range = MetricsRange.Hour, TimeSpan? poll = null) =>
        context.Render<TrafficStatistics>(parameters => parameters
            .Add(p => p.Range, range)
            .Add(p => p.PollInterval, poll ?? TimeSpan.FromHours(1)));

    [Fact]
    public async Task ThereAreNoChartsUntilTheRangeHasTwoPoints_ThenBothAreDrawn()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickAt(2);

        var page = Render(context, poll: TimeSpan.FromMilliseconds(20));

        Assert.Empty(page.FindAll("[data-chart]"));
        Assert.Contains("No traffic in this range yet", page.Find("[data-section=requests-chart]").TextContent, StringComparison.Ordinal);

        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickEverySecondTo(22);

        // The next poll reads the ring again.
        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 2);
        Assert.NotEmpty(page.Find("[data-chart=requests] polyline.pdk-chart-line").GetAttribute("points")!);
        // p50, p95 and p99; nothing was forwarded upstream, so the upstream line has nothing to draw.
        Assert.Equal(3, page.FindAll("[data-chart=latency] polyline").Count);
        Assert.NotEmpty(page.FindAll("[data-chart=requests] rect.pdk-chart-hit[data-time]"));
    }

    [Fact]
    public async Task ATimeTheServerWasNotRunningIsAGapInBothChartsAndAQuietOneIsNot()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 30, status: 200, micros: 2_000);
        rig.TickEverySecondTo(25);
        // The sampler is silent for a hundred seconds (the process was down), then goes on.
        rig.TickAt(125);
        rig.Record(App, 30, status: 200, micros: 2_000);
        rig.TickEverySecondTo(150);

        var page = Render(context);

        Assert.Equal(2, page.FindAll("[data-chart=requests] polyline.pdk-chart-a").Count);
        Assert.Equal(2, page.FindAll("[data-chart=requests] polyline.pdk-chart-err").Count);
        Assert.Equal(2, page.FindAll("[data-chart=latency] polyline.pdk-chart-b").Count);
        Assert.Single(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        Assert.Contains(page.FindAll("[data-chart=requests] rect.pdk-chart-hit"), hit => hit.GetAttribute("data-note") == "No data: Paduraksa was not running");
    }

    [Fact]
    public async Task AStallInWhichRequestsWereServedIsNotAGapInEitherChart()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickEverySecondTo(5);
        rig.Record(App, 300, status: 200, micros: 2_000);
        // The sampler is silent for 25 s, but the proxy answered 300 requests in it: the server was running.
        rig.TickAt(30);
        rig.TickEverySecondTo(60);

        var page = Render(context);

        Assert.Single(page.FindAll("[data-chart=requests] polyline.pdk-chart-a"));
        Assert.Empty(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        Assert.DoesNotContain(page.FindAll("[data-chart=requests] rect.pdk-chart-hit"), hit => hit.GetAttribute("data-note")?.StartsWith("No data", StringComparison.Ordinal) == true);
        Assert.Single(page.FindAll("[data-chart=latency] polyline.pdk-chart-b"));
    }

    [Fact]
    public async Task AQuietStretchTheServerWasUpForKeepsOneLineAtZeroAndAnEmptyLatencyLine()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickEverySecondTo(95);

        var page = Render(context);

        Assert.Single(page.FindAll("[data-chart=requests] polyline.pdk-chart-a"));
        Assert.Empty(page.FindAll("[data-chart=requests] rect.pdk-chart-gap"));
        // Nothing was timed, so there is no latency to draw: the lines are empty, not zeros.
        Assert.Empty(page.FindAll("[data-chart=latency] polyline"));
    }

    [Fact]
    public async Task TheBusiestHostsAreLinkedAndTheRequestsNoHostOwnedAreNotLinkedAtAll()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 100, status: 200, micros: 1_000);
        rig.Record(App, 20, status: 500, micros: 1_000);
        rig.Record(Blog, 400, status: 200, micros: 1_000);
        rig.RecordUnmatched(7);
        rig.TickAt(5);

        var page = Render(context);

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
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Config.Document = new ConfigDocument(Upstreams: [new UpstreamConfig(Web, "web"), new UpstreamConfig(Media, "media")]);
        rig.Publish();
        rig.Record(App, 50, status: 200, micros: 1_000, upstream: Web, upstreamMicros: 800);
        rig.Record(Blog, 40, status: 200, micros: 1_000, upstream: Media, upstreamMicros: 800);
        rig.Record(Blog, 5, status: 502, micros: 1_000, upstream: Media, upstreamMicros: 800, error: 10);
        rig.TickAt(5);

        var page = Render(context);

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
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();

        var page = Render(context);

        Assert.Contains("No requests in this range", page.Find("[data-section=top-hosts]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Nothing was forwarded in this range", page.Find("[data-section=upstream-errors]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongerRangeReadsTheRollupsAndTheTablesFollowIt()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        await rig.UseDatabaseAsync();
        for (var i = 0; i < 12; i++)
        {
            await rig.StoreMinuteAsync(MetricRollupSubject.Total, string.Empty, Start.AddMinutes(-60 + (i * 5)), requests: 600);
        }

        // Over an hour back: a longer range's tables take the last hour from the sampler, which this rig doesn't seed.
        await rig.StoreMinuteAsync(MetricRollupSubject.Host, App.ToString(), Start.AddMinutes(-70), requests: 900);
        await rig.StoreMinuteAsync(MetricRollupSubject.Upstream, Web.ToString(), Start.AddMinutes(-70), requests: 900, errors: 9);
        var page = Render(context);
        Assert.Empty(page.FindAll("[data-section=top-hosts] tbody tr"));

        page.Render(parameters => parameters.Add(p => p.Range, MetricsRange.Day).Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 2);
        Assert.Equal("app.test", page.Find("[data-section=top-hosts] tbody tr").GetAttribute("data-host"));
        Assert.Equal(Web.ToString(), page.Find("[data-section=upstream-errors] tbody tr").GetAttribute("data-upstream"));
        Assert.Contains("MMM", TrafficFigures.TimeFormat(MetricsRange.Week), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARangeThatCannotBeReadShowsTheAlertAndTryAgainReadsAgain()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        // No database is open, so the rollup store fails.
        var page = Render(context, MetricsRange.Week);

        await page.WaitForStateAsync(() => page.FindAll("[data-part=failed]").Count == 1);
        Assert.Contains("couldn't be read", page.Find("[data-part=failed]").TextContent, StringComparison.Ordinal);

        await rig.UseDatabaseAsync();
        await page.Find("[data-part=failed] button").ClickAsync(new());

        await page.WaitForStateAsync(() => page.FindAll("[data-part=failed]").Count == 0);
        Assert.NotEmpty(page.FindAll("[data-section=requests-chart]"));
    }

    [Fact]
    public async Task ThePanelDoesNotReadWhileTheTabIsHiddenAndReadsAtOnceWhenItIsShown()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickAt(2);
        var page = Render(context, poll: TimeSpan.FromMilliseconds(20));
        Assert.Empty(page.FindAll("[data-chart]"));

        page.Render(parameters => parameters.Add(p => p.Visible, false).Add(p => p.Range, MetricsRange.Hour).Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20)));
        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickEverySecondTo(22);
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        Assert.Empty(page.FindAll("[data-chart]"));

        page.Render(parameters => parameters.Add(p => p.Visible, true).Add(p => p.Range, MetricsRange.Hour).Add(p => p.PollInterval, TimeSpan.FromHours(1)));

        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 2);
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
}
