using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The rate-limit picker on its own: inherit (naming what the layer below resolves), no limit, and every policy by
/// name, with the record each choice hands back. The policy's ID is never part of what it shows.
/// </summary>
public sealed class RateLimitFieldTests
{
    private static readonly RateLimitPolicyConfig Strict = new(
        RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50, Key: RateLimitKey.Header, Header: "X-Api-Key");

    private static readonly RateLimitPolicyConfig Bulk = new(
        RateLimitPolicyId.New(), "Bulk", Requests: 60, Per: RateLimitPeriod.Minute, Burst: 20);

    /// <summary>The layer below names the first policy, so the inherit choice has a policy and rate to name.</summary>
    private static readonly RateLimitSettings Inherited = new() { PolicyId = Strict.Id };

    [Fact]
    public async Task ItsChoices_AreInheritNoLimitAndEveryPolicyByName()
    {
        await using DashboardTestContext context = new();
        RateLimitSettings? chosen = null;
        var field = Render(context, null, value => chosen = value);

        Assert.Equal(
            ["inherit", "off", "p0", "p1"],
            field.FindComponents<MudSelectItem<string>>().Select(item => item.Instance.Value));
        Assert.Contains("Inherit (Strict API, 10/s)", Displayed(field), StringComparison.Ordinal);

        await Choose(field, "p1");
        Assert.Equal(new RateLimitSettings { Enabled = true, PolicyId = Bulk.Id }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("Bulk", Displayed(field));

        await Choose(field, "off");
        Assert.Equal(new RateLimitSettings { Enabled = false }, chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Equal("No limit", Displayed(field));

        await Choose(field, "inherit");
        Assert.Null(chosen);
        field.Render(parameters => Parameters(parameters, chosen, value => chosen = value));
        Assert.Contains("Inherit (Strict API, 10/s)", Displayed(field), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItNeverRendersAPolicysId()
    {
        await using DashboardTestContext context = new();
        var field = Render(context, null, _ => { });

        string markup = field.Markup;
        foreach (var policy in new[] { Strict, Bulk })
        {
            Assert.DoesNotContain(policy.Id.ToString(), markup, StringComparison.Ordinal);
        }
    }

    private static IRenderedComponent<RateLimitField> Render(
        DashboardTestContext context, RateLimitSettings? value, Action<RateLimitSettings?> changed) =>
        context.Render<RateLimitField>(parameters => Parameters(parameters, value, changed));

    private static void Parameters(
        ComponentParameterCollectionBuilder<RateLimitField> parameters, RateLimitSettings? value, Action<RateLimitSettings?> changed) =>
        parameters
            .Add(f => f.Value, value)
            .Add(f => f.ValueChanged, changed)
            .Add(f => f.Inherited, Inherited)
            .Add(f => f.Policies, [Strict, Bulk]);

    /// <summary>The text the select shows for its value, which is the selected item's words.</summary>
    private static string Displayed(IRenderedComponent<RateLimitField> field) =>
        field.Find("[data-field=rate-limit]").GetAttribute("value") ?? string.Empty;

    private static Task Choose(IRenderedComponent<RateLimitField> field, string key) =>
        field.InvokeAsync(() => field.FindComponents<MudSelect<string>>()
            .Single(select => select.Instance.Label == "Rate limit")
            .Instance.ValueChanged.InvokeAsync(key));
}
