using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// Which hosts of a config use which certificates, for the dashboard's certificate list and detail: the same selection the
/// compiler makes (a Specific binding, the best certificate per name of every enabled Auto host, the named default), judged against the
/// clock from the inventory's public metadata. A display aid: what is served is the compiler's to decide.
/// </summary>
public static class CertificateUsage
{
    /// <summary>
    /// The uses of every certificate in <paramref name="inventory"/> by <paramref name="document"/> at <paramref name="now"/>. A certificate
    /// no host uses has no entry. Only certificates that are active and inside their validity period are chosen for Auto hosts, as the
    /// compiler does; a Specific binding is listed whatever the certificate's state, since the host names it either way.
    /// </summary>
    public static IReadOnlyDictionary<CertificateId, IReadOnlyList<CertificateUse>> Of(
        ConfigDocument document, IReadOnlyList<CertificateSummary> inventory, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(inventory);
        List<InventoryCertificate> valid =
        [
            .. inventory
                .Where(c => c.Id != AdminCertificate.Id && c.Source != CertificateSource.LocalCaRoot && c.Status == CertificateStatus.Active
                    && now >= c.NotBefore - CertificateInventoryLoader.NotBeforeTolerance && now <= c.NotAfter)
                .Select(c => new InventoryCertificate(c.Id, c.Names, c.NotBefore, c.NotAfter, null, null)),
        ];

        Dictionary<CertificateId, List<CertificateUse>> uses = [];
        if (document.Settings.DefaultCertificateId is { } defaultId)
        {
            Add(uses, defaultId, new CertificateUse(CertificateUseKind.Default));
        }

        foreach (HostConfig host in document.Hosts.Where(h => h.Enabled))
        {
            string display = host.Names.Count > 0 ? host.Names[0] : host.Id.ToString();
            if (host.Certificate is { Mode: CertificateBindingMode.Specific, CertificateId: { } specific })
            {
                Add(uses, specific, new CertificateUse(CertificateUseKind.Specific, host.Id, display));
            }
            else if (host.Certificate.Mode == CertificateBindingMode.Auto)
            {
                foreach (IGrouping<CertificateId, string> chosen in CertificateSelection.ServerNames(host.Names)
                    .Select(name => (Name: name, Best: CertificateSelection.Best(valid, name)))
                    .Where(pick => pick.Best is not null)
                    .GroupBy(pick => pick.Best!.Id, pick => pick.Name))
                {
                    Add(uses, chosen.Key, new CertificateUse(CertificateUseKind.Auto, host.Id, display, [.. chosen]));
                }
            }
        }

        return uses.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<CertificateUse>)pair.Value);
    }

    private static void Add(Dictionary<CertificateId, List<CertificateUse>> uses, CertificateId id, CertificateUse use)
    {
        if (!uses.TryGetValue(id, out List<CertificateUse>? list))
        {
            uses[id] = list = [];
        }

        list.Add(use);
    }
}
