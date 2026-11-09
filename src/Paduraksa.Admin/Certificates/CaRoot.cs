using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Vault;

namespace Paduraksa.Admin.Certificates;

/// <summary>
/// The local CA root as the download endpoints serve it: its public facts and its two encodings, decoded once. Public data
/// only: never a key, never any other certificate.
/// </summary>
/// <param name="Sha256Fingerprint">SHA-256 of the root's DER, lowercase hex.</param>
/// <param name="Subject">The root's subject distinguished name (RFC 4514).</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="Pem">The root as a PEM <c>CERTIFICATE</c> block, ready to serve.</param>
/// <param name="Der">The root's DER encoding, ready to serve.</param>
public sealed record CaRoot(
    string Sha256Fingerprint,
    string Subject,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    byte[] Pem,
    byte[] Der)
{
    /// <summary>Reads the public facts and encodings from the vault's description of the root.</summary>
    public static CaRoot Of(VaultCertificate root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new CaRoot(
            root.Info.Sha256Fingerprint,
            root.Info.Subject,
            root.Info.NotBefore,
            root.Info.NotAfter,
            Encoding.ASCII.GetBytes(root.Info.PublicChainPem),
            PemToDer(root.Info.PublicChainPem));
    }

    /// <summary>
    /// The DER of the first <c>CERTIFICATE</c> block of a PEM chain. The local CA root is its own root, so it is the only
    /// block; decoding it here keeps the endpoints from loading an <c>X509Certificate2</c> just to re-encode it.
    /// </summary>
    /// <exception cref="FormatException">The text has no <c>CERTIFICATE</c> block or it is not valid base64.</exception>
    public static byte[] PemToDer(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        PemFields fields = PemEncoding.Find(pem);
        string base64 = pem[fields.Base64Data].Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
        return Convert.FromBase64String(base64);
    }
}
