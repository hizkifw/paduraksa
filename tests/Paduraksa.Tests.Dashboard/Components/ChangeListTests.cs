using Bunit;

using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Tests.Dashboard.Components;

public sealed class ChangeListTests
{
    private static readonly UpstreamConfig App = new(UpstreamId.New(), "app", Destinations: [new DestinationConfig(DestinationId.New(), "http://app:8080")]);

    private static readonly UpstreamConfig Api = new(UpstreamId.New(), "api", Destinations: [new DestinationConfig(DestinationId.New(), "http://api:9000")]);

    [Fact]
    public async Task ARevisionsDiff_GroupsByObject_WithOldAndNewValues()
    {
        var id = HostId.New();
        var root = RouteId.New();
        var api = RouteId.New();
        HostConfig before = new(id, ["app.example.com"], Routes: [new RouteConfig(root, App.Id), new RouteConfig(api, App.Id, Path: "/api")]);
        var after = before with
        {
            Names = ["app.example.com", "www.example.com"],
            Settings = new HostSettings { ForceHttps = true },
            Routes = [new RouteConfig(root, App.Id), new RouteConfig(api, Api.Id, Path: "/api")],
        };
        ConfigDocument older = new(Upstreams: [App, Api], Hosts: [before]);
        ConfigDocument newer = new(Upstreams: [App, Api], Hosts: [after]);

        var groups = ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer));
        await using DashboardTestContext context = new();
        var list = context.Render<ChangeList>(parameters => parameters.Add(c => c.Groups, groups));

        var group = Assert.Single(groups);
        Assert.Equal(("app.example.com +1", ConfigChangeKind.Modified), (group.Head.Title, group.Head.Kind));
        Assert.Contains(group.Head.Fields, f => f.Label == "Hostnames" && f.Before == "app.example.com" && f.After == "app.example.com, www.example.com");
        Assert.Contains(group.Head.Fields, f => f.Label == "HTTPS redirect" && f.Before is null && f.After == "on");
        var route = Assert.Single(group.Children);
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

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        Assert.Equal(ConfigChangeKind.Added, group.Head.Kind);
        Assert.All(group.Head.Fields, f => Assert.Null(f.Before));
        Assert.Contains(group.Head.Fields, f => f.Label == "Hostnames" && f.After == "new.example.com");
        var route = Assert.Single(group.Children);
        Assert.Equal(ConfigChangeKind.Added, route.Kind);
        Assert.Contains(route.Fields, f => f.Label == "Upstream" && f.After == "app");
    }

    [Fact]
    public void AnAddedRateLimitPolicy_IsItsOwnChange_AndItsFieldsAreLabelled()
    {
        RateLimitPolicyConfig policy = new(
            RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50, Key: RateLimitKey.Header, Header: "X-Api-Key");
        ConfigDocument older = new();
        ConfigDocument newer = new(RateLimitPolicies: [policy]);

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        Assert.Equal(("Strict API", ChangedObject.RateLimitPolicy, ConfigChangeKind.Added), (group.Head.Title, group.Head.What, group.Head.Kind));
        Assert.Contains(group.Head.Fields, f => f.Label == "Requests" && f.After == "10");
        Assert.Contains(group.Head.Fields, f => f.Label == "Burst" && f.After == "50");
        Assert.Contains(group.Head.Fields, f => f.Label == "Count by" && f.After == "Header");
        Assert.DoesNotContain(policy.Id.ToString(), group.Head.Fields.SelectMany(f => new[] { f.Label, f.Before, f.After }).OfType<string>(), StringComparer.Ordinal);

        // The command's document is resolvable too, so the review drawer shows this diff rather than the bare document.
        Assert.Equal(policy.Id, Assert.Single(ChangeView.Apply(older, new UpsertRateLimitPolicyCommand(policy))!.RateLimitPolicies).Id);
    }

    [Fact]
    public void AHostsRateLimitChange_NamesThePolicy_NotItsId()
    {
        RateLimitPolicyConfig policy = new(RateLimitPolicyId.New(), "Strict API", Requests: 10, Per: RateLimitPeriod.Second, Burst: 50);
        HostConfig before = new(HostId.New(), ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200));
        var after = before with { Settings = new HostSettings { RateLimit = new RateLimitSettings { PolicyId = policy.Id } } };
        ConfigDocument older = new(RateLimitPolicies: [policy], Hosts: [before]);
        ConfigDocument newer = older with { Hosts = [after] };

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        var field = Assert.Single(group.Head.Fields, f => f.Label == "Rate limit");
        Assert.Equal(("", "Policy: Strict API"), (field.Before ?? string.Empty, field.After ?? string.Empty));
        Assert.DoesNotContain(policy.Id.ToString(), field.After!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddedAccessList_ShowsItsUsersByNameAndNeverAPasswordOrAHash()
    {
        const string Hash = "$pbkdf2-sha256$i=210000$c2FsdHNhbHRzYWx0c2FsdA$YWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWE";
        AccessListConfig list = new(
            AccessListId.New(),
            "Office",
            Ip: new AccessListIp(["10.0.0.0/8"], ["10.9.0.0/16"], DropDenied: true),
            BasicAuth: new AccessListBasicAuth("Staff", [new AccessListUserConfig("alice", Hash), new AccessListUserConfig("bob", Password: "correct horse")]),
            Satisfy: AccessListSatisfy.Any);
        ConfigDocument older = new();
        ConfigDocument newer = new(AccessLists: [list]);

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        Assert.Equal(("Office", ChangedObject.AccessList, ConfigChangeKind.Added), (group.Head.Title, group.Head.What, group.Head.Kind));
        Assert.Contains(group.Head.Fields, f => f.Label == "Addresses" && f.After!.Contains("10.0.0.0/8", StringComparison.Ordinal));
        Assert.Contains(group.Head.Fields, f => f.Label == "Passwords" && f.After!.Contains("alice, bob (new password)", StringComparison.Ordinal));
        var shown = string.Join('\n', group.Head.Fields.SelectMany(f => new[] { f.Label, f.Before, f.After }).OfType<string>());
        Assert.DoesNotContain(Hash, shown, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(list.Id.ToString(), shown, StringComparison.Ordinal);
        Assert.Equal(list.Id, Assert.Single(ChangeView.Apply(older, new UpsertAccessListCommand(list))!.AccessLists).Id);
    }

    [Fact]
    public void AHostsAccessListChange_NamesTheList_NotItsId()
    {
        AccessListConfig list = new(AccessListId.New(), "Office", Ip: new AccessListIp(["10.0.0.0/8"]));
        HostConfig before = new(HostId.New(), ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200));
        var after = before with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = list.Id } } };
        ConfigDocument older = new(AccessLists: [list], Hosts: [before]);
        ConfigDocument newer = older with { Hosts = [after] };

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        var field = Assert.Single(group.Head.Fields, f => f.Label == "Access list");
        Assert.Equal(("", "Access list: Office"), (field.Before ?? string.Empty, field.After ?? string.Empty));
        Assert.DoesNotContain(list.Id.ToString(), field.After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoChanges_IsAnEmptyState()
    {
        await using DashboardTestContext context = new();

        var list = context.Render<ChangeList>(parameters => parameters
            .Add(c => c.Groups, [])
            .Add(c => c.EmptyTitle, "No differences"));

        Assert.Contains("No differences", list.Find(".pdk-empty").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/names/1", "Hostname 2")]
    [InlineData("/routes/0/path", "Route 1 > Path")]
    [InlineData("/settings/accessList/accessListId", "Settings > Access list > Access list")]
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
