using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Paduraksa.Acme;

/// <summary>Reading the public parts of the PEM chains a CA sends. Only certificates: no key is involved.</summary>
public static class AcmePem
{
    private const string CertificateLabel = "CERTIFICATE";

    /// <summary>The DER of every <c>CERTIFICATE</c> block, in order.</summary>
    /// <exception cref="FormatException">There is none, or a block is not valid base64.</exception>
    public static IReadOnlyList<byte[]> CertificateDers(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var result = new List<byte[]>();
        var rest = pem.AsSpan();
        while (PemEncoding.TryFind(rest, out var fields))
        {
            if (rest[fields.Label].SequenceEqual(CertificateLabel))
            {
                result.Add(Convert.FromBase64String(rest[fields.Base64Data].ToString()));
            }

            rest = rest[fields.Location.End..];
        }

        return result.Count > 0 ? result : throw new FormatException("The text contains no CERTIFICATE block.");
    }

    /// <summary>The first certificate's DER: the leaf of a chain, which is what revocation names.</summary>
    public static byte[] LeafDer(string pem) => CertificateDers(pem)[0];

    /// <summary>
    /// The issuer common name of every certificate in the chain, in order (empty string for one without a CN). Matches
    /// what a <c>preferred_chain</c> setting names (Let's Encrypt's: the issuer CN of any certificate in the chain).
    /// </summary>
    public static IReadOnlyList<string> IssuerCommonNames(string pem)
    {
        var names = new List<string>();
        foreach (var der in CertificateDers(pem))
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            names.Add(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
        }

        return names;
    }

    /// <summary>
    /// Parses every <c>CERTIFICATE</c> block with the X.509 loader and returns the chain rebuilt from those blocks alone (other
    /// blocks and text between them are dropped) with the issuer CN of each certificate.
    /// </summary>
    /// <exception cref="FormatException">There is no certificate, or a block is not a valid certificate.</exception>
    internal static (string Pem, IReadOnlyList<string> IssuerCommonNames) NormalizeChain(string pem)
    {
        var rebuilt = new StringBuilder();
        var names = new List<string>();
        foreach (var der in CertificateDers(pem))
        {
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificate(der);
                names.Add(certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true));
            }
            catch (CryptographicException ex)
            {
                throw new FormatException("A CERTIFICATE block is not a valid certificate.", ex);
            }

            rebuilt.Append(PemEncoding.WriteString(CertificateLabel, der)).Append('\n');
        }

        return (rebuilt.ToString(), names);
    }
}
