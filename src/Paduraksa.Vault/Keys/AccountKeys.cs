using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Vault.Keys;

/// <summary>
/// <see cref="IJwsSigner"/>: ACME account keys (PLAN §8.2, §9). The private key is generated here, sealed with the same
/// envelope as a secret (<see cref="EnvelopeCipher"/>, purpose <see cref="PurposeOf"/>, which carries the key's algorithm
/// and so its type, version <see cref="AccountKeyRow.SealedVersion"/>), and used only inside <see cref="SignAsync"/>, which opens it, signs and
/// zeroes it again.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it signs.</b> Only JWS input: it base64url-encodes the caller's header and payload itself, and signs
/// <c>ASCII(Protected || "." || Payload)</c>, so there is no operation that signs caller-chosen bytes as such. The header
/// is parsed just far enough to require exactly one <c>alg</c> member, equal to the key's algorithm (a duplicate <c>alg</c>
/// is refused outright, since a verifier might read the other one), and to refuse <c>crit</c> and <c>b64</c>: ACME uses
/// neither, and they change how a verifier must treat the JWS (critical extensions, unencoded payloads, RFC 7797).
/// </para>
/// <para>
/// ES256 and ES384 signatures are <c>R || S</c> (IEEE P1363, 64 and 96 bytes); RS256 is RSASSA-PKCS1-v1_5 with SHA-256 over a 2048-bit key.
/// </para>
/// </remarks>
internal sealed class JwsSigner(VaultRuntime runtime) : IJwsSigner
{
    /// <summary>
    /// The purpose bound into an account key's AAD: <c>acme-account-key/ES256</c> and so on. The algorithm decides the key type
    /// (EC curve or RSA), so editing it in the database makes the key fail to open instead of signing as another algorithm.
    /// </summary>
    public static string PurposeOf(JwsAlgorithm algorithm) => $"acme-account-key/{algorithm.ToName()}";

    public const int MaxHeaderBytes = 8 * 1024;

    public const int MaxPayloadBytes = 256 * 1024;

    private const int PrivateKeyBufferSize = 4096;

