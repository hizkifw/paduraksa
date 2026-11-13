using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// A leaf and its intermediates, as public certificates (no keys), disposed together. The first is the leaf.
/// </summary>
internal sealed class CertificateChain : IDisposable
{
    private readonly List<X509Certificate2> _certificates;

    public CertificateChain(List<X509Certificate2> certificates)
    {
        if (certificates.Count == 0)
        {
            throw new ArgumentException("A chain has at least a leaf.", nameof(certificates));
        }

        _certificates = certificates;
    }

    public X509Certificate2 Leaf => _certificates[0];

    public IReadOnlyList<X509Certificate2> Certificates => _certificates;

    public void Dispose()
    {
        foreach (var certificate in _certificates)
        {
            certificate.Dispose();
        }
    }
}

/// <summary>Reads and writes the public side of certificates: chains as PEM, and the facts the vault reports (<see cref="CertificateInfo"/>).</summary>
internal static class CertificateFacts
{
    /// <summary>The most certificates a chain holds; real chains have two or three.</summary>
    public const int MaxChainLength = 16;

    private const string CertificateLabel = "CERTIFICATE";

    /// <summary>Parses concatenated PEM <c>CERTIFICATE</c> blocks, in order. Text outside the blocks is ignored; other block types are not allowed.</summary>
    /// <exception cref="CryptographicException">There is no certificate, too many, or a block is not a certificate.</exception>
    public static List<X509Certificate2> ParseCertificates(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        List<X509Certificate2> certificates = [];
        try
        {
            var remaining = pem.AsSpan();
            while (PemEncoding.TryFind(remaining, out var fields))
            {
                if (!remaining[fields.Label].SequenceEqual(CertificateLabel))
                {
                    throw new CryptographicException($"Expected only {CertificateLabel} blocks in the chain.");
                }

                if (certificates.Count == MaxChainLength)
                {
                    throw new CryptographicException($"A chain has at most {MaxChainLength} certificates.");
                }

                certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(new string(remaining[fields.Base64Data]))));
                remaining = remaining[fields.Location.End..];
            }

            if (certificates.Count == 0)
            {
                throw new CryptographicException("There is no certificate in the PEM.");
            }

