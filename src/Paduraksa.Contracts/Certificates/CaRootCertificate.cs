namespace Paduraksa.Contracts.Certificates;

/// <summary>
/// The local CA root as the download endpoints describe it: public data only, so the first-run wizard
/// can show the fingerprint of the certificate the browser is asked to trust, next to the certificate itself.
/// </summary>
/// <param name="Sha256Fingerprint">SHA-256 of the root's DER encoding, lowercase hex without separators.</param>
/// <param name="Subject">The root's subject distinguished name, RFC 4514 style (e.g. <c>CN=Paduraksa Local CA (1a2b3c4d)</c>).</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
public sealed record CaRootCertificate(
    string Sha256Fingerprint,
    string Subject,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter);
