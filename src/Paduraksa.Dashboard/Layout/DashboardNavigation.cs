using MudBlazor;

using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Layout;

/// <summary>One entry of the side navigation.</summary>
/// <param name="Title">The label.</param>
/// <param name="Href">The page.</param>
/// <param name="Icon">The MudBlazor icon.</param>
/// <param name="Group">The section it is listed under (null: above the sections).</param>
/// <param name="Requires">The permission the page needs to show anything; the entry is hidden without it. Null: every signed-in user.</param>
/// <param name="Available">False while the page is a placeholder (it is listed, with a "soon" badge).</param>
public sealed record NavigationEntry(string Title, string Href, string Icon, string? Group, Permission? Requires, bool Available);

/// <summary>The side navigation (PLAN §12): the M5 pages, in the order an operator works through them.</summary>
public static class DashboardNavigation
{
    public static IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new("Overview", DashboardRoutes.Home, Icons.Material.Outlined.SpaceDashboard, null, null, true),
        new("Hosts", DashboardRoutes.Hosts, Icons.Material.Outlined.Language, "Traffic", Permission.ConfigRead, true),
        new("Upstreams", DashboardRoutes.Upstreams, Icons.Material.Outlined.Hub, "Traffic", Permission.ConfigRead, true),
        new("Certificates", DashboardRoutes.Certificates, Icons.Material.Outlined.WorkspacePremium, "Traffic", Permission.CertificatesRead, true),
        new("Simulator", DashboardRoutes.Simulator, Icons.Material.Outlined.Science, "Insight", Permission.Simulate, true),
        new("History", DashboardRoutes.History, Icons.Material.Outlined.History, "Insight", Permission.ConfigRead, true),
        new("Audit", DashboardRoutes.Audit, Icons.Material.Outlined.FactCheck, "Insight", Permission.AuditRead, false),
        new("Settings", DashboardRoutes.Settings, Icons.Material.Outlined.Settings, "System", null, true),
    ];

    /// <summary>The entries <paramref name="mayView"/> allows, in order.</summary>
    public static IEnumerable<NavigationEntry> VisibleTo(Func<Permission, bool> mayView)
    {
        ArgumentNullException.ThrowIfNull(mayView);
        return Entries.Where(entry => entry.Requires is not { } permission || mayView(permission));
    }
}
