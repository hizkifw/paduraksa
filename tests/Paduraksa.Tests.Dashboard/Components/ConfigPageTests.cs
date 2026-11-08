using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using MudBlazor;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The config pages with a user in a role (PLAN §12): what the role may not do is not offered (the services still refuse), an ID in the
/// address that isn't one is a not-found, and a commit made elsewhere shows without a reload.
/// </summary>
public sealed class ConfigPageTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Version: 1, Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static readonly HostConfig Host = new(HostId.New(), ["app.example.com"], Version: 2, Routes: [new RouteConfig(RouteId.New(), App.Id)]);

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public async Task TheHostsList_OffersAddingOnlyToWriters(Role role, bool offered)
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, role);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);

        IRenderedComponent<Hosts> page = context.Render<Hosts>();

        await page.WaitForStateAsync(() => page.FindAll(".pdk-table").Count == 1);
        Assert.Contains("app.example.com", page.Find(".pdk-table").TextContent, StringComparison.Ordinal);
        Assert.Equal(offered, page.FindAll($"a[href='{Paduraksa.Dashboard.DashboardRoutes.NewHost}']").Count > 0);
    }

    [Fact]
    public async Task TheHostsList_FollowsCommitsMadeElsewhere()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Viewer);
        config.Document = new ConfigDocument(Upstreams: [App]);
        IRenderedComponent<Hosts> page = context.Render<Hosts>();
        await page.WaitForStateAsync(() => page.Markup.Contains("No hosts yet", StringComparison.Ordinal));

        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);
        await page.InvokeAsync(() => config.CommitAsync(new ConfigCommitted(new RevisionRef(ConfigRevisionId.New(), 2), [])));

        await page.WaitForStateAsync(() => page.FindAll(".pdk-table").Count == 1);
        Assert.Contains("app.example.com", page.Find(".pdk-table").TextContent, StringComparison.Ordinal);
        page.Instance.Dispose();
        Assert.Equal(0, config.WatcherCount);
    }

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public async Task TheHostForm_IsReadOnlyWithoutConfigWrite(Role role, bool writable)
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, role);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);
        context.Render<MudPopoverProvider>();

        IRenderedComponent<HostEditor> page = context.Render<HostEditor>(parameters => parameters.Add(p => p.Key, Host.Id.ToString()));

        await page.WaitForStateAsync(() => page.FindAll(".pdk-form-grid[data-kind]").Count == 1);
        Assert.Equal(writable, page.FindAll("[data-action=review]").Count == 1);
        Assert.Equal(writable, page.FindAll("[data-action=delete]").Count == 1);
        Assert.Equal(!writable, page.Markup.Contains("Read-only", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("ups_0J9zb3xQk1Lr8m2V7aYc4D")]
    public async Task AnAddressThatIsNotAHostId_IsNotFound(string key)
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Viewer).Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);

        IRenderedComponent<HostEditor> page = context.Render<HostEditor>(parameters => parameters.Add(p => p.Key, key));

        await page.WaitForStateAsync(() => page.Markup.Contains("There is no such host", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUpstreamInUse_CannotBeDeleted_AndSaysWhy()
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, Role.Operator);
        config.Document = new ConfigDocument(Upstreams: [App], Hosts: [Host]);
        context.Render<MudPopoverProvider>();
        IRenderedComponent<UpstreamEditor> page = context.Render<UpstreamEditor>(parameters => parameters.Add(p => p.Key, App.Id.ToString()));
        await page.WaitForStateAsync(() => page.FindAll("[data-action=delete]").Count == 1);

        await page.Find("[data-action=delete]").ClickAsync(new());

        string banner = page.Find("[data-banner=in-use]").TextContent;
        Assert.Contains("app is still in use, so it can't be deleted.", banner, StringComparison.Ordinal);
        Assert.Contains("app.example.com (route /)", banner, StringComparison.Ordinal);
        Assert.Empty(config.Executed);
        Assert.Contains("app.example.com", page.Find("[data-section=usage]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSimulator_IsRefusedToARoleWithoutSimulate()
    {
        await using DashboardTestContext context = new();
        ConfigPages.SignIn(context, Role.Auditor);

        IRenderedComponent<Simulator> page = context.Render<Simulator>();

        await page.WaitForStateAsync(() => page.Markup.Contains("Your role can't run the simulator", StringComparison.Ordinal));
        Assert.Empty(page.FindAll("[data-action=simulate]"));
    }

    [Theory]
    [InlineData(Role.Operator, true)]
    [InlineData(Role.Viewer, false)]
    public async Task RollingBack_IsOfferedOnlyToWriters(Role role, bool offered)
    {
        await using DashboardTestContext context = new();
        FakeDashboardConfig config = ConfigPages.SignIn(context, role);
        UserId me = UserId.New();
        RevisionDetail first = Revision(1, null, new ConfigDocument(Upstreams: [App]), [new UpstreamChange(ConfigChangeKind.Added, App.Id, "/upstreams/0", [])], me);
        RevisionDetail second = Revision(2, first.Revision.Id, new ConfigDocument(Upstreams: [App], Hosts: [Host]), [new HostChange(ConfigChangeKind.Added, Host.Id, "/hosts/0", [])], me);
        config.Revisions[first.Revision.Id] = first;
        config.Revisions[second.Revision.Id] = second;
        context.Render<MudPopoverProvider>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/history?revision={first.Revision.Id}");

        IRenderedComponent<History> page = context.Render<History>();

        await page.WaitForStateAsync(() => page.Markup.Contains("Revision #1", StringComparison.Ordinal));
        Assert.Equal(2, page.FindAll(".pdk-timeline-item").Count);
        Assert.Contains("1 upstream added", page.Markup, StringComparison.Ordinal);
        Assert.Equal(offered, page.FindAll("[data-action=rollback]").Count == 1);
    }

    private static RevisionDetail Revision(long seq, ConfigRevisionId? parent, ConfigDocument document, IReadOnlyList<ConfigChange> changes, UserId user) =>
        new(
            new RevisionSummary(ConfigRevisionId.New(), seq, new ActorRef(ActorKind.User, user), Channel.Dashboard, DateTimeOffset.UtcNow, new string('0', 64), parent),
            changes,
            document);
}