    public async ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(AcmeAccountId id, JwsAlgorithm algorithm, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(algorithm))
        {
            throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Undefined JWS algorithm.");
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);

        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var transaction = await runtime.BeginSealingAsync(context, cancellationToken).ConfigureAwait(false);
            await using var _ = transaction.ConfigureAwait(false);
            if (await context.AccountKeys.AnyAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"The account {id} already has a key.");
            }

            var (jwk, sealedKey) = Generate(runtime.Kek, id, algorithm);
            var row = new AccountKeyRow
            {
                Id = id,
                Algorithm = algorithm,
                Kty = jwk.Kty,
                Crv = jwk.Crv,
                X = jwk.X,
                Y = jwk.Y,
                N = jwk.N,
                E = jwk.E,
                Thumbprint = Jwk.Thumbprint(jwk),
                CreatedAt = runtime.Time.GetUtcNow(),
                Sealed = sealedKey,
            };
            context.AccountKeys.Add(row);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToInfo(row);
        }
    }

    public async ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.AccountKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : ToInfo(row);
        }
    }

    public async ValueTask<JwsFlattened> SignAsync(
        AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (protectedHeader.Length is 0 or > MaxHeaderBytes)
        {
            throw new ArgumentException($"The protected header is 1 to {MaxHeaderBytes} bytes.", nameof(protectedHeader));
        }

        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException($"The payload is at most {MaxPayloadBytes} bytes.", nameof(payload));
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);

        AccountKeyRow row;
        SecretBuffer privateKey;
        using (await runtime.EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                row = await context.AccountKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, cancellationToken).ConfigureAwait(false)
                    ?? throw new KeyNotFoundException($"The account {id} has no key.");
            }

            // Checked before the key is opened: a wrong header costs no key operation at all.
            RequireAlgorithm(protectedHeader.Span, row.Algorithm);
            privateKey = EnvelopeCipher.Open(runtime.Kek, row.Sealed, ContextOf(id, row.Algorithm));
        }

        using (privateKey)
        {
            var protectedText = Base64Url.EncodeToString(protectedHeader.Span);
            var payloadText = Base64Url.EncodeToString(payload.Span);
            var signingInput = Encoding.ASCII.GetBytes($"{protectedText}.{payloadText}");
            var signature = Sign(row.Algorithm, privateKey.Span, signingInput);
            return new JwsFlattened(protectedText, payloadText, Base64Url.EncodeToString(signature));
        }
    }

    public async ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        using var gate = await runtime.EnterAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.AccountKeys.Where(k => k.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
    }

    public static SealContext ContextOf(AcmeAccountId id, JwsAlgorithm algorithm) => new(id.ToString(), PurposeOf(algorithm), AccountKeyRow.SealedVersion);

    /// <summary>Requires the protected header to be a JSON object with exactly one <c>alg</c> member, equal to the key's algorithm.</summary>
    /// <exception cref="ArgumentException">It is not.</exception>
    internal static void RequireAlgorithm(ReadOnlySpan<byte> protectedHeader, JwsAlgorithm algorithm)
    {
        string? alg = null;
        var seen = 0;
        var forbidden = false;
        try
        {
            using var document = JsonDocument.Parse(protectedHeader.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The protected header is not a JSON object.", nameof(protectedHeader));
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("alg"u8))
                {
                    seen++;
                    alg = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
                else if (property.NameEquals("crit"u8) || property.NameEquals("b64"u8))
                {
                    forbidden = true;
                }
            }
        }
        catch (JsonException)
        {
            throw new ArgumentException("The protected header is not valid JSON.", nameof(protectedHeader));
        }

        if (forbidden)
        {
            throw new ArgumentException("The protected header must not use 'crit' or 'b64'.", nameof(protectedHeader));
        }

        if (seen != 1 || !string.Equals(alg, algorithm.ToName(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"The protected header must have exactly one 'alg', and it must be '{algorithm.ToName()}' (the account key's algorithm).", nameof(protectedHeader));
        }
    }

    private static (JsonWebKeyPublic Jwk, byte[] Sealed) Generate(Kek kek, AcmeAccountId id, JwsAlgorithm algorithm)
    {
        using var buffer = SecretBuffer.Allocate(PrivateKeyBufferSize);
        int written;
        JsonWebKeyPublic jwk;
        if (algorithm == JwsAlgorithm.RS256)
        {
            using var rsa = RSA.Create(2048);
            var parameters = rsa.ExportParameters(includePrivateParameters: false);
            jwk = new JsonWebKeyPublic("RSA", null, null, null, Base64Url.EncodeToString(parameters.Modulus!), Base64Url.EncodeToString(parameters.Exponent!));
            if (!rsa.TryExportPkcs8PrivateKey(buffer.Fill, out written))
            {
                throw new CryptographicException("The private key does not fit its buffer.");
            }
        }
        else
        {
            var (curve, name) = algorithm == JwsAlgorithm.ES384 ? (ECCurve.NamedCurves.nistP384, "P-384") : (ECCurve.NamedCurves.nistP256, "P-256");
            using var ecdsa = ECDsa.Create(curve);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            jwk = new JsonWebKeyPublic("EC", name, Base64Url.EncodeToString(parameters.Q.X!), Base64Url.EncodeToString(parameters.Q.Y!), null, null);
            if (!ecdsa.TryExportPkcs8PrivateKey(buffer.Fill, out written))
            {
                throw new CryptographicException("The private key does not fit its buffer.");
            }
        }

        return (jwk, EnvelopeCipher.Seal(kek, buffer.Span[..written], ContextOf(id, algorithm)));
    }

    private static byte[] Sign(JwsAlgorithm algorithm, ReadOnlySpan<byte> pkcs8, byte[] signingInput)
    {
        if (algorithm == JwsAlgorithm.RS256)
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
        var hash = algorithm == JwsAlgorithm.ES384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;
        return ecdsa.SignData(signingInput, hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static AcmeAccountKeyInfo ToInfo(AccountKeyRow row) =>
        new(row.Algorithm, new JsonWebKeyPublic(row.Kty, row.Crv, row.X, row.Y, row.N, row.E), row.Thumbprint);
}

/// <summary>JWK helpers (RFC 7517, RFC 7638) over the public key type of Core.</summary>
internal static class Jwk
{
    /// <summary>
    /// The RFC 7638 thumbprint: SHA-256 over the JSON object of the key's required members only, in lexicographic order,
    /// no whitespace (EC: <c>crv, kty, x, y</c>; RSA: <c>e, kty, n</c>), base64url without padding.
    /// </summary>
    /// <exception cref="ArgumentException">The key type is neither <c>EC</c> nor <c>RSA</c>, or a required member is missing.</exception>
    public static string Thumbprint(JsonWebKeyPublic jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);
        var canonical = jwk.Kty switch
        {
            "EC" when jwk.Crv is { } crv && jwk.X is { } x && jwk.Y is { } y =>
                $$"""{"crv":{{Quote(crv)}},"kty":"EC","x":{{Quote(x)}},"y":{{Quote(y)}}}""",
            "RSA" when jwk.E is { } e && jwk.N is { } n =>
                $$"""{"e":{{Quote(e)}},"kty":"RSA","n":{{Quote(n)}}}""",
            _ => throw new ArgumentException("Not a complete EC or RSA public key.", nameof(jwk)),
        };
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
