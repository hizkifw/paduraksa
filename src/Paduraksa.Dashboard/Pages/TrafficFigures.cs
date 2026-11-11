using System.Globalization;

using Paduraksa.Contracts.Metrics;

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

    /// <summary>"-" for nothing timed, "0.4 ms", "12 ms", "1.3 s".</summary>
    public static string Millis(double ms) => ms switch
    {
        <= 0 => "-",
        < 1 => string.Create(CultureInfo.InvariantCulture, $"{ms:0.00} ms"),
        < 10 => string.Create(CultureInfo.InvariantCulture, $"{ms:0.0} ms"),
        < 1000 => string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{ms / 1000:0.0} s"),
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
