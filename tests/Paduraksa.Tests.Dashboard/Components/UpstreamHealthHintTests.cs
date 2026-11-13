using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>Both health switches start off: each says what that means for a destination that is down, and says nothing once it is on.</summary>
public sealed class UpstreamHealthHintTests
{
    private static readonly UpstreamConfig Nas = new(UpstreamId.New(), "nas", Destinations: [new DestinationConfig(DestinationId.New(), "http://nas:8080")]);

    [Fact]
    public async Task AnOffSwitchSaysWhatItMeansForADownDestination_AndTurningItOnRemovesTheLine()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [Nas]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, Nas.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-field=active-health]").Count == 1);

        Assert.Contains("only noticed when a request", drawers.Find("[data-hint=active-health]").TextContent, StringComparison.Ordinal);
        Assert.Contains("stays in rotation", drawers.Find("[data-hint=passive-health]").TextContent, StringComparison.Ordinal);

        await drawers.Find("[data-field=active-health]").ChangeAsync(new() { Value = true });

        Assert.Empty(drawers.FindAll("[data-hint=active-health]"));
        Assert.Single(drawers.FindAll("[data-hint=passive-health]"));
    }
}
