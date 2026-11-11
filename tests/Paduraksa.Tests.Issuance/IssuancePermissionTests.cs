using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Data.Commands;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>The up-front permission checks of issuance, accounts and DNS providers: refused before anything is started.</summary>
public sealed class IssuancePermissionTests
{
    private static readonly CommandOrigin Stranger = new(Actor.ForUser(UserId.New()), Channel.Api, System.Net.IPAddress.Loopback);

    private static CertificateIssueRequest Http01(AcmeAccountId account, params string[] names) => new(names, account, AcmeChallengeType.Http01);

    [Fact]
    public async Task ARefusedRequestStartsNoOrderAndLeavesNothingBehind()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Permissions = new Evaluator(allow: []);
        var issuance = h.NewIssuance();
        var requests = h.Server.Requests.Count;

        var result = await issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Stranger, Ct);

        Assert.Equal(IssuanceOutcome.Forbidden, result.Outcome);
        Assert.Empty(result.Errors);
        Assert.Equal(requests, h.Server.Requests.Count);
        Assert.Empty(await h.Certificates.ListAsync(null, Ct));
        Assert.Empty(await issuance.ListOrdersAsync(Ct));
        var audit = Assert.Single(await h.AuditAsync("acme.order.request"));
        Assert.Equal(AuditOutcome.Denied, audit.Outcome);
    }

    [Fact]
    public async Task ARefusedRenewalAsksTheCaNothing()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var issued = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = issued.CertificateId!.Value;
        h.Permissions = new Evaluator(allow: [Permission.ConfigRead]);
        var requests = h.Server.Requests.Count;

        var result = await h.NewIssuance().RenewAsync(id, Stranger, Ct);

        Assert.Equal(IssuanceOutcome.Forbidden, result.Outcome);
        Assert.Equal(id, result.CertificateId);
        Assert.Equal(requests, h.Server.Requests.Count);
        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(id)).Status);
    }

    [Fact]
    public async Task AnOriginWithCertificatesWriteMayRequestAndRenew()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Permissions = new Evaluator(allow: [Permission.CertificatesWrite]);
        var issuance = h.NewIssuance();

        var issued = await issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Stranger, Ct);

        Assert.Equal(IssuanceOutcome.Issued, issued.Outcome);
        Assert.NotEqual(IssuanceOutcome.Forbidden, (await issuance.RenewAsync(issued.CertificateId!.Value, Stranger, Ct)).Outcome);
    }

    [Fact]
    public async Task ProviderMutationsNeedProvidersWrite()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var secret = await h.Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("t", null, ["api.dns.test"]), Ct);
        h.Permissions = new Evaluator(allow: [Permission.CertificatesWrite, Permission.ProvidersRead]);
        var accounts = h.NewAccounts();
        var providers = h.NewDnsProviders();
        var keys = h.Signer.Created.Count;

        var account = await accounts.CreateAsync(new("https://acme.test/directory", ["mailto:ops@example.com"], TermsOfServiceAgreed: true), Stranger, Ct);
        var provider = await providers.CreateAsync(new CreateDnsProviderRequest(DnsProviderKind.DeSec, "deSEC", secret), Stranger, Ct);
        var deleted = await providers.DeleteAsync(DnsProviderId.New(), Stranger, Ct);
        var deactivated = await accounts.DeactivateAsync(AcmeAccountId.New(), Stranger, Ct);

        Assert.Equal(IssuanceChangeStatus.Denied, account.Status);
        Assert.Equal(IssuanceChangeStatus.Denied, provider.Status);
        Assert.Equal(IssuanceChangeStatus.Denied, deleted.Status);
        Assert.Equal(IssuanceChangeStatus.Denied, deactivated.Status);
        Assert.Equal(keys, h.Signer.Created.Count); // no account key was made
        Assert.Empty(await providers.ListAsync(Ct));
        Assert.Equal(4, (await h.Config.AuditAsync()).Count(e => e.Outcome == AuditOutcome.Denied));
    }

    [Fact]
    public async Task ADegradedConfigStoreSkipsTheLookupSoNothingThrows()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var path = Path.Combine(Path.GetDirectoryName(h.Config.Db.DatabasePath)!, LastKnownGoodFile.FileName);
        await using (var writer = new LastKnownGoodFile(path))
        {
            using var store = new SnapshotStore();
            await using var publisher = new SnapshotPublisher(store);
            using var first = new ConfigService(h.Config.Db.Factory, new ConfigServiceHarness.FakeCompiler(), publisher, h.Permissions, lastKnownGood: writer);
            await first.StartAsync(Ct);
            await first.StartupPublishFollowed;
        }

        await h.Config.Db.ExecuteAsync("UPDATE ConfigRevisions SET DocumentHash = zeroblob(32)");
        await using var lastGood = new LastKnownGoodFile(path);
        using var degradedStore = new SnapshotStore();
        await using var degradedPublisher = new SnapshotPublisher(degradedStore);
        using var degraded = new ConfigService(h.Config.Db.Factory, new ConfigServiceHarness.FakeCompiler(), degradedPublisher, h.Permissions, lastKnownGood: lastGood);
        Assert.False((await degraded.StartAsync(Ct)).AcceptsCommands);
        var certificates = new CertificateService(
            h.Config.Db.Factory, degraded, h.Time, NullLogger<CertificateService>.Instance, new Evaluator(throws: true));

        var result = await certificates.DeleteAsync(CertificateId.New(), Stranger, Ct);

        Assert.Equal(CertificateChangeStatus.Unavailable, result.Status);
    }

    private sealed class Evaluator(Permission[]? allow = null, bool throws = false) : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            throws
                ? throw new InvalidOperationException("the permission store is not readable")
                : ValueTask.FromResult<IReadOnlySet<Permission>>((allow ?? []).ToHashSet());

        public async ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) =>
            (await GetPermissionsAsync(origin.Actor, cancellationToken)).Contains(permission);
    }
}
