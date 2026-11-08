using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary><see cref="ICertificateIssuance"/>: CSR and chain, self-signed, local CA, atomic replacement and deletion.</summary>
public sealed class CertificateIssuanceTests : IDisposable
{
    private static readonly string[] AppNames = ["app.example.com"];

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateIssuance Issuance => _vault.Get<ICertificateIssuance>();

    private IServerCertificateSource Serving => _vault.Get<IServerCertificateSource>();

    public void Dispose() => _vault.Dispose();

    private static string Pem(params X509Certificate2[] certificates) => string.Concat(certificates.Select(c => c.ExportCertificatePem() + "\n"));

    private static ReadOnlyMemory<byte> Bytes(string pem) => Encoding.UTF8.GetBytes(pem);

    private static X509Certificate2 First(string pem) => X509Certificate2.CreateFromPem(pem);

    private static bool ChainValidates(string chainPem, X509Certificate2 root)
    {
        List<X509Certificate2> certificates = [];
        var remaining = chainPem.AsSpan();
        while (PemEncoding.TryFind(remaining, out var fields))
        {
            certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(new string(remaining[fields.Base64Data]))));
            remaining = remaining[fields.Location.End..];
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = VaultHarness.Epoch.UtcDateTime; // the vault's clock, not the wall clock
        foreach (var extra in certificates.Skip(1))
        {
            chain.ChainPolicy.ExtraStore.Add(extra);
        }

