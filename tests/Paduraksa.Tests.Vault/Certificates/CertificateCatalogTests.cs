using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Certificates;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary>The vault describing what it holds: public facts per ID, the local CA root marked, and nothing for a key that has no chain.</summary>
public sealed class CertificateCatalogTests : IDisposable
{
    private static readonly string[] AppNames = ["app.example.com", "www.example.com"];

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateIssuance Issuance => _vault.Get<ICertificateIssuance>();

    private ICertificateCatalog Catalog => _vault.Get<ICertificateCatalog>();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task ItDescribesEveryCertificateWithTheFactsTheVaultReportedWhenItStoredIt()
    {
        var selfSignedId = CertificateId.New();
        var rootId = CertificateId.New();
        var leafId = CertificateId.New();
        var selfSigned = await Issuance.CreateSelfSignedAsync(selfSignedId, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var root = await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);
        var leaf = await Issuance.IssueFromLocalCaAsync(leafId, KeySpec.EcdsaP256, ["local.example.com"], TimeSpan.FromDays(30), Ct);

        var listed = (await Catalog.ListAsync(Ct)).ToDictionary(c => c.Id);

        Assert.Equal(3, listed.Count);
        Assert.Equal(selfSigned, listed[selfSignedId].Info, SameFacts.Instance);
        Assert.Equal(root, listed[rootId].Info, SameFacts.Instance);
        Assert.Equal(leaf, listed[leafId].Info, SameFacts.Instance);
        Assert.Equal([rootId], listed.Values.Where(c => c.IsLocalCaRoot).Select(c => c.Id));
        Assert.Equal(leaf.PublicChainPem, listed[leafId].Info.PublicChainPem);
    }

    [Fact]
    public async Task DescribeFindsOneByIdAndAnUnknownIdIsNull()
    {
        var id = CertificateId.New();
        var made = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        var described = await Catalog.DescribeAsync(id, Ct);

        Assert.Equal(made, described!.Info, SameFacts.Instance);
        Assert.False(described.IsLocalCaRoot);
        Assert.Null(await Catalog.DescribeAsync(CertificateId.New(), Ct));
    }

    [Fact]
    public async Task AKeyWaitingForItsChainIsNotACertificateAndAnAttachedOneIs()
    {
        var id = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);
        Assert.Empty(await Catalog.ListAsync(Ct));
        Assert.Null(await Catalog.DescribeAsync(id, Ct));

        using var ca = TestCa.CreateRoot();
        using var issued = ca.Issue(csr.Der.ToArray());
        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Encoding.UTF8.GetBytes(issued.ExportCertificatePem()), Ct);

        Assert.Equal(info.Sha256Fingerprint, (await Catalog.DescribeAsync(id, Ct))!.Info.Sha256Fingerprint);
    }

    [Fact]
    public async Task AReplacementIsDescribedByTheNewPairAndADeletedCertificateIsGone()
    {
        var id = CertificateId.New();
        var first = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var second = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        Assert.NotEqual(first.Sha256Fingerprint, second.Sha256Fingerprint);

        Assert.Equal(second.Sha256Fingerprint, (await Catalog.DescribeAsync(id, Ct))!.Info.Sha256Fingerprint);
        Assert.True(await Issuance.DeleteAsync(id, Ct));

        Assert.Empty(await Catalog.ListAsync(Ct));
    }

    [Fact]
    public async Task ADescriptionSurvivesARestartOfTheVault()
    {
        var id = CertificateId.New();
        var made = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        _vault.Restart();

        Assert.Equal(made, (await Catalog.DescribeAsync(id, Ct))!.Info, SameFacts.Instance);
    }

    [Fact]
    public async Task TheQueryNeverSelectsTheSealedKey()
    {
        await using var db = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);

        var sql = CertificateCatalog.Rows(db, CertificateId.New()).ToQueryString();

        Assert.Contains("ChainPem", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Sealed", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneRowWithAnUnreadableChainIsLeftOutAndTheOthersAreStillListed()
    {
        var good = CertificateId.New();
        var bad = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(good, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        await Issuance.CreateSelfSignedAsync(bad, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        await using (var db = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            _ = await db.CertificateKeys.Where(k => k.Id == bad).ExecuteUpdateAsync(s => s.SetProperty(k => k.ChainPem, "not a certificate"), Ct);
        }

        var listed = await Catalog.ListAsync(Ct);

        Assert.Equal([good], listed.Select(c => c.Id));
        Assert.Null(await Catalog.DescribeAsync(bad, Ct));
    }

    /// <summary><see cref="CertificateInfo"/>'s list members compare by reference, so the facts are compared by value.</summary>
    private sealed class SameFacts : IEqualityComparer<CertificateInfo>
    {
        public static SameFacts Instance { get; } = new();

        public bool Equals(CertificateInfo? x, CertificateInfo? y) =>
            x is not null && y is not null
            && x.Subject == y.Subject
            && x.SubjectAlternativeNames.SequenceEqual(y.SubjectAlternativeNames)
            && x.NotBefore == y.NotBefore
            && x.NotAfter == y.NotAfter
            && x.Issuer == y.Issuer
            && x.SerialNumber == y.SerialNumber
            && x.AuthorityKeyIdentifier == y.AuthorityKeyIdentifier
            && x.Sha256Fingerprint == y.Sha256Fingerprint
            && x.KeyAlgorithm == y.KeyAlgorithm
            && x.ChainIssuers.SequenceEqual(y.ChainIssuers)
            && x.PublicChainPem == y.PublicChainPem;

        public int GetHashCode(CertificateInfo obj) => obj.Sha256Fingerprint.GetHashCode(StringComparison.Ordinal);
    }
}
