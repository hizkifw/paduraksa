using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Setup;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The overview's checklist after first-run setup (PLAN §12.1): what is left, and the page each task links to.</summary>
public sealed class SetupChecklistTests
{
    [Fact]
    public void TheChecklist_ListsWhatIsLeft_AndTheDnsProviderOnlyWithAnAccount()
    {
        SetupProgress fresh = new(false, [], [], [], 0, null);
        Assert.Equal([SetupTask.TwoStep, SetupTask.AcmeAccount, SetupTask.Certificate, SetupTask.FirstHost], SetupChecklist.Visible(fresh));

        SetupProgress withAccount = fresh with { TwoStep = true, Accounts = [Account()] };
        Assert.Equal([SetupTask.DnsProvider, SetupTask.Certificate, SetupTask.FirstHost], SetupChecklist.Visible(withAccount));

        // What the user can't read isn't listed.
        SetupProgress viewer = new(true, null, null, null, 2, null);
        Assert.Empty(SetupChecklist.Visible(viewer));
    }

    [Fact]
    public void EachTask_LinksToThePageThatDoesIt()
    {
        Assert.Equal(Enum.GetValues<SetupTask>(), SetupTasks.All.Select(t => t.Task));
        Assert.Equal(DashboardRoutes.Account, SetupTasks.Info(SetupTask.TwoStep).Href);
        Assert.Equal(DashboardRoutes.AcmeAccounts, SetupTasks.Info(SetupTask.AcmeAccount).Href);
        Assert.Equal(DashboardRoutes.DnsProviders, SetupTasks.Info(SetupTask.DnsProvider).Href);
        Assert.Equal(DashboardRoutes.Certificates, SetupTasks.Info(SetupTask.Certificate).Href);
        Assert.Equal(DashboardRoutes.NewHost, SetupTasks.Info(SetupTask.FirstHost).Href);
    }

    private static AcmeAccountSummary Account() => new(
        AcmeAccountId.New(), "https://acme.test/directory", [], AcmeAccountStatus.Active, "https://acme.test/acct/1", JwsAlgorithm.ES256, true,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
