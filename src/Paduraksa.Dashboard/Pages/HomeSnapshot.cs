using System.Globalization;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Certificates;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.Pages;

/// <summary>
/// What the overview shows, read once per page load and carried from the server's first render into the circuit (so the page does not
/// flash back to its loading state when it becomes interactive). Display data only; a part is null when the user may not read it.
/// </summary>
/// <param name="Revision">The latest revision's number, or null before the first change (or without <c>config:read</c>).</param>
/// <param name="Hosts">How many hosts the config has.</param>
/// <param name="Upstreams">How many upstreams it has.</param>
/// <param name="Certificates">How many certificates the inventory holds.</param>
/// <param name="Expiring">The certificates that expire within <see cref="ExpiryHorizonDays"/> days (or already have), soonest first.</param>
/// <param name="ExpiringInUse">Whether <paramref name="Expiring"/> holds only the certificates the config uses (the user may read it).</param>
public sealed record HomeSnapshot(long? Revision, int? Hosts, int? Upstreams, int? Certificates, IReadOnlyList<ExpiringCertificate>? Expiring, bool ExpiringInUse = false)
{
    /// <summary>"Soon", for a certificate.</summary>
    public const int ExpiryHorizonDays = 30;

    /// <summary>How many hosts the overview lists (the ones that need attention first).</summary>
    public const int HostLines = 6;

    /// <summary>What needs the person, most serious first, each with where to fix it.</summary>
    public IReadOnlyList<AttentionItem> Attention { get; init; } = [];

    /// <summary>The first hosts, those that need attention first.</summary>
    public IReadOnlyList<HostLine>? Lines { get; init; }

    /// <summary>The hosts that serve, and those switched off.</summary>
    public int Online { get; init; }

    public int Off { get; init; }

    /// <summary>The latest revisions, newest first.</summary>
    public IReadOnlyList<RecentChange>? Recent { get; init; }

    /// <summary>
    /// The active certificates from <paramref name="inventory"/> that end within the horizon (or have ended). With <paramref name="document"/>,
    /// only those a host (or the default) uses, chosen as the certificates page does (<see cref="CertificateUsage"/>).
    /// </summary>
    public static IReadOnlyList<ExpiringCertificate> ExpiringOf(IReadOnlyList<CertificateSummary> inventory, ConfigDocument? document, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        DateTimeOffset horizon = now.AddDays(ExpiryHorizonDays);
        IReadOnlyDictionary<CertificateId, IReadOnlyList<CertificateUse>>? uses = document is null ? null : CertificateUsage.Of(document, inventory, now);
        return
        [
            .. inventory
                .Where(certificate => certificate.Status == CertificateStatus.Active && certificate.NotAfter <= horizon)
                .Where(certificate => uses is null || uses.ContainsKey(certificate.Id))
                .OrderBy(certificate => certificate.NotAfter)
                .Select(certificate => new ExpiringCertificate(
                    certificate.Label ?? (certificate.Names.Count > 0 ? certificate.Names[0] : certificate.Subject),
                    certificate.Names.Count,
                    CertificateDisplay.Label(CertificateDisplay.OriginOf(certificate.Source)),
                    certificate.NotAfter)
                {
                    Id = certificate.Id,
                }),
        ];
    }

