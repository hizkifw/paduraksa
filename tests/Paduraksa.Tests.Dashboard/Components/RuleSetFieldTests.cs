using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The rule-set picker on its own: inherit (naming what the layer below resolves), none, every set by name and then the built-in
/// presets, with the record each choice hands back. A set's ID is never part of what it shows.
/// </summary>
public sealed class RuleSetFieldTests
{
    private static readonly RuleSetConfig Scanners = new(
        RuleSetId.New(), "Scanners", Rules: [new RuleConfig(RuleAction.Deny, new RuleMatchConfig(UserAgent: new RuleUserAgentConfig(["sqlmap"])))]);

    private static readonly RuleSetConfig Admin = new(RuleSetId.New(), "Admin");

    /// <summary>The layer below names the first set, so the inherit choice has a name to say.</summary>
    private static readonly RuleSetSettings Inherited = new() { RuleSetId = Scanners.Id };

    [Fact]
    public async Task ItsChoices_AreInheritNoneEverySetByNameAndThenThePresets()
    {
        await using DashboardTestContext context = new();
        RuleSetSettings? chosen = null;
        var field = Render(context, null, value => chosen = value);

        Assert.Equal(
            ["inherit", "off", "p0", "p1", "p2", "p3", "p4"],
            field.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
        Assert.Contains("Inherit (Scanners)", Displayed(field), StringComparison.Ordinal);

        await Choose(field, "p1");
        Assert.Equal(new RuleSetSettings { Enabled = true, RuleSetId = Admin.Id }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("Admin", Displayed(field));

        await Choose(field, "p3");
        Assert.Equal(new RuleSetSettings { Enabled = true, RuleSetId = RuleSetPresets.KnownScanners.Id }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("Block known scanners", Displayed(field));

        await Choose(field, "off");
        Assert.Equal(new RuleSetSettings { Enabled = false }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("No rule set", Displayed(field));

        await Choose(field, "inherit");
        Assert.Null(chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Contains("Inherit (Scanners)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheLayerBelowTurnedOff_IsInheritedAsNone()
    {
        await using DashboardTestContext context = new();
        var field = context.Render<RuleSetField>(parameters => parameters
            .Add(f => f.Inherited, new RuleSetSettings { Enabled = false, RuleSetId = Scanners.Id })
            .Add(f => f.Sets, [Scanners, Admin]));

        Assert.Contains("Inherit (no rule set)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALayerThatInheritsAPreset_NamesIt()
    {
        await using DashboardTestContext context = new();
        var field = context.Render<RuleSetField>(parameters => parameters
            .Add(f => f.Inherited, new RuleSetSettings { RuleSetId = RuleSetPresets.AiCrawlers.Id })
            .Add(f => f.Sets, [Scanners]));

        Assert.Contains("Inherit (Block AI crawlers)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToASetThatIsGone_SaysSoInsteadOfShowingNothing()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, new RuleSetSettings { RuleSetId = RuleSetId.New() }, _ => { });

        Assert.Equal("A set that isn't here", Displayed(field));
    }

    [Fact]
    public async Task ItNeverRendersASetsId()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, null, _ => { });

        foreach (var id in new[] { Scanners.Id, Admin.Id }.Concat(RuleSetPresets.All.Select(preset => preset.Id)))
        {
            Assert.DoesNotContain(id.ToString(), field.Markup, StringComparison.Ordinal);
        }
    }

    private static IRenderedComponent<RuleSetField> Render(
        DashboardTestContext context, RuleSetSettings? value, Action<RuleSetSettings?> changed) =>
        context.Render<RuleSetField>(parameters => Parameters(parameters, value, changed));

    private static ComponentParameterCollectionBuilder<RuleSetField> Parameters(
        ComponentParameterCollectionBuilder<RuleSetField> parameters, RuleSetSettings? value, Action<RuleSetSettings?> changed) =>
        parameters
            .Add(f => f.Value, value)
            .Add(f => f.ValueChanged, changed)
            .Add(f => f.Inherited, Inherited)
            .Add(f => f.Sets, [Scanners, Admin]);

    private static string Displayed(IRenderedComponent<RuleSetField> field) =>
        field.Find("[data-field=rule-set]").GetAttribute("value") ?? string.Empty;

    private static Task Choose(IRenderedComponent<RuleSetField> field, string key) =>
        field.InvokeAsync(() => field.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rule set")
            .Instance.ValueChanged.InvokeAsync(key));
}
