using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// Creates, replaces and deletes certificates whose keys are born in the vault (PLAN §8.2, §9). Keys are generated
/// inside and never leave: what comes out is a CSR, or public <see cref="CertificateInfo"/>. Deletion lives here too
/// (rather than in a separate interface) because the vault owns the key lifecycle of everything this interface creates;
/// uploads, the other way in, are <see cref="ICertificateImport"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller picks the <see cref="CertificateId"/></b> (<c>CertificateId.New()</c>, the same ID as the certificate's
/// row in the config database), so the vault never needs to call back into Application and an ID is known before the key exists.
/// </para>
/// <para>
/// <b>Names</b> are DNS names (a leading <c>*.</c> wildcard label is allowed) or IP addresses, already normalised by
/// the caller (PLAN §5.1); all become SANs. The subject carries a common name only if the first name is a DNS name of at
/// most 64 characters (the RFC 5280 limit); otherwise, and for an IP-only certificate, the subject is empty.
/// </para>
/// <para>
/// <b>Replacing keeps the ID, and the swap is atomic.</b> A host's Specific binding names a <see cref="CertificateId"/>,
/// so every way of replacing a certificate (<see cref="AttachChainAsync"/>, <see cref="CreateSelfSignedAsync"/>,
/// <see cref="IssueFromLocalCaAsync"/>, and <see cref="ICertificateImport.ImportAsync"/>) on an ID that already has a
/// certificate swaps key and chain together, in one transaction: a concurrent <c>LoadForServingAsync</c> sees the old pair
/// or the new pair, never a mix, and a failed replacement leaves the old pair serving. Any key staged for the ID is
/// discarded by such a replacement.
/// </para>
/// <para>
/// <b>Staging.</b> <see cref="CreateKeyAndCsrAsync"/> stages a new key and returns a <see cref="StagedKeyId"/>;
/// <see cref="AttachChainAsync"/> must present it. Staging again for the same ID discards the earlier staged key, and
/// attaching with the earlier token then throws <see cref="StagedKeyMismatchException"/> instead of pairing a chain with the
/// wrong key, so two overlapping renewals cannot silently invalidate each other.
/// </para>
/// <para>
/// Failures throw: <see cref="KeyNotFoundException"/> for an unknown ID, <see cref="StagedKeyMismatchException"/> for a stale
/// token, <see cref="System.Security.Cryptography.CryptographicException"/> for a chain that does not match the key, and
/// <see cref="ArgumentException"/> for bad names or validity.
/// </para>
/// </remarks>
public interface ICertificateIssuance
{
    /// <summary>
    /// Generates a key of the given kind, stages it for <paramref name="id"/> (which may be new, or an existing
    /// certificate being renewed) and returns a PKCS#10 request for <paramref name="names"/> signed with it. Used by ACME
    /// and by manual CSR workflows.
    /// </summary>
    ValueTask<CsrResult> CreateKeyAndCsrAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken);

    /// <summary>
    /// Pairs the issued chain (PEM, leaf first) with the key staged as <paramref name="stagedKey"/> and makes the pair the
    /// serving certificate of <paramref name="id"/>, atomically. The vault checks that the leaf's public key matches the
    /// staged key, that its names cover the requested ones, that it is valid now (not expired, not valid only later than a
    /// five-minute clock tolerance), and that the chain links up by issuer, key identifier and signature; it rejects the
    /// chain otherwise (the staged key keeps waiting). Returns the leaf's public facts, for the certificate's metadata row.
    /// </summary>
    /// <exception cref="StagedKeyMismatchException">The token is not the current staged key of <paramref name="id"/>.</exception>
    ValueTask<CertificateInfo> AttachChainAsync(
        CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a key and a self-signed certificate for <paramref name="names"/>, valid for <paramref name="validity"/>
    /// from now (capped at 825 days, like a local-CA leaf). On an existing <paramref name="id"/> it replaces the certificate atomically.
    /// </summary>
    ValueTask<CertificateInfo> CreateSelfSignedAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the local CA root, a normal certificate with <paramref name="id"/> (so it gets a Certificates row, expiry
    /// monitoring and a downloadable <see cref="CertificateInfo.PublicChainPem"/> like any other). If the local CA already
    /// exists under this <paramref name="id"/> this returns its info unchanged and ignores the other arguments; if it exists under
    /// another ID the vault throws <see cref="LocalCaExistsException"/> naming it (and carrying its info), since there is one
    /// local CA and its ID names its key. The vault uses that key for signing and nothing else. Rotating or deleting
    /// the CA root is later work (PLAN §9.1).
    /// </summary>
    ValueTask<CertificateInfo> EnsureLocalCaAsync(
        CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a key and a leaf certificate for <paramref name="names"/> signed by the local CA, valid for
    /// <paramref name="validity"/> from now (the vault caps it at the CA's own remaining validity, and at 825 days, the
    /// longest validity Apple's trust store accepts). On an existing
    /// <paramref name="id"/> it re-issues under a new key atomically (the one-click re-issue of PLAN §9).
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no local CA yet (<see cref="EnsureLocalCaAsync"/>).</exception>
    ValueTask<CertificateInfo> IssueFromLocalCaAsync(
        CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the key, chain and any staged key of <paramref name="id"/>, whatever created them. Returns false if
    /// there was nothing. Without this a deleted certificate would leave its key in <c>vault.db</c>. The caller has already
    /// checked that no Specific binding still uses the certificate (PLAN §9.1).
    /// </summary>
    ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken);
}
