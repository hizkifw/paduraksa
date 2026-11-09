using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

public sealed class ResourcesInUseTests
{
    [Fact]
    public async Task AnAccountCertificatesAreOrderedWithCannotBeDeactivated()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        Assert.Equal(IssuanceOutcome.Issued, (await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct)).Outcome);

        var result = await h.Accounts.DeactivateAsync(account.Id, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Invalid, result.Status);
        Assert.Equal(IssuanceErrorCodes.AccountInUse, Assert.Single(result.Errors).Code);
        Assert.Equal(AcmeAccountStatus.Active, (await h.Accounts.GetAsync(account.Id, Ct))!.Status);
        Assert.Empty(h.Signer.Deleted);
    }

    [Fact]
    public async Task AProviderInUseCannotBeDeletedButAFreeOneCan()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var used = await h.CreateDnsProviderAsync();
        var free = await h.CreateDnsProviderAsync();
        Assert.Equal(IssuanceOutcome.Issued, (await h.Issuance.RequestCertificateAsync(Dns01(account.Id, used.Id, "example.com"), Admin, Ct)).Outcome);

        var refused = await h.DnsProviders.DeleteAsync(used.Id, Admin, Ct);
        var deleted = await h.DnsProviders.DeleteAsync(free.Id, Admin, Ct);

        Assert.Equal(IssuanceChangeStatus.Invalid, refused.Status);
        Assert.Equal(IssuanceErrorCodes.DnsProviderInUse, Assert.Single(refused.Errors).Code);
        Assert.Equal(IssuanceChangeStatus.Committed, deleted.Status);
        Assert.Equal(used.Id, Assert.Single(await h.DnsProviders.ListAsync(Ct)).Id);
        Assert.Equal(IssuanceChangeStatus.NotFound, (await h.DnsProviders.DeleteAsync(free.Id, Admin, Ct)).Status);
        Assert.Equal([AuditOutcome.Invalid, AuditOutcome.Success], (await h.AuditAsync("acme.dns_provider.delete")).Select(a => a.Outcome));
    }
}
