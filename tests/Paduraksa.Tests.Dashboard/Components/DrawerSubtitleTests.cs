using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>A drawer's subtitle says something the body doesn't (a staged change waits for its host, what uses the object); there is none to say how every change works.</summary>
public sealed class DrawerSubtitleTests
{
    private static readonly ProfileConfig Media = new(ProfileId.New(), "Media");
    private static readonly HostConfig App = new(HostId.New(), ["app.example.com"], ProfileId: Media.Id);

    private static async Task<IRenderedComponent<DrawerHost>> OpenHostAsync(DashboardTestContext context, string key)
    {
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Profiles: [Media], Hosts: [App]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Hosts>(parameters => parameters.Add(p => p.Key, key));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=host]").Count == 1);
        return drawers;
    }

    [Theory]
    [InlineData("new")]
    [InlineData("existing")]
    public async Task ANewHostAndAnExistingOne_HaveNoSubtitle(string which)
    {
        await using DashboardTestContext context = new();

        var drawers = await OpenHostAsync(context, which == "new" ? DashboardRoutes.NewSegment : App.Id.ToString());

        Assert.Empty(drawers.FindAll("[data-drawer=host] .pdk-drawer-subtitle"));
    }

    [Fact]
    public async Task AProfileSaysHowManyHostsUseIt_WhereAnUnusedOneSaysNothing()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = new ConfigDocument(Profiles: [Media, new ProfileConfig(ProfileId.New(), "Spare")], Hosts: [App]);
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        context.Render<Profiles>(parameters => parameters.Add(p => p.Key, Media.Id.ToString()));
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-drawer=profile]").Count == 1);

        Assert.Equal("Used by 1 host", drawers.Find("[data-drawer=profile] .pdk-drawer-subtitle").TextContent);
    }
}
