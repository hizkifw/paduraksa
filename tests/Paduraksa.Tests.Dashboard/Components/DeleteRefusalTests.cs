using Bunit;

using MudBlazor;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Drawers;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>A profile in use can't be deleted, and its drawer names the hosts that use it.</summary>
public sealed class DeleteRefusalTests
{
    private static readonly ProfileConfig Media = new(ProfileId.New(), "Media");

    private static async Task<string> RefusalAsync(ConfigDocument document, Action<DashboardTestContext> openPage)
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Operator).Document = document;
        context.Render<MudPopoverProvider>();
        var drawers = context.Render<DrawerHost>();
        openPage(context);
        await drawers.WaitForStateAsync(() => drawers.FindAll("[data-action=delete]").Count == 1);

        await drawers.Find("[data-action=delete]").ClickAsync(new());

        return drawers.Find("[data-banner=in-use]").TextContent;
    }

    [Fact]
    public async Task AProfileInUse_NamesItsHosts()
    {
        HostConfig[] hosts = [new(HostId.New(), ["app.example.com"], ProfileId: Media.Id), new(HostId.New(), ["api.example.com"], ProfileId: Media.Id)];

        var banner = await RefusalAsync(new ConfigDocument(Profiles: [Media], Hosts: hosts), context => context.Render<Profiles>(p => p.Add(page => page.Key, Media.Id.ToString())));

        Assert.Contains("Used by api.example.com and app.example.com. Pick another profile (or none) for them first.", banner, StringComparison.Ordinal);
    }
}