        return chain.Build(certificates[0]);
    }

    [Fact]
    public async Task CsrThenChainBecomesTheServingCertificate()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, ["App.Example.com", "*.example.net"], Ct);
        Assert.False(csr.StagedKey.IsEmpty);
        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", csr.Pem, StringComparison.Ordinal);
        Assert.Null(await Serving.LoadForServingAsync(id, Ct));

        using var leaf = ca.Issue(csr.Der.ToArray());
        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(leaf, ca.Certificate)), Ct);

        Assert.Equal(["app.example.com", "*.example.net"], info.SubjectAlternativeNames);
        Assert.Equal("CN=app.example.com", info.Subject);
        Assert.Equal(ca.Certificate.SubjectName.Name, info.Issuer);
        Assert.Equal(Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)), info.Sha256Fingerprint);
        Assert.Equal("ECDSA P-256", info.KeyAlgorithm);
        Assert.NotNull(info.AuthorityKeyIdentifier);

        // The root is left out of the stored chain; the leaf's issuer is the last name in ChainIssuers.
        Assert.Equal([ca.Certificate.SubjectName.Name], info.ChainIssuers);
        Assert.Single(info.PublicChainPem.Split("BEGIN CERTIFICATE", StringSplitOptions.RemoveEmptyEntries), p => p.Contains("END", StringComparison.Ordinal));

        var context = await Serving.LoadForServingAsync(id, Ct);
        Assert.NotNull(context);
        Assert.True(context.TargetCertificate.HasPrivateKey);
        Assert.Equal(info.Sha256Fingerprint, Convert.ToHexStringLower(context.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
    }

    [Fact]
    public async Task AnIntermediateStaysInTheChainAndTheRootIsDropped()
    {
        using var root = TestCa.CreateRoot();
        using var intermediate = root.CreateIntermediate();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        using var leaf = intermediate.Issue(csr.Der.ToArray());

        // Given out of order and with the root: the vault orders it and drops the root.
        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(leaf, root.Certificate, intermediate.Certificate)), Ct);

        Assert.Equal([intermediate.Certificate.SubjectName.Name, root.Certificate.SubjectName.Name], info.ChainIssuers);
        Assert.True(ChainValidates(info.PublicChainPem, root.Certificate));
        var context = await Serving.LoadForServingAsync(id, Ct);
        Assert.Single(context!.IntermediateCertificates);
    }

    [Fact]
    public async Task AChainForAnotherKeyIsRefusedAndTheStagedKeyKeepsWaiting()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        using var other = ca.IssueWithKey("app.example.com");

        await Assert.ThrowsAsync<CryptographicException>(
            async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(other)), Ct));

        Assert.Null(await Serving.LoadForServingAsync(id, Ct));
        using var leaf = ca.Issue(csr.Der.ToArray());
        await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(leaf)), Ct);
        Assert.NotNull(await Serving.LoadForServingAsync(id, Ct));
    }

    [Fact]
    public async Task AStaleStagedKeyIsRefusedWithoutPairingTheChain()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var first = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        var second = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        using var firstLeaf = ca.Issue(first.Der.ToArray());

        await Assert.ThrowsAsync<StagedKeyMismatchException>(
            async () => await Issuance.AttachChainAsync(id, first.StagedKey, Bytes(Pem(firstLeaf)), Ct));
        Assert.Null(await Serving.LoadForServingAsync(id, Ct));

        using var secondLeaf = ca.Issue(second.Der.ToArray());
        await Issuance.AttachChainAsync(id, second.StagedKey, Bytes(Pem(secondLeaf)), Ct);

        // After a successful attach the token is spent.
        await Assert.ThrowsAsync<StagedKeyMismatchException>(
            async () => await Issuance.AttachChainAsync(id, second.StagedKey, Bytes(Pem(secondLeaf)), Ct));
    }

    [Fact]
    public async Task AReplacementByAnotherRouteInvalidatesTheStagedKey()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        using var leaf = ca.Issue(csr.Der.ToArray());

        await Assert.ThrowsAsync<StagedKeyMismatchException>(
            async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(leaf)), Ct));
    }

    [Fact]
    public async Task TheChainMustCoverTheRequestedNames()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, ["a.example.com", "b.example.com"], Ct);
        using var shortLeaf = ca.Issue(csr.Der.ToArray(), names: ["a.example.com"]);

        var ex = await Assert.ThrowsAsync<CryptographicException>(
            async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(shortLeaf)), Ct));
        Assert.Contains("b.example.com", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWildcardCoversItsChildrenOnly()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();

        // A request for a name that the issued wildcard certificate covers is fine.
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, ["www.example.com"], Ct);
        using var wildcard = ca.Issue(csr.Der.ToArray(), names: ["*.example.com"]);
        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(wildcard)), Ct);
        Assert.Equal(["*.example.com"], info.SubjectAlternativeNames);

        // But the wildcard does not cover the apex, or a deeper name.
        foreach (var name in new[] { "example.com", "a.b.example.com" })
        {
            var other = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, [name], Ct);
            using var leaf = ca.Issue(other.Der.ToArray(), names: ["*.example.com"]);
            await Assert.ThrowsAsync<CryptographicException>(
                async () => await Issuance.AttachChainAsync(id, other.StagedKey, Bytes(Pem(leaf)), Ct));
        }
    }

    [Theory]
    [InlineData("not a pem")]
    [InlineData("")]
    public async Task GarbageIsNotAChain(string text)
    {
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        await Assert.ThrowsAnyAsync<Exception>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(text), Ct));
    }

    [Theory]
    [InlineData(KeySpec.EcdsaP256, "ECDSA P-256")]
    [InlineData(KeySpec.EcdsaP384, "ECDSA P-384")]
    [InlineData(KeySpec.Rsa2048, "RSA 2048")]
    public async Task SelfSignedCertificateCarriesTheServerExtensions(KeySpec spec, string label)
    {
        var id = CertificateId.New();
        var info = await Issuance.CreateSelfSignedAsync(id, spec, ["host.example.com", "192.0.2.10", "2001:db8::1"], TimeSpan.FromDays(30), Ct);

        Assert.Equal(label, info.KeyAlgorithm);
        Assert.Equal("CN=host.example.com", info.Subject);
        Assert.Equal(info.Subject, info.Issuer);
        Assert.Equal(["host.example.com", "192.0.2.10", "2001:db8::1"], info.SubjectAlternativeNames);
        Assert.Equal(info.NotBefore + TimeSpan.FromDays(30) + TimeSpan.FromMinutes(5), info.NotAfter);
        Assert.Equal([info.Issuer], info.ChainIssuers);
        Assert.Equal(0, info.SerialNumber.Length % 2);
        Assert.False(info.SerialNumber.StartsWith("00", StringComparison.Ordinal));

        using var leaf = First(info.PublicChainPem);
        var basic = leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(basic.CertificateAuthority);
        Assert.True(basic.Critical);
        Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True(leaf.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        Assert.NotNull(leaf.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single());
        Assert.NotNull(info.AuthorityKeyIdentifier);

        // Valid on its own: it is its own root.
        Assert.True(ChainValidates(info.PublicChainPem, leaf));
    }

    [Theory]
    [InlineData("192.0.2.1", "")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.example.com", "")]
    [InlineData("www.example.com", "CN=www.example.com")]
    [InlineData("*.example.com", "CN=*.example.com")]
    public async Task TheSubjectHasACommonNameOnlyForAShortDnsName(string first, string subject)
    {
        var info = await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, [first], TimeSpan.FromDays(1), Ct);
        Assert.Equal(subject, info.Subject);
        Assert.Equal([first], info.SubjectAlternativeNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("*.com")]
    [InlineData("a..b")]
    [InlineData("-a.example.com")]
    [InlineData("a.*.example.com")]
    public async Task InvalidNamesAreRefused(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, [name], TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await Issuance.CreateKeyAndCsrAsync(CertificateId.New(), KeySpec.EcdsaP256, [name], Ct));
    }

    [Fact]
    public async Task NoNamesAndBadValidityAreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, [], TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.Zero, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await Issuance.CreateSelfSignedAsync(CertificateId.New(), (KeySpec)99, AppNames, TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<ArgumentException>(async () => await Issuance.CreateSelfSignedAsync(default, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
    }

    [Fact]
    public async Task TheLocalCaRootIsANormalCertificateAndEnsureIsIdempotent()
    {
        var rootId = CertificateId.New();
        var first = await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);
        var again = await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP384, TimeSpan.FromDays(1), Ct);

        Assert.Equal(first.Sha256Fingerprint, again.Sha256Fingerprint);
        Assert.StartsWith("CN=Paduraksa Local CA (", first.Subject, StringComparison.Ordinal);
        Assert.Equal(first.Subject, first.Issuer);
        Assert.Empty(first.SubjectAlternativeNames);
        Assert.Contains("BEGIN CERTIFICATE", first.PublicChainPem, StringComparison.Ordinal);

        using var root = First(first.PublicChainPem);
        var basic = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, root.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);

        // The root's key is for signing only: it is never served.
        Assert.Null(await Serving.LoadForServingAsync(rootId, Ct));
        Assert.Null(await _vault.Get<IClientCertificateSource>().LoadForUpstreamAsync(rootId, Ct));
    }

    [Fact]
    public async Task TheLocalCaRootCannotBeReplacedOrStagedOver()
    {
        var rootId = CertificateId.New();
        await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Issuance.CreateSelfSignedAsync(rootId, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Issuance.IssueFromLocalCaAsync(rootId, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Issuance.CreateKeyAndCsrAsync(rootId, KeySpec.EcdsaP256, AppNames, Ct));
    }

    [Fact]
    public async Task AnIdThatIsACertificateCannotBecomeTheRoot()
    {
        var id = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Issuance.EnsureLocalCaAsync(id, KeySpec.EcdsaP256, TimeSpan.FromDays(1), Ct));
    }

    [Fact]
    public async Task LeafIssuedByTheLocalCaValidatesAgainstTheRoot()
    {
        var rootId = CertificateId.New();
        var rootInfo = await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);
        var leafId = CertificateId.New();
        var info = await Issuance.IssueFromLocalCaAsync(leafId, KeySpec.Rsa2048, ["nas.lan", "192.168.1.5"], TimeSpan.FromDays(90), Ct);

        Assert.Equal(rootInfo.Subject, info.Issuer);
        Assert.Equal([rootInfo.Subject], info.ChainIssuers);
        Assert.Equal(["nas.lan", "192.168.1.5"], info.SubjectAlternativeNames);
        Assert.Equal("RSA 2048", info.KeyAlgorithm);

        using var root = First(rootInfo.PublicChainPem);
        Assert.True(ChainValidates(info.PublicChainPem, root));

        // The leaf's AKI is the root's SKI.
        var rootSki = root.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifier!.ToLowerInvariant();
        Assert.Equal(rootSki, info.AuthorityKeyIdentifier);

        var context = await Serving.LoadForServingAsync(leafId, Ct);
        Assert.NotNull(context);
        Assert.True(context.TargetCertificate.HasPrivateKey);
    }

    [Fact]
    public async Task ALeafIsCappedAtTheRootsRemainingValidity()
    {
        await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(10), Ct);
        var info = await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(365), Ct);
        Assert.True(info.NotAfter < VaultHarness.Epoch.AddDays(11));
    }

    [Fact]
    public async Task IssuingNeedsALocalCaAndAnExpiredOneIsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));

        // The vault's clock is a year behind the real one, so the root it signs is already past its validity at the real clock...
        // and one year ahead of it the root has expired for the vault too.
        await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(10), Ct);
        _vault.Time.Advance(TimeSpan.FromDays(30));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
    }

    [Fact]
    public async Task ReplacingKeepsTheIdAndSwapsKeyAndChainTogether()
    {
        var id = CertificateId.New();
        var first = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var firstContext = await Serving.LoadForServingAsync(id, Ct);

        var second = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP384, ["other.example.com"], TimeSpan.FromDays(60), Ct);
        var secondContext = await Serving.LoadForServingAsync(id, Ct);

        Assert.NotEqual(first.Sha256Fingerprint, second.Sha256Fingerprint);
        Assert.NotSame(firstContext, secondContext);

        // The served key is the new certificate's own: key and chain are one pair.
        Assert.Equal(second.Sha256Fingerprint, Convert.ToHexStringLower(secondContext!.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
        using var proof = secondContext.TargetCertificate.GetECDsaPrivateKey()!;
        Assert.Equal(proof.ExportSubjectPublicKeyInfo(), secondContext.TargetCertificate.PublicKey.ExportSubjectPublicKeyInfo());

        using var db = await _vault.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        var row = await db.CertificateKeys.SingleAsync(Ct);
        Assert.Equal(2, row.Version);
    }

    [Fact]
    public async Task AFailedReplacementLeavesTheOldPairServing()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var original = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var before = await Serving.LoadForServingAsync(id, Ct);

        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        using var wrong = ca.IssueWithKey("app.example.com");
        await Assert.ThrowsAsync<CryptographicException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(wrong)), Ct));

        var after = await Serving.LoadForServingAsync(id, Ct);
        Assert.Same(before, after);
        Assert.Equal(original.Sha256Fingerprint, Convert.ToHexStringLower(after!.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
    }

    [Fact]
    public async Task RenewalThroughCsrReplacesTheCertificateUnderTheSameId()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var firstCsr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        using var firstLeaf = ca.Issue(firstCsr.Der.ToArray());
        var first = await Issuance.AttachChainAsync(id, firstCsr.StagedKey, Bytes(Pem(firstLeaf)), Ct);
        var firstContext = await Serving.LoadForServingAsync(id, Ct);

        var secondCsr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);

        // Staging alone changes nothing that serves.
        Assert.Same(firstContext, await Serving.LoadForServingAsync(id, Ct));

        using var secondLeaf = ca.Issue(secondCsr.Der.ToArray());
        var second = await Issuance.AttachChainAsync(id, secondCsr.StagedKey, Bytes(Pem(secondLeaf)), Ct);

        Assert.NotEqual(first.Sha256Fingerprint, second.Sha256Fingerprint);
        Assert.NotSame(firstContext, await Serving.LoadForServingAsync(id, Ct));
    }

    [Fact]
    public async Task DeletingTakesTheKeyAndTheStagedKeyWithIt()
    {
        var id = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        Assert.NotNull(await Serving.LoadForServingAsync(id, Ct));
        await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);

        Assert.True(await Issuance.DeleteAsync(id, Ct));
        Assert.False(await Issuance.DeleteAsync(id, Ct));
        Assert.Null(await Serving.LoadForServingAsync(id, Ct));

        using var db = await _vault.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Empty(await db.CertificateKeys.ToListAsync(Ct));
        Assert.Empty(await db.StagedKeys.ToListAsync(Ct));
    }

    [Fact]
    public async Task ADeletedStagedKeyIsStale()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        Assert.True(await Issuance.DeleteAsync(id, Ct));
        using var leaf = ca.Issue(csr.Der.ToArray());
        await Assert.ThrowsAsync<StagedKeyMismatchException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Bytes(Pem(leaf)), Ct));
    }

    [Fact]
    public async Task TheSerialNumberIsRandomAndHasNoSignByte()
    {
        var first = await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct);
        var second = await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct);

        Assert.Equal(32, first.SerialNumber.Length);
        Assert.Equal(first.SerialNumber, first.SerialNumber.ToUpperInvariant());
        Assert.NotEqual(first.SerialNumber, second.SerialNumber);
        Assert.InRange(Convert.ToByte(first.SerialNumber[..2], 16), 0x40, 0x7F);
    }
}
