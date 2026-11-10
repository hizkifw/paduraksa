using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The operational change feed (what the dashboard's certificate, account and provider lists refresh on): every committed, audited change
/// is announced after its commit, a failing subscriber hurts nobody, and a disposed subscription hears nothing more.
/// </summary>
public sealed class OperationalChangeFeedTests
{
    [Fact]
    public async Task AccountsProvidersAndIssuancesAreAnnouncedAfterTheirCommit()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        List<OperationalChange> seen = [];
        using var failing = h.Config.Service.OperationalChanges.Subscribe(_ => throw new InvalidOperationException("a closed page"));
        using var subscription = h.Config.Service.OperationalChanges.Subscribe(seen.Add);

        var account = await h.CreateAccountAsync();
        var provider = await h.CreateDnsProviderAsync();
        var issued = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, issued.Outcome);
        Assert.Contains(new OperationalChange("acme.account.create", account.Id.ToString()), seen);
        Assert.Contains(new OperationalChange("acme.dns_provider.create", provider.Id.ToString()), seen);
        Assert.Contains(seen, change => change.Action == "certificate.add" && change.Target == issued.CertificateId.ToString());
        Assert.Contains(seen, change => change.Action == "acme.order.issued");

        subscription.Dispose();
        var count = seen.Count;
        _ = await h.Issuance.RenewAsync(issued.CertificateId!.Value, Admin, Ct);
        Assert.Equal(count, seen.Count);
    }
}
