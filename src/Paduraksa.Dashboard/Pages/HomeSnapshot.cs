using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;

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
public sealed record HomeSnapshot(long? Revision, int? Hosts, int? Upstreams, int? Certificates, IReadOnlyList<ExpiringCertificate>? Expiring)
{
    /// <summary>"Soon", for a certificate.</summary>
    public const int ExpiryHorizonDays = 30;

    /// <summary>The certificates from <paramref name="inventory"/> that are in use and end within the horizon (or have ended).</summary>
    public static IReadOnlyList<ExpiringCertificate> ExpiringOf(IEnumerable<CertificateSummary> inventory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        DateTimeOffset horizon = now.AddDays(ExpiryHorizonDays);
        return
        [
            .. inventory
                .Where(certificate => certificate.Status == CertificateStatus.Active && certificate.NotAfter <= horizon)
                .OrderBy(certificate => certificate.NotAfter)
                .Select(certificate => new ExpiringCertificate(
                    certificate.Label ?? (certificate.Names.Count > 0 ? certificate.Names[0] : certificate.Subject),
                    certificate.Names.Count,
                    certificate.Source.ToName(),
                    certificate.NotAfter)),
        ];
    }
}

/// <summary>One certificate on the overview's expiry list.</summary>
/// <param name="Name">Its label, else its first name.</param>
/// <param name="NameCount">How many names it covers.</param>
/// <param name="Source">Where it came from (<c>acme</c>, <c>uploaded</c>, <c>local_ca</c>, ...).</param>
/// <param name="NotAfter">When it stops being valid.</param>
public sealed record ExpiringCertificate(string Name, int NameCount, string Source, DateTimeOffset NotAfter);
