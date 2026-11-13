using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// One certificate the vault holds a key for, as the vault describes it: its ID, whether it is the local CA root, and the public
/// facts of its stored chain. Public data only; the key is not here and has no way to get here.
/// </summary>
/// <param name="Id">The ID the vault holds the key under (the certificate's row in the config database, when there is one).</param>
/// <param name="IsLocalCaRoot">This is the local CA root (<see cref="ICertificateIssuance.EnsureLocalCaAsync(CertificateId, KeySpec, TimeSpan, CancellationToken)"/>): its key signs and is never served.</param>
/// <param name="Info">The public facts, exactly as the vault would report them for this chain.</param>
public sealed record VaultCertificate(CertificateId Id, bool IsLocalCaRoot, CertificateInfo Info);

/// <summary>
/// The vault describing what it holds, so an application that lost its record of a certificate can get it
/// back: the config database is lost or restored from an older backup, or a crash left the vault ahead of it (an ACME chain
/// attached whose registration never happened). Read-only and public: no key is opened to answer, and nothing a caller does
/// with the answer can change the vault.
/// </summary>
/// <remarks>
/// A key staged for a CSR (<see cref="ICertificateIssuance.CreateKeyAndCsrAsync"/>) that has no chain yet is not described: it is
/// not a certificate. A certificate that is being renewed is described by the pair that is serving now.
/// </remarks>
public interface ICertificateCatalog
{
    /// <summary>Every certificate the vault holds a key and chain for, in no particular order. Empty when there are none.</summary>
    ValueTask<IReadOnlyList<VaultCertificate>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The certificate held under <paramref name="id"/>; <see langword="null"/> when there is none.</summary>
    ValueTask<VaultCertificate?> DescribeAsync(CertificateId id, CancellationToken cancellationToken);
}
