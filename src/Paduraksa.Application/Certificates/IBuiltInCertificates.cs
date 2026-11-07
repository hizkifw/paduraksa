using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// Certificates the process itself provides, next to the ones in the inventory table: until the vault exists, the
/// placeholder certificate the dev binary serves HTTPS with (PLAN §3). They take part in the inventory like any
/// other certificate (Auto matching, Specific bindings) but have no row, no audit trail and no renewal; the
/// <see cref="IServerCertificateSource"/> loads their serving contexts like everyone else's.
/// </summary>
/// <remarks>The list is read before every compile and may change over time (tests add certificates at run time).</remarks>
public interface IBuiltInCertificates
{
    /// <summary>The certificates as they are now.</summary>
    IReadOnlyList<BuiltInCertificate> Current { get; }
}

/// <summary>A certificate of <see cref="IBuiltInCertificates"/>.</summary>
/// <param name="Id">The certificate's ID, stable across restarts so a stored document can name it.</param>
/// <param name="Info">Its public facts.</param>
/// <param name="IsFallbackDefault">Serves as the default certificate for a config that names none (and for one whose named
/// default is unusable), so the dev binary answers HTTPS out of the box. At most one should be set.</param>
public sealed record BuiltInCertificate(CertificateId Id, CertificateInfo Info, bool IsFallbackDefault = false);
