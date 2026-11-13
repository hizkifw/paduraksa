using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary>The local CA's name constraints (PLAN 9.2) and its rotation.</summary>
public sealed class LocalCaConstraintsTests : IDisposable
{
    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateIssuance Issuance => _vault.Get<ICertificateIssuance>();

    private ICertificateCatalog Catalog => _vault.Get<ICertificateCatalog>();

    public void Dispose() => _vault.Dispose();

    private static X509Certificate2 First(string pem) => X509Certificate2.CreateFromPem(pem);

    [Fact]
    public async Task ARootMadeWithConstraintsCarriesThemAsACriticalExtensionAndSaysSo()
    {
        var info = await Issuance.EnsureLocalCaAsync(
            CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), LocalCaConstraints.Extend(["Example.Org", "*.Lab.Example.Net"], ["203.0.113.7"]), Ct);

        using var root = First(info.PublicChainPem);
        var extension = Assert.Single(root.Extensions, e => e.Oid?.Value == "2.5.29.30");
        Assert.True(extension.Critical);
        Assert.Contains("DNS:home.arpa", info.NameConstraints!);
        Assert.Contains("DNS:example.org", info.NameConstraints!);
        Assert.Contains("DNS:lab.example.net", info.NameConstraints!);
        Assert.Contains("IP:192.168.0.0/16", info.NameConstraints!);
        Assert.Contains("IP:203.0.113.7/32", info.NameConstraints!);
        Assert.Contains("IP:fc00::/7", info.NameConstraints!);
    }

    [Fact]
    public async Task ALeafInsideTheConstraintsValidatesAndOneOutsideIsRefusedBeforeSigning()
    {
        var rootInfo = await Issuance.EnsureLocalCaAsync(
            CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), LocalCaConstraints.Extend(["example.org"]), Ct);
        using var root = First(rootInfo.PublicChainPem);

        var ok = await Issuance.IssueFromLocalCaAsync(
            CertificateId.New(), KeySpec.EcdsaP256, ["nas.lan", "*.example.org", "app.sub.example.org", "192.168.1.5", "fd00::1"], TimeSpan.FromDays(30), Ct);
        Assert.True(ChainValidates(ok.PublicChainPem, root));

        foreach (var outside in new[] { "www.example.com", "example.org.evil.test.com", "8.8.8.8", "notexample.org", "[2001:db8::1]" })
        {
            var name = outside.Trim('[', ']');
            var refused = await Assert.ThrowsAsync<LocalCaNameNotPermittedException>(
                async () => await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, [name], TimeSpan.FromDays(30), Ct));
            Assert.Equal(name, refused.Name);
        }

        // Refused as a whole: one name outside means no certificate.
        await Assert.ThrowsAsync<LocalCaNameNotPermittedException>(
            async () => await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, ["nas.lan", "www.example.com"], TimeSpan.FromDays(30), Ct));
    }

    [Fact]
    public void AClientsChainBuilderEnforcesTheSameExtensionTheVaultWrites()
    {
        // The vault's root key never leaves it, so a leaf outside the constraints can't be signed by it: the same extension goes on a
        // test root, to show that a client refuses what the vault would refuse.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest rootRequest = new("CN=Constrained Test Root", key, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        rootRequest.CertificateExtensions.Add(Paduraksa.Vault.Certificates.NameConstraintsExtension.Build(
            Paduraksa.Vault.Certificates.NameConstraintsExtension.Normalize(LocalCaConstraints.Default)));
        var now = VaultHarness.Epoch;
        using var root = X509CertificateLoader.LoadCertificate(rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(5)).RawData);

        bool Validates(string name)
        {
            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            // As the vault does: an IP-only certificate has no subject, since a common name is judged as a DNS name.
            CertificateRequest request = new(System.Net.IPAddress.TryParse(name, out _) ? string.Empty : "CN=" + name, leafKey, HashAlgorithmName.SHA256);
            SubjectAlternativeNameBuilder san = new();
            if (System.Net.IPAddress.TryParse(name, out var address))
            {
                san.AddIpAddress(address);
            }
            else
            {
                san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var leaf = request.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(key), now.AddDays(-1), now.AddDays(30), RandomNumberGenerator.GetBytes(8));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            return chain.Build(leaf);
        }

        Assert.True(Validates("nas.lan"));
        Assert.True(Validates("192.168.1.5"));
        Assert.False(Validates("www.example.com"), "a client must refuse a DNS name outside the constraints");
        Assert.False(Validates("8.8.8.8"), "a client must refuse an address outside the constraints");
    }

    [Fact]
    public async Task TheFourArgumentFormMakesTheUnconstrainedRootEveryRootWas()
    {
        var info = await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);

        Assert.Null(info.NameConstraints);
        using var root = First(info.PublicChainPem);
        Assert.DoesNotContain(root.Extensions, e => e.Oid?.Value == "2.5.29.30");
        var leaf = await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, ["www.example.com", "8.8.8.8"], TimeSpan.FromDays(30), Ct);
        Assert.True(ChainValidates(leaf.PublicChainPem, root));
    }

    [Theory]
    [InlineData(new string[0], new string[0])]
    [InlineData(new[] { "not a domain" }, new string[0])]
    [InlineData(new[] { "1.2.3.4" }, new string[0])]
    [InlineData(new string[0], new[] { "10.0.0.0/33" })]
    [InlineData(new string[0], new[] { "nonsense" })]
    public async Task BadConstraintsAreRefused(string[] domains, string[] addresses) =>
        await Assert.ThrowsAsync<ArgumentException>(async () => await Issuance.EnsureLocalCaAsync(
            CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(365), new LocalCaConstraints(domains, addresses), Ct));

    [Fact]
    public async Task RotatingMakesTheNewRootTheSigningOneAndDestroysTheOldKey()
    {
        var oldId = CertificateId.New();
        var oldInfo = await Issuance.EnsureLocalCaAsync(oldId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);
        var leafId = CertificateId.New();
        var oldLeaf = await Issuance.IssueFromLocalCaAsync(leafId, KeySpec.EcdsaP256, ["nas.lan"], TimeSpan.FromDays(90), Ct);

        var newId = CertificateId.New();
        var rotation = await Issuance.RotateLocalCaAsync(newId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), LocalCaConstraints.Extend(["example.org"]), Ct);

        Assert.Equal(oldId, rotation.RetiredId);
        Assert.Equal(oldInfo.Sha256Fingerprint, rotation.Retired.Sha256Fingerprint);
        Assert.NotEqual(oldInfo.Subject, rotation.Root.Subject);
        // Exactly one root in the vault, the new one; the old key is gone, so it can sign nothing again.
        var held = Assert.Single(await Catalog.ListAsync(Ct), c => c.IsLocalCaRoot);
        Assert.Equal(newId, held.Id);
        Assert.Null(await Catalog.DescribeAsync(oldId, Ct));

        // A leaf issued now chains to the new root, and the one issued before still serves, chaining to the old.
        var fresh = await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, ["www.example.org"], TimeSpan.FromDays(30), Ct);
        Assert.Equal(rotation.Root.Subject, fresh.Issuer);
        using var newRoot = First(rotation.Root.PublicChainPem);
        using var oldRoot = First(oldInfo.PublicChainPem);
        Assert.True(ChainValidates(fresh.PublicChainPem, newRoot));
        Assert.True(ChainValidates(oldLeaf.PublicChainPem, oldRoot));
        Assert.NotNull(await _vault.Get<IServerCertificateSource>().LoadForServingAsync(leafId, Ct));

        // The new constraints are the new root's: example.org is allowed now, and was not under the unconstrained old root's replacement default.
        await Assert.ThrowsAsync<LocalCaNameNotPermittedException>(
            async () => await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, ["www.example.com"], TimeSpan.FromDays(30), Ct));
    }

    [Fact]
    public async Task RotatingWithNoConstraintsMakesAnUnlimitedRootEvenFromALimitedOne()
    {
        await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), LocalCaConstraints.Default, Ct);

        var rotation = await Issuance.RotateLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), null, Ct);

        Assert.Null(rotation.Root.NameConstraints);
        var leaf = await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, ["www.example.com"], TimeSpan.FromDays(30), Ct);
        Assert.Equal(rotation.Root.Subject, leaf.Issuer);
    }

    [Fact]
    public async Task RotatingNeedsARootAndAFreeId()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.RotateLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(365), LocalCaConstraints.Default, Ct));

        var rootId = CertificateId.New();
        await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(365), Ct);
        var taken = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(taken, KeySpec.EcdsaP256, ["a.lan"], TimeSpan.FromDays(1), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.RotateLocalCaAsync(taken, KeySpec.EcdsaP256, TimeSpan.FromDays(365), LocalCaConstraints.Default, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.RotateLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(365), LocalCaConstraints.Default, Ct));
        // Nothing changed by the refusals.
        Assert.Equal(rootId, Assert.Single(await Catalog.ListAsync(Ct), c => c.IsLocalCaRoot).Id);
    }

    private static bool ChainValidates(string chainPem, X509Certificate2 root)
    {
        using var leaf = First(chainPem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = VaultHarness.Epoch.UtcDateTime;
        return chain.Build(leaf);
    }
}
