using Paduraksa.Contracts.Metrics;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Dashboard.Pages;

/// <summary>The tabs of the Analytics page.</summary>
public enum AnalyticsTab
{
    Traffic,
    Visitors,
    Security,
}

/// <summary>The Analytics page's tab and range as they are written in its address (<c>?tab=visitors&amp;range=7d</c>), so a view can be linked to.</summary>
internal static class AnalyticsView
{
    public const string TabParameter = "tab";

    public const string RangeParameter = "range";

    /// <summary>What a page with no (or an unreadable) range in its address shows; the security overview's 24 hours link relies on it.</summary>
    public const MetricsRange DefaultRange = MetricsRange.Day;

    public static readonly IReadOnlyList<SegmentOption<AnalyticsTab>> TabOptions =
    [
        new(AnalyticsTab.Traffic, "Traffic"),
        new(AnalyticsTab.Visitors, "Visitors"),
        new(AnalyticsTab.Security, "Security"),
    ];

    public static string Token(AnalyticsTab tab) => tab switch
    {
        AnalyticsTab.Visitors => "visitors",
        AnalyticsTab.Security => "security",
        _ => "traffic",
    };

    public static string Token(MetricsRange range) => range switch
    {
        MetricsRange.Hour => "1h",
        MetricsRange.Week => "7d",
        MetricsRange.Month => "30d",
        _ => "24h",
    };

    /// <summary>The tab an address names; anything else is the first.</summary>
    public static AnalyticsTab ParseTab(string? token) => token?.ToLowerInvariant() switch
    {
        "visitors" => AnalyticsTab.Visitors,
        "security" => AnalyticsTab.Security,
        _ => AnalyticsTab.Traffic,
    };

    /// <summary>The range an address names; anything else is <see cref="DefaultRange"/>.</summary>
    public static MetricsRange ParseRange(string? token) => token?.ToLowerInvariant() switch
    {
        "1h" => MetricsRange.Hour,
        "7d" => MetricsRange.Week,
        "30d" => MetricsRange.Month,
        "24h" => MetricsRange.Day,
        _ => DefaultRange,
    };

    /// <summary>The page's address for a tab and a range.</summary>
    public static string Href(AnalyticsTab tab, MetricsRange range) =>
        $"{DashboardRoutes.Analytics}?{TabParameter}={Token(tab)}&{RangeParameter}={Token(range)}";
}
