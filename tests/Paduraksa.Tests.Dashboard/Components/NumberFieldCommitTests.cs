using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;
using Paduraksa.Dashboard.SettingsArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// A number in a drawer or a settings page is committed when the field loses focus (the browser's <c>change</c>), as a text field's is,
/// not only on Enter or on the submit button.
/// </summary>
public sealed class NumberFieldCommitTests
{
    private static readonly UpstreamConfig Nas = new(UpstreamId.New(), "nas", Destinations: [new DestinationConfig(DestinationId.New(), "http://nas:8080")]);

    [Fact]
    public async Task ASettingsNumber_IsHandedOnWhenTheFieldLosesFocus_WithoutEnter()
    {
        await using DashboardTestContext context = new();
        context.Render<MudPopoverProvider>();
        int? committed = null;
        var field = context.Render<CountField>(p => p
            .Add(c => c.Label, "Connections per address")
            .Add(c => c.Field, "max-connections-per-ip")
            .Add(c => c.Inherited, 256)
            .Add(c => c.ValueChanged, EventCallback.Factory.Create<int?>(this, value => committed = value)));
        var input = field.Find("input[data-field=max-connections-per-ip]");

        await input.ChangeAsync(new() { Value = "50" });
        await input.BlurAsync(new());

        Assert.Equal(50, committed);
    }

    [Fact]
    public async Task ADrawersNumber_IsInTheModelOnceTheFieldLosesFocus_SoTheDrawerIsDirtyAndKeepsIt()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [Nas]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, Nas.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-field=active-health]").Count == 1);
        await drawers.Find("[data-field=active-health]").ChangeAsync(new() { Value = true });
        var level = context.Services.GetRequiredService<DrawerStack>().Top!;
        level.IsDirty = false;

        await drawers.FindAll("[data-drawer=upstream] .mud-input-control")
            .First(control => control.TextContent.Contains("Every", StringComparison.Ordinal)).QuerySelector("input")!
            .ChangeAsync(new() { Value = "45" });

        Assert.True(level.IsDirty);
        // The model has it: the field built again from the model (the switch off and on) shows it.
        await drawers.Find("[data-field=active-health]").ChangeAsync(new() { Value = false });
        await drawers.Find("[data-field=active-health]").ChangeAsync(new() { Value = true });
        Assert.Equal("45", drawers.FindAll("[data-drawer=upstream] .mud-input-control")
            .First(control => control.TextContent.Contains("Every", StringComparison.Ordinal)).QuerySelector("input")!.GetAttribute("value"));
    }
}
