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
/// <param name="Also">Other addresses that belong to the entry's area (the profiles to the hosts, ACME accounts to the certificates).</param>
public sealed record NavigationEntry(string Title, string Href, string Icon, string? Group, Permission? Requires, bool Available, params string[] Also);

/// <summary>A page's tab within its area (<c>SectionTabs</c>): the pages that belong together, one row of tabs under the header.</summary>
/// <param name="Title">The tab's label.</param>
/// <param name="Href">The page.</param>
/// <param name="Requires">The permission the page needs; the tab is hidden without it.</param>
public sealed record SectionTab(string Title, string Href, Permission? Requires);

/// <summary>The areas whose pages share a row of tabs.</summary>
public enum SectionArea
{
    /// <summary>Hosts, upstreams and the profiles hosts pick.</summary>
    Traffic,

    /// <summary>Certificates and what ordering them needs: ACME accounts and DNS providers.</summary>
    Certificates,

    /// <summary>The instance's settings: global defaults, users and roles, API tokens.</summary>
    Settings,
}

/// <summary>
/// The side navigation and the areas' tabs (PLAN §12). The navigation lists the areas; each area's pages share a row of tabs, so
/// everything a page relates to is one click away (the profiles from the hosts, the ACME accounts and DNS providers from the
/// certificates). A page's area is the entry with the longest address that its own address starts with.
/// </summary>
public static class DashboardNavigation
{
    public static IReadOnlyList<NavigationEntry> Entries { get; } =
    [
        new("Overview", DashboardRoutes.Home, Icons.Material.Outlined.SpaceDashboard, null, null, true),
        new("Hosts", DashboardRoutes.Hosts, Icons.Material.Outlined.Language, "Traffic", Permission.ConfigRead, true, SettingsRoutes.Profiles),
        new("Upstreams", DashboardRoutes.Upstreams, Icons.Material.Outlined.Hub, "Traffic", Permission.ConfigRead, true),
        new("Certificates", DashboardRoutes.Certificates, Icons.Material.Outlined.WorkspacePremium, "Traffic", Permission.CertificatesRead, true,
            SettingsRoutes.AcmeAccounts, SettingsRoutes.DnsProviders),
        new("Simulator", DashboardRoutes.Simulator, Icons.Material.Outlined.Science, "Insight", Permission.Simulate, true),
        new("History", DashboardRoutes.History, Icons.Material.Outlined.History, "Insight", Permission.ConfigRead, true),
        new("Audit", DashboardRoutes.Audit, Icons.Material.Outlined.FactCheck, "Insight", Permission.AuditRead, true),
        new("Settings", DashboardRoutes.Settings, Icons.Material.Outlined.Settings, "System", null, true, DashboardRoutes.Account),
    ];

    /// <summary>The tabs of each area.</summary>
    public static IReadOnlyList<SectionTab> TabsOf(SectionArea area) => area switch
    {
        SectionArea.Traffic =>
        [
            new("Hosts", DashboardRoutes.Hosts, Permission.ConfigRead),
            new("Upstreams", DashboardRoutes.Upstreams, Permission.ConfigRead),
            new("Profiles", SettingsRoutes.Profiles, Permission.ConfigRead),
        ],
        SectionArea.Certificates =>
        [
            new("Certificates", DashboardRoutes.Certificates, Permission.CertificatesRead),
            new("ACME accounts", SettingsRoutes.AcmeAccounts, Permission.ProvidersRead),
            new("DNS providers", SettingsRoutes.DnsProviders, Permission.ProvidersRead),
        ],
        _ =>
        [
            new("All settings", DashboardRoutes.Settings, null),
            new("Global defaults", SettingsRoutes.Defaults, Permission.ConfigRead),
            new("Users and roles", SettingsRoutes.Users, Permission.UsersRead),
            new("API tokens", SettingsRoutes.Tokens, Permission.TokensOwn),
            new("My account", DashboardRoutes.Account, null),
        ],
    };

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
        int length = -1;
        foreach (NavigationEntry entry in entries)
        {
            foreach (string address in entry.Also.Prepend(entry.Href))
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
