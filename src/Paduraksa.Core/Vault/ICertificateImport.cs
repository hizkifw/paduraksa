using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// Takes an uploaded certificate together with its private key straight into the vault (PLAN §8.2, §9): the stream goes
/// in, public facts come out, and the key is never returned. The upload endpoint streams the request body into
/// <see cref="ImportAsync"/> without buffering it in any other layer (the dashboard posts to that endpoint rather than
/// through the Blazor circuit).
/// </summary>
public interface ICertificateImport
{
    /// <summary>
    /// Imports the certificate chain and private key in <paramref name="pemOrPfx"/> as <paramref name="id"/>. On an
    /// existing <paramref name="id"/> it replaces the certificate atomically, like <see cref="ICertificateIssuance.AttachChainAsync"/>
    /// (key and chain swap together, a failed import leaves the old pair serving), so an uploaded certificate can be
    /// renewed without breaking the Specific bindings that name it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format is detected. PEM must hold certificate blocks and a <b>PKCS#8</b> key block, plain (<c>PRIVATE KEY</c>)
    /// or encrypted (<c>ENCRYPTED PRIVATE KEY</c>); traditional <c>RSA PRIVATE KEY</c> and <c>EC PRIVATE KEY</c> blocks and
    /// legacy OpenSSL-encrypted PEM are refused (<see cref="CertificateImportFailure.UnsupportedKeyFormat"/>; users convert
    /// them with <c>openssl pkcs8 -topk8</c>). Otherwise the data is read as PFX/PKCS#12, under the framework's
    /// <c>Pkcs12LoaderLimits</c> (set to the vault's own strict limits, never the permissive defaults). The first certificate whose
    /// public key matches the key is the leaf, the rest are its chain.
    /// </para>
    /// <para>
    /// <paramref name="password"/> decrypts an encrypted PEM key or the PFX; pass an empty value if there is none. At most
    /// <see cref="CertificateImportLimits.MaxBytes"/> are read.
    /// </para>
    /// </remarks>
    /// <exception cref="CertificateImportException">
    /// Anything wrong with the upload: too large, unreadable, wrong password, unsupported key format, no matching key. The
    /// vault maps its internal cryptographic and argument exceptions to this type, so none of them reach the caller.
    /// </exception>
    ValueTask<CertificateInfo> ImportAsync(
        CertificateId id, Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken cancellationToken);
}
