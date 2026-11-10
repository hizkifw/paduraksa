using System.Buffers.Text;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Vault.Certificates;

/// <summary>
/// <see cref="ICertificateImport"/>: an uploaded certificate and its key go in, public facts come out.
/// </summary>
/// <remarks>
/// <b>Plaintext handling.</b> The upload is read into one pinned buffer (<see cref="SecretBuffer"/>) and a PEM key block is
/// decoded from its bytes into another, never through a <see cref="string"/>; both are zeroed when the import ends. A key is
/// loaded only to prove it matches a certificate and disposed again; what is stored is the PKCS#8 sealed by
/// <see cref="CertificateStore"/>.
///
/// <b>Formats.</b> A PEM upload has certificate blocks and one PKCS#8 key block, plain or encrypted (other block types are
/// ignored, traditional and legacy-encrypted key blocks are refused); anything else is PFX, read under <see cref="Limits"/>.
/// A key that fails to open is <see cref="CertificateImportFailure.WrongPassword"/> when its outer structure is sound
/// (the likeliest reason), otherwise <see cref="CertificateImportFailure.Unreadable"/>. The key derivation of an encrypted key
/// is bounded before it runs (<see cref="MaxKdfIterations"/>) and runs once per key type the upload could belong to (normally
/// once).
///
/// <b>The leaf</b> is the first certificate whose public key matches the key; the others are ordered behind it by issuer and
/// stray ones, expired intermediates and the root are dropped (<see cref="CertificateFacts.Assemble"/>). A leaf is refused
/// without a DNS name or IP address the inventory could use (<see cref="CertificateImportFailure.NoUsableName"/>, judged by
/// the <see cref="ICertificateNameRules"/> the inventory uses), as an RSA key under 2048 bits
/// (<see cref="CertificateImportFailure.WeakKey"/>), or as a CA certificate
/// (<see cref="CertificateImportFailure.CaCertificate"/>). An <b>expired</b> leaf is accepted: the inventory already treats a
/// certificate outside its validity as unusable and republishes at the boundary, and refusing it would stop an admin from
/// storing a certificate before its replacement is due.
///
/// <b>Everything that goes wrong maps to <see cref="CertificateImportException"/></b>, including problems found while reading
/// the certificate's own extensions after parsing; only the database's failures (and cancellation) pass through.
/// </remarks>
internal sealed class CertificateImport(VaultRuntime runtime, CertificateStore store, ICertificateNameRules nameRules) : ICertificateImport
{
    /// <summary>The most iterations of a key-derivation function the vault runs for an uploaded encrypted key or PFX (OpenSSL's default is 2048, tools use up to 100k).</summary>
    public const int MaxKdfIterations = 600_000;

    /// <summary>
    /// The vault's own PKCS#12 limits, strict where the framework's defaults are permissive: a few certificates, one key and
    /// iteration counts well below what makes a hostile upload costly. They are what real tools produce, with room to spare.
    /// </summary>
    public static readonly Pkcs12LoaderLimits Limits = CreateLimits();

    private const int MaxPemBlocks = 64;

    private const string Pbes2 = "1.2.840.113549.1.5.13";

    private const string Pbkdf2 = "1.2.840.113549.1.5.12";

    private static readonly byte[] PemMarker = "-----BEGIN "u8.ToArray();

