using Bunit;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The diff of the review drawer and the history (PLAN §6.1): grouped by object, fields old and new, references by name.</summary>
public sealed class ChangeListTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static readonly UpstreamConfig Api = new(UpstreamId.New(), "api", Destinations: [new DestinationConfig(DestinationId.New(), "http://api:9000")]);

    [Fact]
    public async Task ARevisionsDiff_GroupsByObject_WithOldAndNewValues()
    {
        HostId id = HostId.New();
        RouteId root = RouteId.New();
        RouteId api = RouteId.New();
        HostConfig before = new(id, ["app.example.com"], Routes: [new RouteConfig(root, App.Id), new RouteConfig(api, App.Id, Path: "/api")]);
        HostConfig after = before with
        {
            Names = ["app.example.com", "www.example.com"],
            Settings = new HostSettings { ForceHttps = true },
            Routes = [new RouteConfig(root, App.Id), new RouteConfig(api, Api.Id, Path: "/api")],
        };
        ConfigDocument older = new(Upstreams: [App, Api], Hosts: [before]);
        ConfigDocument newer = new(Upstreams: [App, Api], Hosts: [after]);

        IReadOnlyList<ChangeGroup> groups = ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer));
        await using DashboardTestContext context = new();
        IRenderedComponent<ChangeList> list = context.Render<ChangeList>(parameters => parameters.Add(c => c.Groups, groups));

        ChangeGroup group = Assert.Single(groups);
        Assert.Equal(("app.example.com +1", ConfigChangeKind.Modified), (group.Head.Title, group.Head.Kind));
        Assert.Contains(group.Head.Fields, f => f.Label == "Hostnames" && f.Before == "app.example.com" && f.After == "app.example.com, www.example.com");
        Assert.Contains(group.Head.Fields, f => f.Label == "HTTPS redirect" && f.Before is null && f.After == "on");
        ObjectChange route = Assert.Single(group.Children);
        Assert.Equal(("Route /api", ChangedObject.Route), (route.Title, route.What));
        Assert.Equal(new FieldChange("/upstreamId", "Upstream", "app", "api"), Assert.Single(route.Fields));

        Assert.Single(list.FindAll(".pdk-diff-group.pdk-diff-modified"));
        Assert.Single(list.FindAll(".pdk-diff-child"));
        Assert.Contains(list.FindAll(".pdk-diff-old"), e => e.TextContent == "app");
        Assert.Contains(list.FindAll(".pdk-diff-new"), e => e.TextContent == "api");
        Assert.Contains(list.FindAll(".pdk-diff-note"), e => e.TextContent == "set");
    }

    [Fact]
    public void AnAddedHost_ShowsWhatItSets_AndItsRoutes()
    {
        HostConfig host = new(HostId.New(), ["new.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id, Path: "/")]);
        ConfigDocument older = new(Upstreams: [App]);
        ConfigDocument newer = new(Upstreams: [App], Hosts: [host]);

        ChangeGroup group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        Assert.Equal(ConfigChangeKind.Added, group.Head.Kind);
        Assert.All(group.Head.Fields, f => Assert.Null(f.Before));
        Assert.Contains(group.Head.Fields, f => f.Label == "Hostnames" && f.After == "new.example.com");
        ObjectChange route = Assert.Single(group.Children);
        Assert.Equal(ConfigChangeKind.Added, route.Kind);
        Assert.Contains(route.Fields, f => f.Label == "Upstream" && f.After == "app");
    }

    [Fact]
    public async Task NoChanges_IsAnEmptyState()
    {
        await using DashboardTestContext context = new();

        IRenderedComponent<ChangeList> list = context.Render<ChangeList>(parameters => parameters
            .Add(c => c.Groups, [])
            .Add(c => c.EmptyTitle, "No differences"));

        Assert.Contains("No differences", list.Find(".pdk-empty").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/names/1", "Hostname 2")]
    [InlineData("/routes/0/path", "Route 1 > Path")]
    [InlineData("/settings/ipAllowList/addresses/2", "Settings > IP allow list > Address 3")]
    [InlineData("", "This change")]
    public void ErrorPaths_AreSaidInWords(string path, string expected) =>
        Assert.Equal(expected, ChangeView.Locate(path, null));

    [Fact]
    public void DocumentPaths_NameTheObject()
    {
        HostConfig host = new(HostId.New(), ["app.example.com"], Routes: [new RouteConfig(RouteId.New(), App.Id, Path: "/api")]);

        Assert.Equal("app.example.com > Route /api > Upstream", ChangeView.Locate("/hosts/0/routes/0/upstreamId", new ConfigDocument(Hosts: [host])));
    }
}
