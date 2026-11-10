using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Data.Sqlite;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>
/// The vault describing what it holds after the config database is lost: the process registers the certificates again
/// at startup, and a restored Specific binding serves the certificate it named.
/// </summary>
public sealed class CertificateRecoveryEndToEndTests
{
    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>The recovery's completed one run (an awaited start step): its result, or an exception when it was skipped or failed.</summary>
    private static async Task<CertificateRecoveryResult> RecoveredAsync(PaduraksaHost host) =>
        await host.Shared.RecoveryCompletion.Finished.WaitAsync(TimeSpan.FromSeconds(30), Ct)
            ?? throw new InvalidOperationException("The startup recovery did not run to the end.");

    [Fact]
    public async Task ADatabaseLostWhileTheVaultKeepsItsKeysGetsTheCertificatesBackAndServesThemAgain()
    {
        var data = TestDataDirectories.NewPath();
        CertificateSummary root;
        CertificateSummary leaf;
        CertificateSummary selfSigned;
        string adminFingerprint;
        await using (var first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            var operations = first.Shared.CertificateOperations;
            root = (await operations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct)).Certificate!;
            leaf = (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["local.test"]), Origin, Ct)).Certificate!;
            selfSigned = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["self.test"]), Origin, Ct)).Certificate!;
            adminFingerprint = (await first.Shared.CertificateService.GetAsync(AdminCertificate.Id, Ct))!.Summary.Sha256Fingerprint;
            var bound = RespondHost("l", "local.test", "ok") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = leaf.Id } };
            await CommitAsync(first, new UpsertHostCommand(bound), Ct);
        }

        // The database goes; the vault's file, its master key and the last-known-good config stay.
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(data, ConfigDatabase.FileName + "*"))
        {
            File.Delete(file);
        }

        await using var host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);

        var recovery = await RecoveredAsync(host);
        Assert.Empty(recovery.Failed);
        // The admin listener's own certificate is in the vault too: the recovery skips it and the admin
        // certificate service adopts it, so only the certificates this test made are pinned to the recovery's list.
        Assert.Contains(leaf.Id, recovery.Recovered);
        Assert.Contains(selfSigned.Id, recovery.Recovered);
        var inventory = await host.Shared.CertificateService.ListAsync(null, Ct);
        Assert.Equal(CertificateSource.LocalCaRoot, inventory.Single(c => c.Id == root.Id).Source);
        Assert.Equal(CertificateSource.LocalCa, inventory.Single(c => c.Id == leaf.Id).Source);
        Assert.Equal(CertificateSource.Uploaded, inventory.Single(c => c.Id == selfSigned.Id).Source);
        Assert.All(
            new[] { root.Id, leaf.Id, selfSigned.Id },
            id => Assert.Equal((CertificateStatus.Active, CertificateRecovery.Label), (inventory.Single(c => c.Id == id).Status, inventory.Single(c => c.Id == id).Label)));

        // The admin listener's own certificate was adopted from the vault, not re-issued: the served fingerprint survives.
        var adminLeaf = Assert.Single(await host.Shared.CertificateService.ListAsync(null, Ct), c => c.Id == AdminCertificate.Id);
        Assert.Equal((CertificateStatus.Active, AdminCertificate.Label), (adminLeaf.Status, adminLeaf.Label));
        Assert.Equal(adminFingerprint, adminLeaf.Sha256Fingerprint);
        Assert.Equal(leaf.Sha256Fingerprint, inventory.Single(c => c.Id == leaf.Id).Sha256Fingerprint);
        Assert.Equal(["local.test"], inventory.Single(c => c.Id == leaf.Id).Names);
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, "local.test")).Certificate));
        Assert.Empty(host.Shared.Config.Warnings);

        // The recovered local CA is the local CA: asking for it again finds it instead of making another.
        var again = await host.Shared.CertificateOperations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct);
        Assert.Equal(root.Id, again.Certificate!.Id);
        Assert.Equal(CertificateOperationStatus.Unchanged, again.Status);
    }

    [Fact]
    public async Task TheFirstRequestAfterALostDatabaseIsServedTheVaultHeldCertificate()
    {
        var data = TestDataDirectories.NewPath();
        CertificateSummary leaf;
        await using (var first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            leaf = (await first.Shared.CertificateOperations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["first.test"]), Origin, Ct)).Certificate!;
            var bound = RespondHost("f", "first.test", "ok") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = leaf.Id } };
            await CommitAsync(first, new UpsertHostCommand(bound), Ct);
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(data, ConfigDatabase.FileName + "*"))
        {
            File.Delete(file);
        }

        // A generous bound so the awaited step cannot time out and continue in the background on a loaded machine: this
        // test is precisely about the recovery having finished before StartAsync returns.
        Dictionary<string, string?> settings = new(NoPlaceholder, StringComparer.OrdinalIgnoreCase)
        {
            ["Paduraksa:Certificates:RecoveryStartupTimeout"] = "00:01:00",
        };
        await using var host = TestHost.Create(settings: settings, dataDirectory: data);
        await host.StartAsync(Ct);

        // No wait for RecoveryCompletion: the recovery is an awaited start step, and the publish it queues is part of the
        // first snapshot. The very first HTTPS request must already get the vault-held leaf, not the default or a rejection.
        Assert.True(host.Shared.RecoveryCompletion.Finished.IsCompleted, "the recovery should complete before the start returns");
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, "first.test")).Certificate));
    }

    [Fact]
    public async Task ARestartThatLostNothingChangesNothing()
    {
        var data = TestDataDirectories.NewPath();
        CertificateSummary made;
        await using (var first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            made = (await first.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["same.test"], Label: "mine"), Origin, Ct)).Certificate!;
        }

        await using var host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);
        var recovery = await RecoveredAsync(host);

        Assert.Empty(recovery.Recovered);
        var only = Assert.Single(await host.Shared.CertificateService.ListAsync(new CertificateQuery(Source: CertificateSource.SelfSigned), Ct));
        Assert.Equal((made.Id, CertificateSource.SelfSigned, "mine"), (only.Id, only.Source, only.Label));
    }
}
