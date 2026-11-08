using Bunit;

using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard.Setup;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The first-run wizard's way through (PLAN §12.1): addresses parsed at the page's edge, next and back, skipped steps on the rail, and
/// the overview's checklist naming what is left.
/// </summary>
public sealed class SetupWizardTests
{
    [Theory]
    [InlineData(null, WizardStep.TwoStep)]
    [InlineData("", WizardStep.TwoStep)]
    [InlineData("acme", WizardStep.AcmeAccount)]
    [InlineData("DNS", WizardStep.DnsProvider)]
    [InlineData("certificate", WizardStep.Certificate)]
    [InlineData("host", WizardStep.FirstHost)]
    [InlineData("done", WizardStep.Done)]
    public void AStepIsOpenedByItsAddress(string? slug, WizardStep step) => Assert.Equal(step, SetupWizard.Parse(slug));

    [Theory]
    [InlineData("token")]
    [InlineData("owner")]
    [InlineData("nope")]
    public void TheSetupPageSteps_AndUnknownOnes_AreNotWizardPages(string slug) => Assert.Null(SetupWizard.Parse(slug));

    [Fact]
    public void NextAndBack_WalkTheStepsInOrder_AndStopAtTheEnds()
    {
        List<WizardStep> walked = [SetupWizard.First];
        while (walked[^1] != WizardStep.Done)
        {
            walked.Add(SetupWizard.Next(walked[^1]));
        }

        Assert.Equal([WizardStep.TwoStep, WizardStep.AcmeAccount, WizardStep.DnsProvider, WizardStep.Certificate, WizardStep.FirstHost, WizardStep.Done], walked);
        Assert.Equal(WizardStep.Done, SetupWizard.Next(WizardStep.Done));
        Assert.Equal(WizardStep.TwoStep, SetupWizard.Previous(WizardStep.TwoStep));
        Assert.Equal(WizardStep.DnsProvider, SetupWizard.Previous(WizardStep.Certificate));
        Assert.Equal("/welcome", SetupWizard.PathOf(WizardStep.TwoStep));
        Assert.Equal("/welcome/dns", SetupWizard.PathOf(WizardStep.DnsProvider));
        Assert.All(SetupWizard.Steps.Where(s => s.Step >= SetupWizard.First), info => Assert.Equal(info.Step, SetupWizard.Parse(info.Slug)));
    }

    [Fact]
    public async Task TheRail_ShowsDoneSkippedCurrentAndUpcoming()
    {
        await using DashboardTestContext context = new();

        IRenderedComponent<WizardStepper> rail = context.Render<WizardStepper>(p => p
            .Add(r => r.Current, WizardStep.Certificate)
            .Add(r => r.Skipped, new HashSet<WizardStep> { WizardStep.AcmeAccount, WizardStep.DnsProvider }));

        Assert.Equal(
            ["done", "done", "done", "skipped", "skipped", "current", "upcoming", "upcoming"],
            rail.FindAll(".pdk-wizard-rail-step").Select(step => step.ClassList.Single(c => c.StartsWith("pdk-wizard-", StringComparison.Ordinal) && c != "pdk-wizard-rail-step")["pdk-wizard-".Length..]));
        Assert.Equal("step", rail.Find("[data-step=certificate]").GetAttribute("aria-current"));
    }

    [Fact]
    public void TheChecklist_ListsWhatIsLeft_AndTheDnsProviderOnlyWithAnAccount()
    {
        SetupProgress fresh = new(false, [], [], [], 0, null);
        Assert.Equal([WizardStep.TwoStep, WizardStep.AcmeAccount, WizardStep.Certificate, WizardStep.FirstHost], SetupChecklist.Visible(fresh));

        SetupProgress withAccount = fresh with { TwoStep = true, Accounts = [Account()] };
        Assert.Equal([WizardStep.DnsProvider, WizardStep.Certificate, WizardStep.FirstHost], SetupChecklist.Visible(withAccount));

        // What the user can't read isn't listed.
        SetupProgress viewer = new(true, null, null, null, 2, null);
        Assert.Empty(SetupChecklist.Visible(viewer));
    }

    private static AcmeAccountSummary Account() => new(
        AcmeAccountId.New(), "https://acme.test/directory", [], AcmeAccountStatus.Active, "https://acme.test/acct/1", JwsAlgorithm.ES256, true,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
