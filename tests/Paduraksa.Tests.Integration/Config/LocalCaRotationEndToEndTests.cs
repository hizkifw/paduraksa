using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Integration.Hosting;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;
using static Paduraksa.Tests.Integration.Config.VaultCertificatesEndToEndTests;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>The local CA's name constraints and its rotation, through a real host: what the proxy serves during the overlap and after.</summary>
public sealed class LocalCaRotationEndToEndTests
{
    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static X509Certificate2 RootOf(string pem)
    {
        var fields = PemEncoding.Find(pem);
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pem[fields.Base64Data].Replace("\n", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARootMadeAtFirstRunIsUnlimitedAndRotatingCanLimitIt()
    {
        await using var host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        var operations = host.Shared.CertificateOperations;
        _ = await operations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct);

        // The process's own admin certificate made the root, and nobody chose names: it may issue for anything.
        var status = await operations.GetLocalCaAsync(Ct);
        Assert.NotNull(status.Root);
        Assert.Empty(status.PermittedNames);
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["www.example.com", "203.0.113.9"]), Origin, Ct)).Status);
        // What an operator who limits it is offered: the private-use defaults, the admin names, and what the CA has issued.
        Assert.Contains("home.arpa", status.SuggestedDomains);
        Assert.Contains("192.168.0.0/16", status.SuggestedAddresses);
        Assert.Contains("localhost", status.SuggestedDomains);
        Assert.Contains("127.0.0.1", status.SuggestedAddresses);
        var limited = await operations.RotateLocalCaAsync(new LocalCaRotateRequest(PermittedDomains: ["example.org", "lan"], PermittedAddresses: ["192.168.0.0/16"]), Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Completed, limited.Status);

        var after = await operations.GetLocalCaAsync(Ct);
        Assert.Contains("DNS:example.org", after.PermittedNames);
        Assert.Contains("DNS:localhost", after.PermittedNames); // the admin certificate's names are always kept
        var refused = await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["www.example.com"]), Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Invalid, refused.Status);
        var error = Assert.Single(refused.Errors);
        Assert.Equal(CertificateErrorCodes.LocalCaNameNotPermitted, error.Code);
        Assert.Contains("www.example.com", error.Message, StringComparison.Ordinal);
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["nas.lan", "192.168.1.5"]), Origin, Ct)).Status);

        // Choosing no names makes the next root unlimited again.
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.RotateLocalCaAsync(new LocalCaRotateRequest(), Origin, Ct)).Status);
        Assert.Empty((await operations.GetLocalCaAsync(Ct)).PermittedNames);
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["www.example.com"]), Origin, Ct)).Status);
    }

    [Fact]
    public async Task ALimitedRotationPermitsTheNamesTheAdminCertificateIsWantedForNowNotTheOnesItWasIssuedFor()
    {
        await using var host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        var operations = host.Shared.CertificateOperations;
        _ = await operations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct);
        var stored = (await host.Shared.CertificateService.GetAsync(AdminCertificate.Id, Ct))!.Summary.Names;
        Assert.DoesNotContain("renamed-machine", stored);
        // The machine was renamed (or an admin name added) since the certificate was issued.
        operations.DesiredAdminNames = () => [.. stored, "renamed-machine"];

        var rotated = await operations.RotateLocalCaAsync(new LocalCaRotateRequest(PermittedDomains: ["example.org"]), Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, rotated.Status);
        Assert.Empty(rotated.Warnings);
        Assert.Contains("DNS:renamed-machine", (await operations.GetLocalCaAsync(Ct)).PermittedNames);
        // The new root was able to issue the certificate for the renamed machine (no warning above); the service that owns the certificate then
        // serves it again for its own idea of the names, which this test can't change, so the stored names aren't compared.
        Assert.Equal(rotated.Certificate!.Subject, (await host.Shared.CertificateService.GetAsync(AdminCertificate.Id, Ct))!.Summary.Issuer);
    }

    [Fact]
    public async Task RotatingKeepsTheOldChainServingUntilItsCertificatesAreIssuedAgainThenTheOldRootIsRetired()
    {
        await using var host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        var operations = host.Shared.CertificateOperations;
        var (oldRoot, leaf, oldRootCertificate) = await IssueLocalAsync(host, "vault.test");
        await CommitAsync(host, new UpsertHostCommand(RespondHost("v", "vault.test", "ok") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = leaf.Id } }), Ct);
        Assert.True((await HandshakeAsync(host, "vault.test", oldRootCertificate)).ChainsToRoot);

        var rotated = await operations.RotateLocalCaAsync(new LocalCaRotateRequest(PermittedDomains: ["example.org", "test"]), Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, rotated.Status);
        var newRoot = rotated.Certificate!;
        Assert.NotEqual(oldRoot.Id, newRoot.Id);
        Assert.NotEqual(oldRoot.Subject, newRoot.Subject);
        var status = await operations.GetLocalCaAsync(Ct);
        Assert.Equal(newRoot.Id, status.Root!.Id);
        Assert.Contains("DNS:example.org", status.PermittedNames);
        var retiring = Assert.Single(status.Retiring);
        Assert.Equal(oldRoot.Id, retiring.Root.Id);
        Assert.Equal(1, retiring.Leaves); // the host's certificate; the admin listener's own was issued again at once

        // The overlap: the proxy still serves the certificate the old root signed, and it chains to the old root only.
        var newRootCertificate = RootOf((await host.Shared.CertificateService.GetAsync(newRoot.Id, Ct))!.PublicChainPem);
        var overlap = await HandshakeAsync(host, "vault.test", oldRootCertificate);
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint(overlap.Certificate));
        Assert.True(overlap.ChainsToRoot);
        Assert.False((await HandshakeAsync(host, "vault.test", newRootCertificate)).ChainsToRoot);
        // Both roots are offered for download.
        Assert.NotNull(await host.Shared.CertificateService.GetAsync(oldRoot.Id, Ct));
        Assert.NotNull(await host.Shared.CertificateService.GetAsync(newRoot.Id, Ct));

        // The admin certificate came from the new root.
        var admin = (await host.Shared.CertificateService.GetAsync(AdminCertificate.Id, Ct))!.Summary;
        Assert.Equal(newRoot.Subject, admin.Issuer);

        // The old root can't be retired while something it signed is still in use.
        var refused = await operations.DeleteAsync(oldRoot.Id, Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Invalid, refused.Status);
        Assert.Equal(CertificateErrorCodes.LocalCaInUse, Assert.Single(refused.Errors).Code);
        // The current root can't be deleted while its own certificates exist either.
        Assert.Equal(CertificateOperationStatus.Invalid, (await operations.DeleteAsync(newRoot.Id, Origin, Ct)).Status);

        var reissued = await operations.ReissueLocalCaLeavesAsync(Origin, Ct);

        Assert.Equal(1, reissued.Reissued);
        Assert.Empty(reissued.Failures);
        var after = await HandshakeAsync(host, "vault.test", newRootCertificate);
        Assert.NotEqual(leaf.Sha256Fingerprint, Fingerprint(after.Certificate));
        Assert.True(after.ChainsToRoot);

        var retired = await operations.DeleteAsync(oldRoot.Id, Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, retired.Status);
        Assert.Empty((await operations.GetLocalCaAsync(Ct)).Retiring);
        Assert.Null(await host.Shared.CertificateService.GetAsync(oldRoot.Id, Ct));
        Assert.Equal(CertificateOperationStatus.Completed, (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["x.example.org"]), Origin, Ct)).Status);
        Assert.Equal(CertificateOperationStatus.Invalid, (await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest(["x.example.com"]), Origin, Ct)).Status);
    }

    [Fact]
    public async Task ARotationWithBadConstraintsOrNoRootChangesNothing()
    {
        await using var host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        var operations = host.Shared.CertificateOperations;
        var before = (await operations.GetLocalCaAsync(Ct)).Root;

        var bad = await operations.RotateLocalCaAsync(new LocalCaRotateRequest(PermittedDomains: ["not a domain"]), Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Invalid, bad.Status);
        Assert.Equal(CertificateErrorCodes.LocalCaConstraintsInvalid, Assert.Single(bad.Errors).Code);
        Assert.Equal(before?.Id, (await operations.GetLocalCaAsync(Ct)).Root?.Id);
    }
}
