using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// Certificate keys as the vault handles them: generated here, held as PKCS#8 in a <see cref="SecretBuffer"/> while in use and
/// sealed (<see cref="CertificateSeal"/>) at rest. The key objects themselves (<see cref="ECDsa"/>, <see cref="RSA"/>) are
/// created, used and disposed inside the vault's methods and never returned.
/// </summary>
internal static class KeyPairs
{
    private const int Pkcs8ScratchSize = 8192;

    /// <summary>Generates a key of <paramref name="spec"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="spec"/> is not a defined value.</exception>
    public static AsymmetricAlgorithm Generate(KeySpec spec) => spec switch
    {
        KeySpec.EcdsaP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        KeySpec.EcdsaP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
        KeySpec.Rsa2048 => RSA.Create(2048),
        KeySpec.Rsa3072 => RSA.Create(3072),
        KeySpec.Rsa4096 => RSA.Create(4096),
        _ => throw new ArgumentOutOfRangeException(nameof(spec), spec, "Undefined key spec."),
    };

    /// <summary>The key as PKCS#8, in a buffer of exactly its length that zeroes itself on disposal.</summary>
    public static SecretBuffer ExportPkcs8(AsymmetricAlgorithm key)
    {
        using var scratch = SecretBuffer.Allocate(Pkcs8ScratchSize);
        if (!key.TryExportPkcs8PrivateKey(scratch.Fill, out var written))
        {
            throw new CryptographicException("The private key does not fit its buffer.");
        }

        return SecretBuffer.CopyOf(scratch.Span[..written]);
    }

    /// <summary>
    /// Loads a PKCS#8 key of the kind <paramref name="certificate"/>'s public key says it is (ECDSA or RSA). Does not check that
    /// the key belongs to the certificate; <see cref="Matches"/> does.
    /// </summary>
    /// <exception cref="CryptographicException">The key is not that kind, or not PKCS#8, or the certificate's key is neither ECDSA nor RSA.</exception>
    public static AsymmetricAlgorithm Import(ReadOnlySpan<byte> pkcs8, X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        AsymmetricAlgorithm key;
        using (var ecdsaPublic = certificate.GetECDsaPublicKey())
        {
            if (ecdsaPublic is not null)
            {
                key = ECDsa.Create();
                return Imported(key, pkcs8);
            }
        }

        using (var rsaPublic = certificate.GetRSAPublicKey())
        {
            if (rsaPublic is not null)
            {
                key = RSA.Create();
                return Imported(key, pkcs8);
            }
        }

        throw new CryptographicException("The certificate's public key is neither ECDSA nor RSA.");
    }

    /// <summary>A copy of <paramref name="certificate"/> that carries <paramref name="key"/> (the key object stays the caller's to dispose).</summary>
    public static X509Certificate2 CopyWithKey(X509Certificate2 certificate, AsymmetricAlgorithm key) => key switch
    {
        ECDsa ecdsa => certificate.CopyWithPrivateKey(ecdsa),
        RSA rsa => certificate.CopyWithPrivateKey(rsa),
        _ => throw new CryptographicException("Unsupported key type."),
    };

    /// <summary>True if <paramref name="key"/> is the private half of <paramref name="certificate"/>'s public key.</summary>
    public static bool Matches(AsymmetricAlgorithm key, X509Certificate2 certificate) =>
        key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(certificate.PublicKey.ExportSubjectPublicKeyInfo());

    /// <summary>The signature generator for certificates signed with <paramref name="key"/>.</summary>
    public static X509SignatureGenerator Generator(AsymmetricAlgorithm key) => key switch
    {
        ECDsa ecdsa => X509SignatureGenerator.CreateForECDsa(ecdsa),
        RSA rsa => X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
        _ => throw new CryptographicException("Unsupported key type."),
    };

    /// <summary>A request (and so a certificate) signed with <paramref name="key"/>: SHA-256, or SHA-384 for a 384-bit curve.</summary>
    public static CertificateRequest NewRequest(X500DistinguishedName subject, AsymmetricAlgorithm key) => key switch
    {
        ECDsa ecdsa => new CertificateRequest(subject, ecdsa, ecdsa.KeySize >= 384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256),
        RSA rsa => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        _ => throw new CryptographicException("Unsupported key type."),
    };

    /// <summary>The key usage a TLS server certificate with this key needs: signatures, and key encipherment for RSA.</summary>
    public static X509KeyUsageFlags ServerKeyUsage(AsymmetricAlgorithm key) =>
        key is RSA ? X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature;

    private static AsymmetricAlgorithm Imported(AsymmetricAlgorithm key, ReadOnlySpan<byte> pkcs8)
    {
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}

/// <summary>What a certificate key is sealed to (<see cref="EnvelopeCipher"/>): its ID and version, and in the purpose what the key may be used for.</summary>
internal static class CertificateSeal
{
    /// <summary>
    /// The context of a certificate's key. The purpose says whether it is the local CA's signing key, so a leaf's row edited into
    /// the CA's (to make the vault sign with it) fails to open.
    /// </summary>
    public static SealContext ContextOf(CertificateId id, bool isLocalCaRoot, int version) =>
        new(id.ToString(), isLocalCaRoot ? "certificate-key/local-ca" : "certificate-key", version);

    /// <summary>
    /// The context of a staged key: sealed to the certificate and the staged key's ID together (so it can't be moved onto another
    /// certificate's row or another staging) and, in the purpose, to a digest of the names it was staged for (so a row's names
    /// can't be edited to make a chain for other names acceptable).
    /// </summary>
    public static SealContext StagedContextOf(CertificateId certificate, StagedKeyId id, IReadOnlyList<string> names) =>
        new(
            $"{certificate}/{id}",
            "staged-certificate-key/" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', names)))),
            Persistence.StagedKeyRow.SealedVersion);
}
