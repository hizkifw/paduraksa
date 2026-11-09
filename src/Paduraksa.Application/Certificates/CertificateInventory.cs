using System.Diagnostics.CodeAnalysis;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Application.Certificates;

/// <summary>One certificate as the compiler sees it: the names it covers, its validity period and, when it can be
/// served, the preloaded <see cref="ServerCertificate"/>. Carries public facts and the sanctioned serving handle
/// only; never key material.</summary>
/// <param name="Id">The certificate.</param>
/// <param name="Names">The names it covers, normalised: DNS names, <c>*.parent</c> wildcards, IP addresses.</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="Serving">The preloaded certificate; null when it isn't usable, or valid but not loaded (nothing in the
/// config can choose it).</param>
/// <param name="UnusableReason">Why it can't be used (pending, failed, expired, not yet valid, no key in the vault);
/// null when it is valid at the inventory's clock reading.</param>
public sealed record InventoryCertificate(
    CertificateId Id,
    IReadOnlyList<string> Names,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    ServerCertificate? Serving,
    string? UnusableReason)
{
    /// <summary>Valid now and loaded: a binding can choose it.</summary>
    public bool IsUsable => Serving is not null;

    /// <summary>Valid now (active, inside its validity period), whether or not it was loaded.</summary>
    public bool IsValid => UnusableReason is null;
}

/// <summary>The certificate inventory the compiler resolves bindings against: the certificates the process knows
/// about, which of them are valid at the time it was built, and a resolver over those that were preloaded for
/// serving.</summary>
/// <remarks>
/// Certificate changes never mutate an inventory: the next compile gets a new one. The serving contexts inside are the
/// vault's instances (the same instance for an unchanged certificate, so the proxy's certificate table keeps one
/// options object per certificate across compiles).
/// </remarks>
public sealed class CertificateInventory
{
    private readonly Dictionary<CertificateId, InventoryCertificate> _byId;
    private readonly UsableResolver _resolver;

    /// <param name="certificates">Every certificate, usable or not (an ID appearing twice keeps its first entry).</param>
    /// <param name="nextBoundary">When the validity of an active certificate next changes (see <see cref="NextBoundary"/>).</param>
    /// <param name="fallbackDefault">The certificate to serve as the default when the config names none (or its choice
    /// is unusable); ignored unless that certificate is usable. See <see cref="FallbackDefault"/>.</param>
    public CertificateInventory(IReadOnlyList<InventoryCertificate> certificates, CertificateId? fallbackDefault = null, DateTimeOffset? nextBoundary = null)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        _byId = new Dictionary<CertificateId, InventoryCertificate>(certificates.Count);
        List<InventoryCertificate> unique = new(certificates.Count);
        foreach (InventoryCertificate certificate in certificates)
        {
            if (_byId.TryAdd(certificate.Id, certificate))
            {
                unique.Add(certificate);
            }
        }

        Certificates = unique;
        NextBoundary = nextBoundary;
        _resolver = new UsableResolver(_byId);
        FallbackDefault = fallbackDefault is { } id && _byId.TryGetValue(id, out InventoryCertificate? entry) && entry.IsUsable ? id : null;
    }

    /// <summary>No certificates.</summary>
    public static CertificateInventory Empty { get; } = new([]);

    /// <summary>Every certificate the process knows about, in no particular order.</summary>
    public IReadOnlyList<InventoryCertificate> Certificates { get; }

    /// <summary>The default certificate for a config that names none, or whose named one is unusable: the placeholder
    /// until the vault's certificates replace it.</summary>
    public CertificateId? FallbackDefault { get; }

    /// <summary>
    /// The earliest moment after the clock reading this inventory was built at when an active certificate becomes valid
    /// (its <c>NotBefore</c>, less the loader's tolerance) or expires. The inventory is only right until then, so the
    /// config service republishes at that time. Null: nothing changes with the clock.
    /// </summary>
    public DateTimeOffset? NextBoundary { get; }

    /// <summary>The usable certificates by ID, for the snapshot compiler.</summary>
    public IServerCertificateResolver Resolver => _resolver;

    /// <summary>The certificate with this ID, usable or not; false when the inventory doesn't know it.</summary>
    public bool TryGet(CertificateId id, [NotNullWhen(true)] out InventoryCertificate? certificate) => _byId.TryGetValue(id, out certificate);

    private sealed class UsableResolver(Dictionary<CertificateId, InventoryCertificate> byId) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate)
        {
            certificate = byId.TryGetValue(id, out InventoryCertificate? entry) ? entry.Serving : null;
            return certificate is not null;
        }
    }
}
