using System.Globalization;

using Bunit;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Dashboard;
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
        var hits = chart.FindAll("rect.pdk-chart-hit");
        Assert.Equal(7, hits.Count);
        Assert.Equal("No data: Paduraksa was not running", hits[2].GetAttribute("data-note"));
        Assert.Equal("[]", hits[2].GetAttribute("data-values"));
        Assert.Equal("""["2"]""", hits[1].GetAttribute("data-values"));
        Assert.Null(hits[1].GetAttribute("data-note"));
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
        Assert.Equal("""["-"]""", chart.FindAll("rect.pdk-chart-hit")[2].GetAttribute("data-values"));
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

        Assert.Equal("Observed 4 s of 10 s", chart.FindAll("rect.pdk-chart-hit")[0].GetAttribute("data-note"));
    }

    [Fact]
    public void EveryColumnCarriesItsTextInDataAttributesAndNoNativeTooltip()
    {
        using DashboardTestContext context = new();
        var points = new[] { At(0), At(10) };
        TimeSeriesChart.ChartLine requests = new("Requests", "pdk-chart-a", [4.2, 340]);
        TimeSeriesChart.ChartLine errors = new("Errors", "pdk-chart-err", [0, null]);

        var chart = context.Render<TimeSeriesChart>(parameters => parameters
            .Add(p => p.Name, "test")
            .Add(p => p.Points, points)
            .Add(p => p.Lines, [requests, errors])
            .Add(p => p.Format, rate => rate.ToString("0.#", CultureInfo.InvariantCulture) + "/s"));

        Assert.Empty(chart.FindAll("title"));
        var hits = chart.FindAll("rect.pdk-chart-hit");
        Assert.Equal("2026-11-12 12:00:00 UTC", hits[0].GetAttribute("data-time"));
        Assert.Equal("""["4.2/s","0/s"]""", hits[0].GetAttribute("data-values"));
        Assert.Equal("""["340/s","-"]""", hits[1].GetAttribute("data-values"));
        // The names and swatches the script shows come from the legend, in the same order as the values.
        Assert.Equal(["Requests", "Errors"], chart.FindAll(".pdk-chart-key").Select(key => key.TextContent.Trim()));
    }

    [Fact]
    public void TimesAreWrittenInThePersonsZoneOnTheAxisAndInTheColumns()
    {
        using DashboardTestContext context = new();
        context.Services.GetRequiredService<DisplayFormat>().Use(new DisplayPreferences(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(7), "test", "test"), CultureInfo.InvariantCulture));
        var points = new[] { At(0), At(3600) };

        var chart = Render(context, points, new TimeSeriesChart.ChartLine("Requests", "pdk-chart-a", [1, 2]));

        Assert.Equal(["19:00", "20:00"], chart.FindAll(".pdk-chart-x span").Select(span => span.TextContent));
        Assert.Equal("2026-11-12 19:00:00 UTC+7", chart.FindAll("rect.pdk-chart-hit")[0].GetAttribute("data-time"));
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
