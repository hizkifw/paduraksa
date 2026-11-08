using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Certificates;

namespace Paduraksa.Dashboard.Pages;

/// <summary>
/// What the overview shows, read once per page load and carried from the server's first render into the circuit (so the page does not
/// flash back to its loading state when it becomes interactive). Display data only; a count is null when the user may not read it.
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
                    certificate.NotAfter)),
        ];
    }
}

/// <summary>One certificate on the overview's expiry list.</summary>
/// <param name="Name">Its label, else its first name.</param>
/// <param name="NameCount">How many names it covers.</param>
/// <param name="Source">Where it came from, as the certificates page says it (ACME, Uploaded, Local CA, ...).</param>
/// <param name="NotAfter">When it stops being valid.</param>
public sealed record ExpiringCertificate(string Name, int NameCount, string Source, DateTimeOffset NotAfter);
