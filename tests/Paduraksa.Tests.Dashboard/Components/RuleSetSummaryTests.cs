using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>What a layer's rule-set record says in its card's summary line, naming the set rather than its ID.</summary>
public sealed class RuleSetSummaryTests
{
    private static readonly RuleSetConfig Scanners = new(RuleSetId.New(), "Scanners");

    [Fact]
    public void ALayerSaysWhichSetItNamesOffOrThatItInheritsOne()
    {
        var document = new ConfigDocument(RuleSets: [Scanners]);

        Assert.Equal(["Rules: Scanners"], SettingsSummary.Summary(new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = Scanners.Id } }, document));
        Assert.Equal(["Rules: Block AI crawlers"], SettingsSummary.Summary(new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = RuleSetPresets.AiCrawlers.Id } }, document));
        Assert.Equal(["Rules off"], SettingsSummary.Summary(new HostSettings { RuleSet = new RuleSetSettings { Enabled = false, RuleSetId = Scanners.Id } }, document));
        Assert.Equal(["Rules: the inherited set"], SettingsSummary.Summary(new HostSettings { RuleSet = new RuleSetSettings { Enabled = true } }, document));
        Assert.Empty(SettingsSummary.Summary(new HostSettings()));
    }
}
