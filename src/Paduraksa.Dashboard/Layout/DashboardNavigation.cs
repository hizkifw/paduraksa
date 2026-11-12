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
/// <param name="Also">Other addresses that belong to the entry (none so far: every page has its own entry).</param>
public sealed record NavigationEntry(string Title, string Href, string Icon, string? Group, Permission? Requires, bool Available, params string[] Also);

/// <summary>
/// The side navigation: the one way between pages. Every page has its own entry, grouped by what it is about, and a page's
/// address starts with its group's (/certificates/acme under TLS, /settings/users under Settings), so the address and the marked entry
/// always agree. A page's entry is the one with the longest address that its own address starts with.
/// </summary>
public static class DashboardNavigation
{
    public const string TrafficGroup = "Traffic";

    public const string SecurityGroup = "Security";

    public const string TlsGroup = "TLS";

    public const string InsightGroup = "Insight";

    public const string SettingsGroup = "Settings";

    public static IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new("Overview", DashboardRoutes.Home, Icons.Material.Outlined.SpaceDashboard, null, null, true),
        new("Hosts", DashboardRoutes.Hosts, Icons.Material.Outlined.Language, TrafficGroup, Permission.ConfigRead, true),
        new("Upstreams", DashboardRoutes.Upstreams, Icons.Material.Outlined.Hub, TrafficGroup, Permission.ConfigRead, true),
        new("Profiles", DashboardRoutes.Profiles, Icons.Material.Outlined.Layers, TrafficGroup, Permission.ConfigRead, true),
        new("Security overview", DashboardRoutes.Security, Icons.Material.Outlined.Shield, SecurityGroup, Permission.ConfigRead, true),
        new("Rate limits", DashboardRoutes.RateLimits, Icons.Material.Outlined.Speed, SecurityGroup, Permission.ConfigRead, true),
        new("Access lists", DashboardRoutes.AccessLists, Icons.Material.Outlined.Lock, SecurityGroup, Permission.ConfigRead, true),
        new("Rules", DashboardRoutes.RuleSets, Icons.Material.Outlined.Rule, SecurityGroup, Permission.ConfigRead, true),
        new("Sign-in", DashboardRoutes.AuthProviders, Icons.Material.Outlined.VerifiedUser, SecurityGroup, Permission.ConfigRead, true),
        new("Certificates", DashboardRoutes.Certificates, Icons.Material.Outlined.WorkspacePremium, TlsGroup, Permission.CertificatesRead, true),
        new("Local CA", DashboardRoutes.LocalCa, Icons.Material.Outlined.AccountTree, TlsGroup, Permission.CertificatesRead, true),
        new("ACME accounts", DashboardRoutes.AcmeAccounts, Icons.Material.Outlined.Badge, TlsGroup, Permission.ProvidersRead, true),
        new("DNS providers", DashboardRoutes.DnsProviders, Icons.Material.Outlined.Dns, TlsGroup, Permission.ProvidersRead, true),
        new("Analytics", DashboardRoutes.Analytics, Icons.Material.Outlined.Insights, InsightGroup, Permission.MetricsRead, true),
        new("Simulator", DashboardRoutes.Simulator, Icons.Material.Outlined.Science, InsightGroup, Permission.Simulate, true),
        new("History", DashboardRoutes.History, Icons.Material.Outlined.History, InsightGroup, Permission.ConfigRead, true),
        new("Audit", DashboardRoutes.Audit, Icons.Material.Outlined.FactCheck, InsightGroup, Permission.AuditRead, true),
        new("Logs", DashboardRoutes.Logs, Icons.Material.Outlined.Article, InsightGroup, Permission.LogsRead, true),
        new("Live tail", DashboardRoutes.LiveTail, Icons.Material.Outlined.Sensors, InsightGroup, Permission.LogsRead, true),
        new("Global defaults", SettingsRoutes.Defaults, Icons.Material.Outlined.Tune, SettingsGroup, Permission.ConfigRead, true),
        new("Users and roles", SettingsRoutes.Users, Icons.Material.Outlined.Group, SettingsGroup, Permission.UsersRead, true),
        new("Notifications", SettingsRoutes.Notifications, Icons.Material.Outlined.NotificationsActive, SettingsGroup, Permission.ProvidersRead, true),
        new("Notification history", SettingsRoutes.NotificationHistory, Icons.Material.Outlined.MarkEmailRead, SettingsGroup, Permission.ProvidersRead, true),
        new("Export and import", SettingsRoutes.Transfer, Icons.Material.Outlined.ImportExport, SettingsGroup, Permission.ConfigRead, true),
        new("API tokens", SettingsRoutes.Tokens, Icons.Material.Outlined.Key, SettingsGroup, Permission.TokensOwn, true),
        new("Backup", SettingsRoutes.Backup, Icons.Material.Outlined.Backup, SettingsGroup, Permission.BackupCreate, true),
    ];

    /// <summary>The entries <paramref name="mayView"/> allows, in order.</summary>
    public static IEnumerable<NavigationEntry> VisibleTo(Func<Permission, bool> mayView)
    {
        ArgumentNullException.ThrowIfNull(mayView);
        return Entries.Where(entry => entry.Requires is not { } permission || mayView(permission));
    }

    /// <summary>The entry a page at <paramref name="path"/> belongs to: the one with the longest address the path starts with.</summary>
    public static NavigationEntry? ActiveFor(string path, IEnumerable<NavigationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(entries);
        NavigationEntry? best = null;
        var length = -1;
        foreach (var entry in entries)
        {
            foreach (var address in entry.Also.Prepend(entry.Href))
            {
                if (Matches(path, address) && address.Length > length)
                {
                    best = entry;
                    length = address.Length;
                }
            }
        }

        return best;
    }

    /// <summary>Whether a page at <paramref name="path"/> is <paramref name="address"/> or below it (the home page only itself).</summary>
    public static bool Matches(string path, string address)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(address);
        return address == DashboardRoutes.Home
            ? path == DashboardRoutes.Home
            : path.Equals(address, StringComparison.OrdinalIgnoreCase) || path.StartsWith(address + "/", StringComparison.OrdinalIgnoreCase);
    }
}
