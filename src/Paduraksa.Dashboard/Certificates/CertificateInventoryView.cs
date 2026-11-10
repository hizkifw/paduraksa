using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Certificates;

/// <summary>One certificate as the list shows it: its metadata, where it came from, its ACME order if any, and the hosts that use it.</summary>
public sealed record CertificateRow(CertificateSummary Summary, CertificateOrigin Origin, IReadOnlyList<CertificateUse> Uses, AcmeOrderSummary? Order)
{
    public CertificateId Id => Summary.Id;

    public string Name => CertificateDisplay.DisplayName(Summary);

    /// <summary>The admin listener's own certificate: shown, never changed by hand.</summary>
    public bool IsAdmin => Summary.Id == AdminCertificate.Id;

    public bool IsRoot => Origin == CertificateOrigin.LocalCaRoot;

    public bool IsBuiltIn => Origin == CertificateOrigin.BuiltIn;

    /// <summary>Self-signed and local-CA leaves can be re-issued with a new key under the same ID.</summary>
    public bool CanReissue => !IsAdmin && Origin is CertificateOrigin.SelfSigned or CertificateOrigin.LocalCa;

    public bool CanRenew => Origin == CertificateOrigin.Acme && Order is not null;

    public bool CanDelete => !IsAdmin && !IsBuiltIn;
}

/// <summary>What the certificates page shows: the inventory (with the built-in ones), split into the list and the two managed ones.</summary>
/// <param name="Rows">Every certificate a person manages, newest first.</param>
/// <param name="Admin">The admin listener's certificate, if it is recorded.</param>
/// <param name="Root">The local CA root, if there is one.</param>
/// <param name="UsageKnown">Whether the hosts were read (the user may read the config).</param>
public sealed record CertificateInventoryView(IReadOnlyList<CertificateRow> Rows, CertificateRow? Admin, CertificateRow? Root, bool UsageKnown)
{
    /// <summary>Reads the inventory, the orders and (with <paramref name="config"/>) the hosts that use each certificate.</summary>
    public static async Task<CertificateInventoryView> LoadAsync(
        CertificateService certificates,
        AcmeIssuanceService issuance,
        ConfigService? config,
        IBuiltInCertificates? builtIns,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(issuance);
        List<CertificateSummary> summaries = [.. await certificates.ListAsync(null, cancellationToken).ConfigureAwait(false)];
        HashSet<CertificateId> builtInIds = [];
        foreach (var builtIn in builtIns?.Current ?? [])
        {
            builtInIds.Add(builtIn.Id);
            summaries.Add(new CertificateSummary(
                builtIn.Id, CertificateSource.Uploaded, CertificateStatus.Active, Validity(builtIn.Info.NotBefore, builtIn.Info.NotAfter, now),
                builtIn.Info.SubjectAlternativeNames, builtIn.Info.NotBefore, builtIn.Info.NotAfter, builtIn.Info.Subject, builtIn.Info.Issuer,
                builtIn.Info.SerialNumber, builtIn.Info.Sha256Fingerprint, builtIn.Info.KeyAlgorithm, builtIn.Info.NotBefore, builtIn.Info.NotBefore,
                Label: "Development placeholder", Notes: "Built into this process for development; new in every run."));
        }

        var orders = (await issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(o => o.CertificateId);
        IReadOnlyDictionary<CertificateId, IReadOnlyList<CertificateUse>> uses = new Dictionary<CertificateId, IReadOnlyList<CertificateUse>>();
        if (config is not null)
        {
            var current = await config.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            uses = CertificateUsage.Of(current.Document, summaries, now);
        }

        List<CertificateRow> rows =
        [
            .. summaries
                .Select(s => new CertificateRow(
                    s,
                    builtInIds.Contains(s.Id) ? CertificateOrigin.BuiltIn : CertificateDisplay.OriginOf(s.Source),
                    uses.GetValueOrDefault(s.Id) ?? [],
                    orders.GetValueOrDefault(s.Id)))
                .OrderByDescending(r => r.Summary.CreatedAt)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
        ];
        var admin = rows.FirstOrDefault(r => r.IsAdmin);
        var root = rows.FirstOrDefault(r => r.IsRoot && r.Summary.Status == CertificateStatus.Active)
                   ?? rows.FirstOrDefault(r => r.IsRoot);
        return new CertificateInventoryView([.. rows.Where(r => !r.IsAdmin && !r.IsRoot)], admin, root, config is not null);
    }

    private static CertificateValidity Validity(DateTimeOffset notBefore, DateTimeOffset notAfter, DateTimeOffset now) =>
        now < notBefore ? CertificateValidity.NotYetValid : now > notAfter ? CertificateValidity.Expired : CertificateValidity.Valid;
}
