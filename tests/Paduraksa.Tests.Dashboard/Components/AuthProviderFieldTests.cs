using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The auth-provider picker on its own: inherit (naming what the layer below resolves), none and every provider by name, with the record each
/// choice hands back. A provider's ID is never part of what it shows.
/// </summary>
public sealed class AuthProviderFieldTests
{
    private static readonly AuthProviderConfig Authelia = new(
        AuthProviderId.New(), "Authelia", ForwardAuth: new ForwardAuthConfig("http://authelia:9091", ForwardAuthPreset.Authelia));

    private static readonly AuthProviderConfig Authentik = new(
        AuthProviderId.New(), "authentik", ForwardAuth: new ForwardAuthConfig("http://outpost:9000", ForwardAuthPreset.Authentik));

    /// <summary>The layer below names the first provider, so the inherit choice has a name to say.</summary>
    private static readonly AuthProviderSettings Inherited = new() { AuthProviderId = Authelia.Id };

    [Fact]
    public async Task ItsChoices_AreInheritNoneAndEveryProviderByName()
    {
        await using DashboardTestContext context = new();
        AuthProviderSettings? chosen = null;
        var field = Render(context, null, value => chosen = value);

        Assert.Equal(["inherit", "off", "p0", "p1"], field.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
        Assert.Contains("Inherit (Authelia)", Displayed(field), StringComparison.Ordinal);

        await Choose(field, "p1");
        Assert.Equal(new AuthProviderSettings { Enabled = true, AuthProviderId = Authentik.Id }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("authentik", Displayed(field));

        await Choose(field, "off");
        Assert.Equal(new AuthProviderSettings { Enabled = false }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("No sign-in", Displayed(field));

        await Choose(field, "inherit");
        Assert.Null(chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Contains("Inherit (Authelia)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatTheLayerBelowTurnedOff_IsInheritedAsNoSignIn()
    {
        await using DashboardTestContext context = new();
        var field = context.Render<AuthProviderField>(parameters => parameters
            .Add(f => f.Inherited, new AuthProviderSettings { Enabled = false, AuthProviderId = Authelia.Id })
            .Add(f => f.Providers, [Authelia, Authentik]));

        Assert.Contains("Inherit (no sign-in)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReferenceToAProviderThatIsGone_SaysSoInsteadOfShowingNothing()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, new AuthProviderSettings { AuthProviderId = AuthProviderId.New() }, _ => { });

        Assert.Equal("A provider that isn't here", Displayed(field));
    }

    [Fact]
    public async Task TheNewEntryRaisesOnNewInsteadOfChangingTheValueAndIsOnlyOfferedWhenSomeoneHandlesIt()
    {
        await using DashboardTestContext context = new();
        var raised = 0;
        AuthProviderSettings? chosen = new() { Enabled = false };
        var field = context.Render<AuthProviderField>(parameters => parameters
            .Add(f => f.Value, chosen)
            .Add(f => f.ValueChanged, value => chosen = value)
            .Add(f => f.Inherited, Inherited)
            .Add(f => f.Providers, [Authelia, Authentik])
            .Add(f => f.OnNew, () => raised++));

        Assert.Equal(["inherit", "off", "p0", "p1", "new"], field.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
        await Choose(field, "new");

        Assert.Equal(1, raised);
        Assert.Equal(new AuthProviderSettings { Enabled = false }, chosen);
        var without = Render(context, null, _ => { });
        Assert.DoesNotContain("new", without.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
    }

    [Fact]
    public async Task ItNeverRendersAProvidersId()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, null, _ => { });

        foreach (var id in new[] { Authelia.Id, Authentik.Id })
        {
            Assert.DoesNotContain(id.ToString(), field.Markup, StringComparison.Ordinal);
        }
    }

    private static IRenderedComponent<AuthProviderField> Render(
        DashboardTestContext context, AuthProviderSettings? value, Action<AuthProviderSettings?> changed) =>
        context.Render<AuthProviderField>(parameters => Parameters(parameters, value, changed));

    private static ComponentParameterCollectionBuilder<AuthProviderField> Parameters(
        ComponentParameterCollectionBuilder<AuthProviderField> parameters, AuthProviderSettings? value, Action<AuthProviderSettings?> changed) =>
        parameters
            .Add(f => f.Value, value)
            .Add(f => f.ValueChanged, changed)
            .Add(f => f.Inherited, Inherited)
            .Add(f => f.Providers, [Authelia, Authentik]);

    private static string Displayed(IRenderedComponent<AuthProviderField> field) =>
        field.Find("[data-field=auth-provider]").GetAttribute("value") ?? string.Empty;

    private static Task Choose(IRenderedComponent<AuthProviderField> field, string key) =>
        field.InvokeAsync(() => field.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Sign-in")
            .Instance.ValueChanged.InvokeAsync(key));
}
