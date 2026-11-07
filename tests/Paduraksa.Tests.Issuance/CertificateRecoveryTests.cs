using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// <see cref="CertificateRecovery"/> over the real inventory and config writer, with real certificates in a stand-in vault: what comes back
/// after a database loss, as what, and what must not come back.
/// </summary>
public sealed class CertificateRecoveryTests
{
    private static readonly string[] LeafNames = ["leaf.example.com"];

    /// <summary>A CA certificate and the key that signs with it.</summary>
    private sealed record Ca(X509Certificate2 Certificate, ECDsa Key) : IDisposable
    {
        public static Ca Create(string subject)
        {
            var key = ECDsa.Create();
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return new Ca(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5)), key);
        }

        public X509Certificate2 Issue(string[] names)
        {
            using var leafKey = ECDsa.Create();
            var request = new CertificateRequest($"CN={names[0]}", leafKey, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in names)
            {
                san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
            return request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(Key), DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), RandomNumberGenerator.GetBytes(8));
        }

        public void Dispose()
        {
            Certificate.Dispose();
            Key.Dispose();
        }
    }

    private static CertificateInfo InfoOf(X509Certificate2 certificate, params string[] names) => new(
        certificate.SubjectName.Name,
        names,
        new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
        certificate.IssuerName.Name,
        certificate.SerialNumber,
        null,
        Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)),
        "ECDSA P-256",
        [certificate.IssuerName.Name],
        certificate.ExportCertificatePem() + "\n");

    private static CertificateRecovery RecoveryOf(IssuanceHarness h) =>
        new(h.Certificates, h.Vault, h.Vault, h.Loggers.CreateLogger<CertificateRecovery>());

    [Fact]
    public async Task EachCertificateTheInventoryLostComesBackWithTheSourceItsFactsProve()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        using var ca = Ca.Create("CN=Local CA");
        using var impostor = Ca.Create("CN=Local CA");
        using var genuine = ca.Issue(LeafNames);
        using var lookalike = impostor.Issue(["other.example.com"]);
        CertificateId rootId = CertificateId.New(), leafId = CertificateId.New(), lookalikeId = CertificateId.New();
        h.Vault.Hold(rootId, InfoOf(ca.Certificate), isLocalCaRoot: true);
        h.Vault.Hold(leafId, InfoOf(genuine, LeafNames));
        h.Vault.Hold(lookalikeId, InfoOf(lookalike, "other.example.com"));

        var result = await RecoveryOf(h).RecoverAsync(Ct);

        Assert.Equal(3, result.Recovered.Count);
        Assert.Empty(result.Failed);
        Assert.Equal(CertificateSource.LocalCaRoot, (await h.CertificateAsync(rootId)).Source);
        Assert.Equal(CertificateSource.LocalCa, (await h.CertificateAsync(leafId)).Source);
        // The same issuer name is not the same issuer: the signature does not verify under the root's key.
        Assert.Equal(CertificateSource.Uploaded, (await h.CertificateAsync(lookalikeId)).Source);
        Assert.All(await h.Certificates.ListAsync(null, Ct), c => Assert.Equal((CertificateStatus.Active, CertificateRecovery.Label), (c.Status, c.Label)));
        Assert.Equal(3, (await h.AuditAsync("certificate.add")).Count);
    }

    [Fact]
    public async Task ACertificateThatHasARowIsLeftAloneAndASecondRunChangesNothing()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        using var ca = Ca.Create("CN=Local CA");
        using var certificate = ca.Issue(LeafNames);
        var id = CertificateId.New();
        var info = InfoOf(certificate, LeafNames);
        _ = await h.Certificates.RegisterAsync(new CertificateRegistration(id, CertificateSource.SelfSigned, info, CertificateStatus.Active, "mine"), Admin, Ct);
        h.Vault.Hold(id, info);
        var recovery = RecoveryOf(h);

        var first = await recovery.RecoverAsync(Ct);
        var second = await recovery.RecoverAsync(Ct);

        Assert.Empty(first.Recovered);
        Assert.Empty(second.Recovered);
        var row = await h.CertificateAsync(id);
        Assert.Equal((CertificateSource.SelfSigned, "mine"), (row.Source, row.Label));
        Assert.Single(await h.AuditAsync("certificate.add"));
    }

    [Fact]
    public async Task ACertificateDeletedOnPurposeWhoseKeyStayedIsNotBroughtBackAndItsKeyIsDeleted()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        using var ca = Ca.Create("CN=Local CA");
        using var certificate = ca.Issue(LeafNames);
        var id = CertificateId.New();
        var info = InfoOf(certificate, LeafNames);
        _ = await h.Certificates.RegisterAsync(new CertificateRegistration(id, CertificateSource.Uploaded, info), Admin, Ct);
        // The operator deletes it; the row goes (and the delete is audited), but the vault failed to delete the key.
        var deleted = await h.Certificates.DeleteAsync(id, Admin, Ct);
        Assert.Equal(CertificateChangeStatus.Committed, deleted.Status);
        h.Vault.Hold(id, info);

        var result = await RecoveryOf(h).RecoverAsync(Ct);

        Assert.Equal([id], result.Deleted);
        Assert.Empty(result.Recovered);
        Assert.Equal([id], h.Vault.Deleted);
        Assert.Empty(await h.Certificates.ListAsync(null, Ct));
        Assert.Null(await h.Vault.DescribeAsync(id, Ct));
    }

    [Fact]
    public async Task ACertificateTheVaultCannotDeleteIsReportedAndStaysForTheNextStart()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        using var ca = Ca.Create("CN=Local CA");
        using var certificate = ca.Issue(LeafNames);
        var id = CertificateId.New();
        var info = InfoOf(certificate, LeafNames);
        _ = await h.Certificates.RegisterAsync(new CertificateRegistration(id, CertificateSource.Uploaded, info), Admin, Ct);
        _ = await h.Certificates.DeleteAsync(id, Admin, Ct);
        h.Vault.Hold(id, info);
        var recovery = new CertificateRecovery(h.Certificates, h.Vault, new RefusesDeletion(), h.Loggers.CreateLogger<CertificateRecovery>());

        var result = await recovery.RecoverAsync(Ct);

        Assert.Equal([id], result.Failed);
        Assert.Empty(await h.Certificates.ListAsync(null, Ct));
    }

    private sealed class RefusesDeletion : ICertificateIssuance
    {
        public ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken) => throw new IOException("the vault is busy");

        public ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
