using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Keys;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Keys;

public sealed class JwsSignerTests : IDisposable
{
    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IJwsSigner Signer => _vault.Get<IJwsSigner>();

    public void Dispose() => _vault.Dispose();

    private static byte[] Header(string alg) => Encoding.UTF8.GetBytes($$"""{"alg":"{{alg}}","nonce":"abc","url":"https://ca.example/acme/new-order","kid":"https://ca.example/acct/1"}""");

    private static bool Verify(AcmeAccountKeyInfo info, JwsFlattened jws)
    {
        var input = Encoding.ASCII.GetBytes($"{jws.Protected}.{jws.Payload}");
        var signature = Base64Url.DecodeFromChars(jws.Signature);
        var jwk = info.Jwk;
        if (info.Algorithm == JwsAlgorithm.RS256)
        {
            using var rsa = RSA.Create(new RSAParameters
            {
                Modulus = Base64Url.DecodeFromChars(jwk.N),
                Exponent = Base64Url.DecodeFromChars(jwk.E),
            });
            return rsa.VerifyData(input, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        var curve = info.Algorithm == JwsAlgorithm.ES384 ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256;
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = curve,
            Q = new ECPoint { X = Base64Url.DecodeFromChars(jwk.X), Y = Base64Url.DecodeFromChars(jwk.Y) },
        });
        return ecdsa.VerifyData(
            input, signature, info.Algorithm == JwsAlgorithm.ES384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    [Theory]
    [InlineData(JwsAlgorithm.ES256, 64)]
    [InlineData(JwsAlgorithm.ES384, 96)]
    [InlineData(JwsAlgorithm.RS256, 256)]
    public async Task SignedJwsVerifiesAgainstThePublicJwk(JwsAlgorithm algorithm, int signatureLength)
    {
        var id = AcmeAccountId.New();
        var info = await Signer.CreateAccountKeyAsync(id, algorithm, Ct);
        var header = Header(algorithm.ToName());
        var payload = """{"termsOfServiceAgreed":true}"""u8.ToArray();

        var jws = await Signer.SignAsync(id, header, payload, Ct);

        Assert.Equal(Base64Url.EncodeToString(header), jws.Protected);
        Assert.Equal(Base64Url.EncodeToString(payload), jws.Payload);
        Assert.Equal(signatureLength, Base64Url.DecodeFromChars(jws.Signature).Length);
        Assert.True(Verify(info, jws));
        Assert.DoesNotContain('=', jws.Signature + jws.Protected + jws.Payload);
    }

    [Fact]
    public async Task PostAsGetHasAnEmptyPayloadThatStillVerifies()
    {
        var id = AcmeAccountId.New();
        var info = await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        var jws = await Signer.SignAsync(id, Header("ES256"), ReadOnlyMemory<byte>.Empty, Ct);

        Assert.Equal("", jws.Payload);
        Assert.True(Verify(info, jws));
    }

    [Fact]
    public async Task ATamperedPayloadOrAnotherKeyDoesNotVerify()
    {
        var a = AcmeAccountId.New();
        var b = AcmeAccountId.New();
        var infoA = await Signer.CreateAccountKeyAsync(a, JwsAlgorithm.ES256, Ct);
        var infoB = await Signer.CreateAccountKeyAsync(b, JwsAlgorithm.ES256, Ct);
        var jws = await Signer.SignAsync(a, Header("ES256"), "{}"u8.ToArray(), Ct);

        Assert.True(Verify(infoA, jws));
        Assert.False(Verify(infoB, jws));
        Assert.False(Verify(infoA, jws with { Payload = Base64Url.EncodeToString("{\"x\":1}"u8) }));
    }

    [Fact]
    public async Task SigningTwiceGivesDifferentEcdsaSignaturesBothValid()
    {
        var id = AcmeAccountId.New();
        var info = await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        var first = await Signer.SignAsync(id, Header("ES256"), "{}"u8.ToArray(), Ct);
        var second = await Signer.SignAsync(id, Header("ES256"), "{}"u8.ToArray(), Ct);

        Assert.NotEqual(first.Signature, second.Signature);
        Assert.True(Verify(info, first) && Verify(info, second));
    }

    [Theory]
    [InlineData("ES256", "ES384")]
    [InlineData("ES256", "RS256")]
    [InlineData("ES256", "HS256")]
    [InlineData("ES256", "none")]
    [InlineData("ES256", "es256")]
    [InlineData("RS256", "ES256")]
    public async Task AHeaderWhoseAlgIsNotTheKeysIsRefused(string keyAlg, string headerAlg)
    {
        var id = AcmeAccountId.New();
        await Signer.CreateAccountKeyAsync(id, JwsAlgorithmNames.Parse(keyAlg), Ct);

        await Assert.ThrowsAsync<ArgumentException>(async () => await Signer.SignAsync(id, Header(headerAlg), "{}"u8.ToArray(), Ct));
    }

    [Fact]
    public async Task MalformedHeadersAreRefused()
    {
        var id = AcmeAccountId.New();
        await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        foreach (var header in new[]
        {
            "{}",
            """{"nonce":"x"}""",
            """{"alg":null}""",
            """{"alg":256}""",
            """{"alg":["ES256"]}""",
            """{"alg":"ES256","alg":"none"}""",
            """{"alg":"none","alg":"ES256"}""",
            """{"alg":"ES256","alg":"ES256"}""",
            """["alg","ES256"]""",
            "\"ES256\"",
            "ES256",
            """{"alg":"ES256""",
            """{"alg":"ES256"} trailing""",
            """{"alg":"ES256","crit":["b64"],"b64":false}""",
            """{"alg":"ES256","crit":[]}""",
            """{"alg":"ES256","b64":true}""",
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(async () => await Signer.SignAsync(id, Encoding.UTF8.GetBytes(header), "{}"u8.ToArray(), Ct));
        }

        await Assert.ThrowsAsync<ArgumentException>(async () => await Signer.SignAsync(id, ReadOnlyMemory<byte>.Empty, "{}"u8.ToArray(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Signer.SignAsync(id, new byte[JwsSigner.MaxHeaderBytes + 1], "{}"u8.ToArray(), Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Signer.SignAsync(id, Header("ES256"), new byte[JwsSigner.MaxPayloadBytes + 1], Ct));
    }

    [Fact]
    public async Task ASignatureOverAnUnrelatedHeaderIsNeverProduced()
    {
        // The header's other members are the caller's business; only alg is checked, and it is always the key's own.
        var id = AcmeAccountId.New();
        var info = await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        var jws = await Signer.SignAsync(id, """{"alg":"ES256","jwk":{"kty":"EC"},"extra":[1,2,{"a":"alg"}]}"""u8.ToArray(), "{}"u8.ToArray(), Ct);

        Assert.True(Verify(info, jws));
    }

    [Theory]
    [InlineData(JwsAlgorithm.ES256, "EC", "P-256")]
    [InlineData(JwsAlgorithm.ES384, "EC", "P-384")]
    [InlineData(JwsAlgorithm.RS256, "RSA", null)]
    public async Task ThePublicJwkHasOnlyPublicMembersAndAMatchingThumbprint(JwsAlgorithm algorithm, string kty, string? crv)
    {
        var id = AcmeAccountId.New();

        var info = await Signer.CreateAccountKeyAsync(id, algorithm, Ct);

        Assert.Equal(algorithm, info.Algorithm);
        Assert.Equal(kty, info.Jwk.Kty);
        Assert.Equal(crv, info.Jwk.Crv);
        if (kty == "EC")
        {
            Assert.Equal(algorithm == JwsAlgorithm.ES256 ? 32 : 48, Base64Url.DecodeFromChars(info.Jwk.X!).Length);
            Assert.Equal(algorithm == JwsAlgorithm.ES256 ? 32 : 48, Base64Url.DecodeFromChars(info.Jwk.Y!).Length);
            Assert.Null(info.Jwk.N);
        }
        else
        {
            Assert.Equal(256, Base64Url.DecodeFromChars(info.Jwk.N!).Length);
            Assert.Equal("AQAB", info.Jwk.E);
            Assert.Null(info.Jwk.X);
        }

        // Independently built canonical JSON (RFC 7638 §3.2: required members only, sorted, no whitespace).
        var canonical = kty == "EC"
            ? $$"""{"crv":"{{info.Jwk.Crv}}","kty":"EC","x":"{{info.Jwk.X}}","y":"{{info.Jwk.Y}}"}"""
            : $$"""{"e":"{{info.Jwk.E}}","kty":"RSA","n":"{{info.Jwk.N}}"}""";
        Assert.Equal(Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), info.Thumbprint);
        Assert.Equal(info, await Signer.GetAccountKeyAsync(id, Ct));
    }

    [Fact]
    public void TheThumbprintMatchesTheRfc7638Vector()
    {
        // RFC 7638 §3.1.
        var jwk = new JsonWebKeyPublic(
            "RSA", null, null, null,
            "0vx7agoebGcQSuuPiLJXZptN9nndrQmbXEps2aiAFbWhM78LhWx4cbbfAAtVT86zwu1RK7aPFFxuhDR1L6tSoc_BJECPebWKRXjBZCiFV4n3oknjhMstn64tZ_2W-5JsGY4Hc5n9yBXArwl93lqt7_RN5w6Cf0h4QyQ5v-65YGjQR0_FDW2QvzqY368QQMicAtaSqzs8KJZgnYb9c7d0zgdAZHzu6qMQvRL5hajrn1n91CbOpbISD08qNLyrdkt-bFTWhAI4vMQFh6WeZu0fM4lFd2NcRwr3XPksINHaQ-G_xBniIqbw0Ls1jF44-csFCur-kEgU8awapJzKnqDKgw",
            "AQAB");

        Assert.Equal("NzbLsXh8uDCcd-6MNwXF4W_7noWXFZAfHkxZsRGC9Xs", Jwk.Thumbprint(jwk));
    }

    [Fact]
    public void TheThumbprintOfAnEcKeyIsTheHashOfItsCanonicalJson()
    {
        // RFC 7515 Appendix A.3's P-256 key (the RFC gives no thumbprint for it); checked against the SHA-256 of its
        // canonical JSON written out by hand, plus the refusals.
        var jwk = new JsonWebKeyPublic("EC", "P-256", "f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU", "x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0", null, null);
        const string Canonical = """{"crv":"P-256","kty":"EC","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""";

        Assert.Equal(Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))), Jwk.Thumbprint(jwk));
        Assert.Throws<ArgumentException>(() => Jwk.Thumbprint(new JsonWebKeyPublic("oct", null, null, null, null, null)));
        Assert.Throws<ArgumentException>(() => Jwk.Thumbprint(new JsonWebKeyPublic("EC", "P-256", "x", null, null, null)));
    }

    [Fact]
    public async Task ASecondKeyForTheSameAccountIsRefusedAndTheFirstSurvives()
    {
        var id = AcmeAccountId.New();
        var first = await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.RS256, Ct));

        Assert.Equal(first, await Signer.GetAccountKeyAsync(id, Ct));
    }

