using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The timeout fields of the upstream drawer: the stored value in seconds, the default shown as the placeholder, and the server's error at the field.</summary>
public sealed class UpstreamTimeoutsFieldTests
{
    private const string Connect = "[data-drawer=upstream] [data-field=connect-timeout]";
    private const string Idle = "[data-drawer=upstream] [data-field=idle-timeout]";

    private static readonly UpstreamConfig Nas = new(UpstreamId.New(), "nas", Destinations: [new DestinationConfig(DestinationId.New(), "http://nas:8080")]);

    [Fact]
    public async Task AnUpstreamWithNoTimeoutsShowsTheDefaultsAsPlaceholders()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [Nas]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, Nas.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll(Connect).Count == 1);

        Assert.Equal(string.Empty, drawers.Find(Connect).GetAttribute("value") ?? string.Empty);
        Assert.Equal("10", drawers.Find(Connect).GetAttribute("placeholder"));
        Assert.Equal("3600", drawers.Find(Idle).GetAttribute("placeholder"));
    }

    [Fact]
    public async Task StoredTimeoutsAreShownInSeconds()
    {
        await using DashboardTestContext context = new();
        var tuned = Nas with { Timeouts = new UpstreamTimeoutsConfig(TimeSpan.FromSeconds(3), TimeSpan.Zero) };
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [tuned]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, tuned.Id.ToString()));

        await drawers.WaitForStateAsync(() => drawers.FindAll(Connect).Count == 1);

        Assert.Equal("3", drawers.Find(Connect).GetAttribute("value"));
        Assert.Equal("0", drawers.Find(Idle).GetAttribute("value"));
    }

    [Fact]
    public async Task ATimeoutOutOfRangeIsRefusedAtItsField()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Upstreams: [Nas]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Upstreams>(parameters => parameters.Add(p => p.Key, Nas.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll(Idle).Count == 1);

        MudNumericField<double?> IdleField() => drawers.FindComponents<MudNumericField<double?>>().Single(f => f.Instance.Label == "Idle timeout (seconds)").Instance;
        await drawers.InvokeAsync(() => IdleField().ValueChanged.InvokeAsync(0.5));

        await drawers.WaitForStateAsync(() => drawers.Find(Idle).GetAttribute("aria-invalid") == "true");
        Assert.Contains("The idle timeout must be between 1 second and 24 hours, or zero for none.", drawers.Markup, StringComparison.Ordinal);
    }
}