    public async ValueTask<CertificateInfo> ImportAsync(
        CertificateId id, Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken cancellationToken)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("The certificate ID can't be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(pemOrPfx);

        using var data = await ReadAsync(pemOrPfx, cancellationToken).ConfigureAwait(false);
        using var upload = ParseUpload(data.Span, password.Span);
        using var chain = SelectAndCheck(upload, runtime.Time.GetUtcNow());

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await store.PutAsync(context, id, upload.Pkcs8, chain, isLocalCaRoot: false, "uploaded", cancellationToken).ConfigureAwait(false);
        }
    }

    private static Pkcs12LoaderLimits CreateLimits()
    {
        var limits = new Pkcs12LoaderLimits(Pkcs12LoaderLimits.Defaults)
        {
            MaxCertificates = 32,
            MaxKeys = 1,
            MacIterationLimit = MaxKdfIterations,
            IndividualKdfIterationLimit = MaxKdfIterations,
            TotalKdfIterationLimit = 2 * MaxKdfIterations,
            IgnorePrivateKeys = false,
            IgnoreEncryptedAuthSafes = false,
            AllowDuplicateAttributes = false,
            PreserveStorageProvider = false,
            PreserveKeyName = false,
            PreserveCertificateAlias = false,
            PreserveUnknownAttributes = false,
        };
        limits.MakeReadOnly();
        return limits;
    }

    /// <summary>Reads the whole upload (at most <see cref="CertificateImportLimits.MaxBytes"/>) into a buffer of exactly its length.</summary>
    private static async ValueTask<SecretBuffer> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = SecretBuffer.Allocate(CertificateImportLimits.MaxBytes + 1);
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.FillMemory[length..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > CertificateImportLimits.MaxBytes)
        {
            throw Failure(CertificateImportFailure.TooLarge, $"The upload is larger than {CertificateImportLimits.MaxBytes} bytes.");
        }

        return SecretBuffer.CopyOf(buffer.Span[..length]);
    }

    private static ParsedUpload ParseUpload(ReadOnlySpan<byte> data, ReadOnlySpan<char> password)
    {
        try
        {
            return data.IndexOf(PemMarker) >= 0 ? ParsePem(data, password) : ParsePfx(data, password);
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            throw Failure(CertificateImportFailure.Unreadable, "The upload is not a readable PEM or PKCS#12 file: " + ex.Message);
        }
    }

    /// <summary>
    /// Picks the leaf and its chain and applies the leaf rules. Anything the certificates' own contents make fail here (a malformed
    /// extension, say) is the upload's fault and becomes <see cref="CertificateImportFailure.Unreadable"/>.
    /// </summary>
    private CertificateChain SelectAndCheck(ParsedUpload upload, DateTimeOffset now)
    {
        CertificateChain? chain = null;
        try
        {
            chain = upload.SelectChain(now);
            var leaf = chain.Leaf;
            if (!CertificateFacts.SubjectAlternativeNames(leaf).Any(name => nameRules.Normalize(name) is not null))
            {
                throw Failure(CertificateImportFailure.NoUsableName, "The certificate has no DNS name or IP address in its subject alternative names that Paduraksa can use.");
            }

            using (var rsa = leaf.GetRSAPublicKey())
            {
                if (rsa is { KeySize: < 2048 })
                {
                    throw Failure(CertificateImportFailure.WeakKey, $"The certificate's RSA key is {rsa.KeySize} bits; at least 2048 are required.");
                }
            }

            if (leaf.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
            {
                throw Failure(CertificateImportFailure.CaCertificate, "The certificate is a CA certificate, not a server certificate.");
            }

            // Reads every public fact once, so a certificate that can't be described fails here and not inside the store.
            _ = CertificateFacts.InfoOf(chain);
            return chain;
        }
        catch (Exception ex) when (IsMalformed(ex))
        {
            chain?.Dispose();
            throw Failure(CertificateImportFailure.Unreadable, "The certificate could not be read: " + ex.Message);
        }
        catch
        {
            chain?.Dispose();
            throw;
        }
    }

    private static bool IsMalformed(Exception ex) =>
        ex is CryptographicException or ArgumentException or FormatException or AsnContentException or InvalidOperationException;

    private static ParsedUpload ParsePem(ReadOnlySpan<byte> data, ReadOnlySpan<char> password)
    {
        List<X509Certificate2> certificates = [];
        SecretBuffer? pkcs8 = null;
        SecretBuffer? encrypted = null;
        try
        {
            var blocks = 0;
            var remaining = data;
            while (PemEncoding.TryFindUtf8(remaining, out var fields))
            {
                if (++blocks > MaxPemBlocks)
                {
                    throw Failure(CertificateImportFailure.Unreadable, $"The PEM has more than {MaxPemBlocks} blocks.");
                }

                var label = System.Text.Encoding.ASCII.GetString(remaining[fields.Label]);
                switch (label)
                {
                    case "CERTIFICATE":
                        using (var der = DecodeBase64(remaining[fields.Base64Data]))
                        {
                            certificates.Add(X509CertificateLoader.LoadCertificate(der.Span));
                        }

                        break;
                    case "PRIVATE KEY" or "ENCRYPTED PRIVATE KEY":
                        if (pkcs8 is not null || encrypted is not null)
                        {
                            throw Failure(CertificateImportFailure.Unreadable, "The PEM holds more than one private key.");
                        }

                        using (var der = DecodeBase64(remaining[fields.Base64Data]))
                        {
                            if (label == "PRIVATE KEY")
                            {
                                pkcs8 = SecretBuffer.CopyOf(der.Span);
                            }
                            else
                            {
                                encrypted = SecretBuffer.CopyOf(der.Span);
                            }
                        }

                        break;
                    default:
                        if (label.EndsWith("PRIVATE KEY", StringComparison.Ordinal))
                        {
                            throw Failure(
                                CertificateImportFailure.UnsupportedKeyFormat,
                                $"The PEM key block '{label}' is not PKCS#8. Convert it with: openssl pkcs8 -topk8 -in key.pem -out pkcs8.pem");
                        }

                        break;
                }

                remaining = remaining[fields.Location.End..];
            }

            if (certificates.Count == 0)
            {
                throw Failure(CertificateImportFailure.Unreadable, "The PEM holds no certificate.");
            }

            if (encrypted is not null)
            {
                // Decrypted only now that the certificates are known: they say which key types are possible.
                pkcs8 = DecryptPkcs8(encrypted.Span, password, certificates);
            }

            return new ParsedUpload(certificates, pkcs8 ?? throw Failure(CertificateImportFailure.NoMatchingKey, "The PEM holds no private key."));
        }
        catch
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            pkcs8?.Dispose();
            throw;
        }
        finally
        {
            encrypted?.Dispose();
        }
    }

    private static ParsedUpload ParsePfx(ReadOnlySpan<byte> data, ReadOnlySpan<char> password)
    {
        X509Certificate2Collection collection;
        try
        {
            collection = X509CertificateLoader.LoadPkcs12Collection(
                data, password, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable, Limits);
        }
        catch (Pkcs12LoadLimitExceededException ex)
        {
            throw Failure(CertificateImportFailure.Unreadable, "The PKCS#12 file exceeds the limits the vault accepts: " + ex.Message);
        }
        catch (CryptographicException) when (HasPfxStructure(data))
        {
            throw Failure(CertificateImportFailure.WrongPassword, "The PKCS#12 password is missing or wrong.");
        }

        List<X509Certificate2> certificates = [.. collection.Cast<X509Certificate2>()];
        SecretBuffer? pkcs8 = null;
        try
        {
            foreach (var certificate in certificates.Where(c => c.HasPrivateKey))
            {
                using var key = (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();
                if (key is not null)
                {
                    pkcs8 = KeyPairs.ExportPkcs8(key);
                    break;
                }
            }

            if (certificates.Count == 0)
            {
                throw Failure(CertificateImportFailure.Unreadable, "The PKCS#12 file holds no certificate.");
            }

            return new ParsedUpload(certificates, pkcs8 ?? throw Failure(CertificateImportFailure.NoMatchingKey, "The PKCS#12 file holds no usable private key (RSA or ECDSA)."));
        }
        catch
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            pkcs8?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Decrypts a PKCS#8 <c>ENCRYPTED PRIVATE KEY</c> into a plain PKCS#8 buffer; RSA and ECDSA keys are supported. The
    /// key-derivation cost is bounded first (<see cref="MaxKdfIterations"/>), and the key types tried are only those of the
    /// <paramref name="certificates"/>, so the derivation normally runs once.
    /// </summary>
    private static SecretBuffer DecryptPkcs8(ReadOnlySpan<byte> encrypted, ReadOnlySpan<char> password, IReadOnlyList<X509Certificate2> certificates)
    {
        var iterations = ReadKdfIterations(encrypted);
        if (iterations < 1 || iterations > MaxKdfIterations)
        {
            throw Failure(CertificateImportFailure.Unreadable, $"The private key's password derivation uses {iterations} iterations; the vault accepts at most {MaxKdfIterations}.");
        }

        var creators = CandidateKeyTypes(certificates);
        if (creators.Count == 0)
        {
            throw Failure(CertificateImportFailure.NoMatchingKey, "No certificate in the upload has an RSA or ECDSA key.");
        }

        foreach (var create in creators)
        {
            using var key = create();
            try
            {
                key.ImportEncryptedPkcs8PrivateKey(password, encrypted, out _);
                return KeyPairs.ExportPkcs8(key);
            }
            catch (CryptographicException)
            {
                // Not this key type, or the wrong password; the other type is tried, then the structure (already read) judges.
            }
        }

        throw Failure(CertificateImportFailure.WrongPassword, "The private key's password is missing or wrong (or the key is neither RSA nor ECDSA).");
    }

    /// <summary>
    /// The key types an encrypted key can have, from the public keys of the uploaded certificates: one creator per type present
    /// (ECDSA, then RSA), so a key that can only be one type is decrypted with one derivation. Empty when no certificate has an RSA
    /// or ECDSA key.
    /// </summary>
    internal static List<Func<AsymmetricAlgorithm>> CandidateKeyTypes(IReadOnlyList<X509Certificate2> certificates)
    {
        var hasEc = false;
        var hasRsa = false;
        foreach (var certificate in certificates)
        {
            using (var ec = certificate.GetECDsaPublicKey())
            {
                hasEc |= ec is not null;
            }

            using (var rsa = certificate.GetRSAPublicKey())
            {
                hasRsa |= rsa is not null;
            }
        }

        List<Func<AsymmetricAlgorithm>> creators = [];
        if (hasEc)
        {
            creators.Add(() => ECDsa.Create());
        }

        if (hasRsa)
        {
            creators.Add(() => RSA.Create());
        }

        return creators;
    }

    /// <summary>
    /// The iteration count of the password-based key derivation of an <c>EncryptedPrivateKeyInfo</c>: PBES2 with PBKDF2 (what
    /// <c>openssl pkcs8 -topk8</c> writes) or a PKCS#5 v1 / PKCS#12 PBE scheme.
    /// </summary>
    /// <exception cref="AsnContentException">The structure is not an EncryptedPrivateKeyInfo.</exception>
    /// <exception cref="CertificateImportException">The key derivation is not PBKDF2 (scrypt and the like are not read).</exception>
    private static BigInteger ReadKdfIterations(ReadOnlySpan<byte> der)
    {
        const AsnEncodingRules Rules = AsnEncodingRules.BER;
        AsnDecoder.ReadSequence(der, Rules, out var outerOffset, out var outerLength, out _);
        var info = der.Slice(outerOffset, outerLength);
        AsnDecoder.ReadSequence(info, Rules, out var algorithmOffset, out var algorithmLength, out _);
        var algorithm = info.Slice(algorithmOffset, algorithmLength);
        var scheme = AsnDecoder.ReadObjectIdentifier(algorithm, Rules, out var schemeLength);
        var parameters = algorithm[schemeLength..];

        if (scheme != Pbes2)
        {
            return ReadIterationsAfterSalt(parameters);
        }

        // PBES2-params ::= SEQUENCE { keyDerivationFunc AlgorithmIdentifier, encryptionScheme AlgorithmIdentifier }
        AsnDecoder.ReadSequence(parameters, Rules, out var paramsOffset, out var paramsLength, out _);
        var pbes2 = parameters.Slice(paramsOffset, paramsLength);
        AsnDecoder.ReadSequence(pbes2, Rules, out var kdfOffset, out var kdfLength, out _);
        var kdf = pbes2.Slice(kdfOffset, kdfLength);
        var kdfOid = AsnDecoder.ReadObjectIdentifier(kdf, Rules, out var kdfOidLength);
        return kdfOid == Pbkdf2
            ? ReadIterationsAfterSalt(kdf[kdfOidLength..])
            : throw Failure(CertificateImportFailure.Unreadable, "The private key uses a password derivation other than PBKDF2, which the vault does not read.");
    }

    /// <summary>Reads <c>SEQUENCE { salt, iterationCount INTEGER, ... }</c> and returns the count.</summary>
    private static BigInteger ReadIterationsAfterSalt(ReadOnlySpan<byte> parameters)
    {
        const AsnEncodingRules Rules = AsnEncodingRules.BER;
        AsnDecoder.ReadSequence(parameters, Rules, out var offset, out var length, out _);
        var body = parameters.Slice(offset, length);
        AsnDecoder.ReadEncodedValue(body, Rules, out _, out _, out var saltLength);
        return AsnDecoder.ReadInteger(body[saltLength..], Rules, out _);
    }

    /// <summary>Decodes base64 that may contain line breaks into a zeroing buffer.</summary>
    private static SecretBuffer DecodeBase64(ReadOnlySpan<byte> base64)
    {
        using var compact = SecretBuffer.Allocate(base64.Length);
        var length = 0;
        foreach (var b in base64)
        {
            if (b is not (byte)' ' and not (byte)'\t' and not (byte)'\r' and not (byte)'\n')
            {
                compact.Fill[length++] = b;
            }
        }

        var decoded = SecretBuffer.Allocate(Base64.GetMaxDecodedFromUtf8Length(length));
        try
        {
            if (Base64.DecodeFromUtf8(compact.Span[..length], decoded.Fill, out _, out var written) != System.Buffers.OperationStatus.Done)
            {
                throw new FormatException("Invalid base64 in a PEM block.");
            }

            return SecretBuffer.CopyOf(decoded.Span[..written]);
        }
        finally
        {
            decoded.Dispose();
        }
    }

    /// <summary>True if <paramref name="data"/> starts like a PFX (<c>SEQUENCE { version 3, ... }</c>); read in place, nothing is copied.</summary>
    private static bool HasPfxStructure(ReadOnlySpan<byte> data)
    {
        try
        {
            AsnDecoder.ReadSequence(data, AsnEncodingRules.BER, out var offset, out var length, out _);
            var body = data.Slice(offset, length);
            return AsnDecoder.ReadInteger(body, AsnEncodingRules.BER, out var consumed) == 3 && consumed < body.Length;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static CertificateImportException Failure(CertificateImportFailure reason, string message) => new(reason, message);

    /// <summary>The upload as read: its certificates and the key as plain PKCS#8, all disposed together.</summary>
    private sealed class ParsedUpload(List<X509Certificate2> certificates, SecretBuffer pkcs8) : IDisposable
    {
        public SecretBuffer Pkcs8 { get; } = pkcs8;

        /// <summary>The leaf (the first certificate matching the key) with its chain behind it, as public copies.</summary>
        /// <exception cref="CertificateImportException">No certificate matches the key.</exception>
        public CertificateChain SelectChain(DateTimeOffset now)
        {
            foreach (var candidate in certificates)
            {
                if (Matches(candidate))
                {
                    return CertificateFacts.Assemble(candidate, certificates, now);
                }
            }

            throw Failure(CertificateImportFailure.NoMatchingKey, "No certificate in the upload matches the private key.");
        }

        public void Dispose()
        {
            foreach (var certificate in certificates)
            {
                certificate.Dispose();
            }

            Pkcs8.Dispose();
        }

        private bool Matches(X509Certificate2 candidate)
        {
            try
            {
                using var key = KeyPairs.Import(Pkcs8.Span, candidate);
                return KeyPairs.Matches(key, candidate);
            }
            catch (CryptographicException)
            {
                // A certificate of another key type; not this key's.
                return false;
            }
        }
    }
}
