using Bunit;

using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The overview's strip of four live tiles: the pace of the last half minute, the last hour behind each, and the link to Analytics.</summary>
public sealed class TrafficStripTests
{
    private static readonly HostId App = MetricsRig.App;

    private static IRenderedComponent<TrafficStrip> Render(DashboardTestContext context, TimeSpan? poll = null) =>
        context.Render<TrafficStrip>(parameters => parameters.Add(p => p.PollInterval, poll ?? TimeSpan.FromHours(1)));

    [Fact]
    public async Task TheTilesShowThePaceOfTheLastHalfMinute()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 270, status: 200, micros: 2_000);
        rig.Record(App, 30, status: 502, micros: 4_000, error: 10);
        rig.TickAt(5);

        var page = Render(context);

        Assert.Equal(["requests", "errors", "refused", "latency"], page.FindAll("[data-tile]").Select(tile => tile.GetAttribute("data-tile")));
        Assert.Equal("60/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);
        Assert.Equal("10%", page.Find("[data-tile=errors] [data-part=value]").TextContent);
        Assert.Equal("0/s", page.Find("[data-tile=refused] [data-part=value]").TextContent);
        Assert.EndsWith(" ms", page.Find("[data-tile=latency] [data-part=value]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Latency p95", page.Find("[data-tile=latency] .pdk-sec-label").TextContent);
        Assert.Empty(page.FindAll("[data-field=range]"));
    }

    [Fact]
    public async Task EveryTileLinksToTheTrafficTabOverTheLastHour()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.TickAt(2);

        var page = Render(context);

        var links = page.FindAll("[data-tile] a[data-part=link]");
        Assert.Equal(4, links.Count);
        Assert.All(links, link => Assert.Equal("/analytics?tab=traffic&range=1h", link.GetAttribute("href")));
    }

    [Fact]
    public async Task EachTileHasASparklineOfTheLastHourOnceThereAreTwoSteps()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickAt(2);

        var page = Render(context, TimeSpan.FromMilliseconds(20));

        Assert.Empty(page.FindAll("[data-chart]"));

        rig.Record(App, 10, status: 200, micros: 1_000);
        rig.TickEverySecondTo(22);

        await page.WaitForStateAsync(() => page.FindAll("[data-chart]").Count == 4);
        Assert.Equal(["requests", "errors", "refused", "latency"], page.FindAll("[data-chart]").Select(chart => chart.GetAttribute("data-chart")));
        Assert.All(page.FindAll("[data-chart]"), chart => Assert.Contains("pdk-chart-compact", chart.ClassName, StringComparison.Ordinal));
        // A sparkline is the line alone: its tooltip still reads (the columns carry the time and the values), but there are no axes.
        Assert.Empty(page.FindAll("[data-chart] .pdk-chart-axis"));
        Assert.Empty(page.FindAll("[data-chart] .pdk-chart-x"));
        Assert.NotEmpty(page.FindAll("[data-tile=requests] rect.pdk-chart-hit[data-time]"));
    }

    [Fact]
    public async Task AnInstallThatHasServedNothingShowsZerosAndNoLatency()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();

        var page = Render(context);

        Assert.Equal("0/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);
        Assert.Equal("-", page.Find("[data-tile=latency] [data-part=value]").TextContent);
    }

    [Fact]
    public async Task TheSparklinesLaterStepsAreReadOnTheNextPoll()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 100, status: 200, micros: 1_000);
        rig.TickEverySecondTo(25);
        var page = Render(context, TimeSpan.FromMilliseconds(20));
        var before = page.Find("[data-tile=requests]").QuerySelectorAll("rect.pdk-chart-hit").Length;

        rig.TickEverySecondTo(55);

        await page.WaitForStateAsync(() => page.Find("[data-tile=requests]").QuerySelectorAll("rect.pdk-chart-hit").Length > before);
    }

    [Fact]
    public async Task AHiddenBrowserTabStopsTheReadsAndShowingItReadsAtOnce()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        rig.Record(App, 100, status: 200, micros: 1_000);
        rig.TickAt(5);
        var page = Render(context, TimeSpan.FromMilliseconds(20));
        Assert.Equal("20/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(false));
        rig.Record(App, 500, status: 200, micros: 1_000);
        rig.TickAt(10);
        await Task.Delay(150, Xunit.TestContext.Current.CancellationToken);
        Assert.Equal("20/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);

        await page.InvokeAsync(() => page.Instance.PageVisibilityChangedAsync(true));

        Assert.NotEqual("20/s", page.Find("[data-tile=requests] [data-part=value]").TextContent);
    }

    [Fact]
    public async Task TheOverviewHasTheStripAndNeitherARangePickerNorTheChartsThatMovedToAnalytics()
    {
        await using MetricsRig rig = new();
        var context = rig.SignIn();
        rig.Publish();
        context.ComponentFactories.AddStub<Paduraksa.Dashboard.Setup.SetupChecklist>();

        var page = context.Render<Home>();

        await page.WaitForStateAsync(() => page.FindAll("[data-section=traffic-strip]").Count == 1);
        Assert.Empty(page.FindAll("[data-field=range]"));
        Assert.Empty(page.FindAll("[data-section=top-hosts]"));
        Assert.Empty(page.FindAll("[data-section=upstream-errors]"));
        Assert.Empty(page.FindAll("[data-link=analytics]"));
        // The hosts and the latest changes stay under the strip.
        Assert.Equal(["traffic-strip", "hosts", "recent"], page.FindAll("[data-section=traffic-strip], [data-section=hosts], [data-section=recent]").Select(section => section.GetAttribute("data-section")));
    }
}
