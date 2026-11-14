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

    // The numbers are DisplayFormat's. These keep the names the pages and other branches call; they write the invariant default, and a page
    // that shows them to a person calls the injected DisplayFormat instead.
    public static string Compact(double value) => DisplayFormat.Invariant.Compact(value);

    public static string Bytes(double bytes) => DisplayFormat.Invariant.Bytes(bytes);

    public static string Share(long part, long whole) => DisplayFormat.Invariant.Share(part, whole);

    public static string Count(long value) => DisplayFormat.Invariant.Count(value);

    /// <summary>A count a sketch may have overshot is marked; one that is exact (no error) is not.</summary>
    public static string Counted(long count, long error, DisplayFormat? format = null) =>
        error > 0 ? $"{Approximately} {(format ?? DisplayFormat.Invariant).Count(count)}" : (format ?? DisplayFormat.Invariant).Count(count);

    /// <summary>The tooltip of a row that may have been overcounted: the range the truth is in.</summary>
    public static string? Range(long count, long error, DisplayFormat? format = null)
    {
        format ??= DisplayFormat.Invariant;
        return error > 0 ? $"Between {format.Count(Math.Max(0, count - error))} and {format.Count(count)}" : null;
    }

    /// <summary>A tooltip line for a step observed in part (the server started or stopped inside it); null when it saw all of it or none.</summary>
    public static string? Coverage(AnalyticsStep step, DisplayFormat? format = null)
    {
        format ??= DisplayFormat.Invariant;
        return step.CoveredSeconds > 0 && step.CoveredSeconds < step.WidthSeconds
            ? $"Observed {format.Seconds(step.CoveredSeconds)} of {format.Seconds(step.WidthSeconds)}"
            : null;
    }

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