    /// <summary>
    /// What needs the person: hosts the live config warns about or whose
    /// certificate is a problem, upstreams a host uses with no destination switched on, certificates that failed, and certificates in use
    /// that end soon. Each with the drawer that fixes it.
    /// </summary>
    public static IReadOnlyList<AttentionItem> AttentionOf(
        ConfigDocument? document, IReadOnlyList<HostRow>? hosts, IReadOnlyList<CertificateSummary>? certificates, IReadOnlyList<ExpiringCertificate>? expiring, DateTimeOffset now)
    {
        List<AttentionItem> items = [];
        if (certificates is not null)
        {
            foreach (CertificateSummary failed in certificates.Where(c => c.Status == CertificateStatus.Failed))
            {
                items.Add(new AttentionItem(Tone.Error, $"{HostOverview.CertificateName(failed)} failed to issue",
                    failed.StatusMessage ?? "The CA refused it; the scheduler tries again.", DashboardRoutes.Certificate(failed.Id), "Open certificate"));
            }
        }

        foreach (ExpiringCertificate soon in expiring ?? [])
        {
            TimeSpan left = soon.NotAfter - now;
            string when = left <= TimeSpan.Zero ? "has expired" : left.TotalDays < 1 ? "expires today" : $"expires in {Plural((int)left.TotalDays, "day", "days")}";
            items.Add(new AttentionItem(left <= TimeSpan.FromDays(7) ? Tone.Error : Tone.Warning, $"{soon.Name} {when}",
                $"A {soon.Source} certificate in use. Renew it, or check why it hasn't renewed on its own.",
                soon.Id is { } id ? DashboardRoutes.Certificate(id) : DashboardRoutes.Certificates, "Open certificate"));
        }

        foreach (HostRow row in hosts ?? [])
        {
            if (row.Health == Tone.Warning)
            {
                string detail = row.Problems.Count > 0 ? string.Join(" ", row.Problems) : row.Certificate?.Detail ?? "Needs attention.";
                items.Add(new AttentionItem(Tone.Warning, HostName(row.Host), detail, DashboardRoutes.Host(row.Host.Id), "Open host"));
            }
        }

        if (document is not null)
        {
            foreach (UpstreamConfig upstream in document.Upstreams)
            {
                if (!upstream.Destinations.Any(d => d.Enabled) && HostOverview.UsesOf(document, upstream.Id).Any(u => u.Host.Enabled))
                {
                    items.Add(new AttentionItem(Tone.Error, $"{upstream.Name} has no destination switched on",
                        "The hosts that use it answer 502 until one is.", DashboardRoutes.Upstream(upstream.Id), "Open upstream"));
                }
            }
        }

        return [.. items.OrderBy(i => i.Tone == Tone.Error ? 0 : 1)];
    }

    /// <summary>The hosts as the overview lists them: those that need attention first, then by name.</summary>
    public static IReadOnlyList<HostLine> LinesOf(IReadOnlyList<HostRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return
        [
            .. rows
                .OrderBy(r => r.Health == Tone.Warning ? 0 : r.Health == Tone.Default ? 2 : 1)
                .Take(HostLines)
                .Select(r => new HostLine(
                    HostName(r.Host),
                    DashboardRoutes.Host(r.Host.Id),
                    r.Destinations.Count == 0 ? string.Empty
                        : $"{r.Destinations[0].Label}{(r.Destinations[0].Detail is { } detail ? $"  {detail}" : string.Empty)}{(r.Destinations.Count > 1 ? $" +{r.Destinations.Count - 1}" : string.Empty)}",
                    r.Health == Tone.Default ? Tone.Default : r.Health == Tone.Warning ? Tone.Warning : Tone.Success,
                    r.Health == Tone.Default ? "Off" : r.Health == Tone.Warning ? "Attention" : "Online")),
        ];
    }

    private static string HostName(HostConfig host) => host.Names.Count > 0 ? host.Names[0] : "Unnamed host";

    private static string Plural(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}

/// <summary>One certificate on the overview's expiry list.</summary>
/// <param name="Name">Its label, else its first name.</param>
/// <param name="NameCount">How many names it covers.</param>
/// <param name="Source">Where it came from, as the certificates page says it (ACME, Uploaded, Local CA, ...).</param>
/// <param name="NotAfter">When it stops being valid.</param>
public sealed record ExpiringCertificate(string Name, int NameCount, string Source, DateTimeOffset NotAfter)
{
    /// <summary>The certificate, for its drawer.</summary>
    public CertificateId? Id { get; init; }
}

/// <summary>Something that needs the person, and where to fix it.</summary>
/// <param name="Tone">How serious: an error (something is broken) or a warning (something will break, or isn't as it should be).</param>
/// <param name="Title">What, in a line.</param>
/// <param name="Detail">Why, and what to do.</param>
/// <param name="Href">The drawer that fixes it.</param>
/// <param name="Action">The link's text.</param>
public sealed record AttentionItem(Tone Tone, string Title, string Detail, string Href, string Action);

/// <summary>A host on the overview: its name and address, where its requests go, and its status.</summary>
public sealed record HostLine(string Name, string Href, string Destination, Tone Tone, string Status);

/// <summary>A recent revision on the overview.</summary>
public sealed record RecentChange(long Seq, string Title, string Who, DateTimeOffset At, string Href);
