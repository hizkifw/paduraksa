using Paduraksa.Contracts.Metrics;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Dashboard.Pages;

/// <summary>How the overview writes the traffic numbers: short, with the unit, and the same everywhere on the page.</summary>
internal static class TrafficFigures
{
    // The numbers are DisplayFormat's. These keep the names the pages and other branches call; they write the invariant default, and a page
    // that shows them to a person calls the injected DisplayFormat instead.
    public static string PerSecond(double rate) => DisplayFormat.Invariant.PerSecond(rate);

    public static string Percent(double share) => DisplayFormat.Invariant.Percent(share);

    public static string Millis(double ms) => DisplayFormat.Invariant.Latency(ms);

    public static string Count(long value) => DisplayFormat.Invariant.Count(value);

    /// <summary>A tooltip line for a step the server observed only in part (it started or stopped inside it); null when it saw all of it or none.</summary>
    public static string? Coverage(MetricsSeriesPoint point, DisplayFormat? format = null) => Coverage(point.CoveredSeconds, point.WidthSeconds, format);

    /// <inheritdoc cref="Coverage(MetricsSeriesPoint, DisplayFormat?)"/>
    public static string? Coverage(int coveredSeconds, int widthSeconds, DisplayFormat? format = null)
    {
        format ??= DisplayFormat.Invariant;
        return coveredSeconds > 0 && coveredSeconds < widthSeconds
            ? $"Observed {format.Seconds(coveredSeconds)} of {format.Seconds(widthSeconds)}"
            : null;
    }

    public static string Label(MetricsRange range) => range switch
    {
        MetricsRange.Hour => "1 h",
        MetricsRange.Day => "24 h",
        MetricsRange.Week => "7 d",
        _ => "30 d",
    };

    /// <summary>The range switch: the same four segments on the overview and on Analytics.</summary>
    public static readonly IReadOnlyList<SegmentOption<MetricsRange>> RangeOptions =
    [
        new(MetricsRange.Hour, "1 h", Tooltip: "Last hour"),
        new(MetricsRange.Day, "24 h", Tooltip: "Last 24 hours"),
        new(MetricsRange.Week, "7 d", Tooltip: "Last 7 days"),
        new(MetricsRange.Month, "30 d", Tooltip: "Last 30 days"),
    ];

    /// <summary>The format of the chart's two end labels: the time of day for the hour, the date and time beyond.</summary>
    public static string TimeFormat(MetricsRange range) => range == MetricsRange.Hour ? "HH:mm" : "MMM d HH:mm";
}
