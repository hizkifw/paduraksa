using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The Host header field of the upstream drawer: its mode, and the name only when the name is the setting.</summary>
public sealed class UpstreamHostHeaderFieldTests
{
    private const string Mode = "[data-drawer=upstream] [data-field=host-header]";
    private const string Value = "[data-drawer=upstream] [data-field=host-header-value]";

    private static readonly UpstreamConfig Nas = new(UpstreamId.New(), "nas", Destinations: [new DestinationConfig(DestinationId.New(), "https://nas:8443")]);

    [Fact]
    public async Task TheFieldStartsAtTheClientsHostAndAsksForANameOnlyForAFixedOne()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [Nas]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, Nas.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll(Mode).Count == 1);
        Assert.Empty(drawers.FindAll(Value));

        await ChooseAsync(drawers, UpstreamHostHeaderMode.Destination);
        Assert.Empty(drawers.FindAll(Value));

        await ChooseAsync(drawers, UpstreamHostHeaderMode.Custom);
        Assert.Single(drawers.FindAll(Value));
    }

    [Fact]
    public async Task AStoredFixedNameIsShown()
    {
        await using DashboardTestContext context = new();
        var named = Nas with { HostHeader = new UpstreamHostHeaderConfig(UpstreamHostHeaderMode.Custom, "nas.internal:8443") };
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [named]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, named.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll(Value).Count == 1);

        Assert.Equal("nas.internal:8443", drawers.Find(Value).GetAttribute("value"));
    }

    private static Task ChooseAsync(IRenderedComponent<DrawerHost> drawers, UpstreamHostHeaderMode mode) =>
        drawers.InvokeAsync(() => drawers.FindComponents<MudSelect<UpstreamHostHeaderMode>>().Single(s => s.Instance.Label == "Host header sent to the upstream").Instance.ValueChanged.InvokeAsync(mode));
}
