using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Certificates;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Certificates;

public sealed class CertificateIssuanceHardeningTests : IDisposable
{
    private static readonly string[] AppNames = ["app.example.com"];

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateIssuance Issuance => _vault.Get<ICertificateIssuance>();

    private IServerCertificateSource Serving => _vault.Get<IServerCertificateSource>();

    public void Dispose() => _vault.Dispose();

    private static ReadOnlyMemory<byte> Pem(params X509Certificate2[] certificates) =>
        Encoding.UTF8.GetBytes(string.Concat(certificates.Select(c => c.ExportCertificatePem() + "\n")));

    private async Task<(CertificateId Id, CsrResult Csr)> StageAsync()
    {
        var id = CertificateId.New();
        return (id, await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct));
    }

    [Fact]
    public async Task AnExpiredOrNotYetValidLeafIsRefusedAndTheStagedKeyKeepsWaiting()
    {
        using var ca = TestCa.CreateRoot();
        var (id, csr) = await StageAsync();
        using var expired = ca.Issue(csr.Der.ToArray(), validity: TimeSpan.FromDays(10), notBefore: VaultHarness.Epoch.AddDays(-100));
        using var future = ca.Issue(csr.Der.ToArray(), notBefore: VaultHarness.Epoch.AddDays(1));

        var early = await Assert.ThrowsAsync<CryptographicException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(expired), Ct));
        var late = await Assert.ThrowsAsync<CryptographicException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(future), Ct));

        Assert.Contains("expired", early.Message, StringComparison.Ordinal);
        Assert.Contains("not valid before", late.Message, StringComparison.Ordinal);
        Assert.Null(await Serving.LoadForServingAsync(id, Ct));
        using var good = ca.Issue(csr.Der.ToArray());
        await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(good), Ct);
        Assert.NotNull(await Serving.LoadForServingAsync(id, Ct));
    }

    [Fact]
    public async Task ACertificateWithTheIssuersNameButAnotherKeyIsNotTheIssuer()
    {
        using var root = TestCa.CreateRoot();
        using var real = root.CreateIntermediate("Same Name");
        using var impostor = root.CreateIntermediate("Same Name");
        var (id, csr) = await StageAsync();
        using var leaf = real.Issue(csr.Der.ToArray());

        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf, impostor.Certificate, real.Certificate), Ct);

        // The impostor is dropped even though it comes first and carries the same name; the real intermediate is kept.
        Assert.Equal(2, info.ChainIssuers.Count);
        Assert.Contains(real.Certificate.ExportCertificatePem().ReplaceLineEndings("\n").Trim(), info.PublicChainPem, StringComparison.Ordinal);
        Assert.DoesNotContain(impostor.Certificate.ExportCertificatePem().ReplaceLineEndings("\n").Trim(), info.PublicChainPem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChainWhoseOnlyIntermediateDidNotSignTheLeafKeepsOnlyTheLeaf()
    {
        using var root = TestCa.CreateRoot();
        using var other = root.CreateIntermediate("Unrelated");
        using var signer = root.CreateIntermediate("Signer");
        var (id, csr) = await StageAsync();
        using var leaf = signer.Issue(csr.Der.ToArray());

        var info = await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf, other.Certificate), Ct);

        Assert.Equal([signer.Certificate.SubjectName.Name], info.ChainIssuers);
    }

    [Fact]
    public async Task LeavesAreCappedAt825DaysAndTheRootIsNot()
    {
        var selfSigned = await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(3650), Ct);
        var root = await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(3650), Ct);
        var leaf = await Issuance.IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(3650), Ct);

        Assert.Equal(TimeSpan.FromDays(825) + CertificateIssuance.ClockSkew, selfSigned.NotAfter - selfSigned.NotBefore);
        Assert.Equal(TimeSpan.FromDays(825) + CertificateIssuance.ClockSkew, leaf.NotAfter - leaf.NotBefore);
        Assert.Equal(TimeSpan.FromDays(3650) + CertificateIssuance.ClockSkew, root.NotAfter - root.NotBefore);
    }

    [Fact]
    public async Task ALocalCaThatExistsUnderAnotherIdIsNamedNotRecreated()
    {
        var rootId = CertificateId.New();
        var first = await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(30), Ct);

        var ex = await Assert.ThrowsAsync<LocalCaExistsException>(
            async () => await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(30), Ct));

        Assert.Equal(rootId, ex.ExistingId);
        Assert.Equal(first.Sha256Fingerprint, ex.Info.Sha256Fingerprint);
    }

    [Fact]
    public async Task AStagedKeyOlderThanItsLifetimeIsDeletedByTheNextStaging()
    {
        var (abandoned, abandonedCsr) = await StageAsync();
        _vault.Time.Advance(CertificateIssuance.StagedKeyLifetime + TimeSpan.FromDays(1));

        await StageAsync();

        await using var db = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Single(await db.StagedKeys.ToListAsync(Ct));
        using var ca = TestCa.CreateRoot();
        using var leaf = ca.Issue(abandonedCsr.Der.ToArray());
        await Assert.ThrowsAsync<StagedKeyMismatchException>(async () => await Issuance.AttachChainAsync(abandoned, abandonedCsr.StagedKey, Pem(leaf), Ct));
    }

    [Fact]
    public async Task EditingAStagedRowsNamesMakesItsKeyFailToOpen()
    {
        using var ca = TestCa.CreateRoot();
        var (id, csr) = await StageAsync();
        await using (var db = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct))
        {
            var row = await db.StagedKeys.SingleAsync(Ct);
            row.Names = ["evil.example.com"];
            await db.SaveChangesAsync(Ct);
        }

        using var leaf = ca.Issue(csr.Der.ToArray(), names: ["evil.example.com"]);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf), Ct));
    }

    [Fact]
    public async Task AProcessWhoseMasterKeyWasRotatedElsewhereRefusesToSealAnything()
    {
        using var other = _vault.Another();
        await other.GetRequiredService<IVaultLifecycle>().InitializeAsync(Ct);
        await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct);
        using var newKek = Kek.Generate();
        await other.GetRequiredService<IVaultKekRotation>().RotateKekAsync(newKek.Encode().AsMemory(), Ct);

        // This process still holds the old key: every route that seals checks the database's key first and writes nothing.
        var selfSigned = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Issuance.CreateKeyAndCsrAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _vault.Get<ISecretStore>().PutAsync(SecretKind.DnsApiToken, new byte[] { 1, 2, 3 }, new SecretOptions(null, null, ["api.example.com"]), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IJwsSigner>().CreateAccountKeyAsync(AcmeAccountId.New(), JwsAlgorithm.ES256, Ct));

        Assert.Contains("master key changed", selfSigned.Message, StringComparison.Ordinal);
        await using var db = await _vault.Get<IDbContextFactory<VaultDbContext>>().CreateDbContextAsync(Ct);
        Assert.Single(await db.CertificateKeys.ToListAsync(Ct));
        Assert.Empty(await db.Secrets.ToListAsync(Ct));
        Assert.Empty(await db.AccountKeys.ToListAsync(Ct));
    }

    [Fact]
    public async Task AWriteThatFailsMidReplacementLeavesTheOldPairAndTheStagedKey()
    {
        using var ca = TestCa.CreateRoot();
        var id = CertificateId.New();
        var original = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var before = await Serving.LoadForServingAsync(id, Ct);
        var csr = await Issuance.CreateKeyAndCsrAsync(id, KeySpec.EcdsaP256, AppNames, Ct);

        // The database refuses the row's update, after the staged row was already scheduled for deletion.
        var factory = _vault.Get<IDbContextFactory<VaultDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER refuse_update BEFORE UPDATE ON CertificateKeys BEGIN SELECT RAISE(ABORT, 'refused'); END;", Ct);
        }

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            async () => await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP384, ["other.example.com"], TimeSpan.FromDays(30), Ct));
        using var leaf = ca.Issue(csr.Der.ToArray());
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () => await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf), Ct));

        Assert.Same(before, await Serving.LoadForServingAsync(id, Ct));
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var row = await db.CertificateKeys.SingleAsync(Ct);
            Assert.Equal(1, row.Version);
            Assert.Equal(original.PublicChainPem, row.ChainPem);
            Assert.Equal(csr.StagedKey, (await db.StagedKeys.SingleAsync(Ct)).StagedKeyId);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER refuse_update;", Ct);
        }

        // The staged key survived, so the same token still completes the renewal.
        var renewed = await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf), Ct);
        Assert.NotEqual(original.Sha256Fingerprint, renewed.Sha256Fingerprint);
    }

    [Fact]
    public async Task AnInterruptedRotationsBackupKeyIsNamedWhenTheMasterKeyDoesNotMatch()
    {
        _vault.Variables.Remove(KekProvider.KeyVariable);
        await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct);
        var keyFile = _vault.Data.File("master.key");
        var original = await File.ReadAllTextAsync(keyFile, Ct);
        _vault.Restart();

        // A rotation that got as far as moving the new key in, then died: the old key is still beside it.
        await File.WriteAllTextAsync(_vault.Data.File("master.key.old"), original, Ct);
        using var other = Kek.Generate();
        await File.WriteAllTextAsync(keyFile, other.Encode() + "\n", Ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_vault.Data.File("master.key.old"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _vault.Get<IVaultLifecycle>().InitializeAsync(Ct));

        Assert.Contains("master.key.old", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not match", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextsStayUsableAfterTheParsedIntermediatesAreDisposed()
    {
        using var root = TestCa.CreateRoot();
        using var intermediate = root.CreateIntermediate();
        var (id, csr) = await StageAsync();
        using var leaf = intermediate.Issue(csr.Der.ToArray());
        await Issuance.AttachChainAsync(id, csr.StagedKey, Pem(leaf, intermediate.Certificate), Ct);

        var context = await Serving.LoadForServingAsync(id, Ct);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var served = Assert.Single(context!.IntermediateCertificates);
        Assert.Equal(intermediate.Certificate.RawData, served.RawData);
    }
}
