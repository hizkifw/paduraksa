namespace Paduraksa.Core.Vault;

/// <summary>
/// <c>ICertificateIssuance.EnsureLocalCaAsync</c> found a local CA root, but under another <see cref="Paduraksa.Core.Model.CertificateId"/>
/// than the one it was asked to create. There is one local CA, and its ID is the one that names its key.
/// </summary>
/// <param name="existingId">The ID the vault holds the root under.</param>
/// <param name="info">The existing root's public facts, so a caller that lost its record of it can register it again.</param>
public sealed class LocalCaExistsException(Paduraksa.Core.Model.CertificateId existingId, CertificateInfo info)
    : InvalidOperationException($"The local CA already exists as {existingId}.")
{
    /// <summary>The ID the vault holds the root under.</summary>
    public Paduraksa.Core.Model.CertificateId ExistingId { get; } = existingId;

    /// <summary>The existing root's public facts.</summary>
    public CertificateInfo Info { get; } = info;
}
