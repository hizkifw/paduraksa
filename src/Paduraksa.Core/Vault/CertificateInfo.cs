namespace Paduraksa.Core.Vault;

/// <summary>The public facts about a certificate, as the vault reports them after it creates, imports or completes
/// one.</summary>
/// <remarks>
/// Equality is by reference for the list members (the usual record equality over <see cref="IReadOnlyList{T}"/>), so two
/// infos describing the same certificate are not <c>==</c>; compare <see cref="Sha256Fingerprint"/> instead.
/// </remarks>
/// <param name="Subject">
/// The subject distinguished name, RFC 4514 style (e.g. <c>CN=example.com</c>), or the empty string. Certificates the
/// vault creates carry a common name only when the first name is a DNS name of at most 64 characters (the RFC 5280
/// limit); otherwise (a longer name, or an IP-only certificate) the subject is empty and the SANs carry the identity.
/// </param>
/// <param name="SubjectAlternativeNames">DNS names and IP addresses from the SAN extension, lowercase, in certificate order.</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="Issuer">The issuer distinguished name, RFC 4514 style.</param>
/// <param name="SerialNumber">
/// The serial number exactly as the DER INTEGER's content bytes, as uppercase hex without separators: a leading
/// <c>00</c> byte (added when the high bit is set, so the integer stays positive) is kept. This is the form RFC 9773
/// needs for the ARI certificate ID.
/// </param>
/// <param name="AuthorityKeyIdentifier">
/// The keyIdentifier of the Authority Key Identifier extension as lowercase hex, or <see langword="null"/> if the
/// certificate has none. With <paramref name="SerialNumber"/> it makes the RFC 9773 ARI certificate ID:
/// <c>base64url(AKI keyIdentifier) "." base64url(serial content bytes)</c>.
/// </param>
/// <param name="Sha256Fingerprint">SHA-256 of the leaf's DER encoding as lowercase hex without separators.</param>
/// <param name="KeyAlgorithm">A display label for the public key, e.g. <c>ECDSA P-256</c> or <c>RSA 2048</c>.</param>
/// <param name="ChainIssuers">
/// The issuer distinguished name of every certificate in <paramref name="PublicChainPem"/>, in order: the leaf's issuer
/// first, the issuer of the last certificate (usually the root's name) last. It is what ACME's <c>preferred_chain</c>
/// selection matches against, so the choice can be made and shown from stored metadata alone.
/// </param>
/// <param name="NameConstraints">
/// The permitted subtrees of a CA certificate's name constraints as <c>DNS:domain</c> and <c>IP:network</c> entries; null when it has none.
/// </param>
/// <param name="PublicChainPem">
/// The leaf first, then its intermediates, as concatenated PEM <c>CERTIFICATE</c> blocks. The root is left out, except
/// for a self-signed certificate and the local CA root itself, which are their own root.
/// </param>
public sealed record CertificateInfo(
    string Subject,
    IReadOnlyList<string> SubjectAlternativeNames,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Issuer,
    string SerialNumber,
    string? AuthorityKeyIdentifier,
    string Sha256Fingerprint,
    string KeyAlgorithm,
    IReadOnlyList<string> ChainIssuers,
    string PublicChainPem,
    IReadOnlyList<string>? NameConstraints = null);
