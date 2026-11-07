using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Acme.Fakes;

/// <summary>
/// An <see cref="IJwsSigner"/> with real ES256, ES384 or RS256 keys held in the test process, standing in for the vault. It
/// signs what it is given and refuses a header whose <c>alg</c> differs from the key's, as the real one must. Safe for
/// parallel signing.
/// </summary>
internal sealed class FakeJwsSigner : IJwsSigner
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AcmeAccountId, (JwsAlgorithm Algorithm, AsymmetricAlgorithm Key)> _keys = [];
    private int _signCount;

    public int SignCount => Volatile.Read(ref _signCount);

    public ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(AcmeAccountId id, JwsAlgorithm algorithm, CancellationToken cancellationToken)
    {
        AsymmetricAlgorithm key = algorithm switch
        {
            JwsAlgorithm.ES256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
            JwsAlgorithm.ES384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            _ => RSA.Create(2048),
        };
        lock (_gate)
        {
            _keys.Add(id, (algorithm, key));
        }

        return ValueTask.FromResult(InfoOf(algorithm, key));
    }

    public ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_keys.TryGetValue(id, out var entry) ? InfoOf(entry.Algorithm, entry.Key) : null);
        }
    }

    public ValueTask<JwsFlattened> SignAsync(
        AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        (JwsAlgorithm Algorithm, AsymmetricAlgorithm Key) entry;
        lock (_gate)
        {
            entry = _keys.TryGetValue(id, out var found) ? found : throw new KeyNotFoundException();
        }

        using (var header = JsonDocument.Parse(protectedHeader))
        {
            if (header.RootElement.GetProperty("alg").GetString() != entry.Algorithm.ToName())
            {
                throw new ArgumentException("alg is not the key's algorithm.");
            }
        }

        Interlocked.Increment(ref _signCount);
        var protectedPart = Base64Url.EncodeToString(protectedHeader.Span);
        var payloadPart = Base64Url.EncodeToString(payload.Span);
        var input = Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}");
        var signature = entry.Key switch
        {
            ECDsa ecdsa => ecdsa.SignData(input, entry.Algorithm == JwsAlgorithm.ES256 ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA384),
            RSA rsa => rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new InvalidOperationException(),
        };
        return ValueTask.FromResult(new JwsFlattened(protectedPart, payloadPart, Base64Url.EncodeToString(signature)));
    }

    public ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_keys.Remove(id));
        }
    }

    /// <summary>The RFC 7638 thumbprint: SHA-256 of the required members in lexicographic order, no whitespace.</summary>
    public static string ThumbprintOf(JsonWebKeyPublic jwk) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(jwk.Kty == "RSA"
            ? $"{{\"e\":\"{jwk.E}\",\"kty\":\"RSA\",\"n\":\"{jwk.N}\"}}"
            : $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"{jwk.Kty}\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}")));

    private static AcmeAccountKeyInfo InfoOf(JwsAlgorithm algorithm, AsymmetricAlgorithm key)
    {
        JsonWebKeyPublic jwk;
        if (key is ECDsa ecdsa)
        {
            var p = ecdsa.ExportParameters(includePrivateParameters: false);
            jwk = new JsonWebKeyPublic(
                "EC", algorithm == JwsAlgorithm.ES256 ? "P-256" : "P-384", Base64Url.EncodeToString(p.Q.X!), Base64Url.EncodeToString(p.Q.Y!), null, null);
        }
        else
        {
            var p = ((RSA)key).ExportParameters(includePrivateParameters: false);
            jwk = new JsonWebKeyPublic("RSA", null, null, null, Base64Url.EncodeToString(p.Modulus!), Base64Url.EncodeToString(p.Exponent!));
        }

        return new AcmeAccountKeyInfo(algorithm, jwk, ThumbprintOf(jwk));
    }
}

/// <summary>An <see cref="IMacKey"/> over a real HMAC key.</summary>
internal sealed class FakeMacKey(byte[] secret, MacAlgorithm algorithm = MacAlgorithm.HmacSha256) : IMacKey
{
    public MacAlgorithm Algorithm => algorithm;

    public int TagLength => algorithm switch
    {
        MacAlgorithm.HmacSha256 => 32,
        MacAlgorithm.HmacSha384 => 48,
        _ => 64,
    };

    public int Sign(ReadOnlySpan<byte> data, Span<byte> tag) => algorithm switch
    {
        MacAlgorithm.HmacSha256 => HMACSHA256.HashData(secret, data, tag),
        MacAlgorithm.HmacSha384 => HMACSHA384.HashData(secret, data, tag),
        _ => HMACSHA512.HashData(secret, data, tag),
    };

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
    {
        Span<byte> expected = stackalloc byte[64];
        var written = Sign(data, expected);
        return CryptographicOperations.FixedTimeEquals(expected[..written], tag);
    }
}

/// <summary>An <see cref="IMacKeySource"/> that opens one secret as <see cref="SecretKind.EabHmacKey"/>.</summary>
internal sealed class FakeMacKeySource(SecretId id, IMacKey key) : IMacKeySource
{
    public ValueTask<IMacKey?> OpenAsync(SecretId requested, SecretKind expected, CancellationToken cancellationToken) =>
        ValueTask.FromResult(requested == id && expected == SecretKind.EabHmacKey ? key : null);
}