    [Fact]
    public async Task UnknownAccountsAndDeletion()
    {
        var id = AcmeAccountId.New();
        Assert.Null(await Signer.GetAccountKeyAsync(id, Ct));
        Assert.False(await Signer.DeleteAccountKeyAsync(id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Signer.SignAsync(id, Header("ES256"), "{}"u8.ToArray(), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Signer.CreateAccountKeyAsync(id, (JwsAlgorithm)9, Ct));

        await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);
        Assert.True(await Signer.DeleteAccountKeyAsync(id, Ct));
        Assert.Null(await Signer.GetAccountKeyAsync(id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () => await Signer.SignAsync(id, Header("ES256"), "{}"u8.ToArray(), Ct));
    }

    [Fact]
    public async Task TheKeySurvivesARestartAndStillSigns()
    {
        var id = AcmeAccountId.New();
        var info = await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);

        _vault.Restart();

        Assert.True(Verify(info, await Signer.SignAsync(id, Header("ES256"), "{}"u8.ToArray(), Ct)));
    }

    [Fact]
    public async Task ChangingTheAlgorithmInTheDatabaseMakesTheKeyFailToOpen()
    {
        var id = AcmeAccountId.New();
        await Signer.CreateAccountKeyAsync(id, JwsAlgorithm.ES256, Ct);
        await using (var context = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            await context.AccountKeys.Where(k => k.Id == id).ExecuteUpdateAsync(u => u.SetProperty(k => k.Algorithm, JwsAlgorithm.ES384), Ct);
        }

        // The header now agrees with the (forged) row, so only the AAD stands in the way.
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await Signer.SignAsync(id, Header("ES384"), "{}"u8.ToArray(), Ct));
    }

    [Fact]
    public async Task ThePrivateKeyIsNotOnDiskAndIsBoundToItsAccount()
    {
        var a = AcmeAccountId.New();
        var b = AcmeAccountId.New();
        await Signer.CreateAccountKeyAsync(a, JwsAlgorithm.ES256, Ct);
        await Signer.CreateAccountKeyAsync(b, JwsAlgorithm.ES256, Ct);
        var factory = _vault.Get<IDbContextFactory<VaultDbContext>>();
        await using (var context = await factory.CreateDbContextAsync(Ct))
        {
            var rows = await context.AccountKeys.AsNoTracking().ToListAsync(Ct);

            // A sealed PKCS#8 key starts with a DER SEQUENCE once decrypted; the sealed value has no such structure in the clear.
            Assert.All(rows, r => Assert.True(r.Sealed.Length > 100));
            var donor = rows.Single(r => r.Id == b).Sealed;
            await context.AccountKeys.Where(r => r.Id == a).ExecuteUpdateAsync(u => u.SetProperty(r => r.Sealed, donor), Ct);
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await Signer.SignAsync(a, Header("ES256"), "{}"u8.ToArray(), Ct));
    }
}
