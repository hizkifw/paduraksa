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
/// The vault describing what it holds after the config database is lost (PLAN §9.2): the process registers the certificates again
/// at startup, and a restored Specific binding serves the certificate it named.
/// </summary>
public sealed class CertificateRecoveryEndToEndTests
{
    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>The recovery runs while the web apps start: waits for its one run to finish (every registration has gone live by then) and returns what it did.</summary>
    private static async Task<CertificateRecoveryResult> RecoveredAsync(PaduraksaHost host) =>
        await host.Shared.RecoveryCompletion.Finished.WaitAsync(TimeSpan.FromSeconds(30), Ct)
            ?? throw new InvalidOperationException("The startup recovery did not run to the end.");

    [Fact]
    public async Task ADatabaseLostWhileTheVaultKeepsItsKeysGetsTheCertificatesBackAndServesThemAgain()
    {
        string data = TestDataDirectories.NewPath();
        CertificateSummary root;
        CertificateSummary leaf;
        CertificateSummary selfSigned;
        await using (PaduraksaHost first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            CertificateOperations operations = first.Shared.CertificateOperations;
            root = (await operations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct)).Certificate!;
            leaf = (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["local.test"]), Origin, Ct)).Certificate!;
            selfSigned = (await operations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["self.test"]), Origin, Ct)).Certificate!;
            HostConfig bound = RespondHost("l", "local.test", "ok") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = leaf.Id } };
            await CommitAsync(first, new UpsertHostCommand(bound), Ct);
        }

        // The database goes; the vault's file, its master key and the last-known-good config stay.
        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(data, ConfigDatabase.FileName + "*"))
        {
            File.Delete(file);
        }

        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);

        CertificateRecoveryResult recovery = await RecoveredAsync(host);
        Assert.Equal(3, recovery.Recovered.Count);
        Assert.Empty(recovery.Failed);
        IReadOnlyList<CertificateSummary> inventory = await host.Shared.CertificateService.ListAsync(null, Ct);
        Assert.Equal(3, inventory.Count);
        Assert.All(inventory, c => Assert.Equal((CertificateStatus.Active, CertificateRecovery.Label), (c.Status, c.Label)));
        Assert.Equal(CertificateSource.LocalCaRoot, inventory.Single(c => c.Id == root.Id).Source);
        Assert.Equal(CertificateSource.LocalCa, inventory.Single(c => c.Id == leaf.Id).Source);
        Assert.Equal(CertificateSource.Uploaded, inventory.Single(c => c.Id == selfSigned.Id).Source);
        Assert.Equal(leaf.Sha256Fingerprint, inventory.Single(c => c.Id == leaf.Id).Sha256Fingerprint);
        Assert.Equal(["local.test"], inventory.Single(c => c.Id == leaf.Id).Names);
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, "local.test")).Certificate));
        Assert.Empty(host.Shared.Config.Warnings);

        // The recovered local CA is the local CA: asking for it again finds it instead of making another.
        CertificateOperationResult again = await host.Shared.CertificateOperations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct);
        Assert.Equal(root.Id, again.Certificate!.Id);
        Assert.Equal(CertificateOperationStatus.Unchanged, again.Status);
    }

    [Fact]
    public async Task ARestartThatLostNothingChangesNothing()
    {
        string data = TestDataDirectories.NewPath();
        CertificateSummary made;
        await using (PaduraksaHost first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            made = (await first.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["same.test"], Label: "mine"), Origin, Ct)).Certificate!;
        }

        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);
        CertificateRecoveryResult recovery = await RecoveredAsync(host);

        Assert.Empty(recovery.Recovered);
        CertificateSummary only = Assert.Single(await host.Shared.CertificateService.ListAsync(null, Ct));
        Assert.Equal((made.Id, CertificateSource.SelfSigned, "mine"), (only.Id, only.Source, only.Label));
    }
}
