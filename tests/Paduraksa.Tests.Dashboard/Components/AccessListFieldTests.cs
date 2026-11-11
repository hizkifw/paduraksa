using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The access-list picker on its own: inherit (naming what the layer below resolves), none, and every list by name, with the
/// record each choice hands back. A list's ID is never part of what it shows.
/// </summary>
public sealed class AccessListFieldTests
{
    private static readonly AccessListConfig Office = new(
        AccessListId.New(), "Office", Ip: new AccessListIp(["10.0.0.0/8"]), BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", PasswordCredentialId.New())]));

    private static readonly AccessListConfig Guests = new(AccessListId.New(), "Guests", Ip: new AccessListIp(["203.0.113.0/24"]));

    /// <summary>The layer below names the first list, so the inherit choice has a name to say.</summary>
    private static readonly AccessListSettings Inherited = new() { AccessListId = Office.Id };

    [Fact]
    public async Task ItsChoices_AreInheritNoneAndEveryListByName()
    {
        await using DashboardTestContext context = new();
        AccessListSettings? chosen = null;
        var field = Render(context, null, value => chosen = value);

        Assert.Equal(
            ["inherit", "off", "p0", "p1"],
            field.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
        Assert.Contains("Inherit (Office)", Displayed(field), StringComparison.Ordinal);

        await Choose(field, "p1");
        Assert.Equal(new AccessListSettings { Enabled = true, AccessListId = Guests.Id }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("Guests", Displayed(field));

        await Choose(field, "off");
        Assert.Equal(new AccessListSettings { Enabled = false }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("No access list", Displayed(field));

        await Choose(field, "inherit");
        Assert.Null(chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Contains("Inherit (Office)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheLayerBelowTurnedOff_IsInheritedAsNone()
    {
        await using DashboardTestContext context = new();
        var field = context.Render<AccessListField>(parameters => parameters
            .Add(f => f.Inherited, new AccessListSettings { Enabled = false, AccessListId = Office.Id })
            .Add(f => f.Lists, [Office, Guests]));

        Assert.Contains("Inherit (no access list)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToAListThatIsGone_SaysSoInsteadOfShowingNothing()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, new AccessListSettings { AccessListId = AccessListId.New() }, _ => { });

        Assert.Equal("A list that isn't here", Displayed(field));
    }

    [Fact]
    public async Task ItNeverRendersAListsId()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, null, _ => { });

        var markup = field.Markup;
        foreach (var list in new[] { Office, Guests })
        {
            Assert.DoesNotContain(list.Id.ToString(), markup, StringComparison.Ordinal);
        }
    }

    private static IRenderedComponent<AccessListField> Render(
        DashboardTestContext context, AccessListSettings? value, Action<AccessListSettings?> changed) =>
        context.Render<AccessListField>(parameters => Parameters(parameters, value, changed));

    private static ComponentParameterCollectionBuilder<AccessListField> Parameters(
        ComponentParameterCollectionBuilder<AccessListField> parameters, AccessListSettings? value, Action<AccessListSettings?> changed) =>
        parameters
            .Add(f => f.Value, value)
            .Add(f => f.ValueChanged, changed)
            .Add(f => f.Inherited, Inherited)
            .Add(f => f.Lists, [Office, Guests]);

    /// <summary>The text the select shows for its value, which is the selected item's words.</summary>
    private static string Displayed(IRenderedComponent<AccessListField> field) =>
        field.Find("[data-field=access-list]").GetAttribute("value") ?? string.Empty;

    private static Task Choose(IRenderedComponent<AccessListField> field, string key) =>
        field.InvokeAsync(() => field.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Access list")
            .Instance.ValueChanged.InvokeAsync(key));
}
