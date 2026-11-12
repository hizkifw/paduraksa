using System.Globalization;

using Paduraksa.Contracts.Metrics;
using Paduraksa.Dashboard.LogFiles;

namespace Paduraksa.Dashboard.Pages;

/// <summary>How the overview writes the traffic numbers: short, with the unit, and the same everywhere on the page.</summary>
internal static class TrafficFigures
{
    /// <summary>"0/s", "&lt;0.1/s", "4.2/s", "340/s", "12k/s".</summary>
    public static string PerSecond(double rate) => rate switch
    {
        <= 0 => "0/s",
        < 0.1 => "<0.1/s",
        < 10 => string.Create(CultureInfo.InvariantCulture, $"{rate:0.0}/s"),
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{rate:0}/s"),
        < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{rate / 1000:0.0}k/s"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{rate / 1000:0}k/s"),
    };

    /// <summary>"0%", "&lt;0.1%", "0.4%", "12%".</summary>
    public static string Percent(double share) => share switch
    {
        <= 0 => "0%",
        < 0.001 => "<0.1%",
        < 0.1 => string.Create(CultureInfo.InvariantCulture, $"{share * 100:0.0}%"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{share * 100:0}%"),
    };

    /// <summary>"-" for nothing timed, else the live tail's wording of a duration.</summary>
    public static string Millis(double ms) => ms <= 0 ? "-" : LiveTailText.Duration(ms);

    /// <summary>A tooltip line for a step the server observed only in part (it started or stopped inside it); null when it saw all of it or none.</summary>
    public static string? Coverage(MetricsSeriesPoint point) => point.CoveredSeconds > 0 && point.CoveredSeconds < point.WidthSeconds
        ? string.Create(CultureInfo.InvariantCulture, $"Observed {Span(point.CoveredSeconds)} of {Span(point.WidthSeconds)}")
        : null;

    private static string Span(int seconds) => seconds switch
    {
        < 90 => string.Create(CultureInfo.InvariantCulture, $"{seconds} s"),
        < 5_400 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60.0:0.#} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600.0:0.#} h"),
    };

    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Label(MetricsRange range) => range switch
    {
        MetricsRange.Hour => "1 h",
        MetricsRange.Day => "24 h",
        MetricsRange.Week => "7 d",
        _ => "30 d",
    };

    /// <summary>The format of the chart's two end labels: the time of day for the hour, the date and time beyond.</summary>
    public static string TimeFormat(MetricsRange range) => range == MetricsRange.Hour ? "HH:mm" : "MMM d HH:mm";
}
