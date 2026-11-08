using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Cli;
using Paduraksa.Server.Configuration;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Config;

/// <summary>
/// The vault through a real <see cref="PaduraksaHost"/> (PLAN §8, §9): certificates the application creates through the vault are
/// what a TLS client gets from the public listener, the vault must start or the process doesn't, the placeholder is a switch,
/// and the CLI rotates the master key.
/// </summary>
public sealed class VaultCertificatesEndToEndTests
{
    private static readonly Dictionary<string, string?> NoPlaceholder = new() { ["Paduraksa:Development:PlaceholderCertificate"] = "false" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A TLS handshake with the public listener for <paramref name="serverName"/>: the certificate it presented, and whether it chains to <paramref name="root"/>.</summary>
    internal static async Task<(X509Certificate2 Certificate, bool ChainsToRoot)> HandshakeAsync(PaduraksaHost host, string serverName, X509Certificate2? root = null)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Public, "https").Port, Ct);
        X509Certificate2? presented = null;
        bool chains = false;
        await using SslStream ssl = new(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, chain, _) =>
            {
                presented = new X509Certificate2(certificate!);
                if (root is not null)
                {
                    using X509Chain verify = new();
                    verify.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    verify.ChainPolicy.CustomTrustStore.Add(root);
                    verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    foreach (X509ChainElement element in chain!.ChainElements)
                    {
                        verify.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }

                    chains = verify.Build(presented);
                }

                return certificate is not null;
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, Ct);
        return (presented!, chains);
    }

    /// <summary>The first certificate of a PEM chain (public data).</summary>
    private static X509Certificate2 FirstCertificate(string pem)
    {
        PemFields fields = PemEncoding.Find(pem);
        return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(pem[fields.Base64Data].Replace("\n", string.Empty, StringComparison.Ordinal)));
    }

    /// <summary>The handshake for <paramref name="serverName"/> is refused by the server (no certificate to answer with), not by anything else.</summary>
    private static async Task AssertRejectedAsync(PaduraksaHost host, string serverName)
    {
        Exception? failure = await Record.ExceptionAsync(async () => await HandshakeAsync(host, serverName));
        Assert.True(failure is AuthenticationException or IOException, failure?.ToString() ?? "the handshake succeeded");
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private static async Task<(CertificateSummary Root, CertificateSummary Leaf, X509Certificate2 RootCertificate)> IssueLocalAsync(PaduraksaHost host, string name)
    {
        CertificateOperations operations = host.Shared.CertificateOperations;
        CertificateOperationResult root = await operations.EnsureLocalCaAsync(new LocalCaRequest(), Origin, Ct);
        CertificateOperationResult leaf = await operations.IssueFromLocalCaAsync(new LocalCaLeafRequest([name]), Origin, Ct);
        // The process's own admin certificate may have created the local CA root already (PLAN §3), so "ensure" can be Unchanged.
        Assert.True(root.Status is CertificateOperationStatus.Completed or CertificateOperationStatus.Unchanged, root.Status.ToString());
        Assert.Equal(CertificateOperationStatus.Completed, leaf.Status);
        CertificateDetail rootDetail = (await host.Shared.CertificateService.GetAsync(root.Certificate!.Id, Ct))!;
        return (root.Certificate, leaf.Certificate!, FirstCertificate(rootDetail.PublicChainPem));
    }

    [Fact]
    public async Task AVaultIssuedCertificateIsWhatTheProxyServes()
    {
        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        (CertificateSummary root, CertificateSummary leaf, X509Certificate2 rootCertificate) = await IssueLocalAsync(host, "vault.test");
        await CommitAsync(host, new UpsertHostCommand(RespondHost("v", "vault.test", "ok")), Ct);

        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "vault.test", rootCertificate);

        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint(presented));
        Assert.True(chains, "the served certificate chains to the local CA root");
        Assert.Equal(CertificateSource.LocalCaRoot, root.Source);
        Assert.Empty(host.Shared.Config.Warnings);
    }

    [Fact]
    public async Task AReissueUnderTheSameIdIsServedWithoutANewRevision()
    {
        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        (_, CertificateSummary leaf, X509Certificate2 rootCertificate) = await IssueLocalAsync(host, "vault.test");
        HostConfig bound = RespondHost("v", "vault.test", "ok") with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = leaf.Id } };
        RevisionRef revision = await CommitAsync(host, new UpsertHostCommand(bound), Ct);
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint((await HandshakeAsync(host, "vault.test", rootCertificate)).Certificate));

        CertificateOperationResult again = await host.Shared.CertificateOperations.ReissueAsync(leaf.Id, null, Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, again.Status);
        Assert.NotEqual(leaf.Sha256Fingerprint, again.Certificate!.Sha256Fingerprint);
        Assert.Equal(revision, (await host.Shared.Config.GetCurrentAsync(Ct)).Revision);
        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "vault.test", rootCertificate);
        Assert.Equal(again.Certificate.Sha256Fingerprint, Fingerprint(presented));
        Assert.True(chains);

        // A certificate a Specific binding uses can't be deleted; the key stays and keeps serving.
        CertificateOperationResult refused = await host.Shared.CertificateOperations.DeleteAsync(leaf.Id, Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Invalid, refused.Status);
        Assert.Equal(again.Certificate.Sha256Fingerprint, Fingerprint((await HandshakeAsync(host, "vault.test", rootCertificate)).Certificate));
    }

    [Fact]
    public async Task ADeletedCertificateIsNotServedAndItsHostsFallBackToNothing()
    {
        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        CertificateOperationResult made = await host.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["gone.test"]), Origin, Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("g", "gone.test", "ok")), Ct);
        Assert.Equal(made.Certificate!.Sha256Fingerprint, Fingerprint((await HandshakeAsync(host, "gone.test")).Certificate));

        CertificateOperationResult deleted = await host.Shared.CertificateOperations.DeleteAsync(made.Certificate.Id, Origin, Ct);

        Assert.Equal(CertificateOperationStatus.Completed, deleted.Status);
        Assert.True(deleted.Publish!.Succeeded);
        await AssertRejectedAsync(host, "gone.test");
    }

    [Fact]
    public async Task VaultCertificatesSurviveARestart()
    {
        string data = TestDataDirectories.NewPath();
        string fingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: NoPlaceholder, dataDirectory: data))
        {
            await first.StartAsync(Ct);
            CertificateOperationResult made = await first.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["again.test"]), Origin, Ct);
            fingerprint = made.Certificate!.Sha256Fingerprint;
            await CommitAsync(first, new UpsertHostCommand(RespondHost("a", "again.test", "ok")), Ct);
        }

        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder, dataDirectory: data);
        await host.StartAsync(Ct);

        Assert.Equal(fingerprint, Fingerprint((await HandshakeAsync(host, "again.test")).Certificate));
    }

    [Fact]
    public async Task AWrongMasterKeyStopsTheProcessWithExitCode2BeforeAnythingListens()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(dataDirectory: data))
        {
            await first.StartAsync(Ct);
        }

        // vault.db now belongs to the generated master.key; put another key in its place.
        await File.WriteAllTextAsync(Path.Combine(data, "master.key"), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "\n", Ct);
        EventLog events = new();
        LogCapture logs = new();
        using StringWriter error = new();

        int exitCode = await PaduraksaCli.ServeAsync(TestHost.CreateOptions(events, dataDirectory: data, logs: logs), error, includeStackTrace: false, Ct);

        Assert.Equal(ExitCodes.StartupFailed, exitCode);
        Assert.Contains(logs.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Critical && e.Message.Contains("vault can't start", StringComparison.Ordinal) && e.Message.Contains("does not match", StringComparison.Ordinal));
        Assert.DoesNotContain(events.Events, e => e is "Public:start" or "Admin:start" or "Core:start");
    }

    [Fact]
    public async Task WithoutThePlaceholderAHostWithoutACertificateGetsNoHandshake()
    {
        await using PaduraksaHost host = TestHost.Create(settings: NoPlaceholder);
        await host.StartAsync(Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("n", "nocert.test", "ok")), Ct);

        await AssertRejectedAsync(host, "nocert.test");
        Assert.Contains(host.Shared.Config.Warnings, w => w.Code == "certificate.auto.noCoverage");
    }

    [Fact]
    public async Task WithThePlaceholderOnItIsTheFallbackAndAVaultCertificateWinsForItsNames()
    {
        await using PaduraksaHost host = TestHost.Create();
        await host.StartAsync(Ct);
        CertificateOperationResult made = await host.Shared.CertificateOperations.IssueSelfSignedAsync(new SelfSignedCertificateRequest(["mine.test"]), Origin, Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("m", "mine.test", "ok")), Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("o", "other.test", "ok")), Ct);

        Assert.Equal(made.Certificate!.Sha256Fingerprint, Fingerprint((await HandshakeAsync(host, "mine.test")).Certificate));
        Assert.Equal("Paduraksa placeholder", (await HandshakeAsync(host, "other.test")).Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
    }

    [Fact]
    public void ThePlaceholderIsOffByDefaultInAReleaseBuildAndOnInADebugBuild()
    {
#if DEBUG
        Assert.True(new PaduraksaOptions().Development.PlaceholderCertificate);
#else
        Assert.False(new PaduraksaOptions().Development.PlaceholderCertificate);
#endif
    }
}
