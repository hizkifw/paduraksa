using MudBlazor;

namespace Paduraksa.Dashboard.Setup;

/// <summary>What is worth setting up after the Owner exists (PLAN §12.1), in the order the overview's checklist lists it.</summary>
public enum SetupTask
{
    TwoStep,
    AcmeAccount,
    DnsProvider,
    Certificate,
    FirstHost,
}

/// <summary>One task as the checklist shows it: what it is, why, and the page where it is done.</summary>
public sealed record SetupTaskInfo(SetupTask Task, string Title, string Slug, string Icon, string Hint, string Href);

/// <summary>
/// The tasks after first-run setup. There is no wizard: setup creates the Owner and lands on the overview, whose checklist links each
/// open task to the page that does it, until it is done or dismissed.
/// </summary>
public static class SetupTasks
{
    public static IReadOnlyList<SetupTaskInfo> All { get; } =
    [
        new(SetupTask.TwoStep, "Two-step sign-in", "two-step", Icons.Material.Outlined.PhonelinkLock,
            "A code from your phone on top of your password.", DashboardRoutes.Account),
        new(SetupTask.AcmeAccount, "ACME account", "acme", Icons.Material.Outlined.Badge,
            "For free, browser-trusted certificates from Let's Encrypt.", DashboardRoutes.AcmeAccounts),
        new(SetupTask.DnsProvider, "DNS provider", "dns", Icons.Material.Outlined.Dns,
            "For wildcard certificates (DNS-01).", DashboardRoutes.DnsProviders),
        new(SetupTask.Certificate, "Certificate", "certificate", Icons.Material.Outlined.WorkspacePremium,
            "So your hosts answer over HTTPS.", DashboardRoutes.Certificates),
        new(SetupTask.FirstHost, "First host", "host", Icons.Material.Outlined.Language,
            "Proxy a hostname to a service on your network.", DashboardRoutes.NewHost),
    ];

    public static SetupTaskInfo Info(SetupTask task) => All[(int)task];
}
