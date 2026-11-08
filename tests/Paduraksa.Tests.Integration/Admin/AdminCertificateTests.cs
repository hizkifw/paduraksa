using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

using Paduraksa.Admin.Certificates;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// The admin listener's certificate (PLAN §3, §10.1): issued by the local CA, served through the vault, kept current
/// without a restart, never the proxy's to serve, and offered as a root the owner trusts once. All against the real host,
/// vault and config database.
/// </summary>
public sealed class AdminCertificateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAdminListenerServesALocalCaLeafCoveringEveryWayIn()
    {
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"));
        await host.StartAsync(Ct);

        CertificateSummary leaf = await AdminCertificateAsync(host);
        Assert.Equal((AdminCertificate.Id, CertificateSource.LocalCa, AdminCertificate.Label), (leaf.Id, leaf.Source, leaf.Label));
        Assert.Equal(CertificateStatus.Active, leaf.Status);
        Assert.Contains("admin.test", leaf.Names);
        Assert.Contains("localhost", leaf.Names);
        Assert.Contains("127.0.0.1", leaf.Names);
        Assert.Contains("::1", leaf.Names);
        if (CertificateNames.Normalize(Environment.MachineName) is { } machine)
        {
            Assert.Contains(machine, leaf.Names);
        }

        X509Certificate2 root = await RootCertificateAsync(host);
        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "admin.test", root);

        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint(presented));
        Assert.True(chains, "the admin listener's certificate chains to the local CA root");
        Assert.Equal(root.Subject, presented.Issuer);
        Assert.Contains("admin.test", SubjectAlternativeNames(presented));
        Assert.Contains("127.0.0.1", SubjectAlternativeNames(presented));
    }

    [Fact]
    public async Task TheCaEndpointsServeTheRootBeforeAndAfterSetupAndItValidatesTheLeaf()
    {
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"));
        await host.StartAsync(Ct);
        using HttpClient client = TestHost.CreateAdminClient(host);
        Uri admin = TestHost.Endpoint(host, HostRole.Admin, "https");

        // Before setup: anonymous, open, and it is the certificate the listener serves.
        X509Certificate2 root = await RootCertificateAsync(client, admin);
        CertificateSummary leaf = await AdminCertificateAsync(host);
        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "admin.test", root);
        Assert.True(chains);
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint(presented));

        using (HttpResponseMessage pem = await client.GetAsync(new Uri(admin, "/api/v1/ca/root.pem"), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, pem.StatusCode);
            Assert.Equal("application/x-pem-file", pem.Content.Headers.ContentType!.MediaType);
            Assert.Equal(root.RawData, X509CertificateLoader.LoadCertificate(CaRoot.PemToDer(await pem.Content.ReadAsStringAsync(Ct))).RawData);
        }

        // The DER endpoint uses the type Android and iOS offer to install.
        using (HttpResponseMessage crt = await client.GetAsync(new Uri(admin, "/api/v1/ca/root.crt"), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, crt.StatusCode);
            Assert.Equal("application/x-x509-ca-cert", crt.Content.Headers.ContentType!.MediaType);
            Assert.Equal(root.RawData, await crt.Content.ReadAsByteArrayAsync(Ct));
        }

        // HEAD answers with the headers and no body.
        using (HttpRequestMessage head = new(HttpMethod.Head, new Uri(admin, "/api/v1/ca/root.pem")))
        using (HttpResponseMessage response = await client.SendAsync(head, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-pem-file", response.Content.Headers.ContentType!.MediaType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync(Ct));
        }

        using (HttpResponseMessage json = await client.GetAsync(new Uri(admin, "/api/v1/ca"), Ct))
        {
            Assert.Equal(HttpStatusCode.OK, json.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await json.Content.ReadAsStringAsync(Ct));
            Assert.Equal(Fingerprint(root), document.RootElement.GetProperty("sha256Fingerprint").GetString());
            Assert.Equal(root.Subject, document.RootElement.GetProperty("subject").GetString());
            Assert.True(document.RootElement.GetProperty("notAfter").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        }

        // Complete setup; the endpoints stay open (they are the wizard's own step).
        string token = await host.Shared.Setup.EnsureTokenAsync(Ct) ?? throw new InvalidOperationException("Setup is already complete.");
        SetupResult setup = await host.Shared.Setup.CompleteSetupAsync(token, "owner", "correct horse battery staple", null, new ClientContext(Channel.Api, IPAddress.Loopback), Ct);
        Assert.Equal(SetupOutcome.Succeeded, setup.Outcome);

        using HttpResponseMessage after = await client.GetAsync(new Uri(admin, "/api/v1/ca/root.crt"), Ct);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task ChangingTheNamesReissuesUnderTheSameId()
    {
        string data = TestDataDirectories.NewPath();
        string firstSerial;
        string rootFingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("one.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            firstSerial = (await AdminCertificateAsync(first)).SerialNumber;
            rootFingerprint = (await RootSummaryAsync(first)).Sha256Fingerprint;
        }

        await using PaduraksaHost host = TestHost.Create(settings: WithNames("one.test", "two.test"), dataDirectory: data);
        await host.StartAsync(Ct);

        CertificateSummary again = await AdminCertificateAsync(host);
        Assert.Equal(AdminCertificate.Id, again.Id);
        Assert.NotEqual(firstSerial, again.SerialNumber);
        Assert.Contains("two.test", again.Names);
        Assert.Equal(rootFingerprint, (await RootSummaryAsync(host)).Sha256Fingerprint);
    }

    [Fact]
    public async Task ARestartKeepsTheSameRootAndLeafAndDoesNotPublish()
    {
        string data = TestDataDirectories.NewPath();
        string rootFingerprint;
        string leafSerial;
        string leafFingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            rootFingerprint = (await RootSummaryAsync(first)).Sha256Fingerprint;
            CertificateSummary leaf = await AdminCertificateAsync(first);
            leafSerial = leaf.SerialNumber;
            leafFingerprint = leaf.Sha256Fingerprint;
        }

        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data);
        await host.StartAsync(Ct);

        Assert.Equal(rootFingerprint, (await RootSummaryAsync(host)).Sha256Fingerprint);
        CertificateSummary again = await AdminCertificateAsync(host);
        Assert.Equal((leafSerial, leafFingerprint), (again.SerialNumber, again.Sha256Fingerprint));

        // Preparing the process's own certificate registers it quietly: the startup snapshot stays generation 1.
        Assert.Equal(1, host.Shared.SnapshotStore.Current.Generation);
    }

    [Fact]
    public async Task ARenewalHotSwapsWithoutARestart()
    {
        string data = TestDataDirectories.NewPath();
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data);
        await host.StartAsync(Ct);
        X509Certificate2 root = await RootCertificateAsync(host);
        CertificateSummary before = await AdminCertificateAsync(host);
        string serial = (await HandshakeAsync(host, "admin.test", root)).Certificate.SerialNumber;

        // The stored certificate is suddenly near expiry (as if months passed): the next run re-issues it under the same ID.
        await ExecuteSqlAsync(data, $"UPDATE Certificates SET NotAfter = '2000-01-01T00:00:00.0000000Z' WHERE Sha256Fingerprint = '{before.Sha256Fingerprint}'", Ct);
        await host.Shared.AdminCertificate.EnsureAsync(Ct);

        CertificateSummary after = await AdminCertificateAsync(host);
        Assert.Equal(AdminCertificate.Id, after.Id);
        Assert.NotEqual(before.Sha256Fingerprint, after.Sha256Fingerprint);

        // The listener swapped without a restart: the next handshake gets the new certificate, still chaining to the root.
        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "admin.test", root);
        Assert.NotEqual(serial, presented.SerialNumber);
        Assert.Equal(after.Sha256Fingerprint, Fingerprint(presented));
        Assert.True(chains);
    }

    [Fact]
    public async Task TheManagedAdminCertificateCannotBeDeletedOrReissued()
    {
        string data = TestDataDirectories.NewPath();
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data);
        await host.StartAsync(Ct);

        CertificateOperationResult deleted = await host.Shared.CertificateOperations.DeleteAsync(AdminCertificate.Id, Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Invalid, deleted.Status);
        Assert.Contains(deleted.Errors, error => error.Code == CertificateErrorCodes.Managed);

        CertificateOperationResult reissued = await host.Shared.CertificateOperations.ReissueAsync(AdminCertificate.Id, null, Origin, Ct);
        Assert.Equal(CertificateOperationStatus.Invalid, reissued.Status);
        Assert.Contains(reissued.Errors, error => error.Code == CertificateErrorCodes.Managed);

        // The refusal is audited like the in-use one, and the certificate still serves.
        long refused = Convert.ToInt64(await ScalarAsync(data, $"SELECT count(*) FROM AuditEvents WHERE \"Action\" = 'certificate.delete' AND \"Outcome\" = 'invalid' AND \"Target\" = '{AdminCertificate.Id}'", Ct));
        Assert.Equal(1L, refused);
        Assert.Equal(AdminCertificate.Id, (await AdminCertificateAsync(host)).Id);
    }

    [Fact]
    public async Task TheAdminCertificateIsNeverServedByTheProxy()
    {
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.lan"));
        await host.StartAsync(Ct);

        // A host with the admin certificate's own name: Auto must not find it (the binding is refused by the validator, and
        // the compiler's inventory leaves it out), so the placeholder serves and the missing coverage is reported.
        await CommitAsync(host, new UpsertHostCommand(RespondHost("a", "admin.lan", "ok")), Ct);
        Assert.Equal("Paduraksa placeholder", (await VaultCertificatesEndToEndTests.HandshakeAsync(host, "admin.lan")).Certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false));
        Assert.Contains(host.Shared.Config.Warnings, warning => warning.Code == "certificate.auto.noCoverage");
    }

    [Fact]
    public async Task TheStartupLogNamesTheAdminUrlAndTheRootFingerprint()
    {
        LogCapture logs = new();
        Dictionary<string, string?> settings = WithNames("admin.test");
        settings["Logging:LogLevel:Default"] = "Information";
        // A fixed port: the log line names the configured one, which is 0 (OS-assigned) in the other tests. The probe can
        // lose a race with another test's listener, so a bind failure retries with a fresh port.
        PaduraksaHost host;
        int port;
        for (int attempt = 1; ; attempt++)
        {
            port = FreePort();
            settings["Paduraksa:Listen:Admin"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            host = TestHost.Create(settings: settings, logs: logs);
            try
            {
                await host.StartAsync(Ct);
                break;
            }
            catch (IOException ex) when (attempt < 5 && IsAddressInUse(ex))
            {
                await host.DisposeAsync();
            }
        }

        await using PaduraksaHost started = host;

        CertificateSummary root = await RootSummaryAsync(started);
        Assert.Equal(port, TestHost.Endpoint(started, HostRole.Admin, "https").Port);
        Assert.Contains(
            logs.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains($"https://admin.test:{port}/", StringComparison.Ordinal)
                && e.Message.Contains(root.Sha256Fingerprint, StringComparison.Ordinal)
                && e.Message.Contains("/api/v1/ca/root.crt", StringComparison.Ordinal));
    }

    /// <summary>A port nothing is listening on (bound and released): the admin listener keeps it, so the names are fixed.</summary>
    private static int FreePort()
    {
        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>The start failed because another listener took the probed port first (Kestrel wraps the socket error).</summary>
    private static bool IsAddressInUse(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public async Task ASlowFirstRunDoesNotHoldThePublicListenerAndKeepsServingTheVaultsLeaf()
    {
        // A second process on a data directory whose vault already holds the certificate: while its first run is held past the
        // bound, the admin port must serve that certificate — the placeholder would be the HSTS hard-fail (the browser pinned
        // the name when it trusted the root).
        string data = TestDataDirectories.NewPath();
        string fingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            fingerprint = (await AdminCertificateAsync(first)).Sha256Fingerprint;
        }

        Dictionary<string, string?> settings = WithNames("admin.test");
        settings["Paduraksa:Admin:CertificateStartupTimeout"] = "00:00:01";
        LogCapture logs = new();

        await using PaduraksaHost host = TestHost.Create(settings: settings, dataDirectory: data, logs: logs);
        // A first run that outlives the bound: the listeners must not wait for it.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Shared.AdminCertificate.FirstRunDelayForTests = cancellationToken => release.Task.WaitAsync(cancellationToken);

        await host.StartAsync(Ct);

        Assert.Equal(HostState.Running, host.State);
        Assert.Contains(host.GetListenAddresses(HostRole.Public), address => address.StartsWith("http://", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("was not ready within", StringComparison.Ordinal));

        // Still inside the timeout window (the run is held): the vault's certificate is what the admin port serves.
        (X509Certificate2 during, bool chains) = await HandshakeAsync(host, "admin.test", await RootCertificateAsync(host));
        Assert.Equal(fingerprint, Fingerprint(during));
        Assert.True(chains);

        // Let the run finish; nothing changes, because the vault already held the right certificate.
        release.TrySetResult();
        await host.Shared.AdminCertificate.EnsureAsync(Ct);
        Assert.Equal(fingerprint, Fingerprint((await HandshakeAsync(host, "admin.test")).Certificate));
    }

    [Fact]
    public async Task StoppingWhileTheFirstRunIsHeldReturnsPromptlyAndLogsNoError()
    {
        Dictionary<string, string?> settings = WithNames("admin.test");
        settings["Paduraksa:Admin:CertificateStartupTimeout"] = "00:00:01";
        LogCapture logs = new();

        await using PaduraksaHost host = TestHost.Create(settings: settings, logs: logs);
        // Held forever: only the stop can end it, which is what this test is about.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Shared.AdminCertificate.FirstRunDelayForTests = cancellationToken => release.Task.WaitAsync(cancellationToken);

        await host.StartAsync(Ct);
        Assert.Equal(HostState.Running, host.State);

        Stopwatch clock = Stopwatch.StartNew();
        await host.StopAsync(Ct);
        clock.Stop();

        // The background run is cancelled with the service, not left reading a vault and a config store that stop after it.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the stop took {clock.Elapsed}");
        Assert.Equal(HostState.Stopped, host.State);
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
        release.TrySetResult();
    }

    [Fact]
    public async Task AFailingConfigStoreKeepsServingTheVaultsAdminCertificate()
    {
        string data = TestDataDirectories.NewPath();
        string fingerprint;
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            fingerprint = (await AdminCertificateAsync(first)).Sha256Fingerprint;
            await CommitAsync(first, new UpsertHostCommand(RespondHost("app", "app.test", "ok")), Ct);
        }

        DamageConfigDatabase(data);
        LogCapture logs = new();
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data, logs: logs);
        await host.StartAsync(Ct);

        // The inventory step is skipped (the store refuses changes) and the vault's certificate keeps serving: a browser
        // that trusted the root must not get the self-signed placeholder under HSTS.
        VaultCertificate leaf = (await host.Shared.Catalog.DescribeAsync(AdminCertificate.Id, Ct))!;
        Assert.Equal(fingerprint, leaf.Info.Sha256Fingerprint);
        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "admin.test", await RootCertificateAsync(host));
        Assert.Equal(fingerprint, Fingerprint(presented));
        Assert.True(chains);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("refuses changes", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("admin certificate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyVaultWithAFailingConfigStoreServesThePlaceholderAndHasNoRootToDownload()
    {
        string data = TestDataDirectories.NewPath();
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            await CommitAsync(first, new UpsertHostCommand(RespondHost("app", "app.test", "ok")), Ct);
        }

        DamageConfigDatabase(data);
        // The vault goes too: nothing to serve and nothing to download, and the store can't make either.
        File.Delete(Path.Combine(data, "vault.db"));
        File.Delete(Path.Combine(data, "vault.db-wal"));
        File.Delete(Path.Combine(data, "vault.db-shm"));

        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data);
        await host.StartAsync(Ct);

        (X509Certificate2 presented, _) = await HandshakeAsync(host, "127.0.0.1");
        Assert.Equal(Fingerprint(host.Shared.ServerCertificate.Certificate), Fingerprint(presented));
        Assert.Empty(await host.Shared.Catalog.ListAsync(Ct));

        using HttpClient client = TestHost.CreateAdminClient(host, allowPlaceholder: true);
        Uri admin = TestHost.Endpoint(host, HostRole.Admin, "https");
        foreach (string path in new[] { "/api/v1/ca", "/api/v1/ca/root.pem", "/api/v1/ca/root.crt" })
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(admin, path), Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            ApiProblem problem = await AdminApiHarness.ProblemAsync(response, Ct);
            Assert.Equal("ca.notFound", problem.Code);
        }
    }

    [Fact]
    public async Task ALostVaultIsRepairedWithoutAnOperator()
    {
        // vault.db goes while the config database keeps its rows: the root and leaf rows are active, the vault holds neither.
        // The service must notice, retire the stale root and issue both again — every manual way out (deleting the root,
        // deleting the admin certificate, re-issuing it by hand) is refused, so without this the owner would be locked out.
        string data = TestDataDirectories.NewPath();
        string oldRoot;
        await using (PaduraksaHost first = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data))
        {
            await first.StartAsync(Ct);
            oldRoot = (await RootSummaryAsync(first)).Sha256Fingerprint;
            await CommitAsync(first, new UpsertHostCommand(RespondHost("app", "app.test", "ok")), Ct);
        }

        SqliteConnection.ClearAllPools();
        foreach (string file in Directory.GetFiles(data, "vault.db*"))
        {
            File.Delete(file);
        }

        LogCapture logs = new();
        await using PaduraksaHost host = TestHost.Create(settings: WithNames("admin.test"), dataDirectory: data, logs: logs);
        await host.StartAsync(Ct);

        CertificateSummary root = await RootSummaryAsync(host);
        Assert.Equal(CertificateStatus.Active, root.Status);
        Assert.NotEqual(oldRoot, root.Sha256Fingerprint);
        CertificateSummary leaf = await AdminCertificateAsync(host);
        Assert.Equal((AdminCertificate.Id, CertificateStatus.Active), (leaf.Id, leaf.Status));

        (X509Certificate2 presented, bool chains) = await HandshakeAsync(host, "admin.test", await RootCertificateAsync(host));
        Assert.Equal(leaf.Sha256Fingerprint, Fingerprint(presented));
        Assert.True(chains);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("has no key in the vault", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Level == LogLevel.Error);
    }

    private static Dictionary<string, string?> WithNames(params string[] names)
    {
        Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            settings[$"Paduraksa:Admin:CertificateNames:{i}"] = names[i];
        }

        return settings;
    }

    /// <summary>
    /// Makes the config database unusable while the last-known-good file stays: the start is degraded. Every pooled
    /// connection is closed first, so a handle to the file can neither survive nor keep serving the old contents.
    /// </summary>
    private static void DamageConfigDatabase(string data)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath(data) + "-wal");
        File.Delete(DatabasePath(data) + "-shm");
        File.WriteAllText(DatabasePath(data), "this isn't a SQLite database, it's a grocery list\n");
    }

    private static async Task<CertificateSummary> AdminCertificateAsync(PaduraksaHost host) =>
        await host.Shared.CertificateService.GetAsync(AdminCertificate.Id, Ct) is { } detail
            ? detail.Summary
            : throw new InvalidOperationException("The admin certificate is not in the inventory.");

    private static async Task<CertificateSummary> RootSummaryAsync(PaduraksaHost host) =>
        Assert.Single(await host.Shared.CertificateService.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot), Ct));

    private static async Task<X509Certificate2> RootCertificateAsync(PaduraksaHost host)
    {
        using HttpClient client = TestHost.CreateAdminClient(host);
        return await RootCertificateAsync(client, TestHost.Endpoint(host, HostRole.Admin, "https"));
    }

    private static async Task<X509Certificate2> RootCertificateAsync(HttpClient client, Uri admin)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(admin, "/api/v1/ca/root.crt"), Ct);
        response.EnsureSuccessStatusCode();
        return X509CertificateLoader.LoadCertificate(await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>A TLS handshake with the admin listener for <paramref name="serverName"/>: the certificate it presented, and whether it chains to <paramref name="root"/>.</summary>
    private static async Task<(X509Certificate2 Certificate, bool ChainsToRoot)> HandshakeAsync(PaduraksaHost host, string serverName, X509Certificate2? root = null)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Admin, "https").Port, Ct);
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
                    verify.ChainPolicy.DisableCertificateDownloads = true;
                    chains = verify.Build(presented);
                }

                return certificate is not null;
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, Ct);
        return (presented!, chains);
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private static List<string> SubjectAlternativeNames(X509Certificate2 certificate)
    {
        List<string> names = [];
        foreach (X509Extension extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == "2.5.29.17")
            {
                X509SubjectAlternativeNameExtension san = new(extension.RawData, extension.Critical);
                names.AddRange(san.EnumerateDnsNames());
                names.AddRange(san.EnumerateIPAddresses().Select(ip => ip.ToString()));
            }
        }

        return names;
    }
}
