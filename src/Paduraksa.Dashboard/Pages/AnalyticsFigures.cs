using System.Globalization;

using Paduraksa.Contracts.Analytics;
using Paduraksa.Contracts.Logging;

namespace Paduraksa.Dashboard.Pages;

/// <summary>How the analytics page writes its numbers: short, with the unit, and the same everywhere on the page.</summary>
internal static class AnalyticsFigures
{
    /// <summary>The mark a table row a sketch may have overcounted carries (its tooltip gives the range).</summary>
    public const string Approximately = "≈";

    /// <summary>
    /// The visitor sketch's error, under the count. It is the HyperLogLog's standard error for its register count; a visitor active
    /// across a restart also counts once on each side of it.
    /// </summary>
    public const string VisitorsAccuracy = "±1.6%";

    /// <summary>"950", "1.2k", "34k", "5.6M": for an axis, where a full count would only crowd it.</summary>
    public static string Compact(double value) => value switch
    {
        < 1_000 => string.Create(CultureInfo.InvariantCulture, $"{value:0}"),
        < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000:0.0}k"),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000:0}k"),
        < 10_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000:0.0}M"),
        < 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000:0}M"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000:0.0}G"),
    };

    /// <summary>"0 B", "512 B", "1.5 kB", "34 MB", "2.1 GB": decimal units, as a network is measured.</summary>
    public static string Bytes(double bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes:0} B"),
        < 10_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000:0.0} kB"),
        < 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000:0} kB"),
        < 10_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000:0.0} MB"),
        < 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000:0} MB"),
        < 10_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000:0.0} GB"),
        < 1_000_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000:0} GB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_000_000_000_000:0.0} TB"),
    };

    /// <summary>"0%", "&lt;1%", "12%": a share of a whole, whole percents.</summary>
    public static string Share(long part, long whole) => whole <= 0 || part <= 0
        ? "0%"
        : part * 100.0 / whole < 1 ? "<1%" : string.Create(CultureInfo.InvariantCulture, $"{part * 100.0 / whole:0}%");

    /// <summary>A count with thousands separators.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A count a sketch may have overshot is marked; one that is exact (no error) is not.</summary>
    public static string Counted(long count, long error) => error > 0 ? $"{Approximately} {Count(count)}" : Count(count);

    /// <summary>The tooltip of a row that may have been overcounted: the range the truth is in.</summary>
    public static string? Range(long count, long error) => error > 0
        ? string.Create(CultureInfo.InvariantCulture, $"Between {Count(Math.Max(0, count - error))} and {Count(count)}")
        : null;

    /// <summary>A tooltip line for a step observed in part (the server started or stopped inside it); null when it saw all of it or none.</summary>
    public static string? Coverage(AnalyticsStep step) => step.CoveredSeconds > 0 && step.CoveredSeconds < step.WidthSeconds
        ? string.Create(CultureInfo.InvariantCulture, $"Observed {Span(step.CoveredSeconds)} of {Span(step.WidthSeconds)}")
        : null;

    private static string Span(int seconds) => seconds switch
    {
        < 90 => string.Create(CultureInfo.InvariantCulture, $"{seconds} s"),
        < 5_400 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60.0:0.#} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600.0:0.#} h"),
    };

    /// <summary>
    /// The live tail filtered to a path (and to a host when the analytics are for one), or null for a path the tail can't match whole:
    /// one the analytics cut at their cap, which ends with <c>...</c>.
    /// </summary>
    public static string? TailLink(string path, string? hostName)
    {
        if (path.EndsWith("...", StringComparison.Ordinal) || path.Length > LogTailFilter.MaxTextLength)
        {
            return null;
        }

        var link = DashboardRoutes.LiveTail + "?path=" + Uri.EscapeDataString(path);
        return hostName is null ? link : link + "&host=" + Uri.EscapeDataString(hostName);
    }

    /// <summary>A bot kind as the page says it: <c>searchEngine</c> is "Search engine".</summary>
    public static string Kind(string kind) => kind switch
    {
        "searchEngine" => "Search engine",
        "aiCrawler" => "AI crawler",
        "monitor" => "Monitor",
        "socialPreview" => "Social preview",
        "scanner" => "Scanner",
        "library" => "HTTP library",
        "unknown" => "Other bot",
        _ => kind,
    };

    /// <summary>A status label as the page says it: <c>none</c>, the class of a dropped connection that sent no status, is "No response".</summary>
    public static string Status(string label) => label == "none" ? "No response" : label;

    /// <summary>A device class as the page says it.</summary>
    public static string Device(string device) => device switch
    {
        "desktop" => "Desktop",
        "mobile" => "Mobile",
        "tablet" => "Tablet",
        "bot" => "Bot",
        "other" => "Other",
        _ => device,
    };
}
