using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Data.Sqlite;

using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Storage.Sqlite;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;

using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Acme;

/// <summary>
/// ACME issuance and renewal with the real stack against a validating Pebble (<see cref="PebbleCa"/>): a <see cref="PaduraksaHost"/> with the
/// real vault, config database, scheduler and public listener, which Pebble's validation authority reaches over HTTP to fetch the HTTP-01
/// answer from the gate. They skip when Docker or the Pebble image is unavailable (M3's exit criterion).
/// </summary>
public sealed class PebbleIssuanceEndToEndTests(PebbleRoot pebble) : IClassFixture<PebbleRoot>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    private Dictionary<string, string?> Settings(int httpPort, bool fastScheduler = false) => new()
    {
        ["Paduraksa:Development:PlaceholderCertificate"] = "false",
        ["Paduraksa:Listen:Http"] = httpPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Paduraksa:Acme:TrustedRootsFile"] = pebble.RootFile,
        ["Paduraksa:Acme:SchedulerStartDelay"] = fastScheduler ? "00:00:01" : "00:10:00",
        ["Paduraksa:Acme:SchedulerPollInterval"] = "00:00:01",
    };

    private static async Task<AcmeAccountSummary> CreateAccountAsync(PaduraksaHost host, PebbleCa ca)
    {
        var created = await host.Shared.AcmeAccounts.CreateAsync(
            new CreateAcmeAccountRequest(ca.Directory.AbsoluteUri, ["mailto:ops@example.com"], TermsOfServiceAgreed: true), Origin, Ct);
        Assert.True(created.Status == IssuanceChangeStatus.Committed, string.Join("; ", created.Errors.Select(e => e.Message)));
        return created.Account!;
    }

    private static async Task<IssuanceResult> IssueAsync(PaduraksaHost host, AcmeAccountSummary account, params string[] names)
    {
        var issued = await host.Shared.AcmeIssuance.RequestCertificateAsync(new CertificateIssueRequest(names, account.Id), Origin, Ct);
        Assert.True(issued.Outcome == IssuanceOutcome.Issued, issued.Message);
        return issued;
    }

    [Fact]
    public async Task ACertificateIssuedByHttp01WithPebbleFetchingTheChallengeFromTheGateIsWhatTheProxyServes()
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        string[] names = ["issue.pebble.test", "www.issue.pebble.test"];
        // The process under test binds an OS-assigned port and Pebble is told that port afterwards, so neither has to
        // win a race for a probed one.
        await using var host = TestHost.Create(settings: Settings(0));
        await host.StartAsync(Ct);
        var port = TestHost.Endpoint(host, HostRole.Public, "http").Port;
        await using var ca = await PebbleCa.StartAsync(port, names, Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("issue", names[0], "ok") with { Names = names }), Ct);
        var account = await CreateAccountAsync(host, ca);

        // Before a certificate there is nothing to answer the handshake with (no placeholder here).
        Assert.Contains(host.Shared.Config.Warnings, w => w.Code == "certificate.auto.noCoverage");

        var issued = await IssueAsync(host, account, names);

        var certificate = issued.Certificate!;
        Assert.Equal((CertificateSource.Acme, CertificateStatus.Active), (certificate.Source, certificate.Status));
        Assert.Equal(names, certificate.Names);
        Assert.Contains("Pebble", certificate.Issuer, StringComparison.Ordinal);
        foreach (var name in names)
        {
            var presented = (await VaultCertificatesEndToEndTests.HandshakeAsync(host, name)).Certificate;
            Assert.Equal(certificate.Sha256Fingerprint, Fingerprint(presented));
            Assert.Contains("Pebble", presented.Issuer, StringComparison.Ordinal);
        }

        // The challenge was answered and cleaned up; the order is done and the key never left the vault (only the chain is in the database).
        Assert.Equal(0, host.Shared.Challenges.Count);
        var order = (await host.Shared.AcmeIssuance.GetOrderAsync(certificate.Id, Ct))!;
        Assert.Equal((AcmeOrderStatus.Valid, 0), (order.Status, order.Attempts));
        Assert.Empty(host.Shared.Config.Warnings);
    }

    [Fact]
    public async Task ARenewalIssuesANewCertificateUnderTheSameIdAndItIsServedWithoutANewRevision()
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        string[] names = ["renew.pebble.test"];
        await using var host = TestHost.Create(settings: Settings(0));
        await host.StartAsync(Ct);
        var port = TestHost.Endpoint(host, HostRole.Public, "http").Port;
        await using var ca = await PebbleCa.StartAsync(port, names, Ct);
        var revision = await CommitAsync(host, new UpsertHostCommand(RespondHost("renew", names[0], "ok")), Ct);
        var account = await CreateAccountAsync(host, ca);
        var first = (await IssueAsync(host, account, names)).Certificate!;
        Assert.Equal(first.Sha256Fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, names[0])).Certificate));

        // Pebble speaks ARI: the window for the certificate it just issued was found by the ID we computed, and it schedules the renewal.
        var before = (await host.Shared.AcmeIssuance.GetOrderAsync(first.Id, Ct))!;
        Assert.NotNull(before.AriWindowStart);
        Assert.True(before.AriWindowStart < before.AriWindowEnd);
        Assert.NotNull(before.NextRenewalAt);
        Assert.True(before.NextRenewalAt <= before.AriWindowEnd);
        Assert.NotNull(before.AriCheckAt);

        var renewed = await host.Shared.AcmeIssuance.RenewAsync(first.Id, Origin, Ct);

        Assert.True(renewed.Outcome == IssuanceOutcome.Issued, renewed.Message);
        Assert.Equal(first.Id, renewed.CertificateId);
        var second = renewed.Certificate!;
        Assert.NotEqual(first.Sha256Fingerprint, second.Sha256Fingerprint);
        Assert.NotEqual(first.SerialNumber, second.SerialNumber);
        Assert.Equal(second.Sha256Fingerprint, Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, names[0])).Certificate));
        Assert.Equal(revision, (await host.Shared.Config.GetCurrentAsync(Ct)).Revision);
        Assert.Single(await host.Shared.CertificateService.ListAsync(new CertificateQuery(Source: CertificateSource.Acme), Ct));
    }

    [Fact]
    public async Task ARestartedProcessRenewsADueCertificateWithItsSchedulerAndServesTheNewOne()
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        string[] names = ["again.pebble.test"];
        var data = TestDataDirectories.NewPath();
        var firstFingerprint = "";
        CertificateId id = default;
        var port = 0;
        PebbleCa? ca = null;
        try
        {
            // The first process binds an OS-assigned port; Pebble is told it once it is bound (the second process has to
            // bind the same port afterwards, because that is where Pebble's validation authority fetches the challenge).
            await using (var first = TestHost.Create(settings: Settings(0), dataDirectory: data))
            {
                await first.StartAsync(Ct);
                port = TestHost.Endpoint(first, HostRole.Public, "http").Port;
                ca = await PebbleCa.StartAsync(port, names, Ct);
                await CommitAsync(first, new UpsertHostCommand(RespondHost("again", names[0], "ok")), Ct);
                var issued = (await IssueAsync(first, await CreateAccountAsync(first, ca), names)).Certificate!;
                firstFingerprint = issued.Sha256Fingerprint;
                id = issued.Id;
            }

            // While the process was down the renewal came due.
            SqliteConnection.ClearAllPools();
            await using (SqliteConnection connection = new(PaduraksaSqlite.ConnectionString(Path.Combine(data, ConfigDatabase.FileName))))
            {
                await connection.OpenAsync(Ct);
                await using var due = connection.CreateCommand();
                due.CommandText = "UPDATE AcmeOrders SET NextRenewalAt = '2000-01-01T00:00:00.0000000Z', AriCheckAt = NULL";
                Assert.Equal(1, await due.ExecuteNonQueryAsync(Ct));
            }

            await using var host = await TestPorts.StartOnPortAsync(
                port, () => TestHost.Create(settings: Settings(port, fastScheduler: true), dataDirectory: data));

            // The scheduler starts with the process, after the vault and the config store, and renews on its own. The
            // deadline covers an external process (Pebble in Docker) and a whole ACME order, so it is generous; the
            // loop itself waits on the served certificate, the real signal.
            var served = firstFingerprint;
            var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            while (served == firstFingerprint && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
                served = Fingerprint((await VaultCertificatesEndToEndTests.HandshakeAsync(host, names[0])).Certificate);
            }

            Assert.True(served != firstFingerprint, $"the scheduler had not renewed the due certificate after 3 min (order: {(await host.Shared.AcmeIssuance.GetOrderAsync(id, Ct))?.Status}, error: {(await host.Shared.AcmeIssuance.GetOrderAsync(id, Ct))?.Error})");

            var current = Assert.Single(await host.Shared.CertificateService.ListAsync(new CertificateQuery(Source: CertificateSource.Acme), Ct));
            Assert.Equal((id, served), (current.Id, current.Sha256Fingerprint));
            var order = (await host.Shared.AcmeIssuance.GetOrderAsync(id, Ct))!;
            Assert.Equal((AcmeOrderStatus.Valid, 0), (order.Status, order.Attempts));
            Assert.True(order.NextRenewalAt > DateTimeOffset.UtcNow);
        }
        finally
        {
            if (ca is not null)
            {
                await ca.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ANameThePublicListenerDoesNotAnswerFailsValidationAndLeavesNothingServed()
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        string[] names = ["unreachable.pebble.test"];
        await using var host = TestHost.Create(settings: Settings(0));
        await host.StartAsync(Ct);
        // Pebble is told another port for validation: its challenge fetch finds nobody there.
        await using var ca = await PebbleCa.StartAsync(PebbleCa.FreePort(), names, Ct);
        await CommitAsync(host, new UpsertHostCommand(RespondHost("unreachable", names[0], "ok")), Ct);
        var account = await CreateAccountAsync(host, ca);

        var failed = await host.Shared.AcmeIssuance.RequestCertificateAsync(new CertificateIssueRequest(names, account.Id), Origin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Equal(0, host.Shared.Challenges.Count);
        var certificate = Assert.Single(await host.Shared.CertificateService.ListAsync(new CertificateQuery(Source: CertificateSource.Acme), Ct));
        Assert.Equal(CertificateStatus.Failed, certificate.Status);
        Assert.NotNull((await host.Shared.AcmeIssuance.GetOrderAsync(certificate.Id, Ct))!.RetryAt);

        // Nothing is served for the name: no certificate was ever active, and the handshake is refused (no placeholder here).
        var handshake = await Record.ExceptionAsync(async () => await VaultCertificatesEndToEndTests.HandshakeAsync(host, names[0]));
        Assert.True(handshake is System.Security.Authentication.AuthenticationException or IOException, handshake?.ToString() ?? "the handshake succeeded");
        Assert.Contains(host.Shared.Config.Warnings, w => w.Code == "certificate.auto.noCoverage");
    }

    [Fact]
    public async Task WithoutTheCaRootInTheTrustFileTheDirectoryIsNotTrusted()
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        var settings = Settings(0);
        settings.Remove("Paduraksa:Acme:TrustedRootsFile");
        await using var host = TestHost.Create(settings: settings);
        await host.StartAsync(Ct);
        var port = TestHost.Endpoint(host, HostRole.Public, "http").Port;
        await using var ca = await PebbleCa.StartAsync(port, ["trust.pebble.test"], Ct);

        var created = await host.Shared.AcmeAccounts.CreateAsync(
            new CreateAcmeAccountRequest(ca.Directory.AbsoluteUri, ["mailto:ops@example.com"], TermsOfServiceAgreed: true), Origin, Ct);

        // Pebble's certificate chains to a root the system does not trust: nothing is sent to it, nothing is recorded, no key is left.
        Assert.Equal(IssuanceChangeStatus.Failed, created.Status);
        Assert.Empty(await host.Shared.AcmeAccounts.ListAsync(Ct));
    }
}