            return certificates;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Dispose(certificates);
            throw new CryptographicException("The PEM chain is malformed.", ex);
        }
        catch
        {
            Dispose(certificates);
            throw;
        }
    }

    /// <summary>
    /// Puts <paramref name="others"/> after <paramref name="leaf"/> in issuing order, drops what does not belong to this chain
    /// (duplicates, strays, intermediates expired at <paramref name="now"/>) and a final self-signed root unless the leaf is
    /// itself that root. A certificate counts as the issuer of the one before only if the names link up, the key identifiers
    /// agree where both certificates carry them, and the issuer's key verifies the child's signature (<see cref="Issues"/>).
    /// The caller keeps ownership of every input; the result holds a new copy of each certificate it kept.
    /// </summary>
    public static CertificateChain Assemble(X509Certificate2 leaf, IEnumerable<X509Certificate2> others, DateTimeOffset now)
    {
        List<X509Certificate2> pool = [.. others.Where(o => !ReferenceEquals(o, leaf) && new DateTimeOffset(o.NotAfter) >= now)];
        List<X509Certificate2> ordered = [leaf];
        var current = leaf;
        while (!IsSelfSigned(current) && ordered.Count < MaxChainLength)
        {
            var next = pool.Find(c => !ordered.Exists(o => o.Thumbprint == c.Thumbprint) && Issues(c, current));
            if (next is null)
            {
                break;
            }

            ordered.Add(next);
            pool.Remove(next);
            current = next;
        }

        if (ordered.Count > 1 && IsSelfSigned(ordered[^1]))
        {
            ordered.RemoveAt(ordered.Count - 1);
        }

        return new CertificateChain([.. ordered.Select(c => X509CertificateLoader.LoadCertificate(c.RawData))]);
    }

    /// <summary>
    /// True if <paramref name="issuer"/> issued <paramref name="child"/>: the issuer name is the issuer's subject, the child's
    /// Authority Key Identifier is the issuer's Subject Key Identifier when both exist, the issuer is a CA, and the issuer's
    /// public key verifies the child's signature (ECDSA or RSA PKCS#1 v1.5 with SHA-2; anything else is not accepted). Time is not
    /// judged here, the caller does that.
    /// </summary>
    public static bool Issues(X509Certificate2 issuer, X509Certificate2 child)
    {
        if (!issuer.SubjectName.RawData.AsSpan().SequenceEqual(child.IssuerName.RawData))
        {
            return false;
        }

        var authority = AuthorityKeyIdentifierOf(child);
        var subjectKey = issuer.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        if (authority is not null && subjectKey is not null && !string.Equals(authority, subjectKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!issuer.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(child.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = reader.ReadEncodedValue();
            var algorithm = reader.ReadSequence().ReadObjectIdentifier();
            var signature = reader.ReadBitString(out var unusedBits);
            if (unusedBits != 0)
            {
                return false;
            }

            return algorithm switch
            {
                "1.2.840.10045.4.3.2" => VerifyEcdsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA256),
                "1.2.840.10045.4.3.3" => VerifyEcdsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA384),
                "1.2.840.10045.4.3.4" => VerifyEcdsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA512),
                "1.2.840.113549.1.1.11" => VerifyRsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA256),
                "1.2.840.113549.1.1.12" => VerifyRsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA384),
                "1.2.840.113549.1.1.13" => VerifyRsa(issuer, tbs.Span, signature, HashAlgorithmName.SHA512),
                _ => false,
            };
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException)
        {
            return false;
        }
    }

    private static bool VerifyEcdsa(X509Certificate2 issuer, ReadOnlySpan<byte> tbs, ReadOnlySpan<byte> signature, HashAlgorithmName hash)
    {
        using var key = issuer.GetECDsaPublicKey();
        return key is not null && key.VerifyData(tbs, signature, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool VerifyRsa(X509Certificate2 issuer, ReadOnlySpan<byte> tbs, ReadOnlySpan<byte> signature, HashAlgorithmName hash)
    {
        using var key = issuer.GetRSAPublicKey();
        return key is not null && key.VerifyData(tbs, signature, hash, RSASignaturePadding.Pkcs1);
    }

    /// <summary>The chain as concatenated PEM blocks, each ending in a newline.</summary>
    public static string ToPem(IEnumerable<X509Certificate2> certificates)
    {
        StringBuilder text = new();
        foreach (var certificate in certificates)
        {
            text.Append(certificate.ExportCertificatePem()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The public facts of a chain (<paramref name="chain"/>'s first certificate is the leaf).</summary>
    public static CertificateInfo InfoOf(CertificateChain chain)
    {
        var leaf = chain.Leaf;
        return new CertificateInfo(
            leaf.SubjectName.Name,
            SubjectAlternativeNames(leaf),
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            leaf.IssuerName.Name,
            SerialNumberOf(leaf),
            AuthorityKeyIdentifierOf(leaf),
            Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)),
            KeyAlgorithmOf(leaf),
            [.. chain.Certificates.Select(c => c.IssuerName.Name)],
            ToPem(chain.Certificates),
            NameConstraintsExtension.Read(leaf) is { } constraints ? NameConstraintsExtension.Describe(constraints) : null);
    }

    /// <summary>DNS names (lowercase) and IP addresses of the SAN extension, in the order they appear in the certificate.</summary>
    public static IReadOnlyList<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        List<string> names = [];
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            try
            {
                var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
                while (reader.HasData)
                {
                    var tag = reader.PeekTag();
                    if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
                    {
                        names.Add(reader.ReadCharacterString(UniversalTagNumber.IA5String, tag).ToLowerInvariant());
                    }
                    else if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 7)
                    {
                        names.Add(new IPAddress(reader.ReadOctetString(tag)).ToString());
                    }
                    else
                    {
                        reader.ReadEncodedValue();
                    }
                }
            }
            catch (Exception ex) when (ex is AsnContentException or ArgumentException)
            {
                throw new CryptographicException("The subject alternative name extension is malformed.", ex);
            }
        }

        return names;
    }

    /// <summary>
    /// The serial number as the DER INTEGER's content bytes in uppercase hex (what RFC 9773 puts in an ARI certificate ID):
    /// <c>SerialNumberBytes</c> is already exactly those bytes, big-endian with the leading <c>00</c> of a positive serial whose high
    /// bit is set.
    /// </summary>
    public static string SerialNumberOf(X509Certificate2 certificate) => Convert.ToHexString(certificate.SerialNumberBytes.Span);

    private static string? AuthorityKeyIdentifierOf(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == "2.5.29.35")
            {
                var identifier = new X509AuthorityKeyIdentifierExtension(extension.RawData, extension.Critical).KeyIdentifier;
                return identifier is { } id ? Convert.ToHexStringLower(id.Span) : null;
            }
        }

        return null;
    }

    private static string KeyAlgorithmOf(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return $"RSA {rsa.KeySize}";
        }

        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null)
        {
            return ecdsa.KeySize is 256 or 384 or 521 ? $"ECDSA P-{ecdsa.KeySize}" : $"ECDSA {ecdsa.KeySize}";
        }

        return certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? string.Empty;
    }

    private static bool IsSelfSigned(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    private static void Dispose(List<X509Certificate2> certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}
