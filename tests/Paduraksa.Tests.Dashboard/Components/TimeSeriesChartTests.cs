using System.Globalization;

using Bunit;

using Paduraksa.Dashboard.Components;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class TimeSeriesChartTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static TimeSeriesChart.ChartPoint At(int seconds, int width = 10, bool noData = false, string? note = null) =>
        new(Start.AddSeconds(seconds), TimeSpan.FromSeconds(width), noData, note);

    private static IRenderedComponent<TimeSeriesChart> Render(
        DashboardTestContext context, IReadOnlyList<TimeSeriesChart.ChartPoint> points, params TimeSeriesChart.ChartLine[] lines) =>
        context.Render<TimeSeriesChart>(parameters => parameters
            .Add(p => p.Name, "test")
            .Add(p => p.Points, points)
            .Add(p => p.Lines, lines));

    private static double[][] Coordinates(AngleSharp.Dom.IElement polyline) =>
        [.. polyline.GetAttribute("points")!.Split(' ').Select(pair => pair.Split(',').Select(number => double.Parse(number, CultureInfo.InvariantCulture)).ToArray())];

    [Fact]
    public void ALineIsOnePolylinePerStretchOfValuesAndNeverCrossesAGap()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10), At(20, noData: true), At(30, noData: true), At(40), At(50), At(60) };
        TimeSeriesChart.ChartLine line = new("Requests", "pdk-chart-a", [1, 2, null, null, 3, 4, 5], Area: true);

        var chart = Render(context, points, line);

        var runs = chart.FindAll("polyline.pdk-chart-line");
        Assert.Equal(2, runs.Count);
        Assert.Equal(2, Coordinates(runs[0]).Length);
        Assert.Equal(3, Coordinates(runs[1]).Length);
        // The first run ends at its last point (20 s of 60 s of a 600 wide plot is 100), the second starts after the gap, at 40 s.
        Assert.Equal(100, Coordinates(runs[0])[^1][0], 3);
        Assert.Equal(400, Coordinates(runs[1])[0][0], 3);
        Assert.Equal(2, chart.FindAll("path.pdk-chart-area").Count);
    }

    [Fact]
    public void AStretchNobodyObservedIsShadedFromWhereItStartsToWhereTheDataResumes()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10), At(20, noData: true), At(30, noData: true), At(40), At(50), At(60) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, 2, null, null, 3, 4, 5]));

        var band = Assert.Single(chart.FindAll("rect.pdk-chart-gap"));
        Assert.Equal("200", band.GetAttribute("x"));
        Assert.Equal("200", band.GetAttribute("width"));
        var tooltips = chart.FindAll("rect.pdk-chart-hit title").Select(title => title.TextContent).ToArray();
        Assert.Equal(7, tooltips.Length);
        Assert.Contains("No data: Paduraksa was not running", tooltips[2], StringComparison.Ordinal);
        Assert.DoesNotContain("Requests", tooltips[2], StringComparison.Ordinal);
        Assert.Contains("Requests: ", tooltips[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleStretchOfCoveredZerosIsOneContinuousLineAtTheBottomAndShadesNothing()
    {
        using DashboardTestContext context = new();
        var points = Enumerable.Range(0, 30).Select(i => At(i * 10)).ToArray();

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [.. points.Select(_ => (double?)0)]));

        var line = Assert.Single(chart.FindAll("polyline.pdk-chart-line"));
        Assert.Equal(30, Coordinates(line).Length);
        Assert.All(Coordinates(line), pair => Assert.Equal(140, pair[1]));
        Assert.Empty(chart.FindAll("rect.pdk-chart-gap"));
    }

    [Fact]
    public void AValueThatIsMissingInsideObservedTimeBreaksTheLineWithoutShadingIt()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10), At(20), At(30) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("p95", "pdk-chart-c", [5, 6, null, 7]));

        Assert.Equal(2, chart.FindAll("polyline.pdk-chart-line").Count);
        Assert.Empty(chart.FindAll("rect.pdk-chart-gap"));
        Assert.Contains("p95: -", chart.FindAll("rect.pdk-chart-hit title")[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AStretchOfOnePointIsStillAMarkTheWidthOfItsStep()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10, noData: true), At(20, width: 20), At(40, noData: true), At(50) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, null, 4, null, 2]));

        var runs = chart.FindAll("polyline.pdk-chart-line");
        Assert.Equal(3, runs.Count);
        var lone = Coordinates(runs[1]);
        Assert.Equal(2, lone.Length);
        Assert.Equal(240, lone[0][0], 3);
        Assert.Equal(480, lone[1][0], 3);
        Assert.Equal(lone[0][1], lone[1][1]);
    }

    [Fact]
    public void StepsOfDifferentWidthsSitWhereTheyWereInTime()
    {
        using DashboardTestContext context = new();
        // Three minute wide steps, then ten second ones: the axis is time, so the second group is packed at the right.
        var points = new[] { At(0, 60), At(60, 60), At(120, 60), At(180), At(190), At(200), At(210), At(220), At(230), At(240) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [.. points.Select(_ => (double?)1)]));

        var xs = Coordinates(Assert.Single(chart.FindAll("polyline.pdk-chart-line"))).Select(pair => pair[0]).ToArray();
        Assert.Equal(0, xs[0]);
        Assert.Equal(450, xs[3], 3);
        Assert.Equal(600, xs[^1], 3);
        Assert.Equal(2.5 * 20, xs[5] - xs[3], 3);
        // The tooltip columns tile the plot with no hole and no overlap.
        var columns = chart.FindAll("rect.pdk-chart-hit").Select(hit => (X: double.Parse(hit.GetAttribute("x")!, CultureInfo.InvariantCulture), W: double.Parse(hit.GetAttribute("width")!, CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(0, columns[0].X);
        for (var i = 1; i < columns.Length; i++)
        {
            Assert.Equal(columns[i - 1].X + columns[i - 1].W, columns[i].X, 1);
        }

        Assert.Equal(600, columns[^1].X + columns[^1].W, 1);
    }

    [Fact]
    public void AStepObservedInPartSaysSoInItsTooltip()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0, note: "Observed 4 s of 10 s"), At(10) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, 2]));

        Assert.EndsWith("Observed 4 s of 10 s", chart.FindAll("rect.pdk-chart-hit title")[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDescriptionCountsTheStepsWithNoData()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10, noData: true), At(20) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, null, 3]));

        Assert.Contains("1 with no data", chart.Find("figure").GetAttribute("aria-label"), StringComparison.Ordinal);
    }
}
