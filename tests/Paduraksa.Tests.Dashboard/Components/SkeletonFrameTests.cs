using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// A skeleton is the loaded component's own frame with placeholders, so the two have the same elements with the same classes: what sizes
/// them is the shared CSS, and the browser guard (<c>SkeletonShiftEndToEndTests</c>) measures the result. These check the structure, so a
/// skeleton can't drift into its own markup without a browser.
/// </summary>
public sealed class SkeletonFrameTests
{
    // The element tree as tag and the layout classes; a placeholder's own classes and a data attribute are what is allowed to differ.
    private static List<string> Shape(IElement root) =>
    [
        .. root.QuerySelectorAll("*").Select(e => e.TagName + "." + string.Join('.', e.ClassList.Where(c => c != "pdk-pending" && c != "pdk-pending-block" && !System.Text.RegularExpressions.Regex.IsMatch(c, "^pdk-chart-(a|b|c|err|up|r[0-9])$") && !c.StartsWith("pdk-chart-pending", StringComparison.Ordinal) && c != "pdk-chart-empty-legend").Order())),
    ];

    [Fact]
    public void ATilePendingHasTheLoadedTilesElements()
    {
        using DashboardTestContext context = new();
        RenderFragment spark = builder => builder.AddContent(0, "chart");

        var loaded = context.Render<SecurityTile>(p => p.Add(t => t.Name, "requests").Add(t => t.Label, "Requests").Add(t => t.Value, "5/s").Add(t => t.Detail, "d").Add(t => t.Href, "/x").Add(t => t.Spark, spark));
        var pending = context.Render<SecurityTile>(p => p.Add(t => t.Name, "requests").Add(t => t.Label, "Requests").Add(t => t.Pending, true).Add(t => t.Detail, "d").Add(t => t.Href, "/x").Add(t => t.Spark, spark));

        // The sparkline's room is the same div; what is inside it (the chart, or the placeholder block) is what differs.
        Assert.Equal(Shape(loaded.Find("[data-tile]")), Shape(pending.Find("[data-pending]")).Where(s => s != "DIV."));
        Assert.Empty(pending.FindAll("[data-tile]"));
        Assert.Empty(loaded.FindAll("[data-pending]"));
    }

    [Fact]
    public void ACountCardLoadingHasTheLoadedCardsElements()
    {
        using DashboardTestContext context = new();
        RenderFragment value = builder => builder.AddContent(0, "3");

        var loaded = context.Render<StatCard>(p => p.Add(c => c.Label, "Hosts").Add(c => c.Value, value).Add(c => c.Detail, "1 online").Add(c => c.Href, "/hosts"));
        var loading = context.Render<StatCard>(p => p.Add(c => c.Label, "Hosts").Add(c => c.Loading, true).Add(c => c.Href, "/hosts"));

        Assert.Equal(Shape(loaded.Find(".pdk-stat")), Shape(loading.Find(".pdk-stat")));
    }

    [Fact]
    public void AChartPendingAndAnEmptyChartHaveTheLoadedChartsFrame()
    {
        using DashboardTestContext context = new();
        var points = new[] { new TimeSeriesChart.ChartPoint(DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(1)), new(DateTimeOffset.UnixEpoch.AddMinutes(1), TimeSpan.FromMinutes(1)) };
        var lines = new[] { new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, 2]) };

        var loaded = context.Render<TimeSeriesChart>(p => p.Add(c => c.Name, "r").Add(c => c.Points, points).Add(c => c.Lines, lines).Add(c => c.Format, v => $"{v}"));
        var pending = context.Render<TimeSeriesChart>(p => p.Add(c => c.Name, "r").Add(c => c.Pending, true));
        var empty = context.Render<TimeSeriesChart>(p => p.Add(c => c.Name, "r").Add(c => c.EmptyMessage, "Nothing here"));

        // The loaded chart's plot has the lines and hit columns inside; the frame around it is what has to match.
        static List<string> Frame(IElement figure) => [.. Shape(figure).Where(s => !s.StartsWith("polyline", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("rect", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("line", StringComparison.OrdinalIgnoreCase) && !s.StartsWith("path", StringComparison.OrdinalIgnoreCase))];
        var frame = Frame(loaded.Find("figure"));
        Assert.Equal(frame, Frame(pending.Find("figure")));
        // The message is the one addition: it sits over the plot.
        Assert.Equal(frame, Frame(empty.Find("figure")).Where(s => s != "DIV.pdk-chart-empty"));
        Assert.Empty(pending.FindAll("[data-chart]"));
        Assert.Empty(empty.FindAll("[data-chart]"));
    }
}
