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
    public void AnAddedAccessList_ShowsItsUsersByNameAndNeverAPasswordOrACredentialId()
    {
        var alice = PasswordCredentialId.New();
        AccessListConfig list = new(
            AccessListId.New(),
            "Office",
            Ip: new AccessListIp(["10.0.0.0/8"], ["10.9.0.0/16"], DropDenied: true),
            BasicAuth: new AccessListBasicAuth("Staff", [new AccessListUserConfig("alice", alice), new AccessListUserConfig("bob", Password: "correct horse")]),
            Satisfy: AccessListSatisfy.Any);
        ConfigDocument older = new();
        ConfigDocument newer = new(AccessLists: [list]);

        var group = Assert.Single(ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer)));

        Assert.Equal(("Office", ChangedObject.AccessList, ConfigChangeKind.Added), (group.Head.Title, group.Head.What, group.Head.Kind));
        Assert.Contains(group.Head.Fields, f => f.Label == "Addresses" && f.After!.Contains("10.0.0.0/8", StringComparison.Ordinal));
        Assert.Contains(group.Head.Fields, f => f.Label == "Passwords" && f.After!.Contains("alice, bob (new password)", StringComparison.Ordinal));
        var shown = string.Join('\n', group.Head.Fields.SelectMany(f => new[] { f.Label, f.Before, f.After }).OfType<string>());
        Assert.DoesNotContain(alice.ToString(), shown, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(list.Id.ToString(), shown, StringComparison.Ordinal);
        Assert.Equal(list.Id, Assert.Single(ChangeView.Apply(older, new UpsertAccessListCommand(list))!.AccessLists).Id);
    }

    private static AccessListConfig Office(params AccessListUserConfig[] users) => new(
        AccessListId.New(),
        "Office",
        Ip: new AccessListIp(["10.0.0.0/8"], DropDenied: true),
        BasicAuth: new AccessListBasicAuth("Staff", users),
        Satisfy: AccessListSatisfy.Any,
        PassCredentialsUpstream: true);

    // The review is built from the server's change list, which names a list whose only change is a password (the documents' users are the same).
    private static ObjectChange Reviewed(ConfigDocument before, ConfigCommand command, IReadOnlyList<ConfigChange>? changes = null)
    {
        var after = ChangeView.Apply(before, command)!;
        return Assert.Single(ChangeView.Build(before, after, changes ?? ConfigDiff.Compare(before, after))).Head;
    }

    private static string Shown(ObjectChange change, string label) =>
        string.Join(" | ", change.Fields.Where(f => f.Label == label).SelectMany(f => new[] { f.Before, f.After }).OfType<string>());

    [Fact]
    public void AnAddedAccessListInAReview_ReadsLikeTheRestOfTheDashboard_PasswordsByNameAndWordsForEveryChoice()
    {
        var list = Office(new AccessListUserConfig("alice", Password: "correct horse"), new AccessListUserConfig("bob", Password: "battery staple"));

        var change = Reviewed(new ConfigDocument(), new UpsertAccessListCommand(list));

        var shown = string.Join('\n', change.Fields.Select(f => $"{f.Label}: {f.After}"));
        Assert.Contains("Passwords: Realm: Staff · alice (new password), bob (new password)", shown, StringComparison.Ordinal);
        Assert.Contains("Combine: Address or password", shown, StringComparison.Ordinal);
        Assert.Contains("Drop denied requests: on", shown, StringComparison.Ordinal);
        Assert.Contains("Send credentials upstream: on", shown, StringComparison.Ordinal);
        Assert.Contains("Allowed addresses: 10.0.0.0/8", shown, StringComparison.Ordinal);
        foreach (var raw in new[] { "Username", "Users:", "username", "credentialId", "correct horse", "(pending)", "\"any\"", ": any" })
        {
            Assert.DoesNotContain(raw, shown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ChangingOnlyAPassword_ReadsAsThatUsersPasswordChanged()
    {
        var alice = new AccessListUserConfig("alice", PasswordCredentialId.New());
        var list = Office(alice, new AccessListUserConfig("bob", PasswordCredentialId.New()));
        ConfigDocument before = new(AccessLists: [list]);

        var change = Reviewed(
            before,
            new UpsertAccessListCommand(list with { BasicAuth = list.BasicAuth! with { Users = [alice with { Password = "a new password" }, list.BasicAuth.Users[1]] } }),
            [new AccessListChange(ConfigChangeKind.Modified, list.Id, "/accessLists/0", ["/accessLists/0/basicAuth/users"])]);

        Assert.Equal("Passwords > Users", Assert.Single(change.Fields).Label);
        Assert.Equal("alice, bob | alice (password changed), bob", Shown(change, "Passwords > Users"));
    }

    [Fact]
    public void AddingARemovingAndChangingUsers_AreEachSaidInTheirOwnWords()
    {
        var alice = new AccessListUserConfig("alice", PasswordCredentialId.New());
        var bob = new AccessListUserConfig("bob", PasswordCredentialId.New());
        var list = Office(alice, bob);
        ConfigDocument before = new(AccessLists: [list]);

        var change = Reviewed(before, new UpsertAccessListCommand(list with
        {
            Satisfy = AccessListSatisfy.All,
            BasicAuth = list.BasicAuth! with { Users = [alice, new AccessListUserConfig("carol", Password: "carol's password")] },
        }));

        Assert.Equal("alice, bob (removed) | alice, carol (new password)", Shown(change, "Passwords > Users"));
        Assert.Equal("Address or password | Address and password", Shown(change, "Combine"));
    }

    [Fact]
    public void AHistoryEntryWhoseUsersAreTheSame_SaysAPasswordWasChangedInsteadOfShowingNoChange()
    {
        var list = Office(new AccessListUserConfig("alice", PasswordCredentialId.New()));
        ConfigDocument before = new(AccessLists: [list]);
        ConfigDocument after = new(AccessLists: [list with { Version = 5 }]);
        List<ConfigChange> changes = [new AccessListChange(ConfigChangeKind.Modified, list.Id, "/accessLists/0", ["/accessLists/0/basicAuth/users"])];

        var change = Assert.Single(ChangeView.Build(before, after, changes)).Head;

        Assert.Equal("alice | alice; a password was changed", Shown(change, "Passwords > Users"));
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
    public void ARuleSetsRules_AreShownAsNumberedSentences_AndAHostsSetByItsName()
    {
        RuleSetConfig set = new(
            RuleSetId.New(),
            "Scanners",
            Rules:
            [
                new RuleConfig(RuleAction.Allow, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/health"))),
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Exact, "/admin"), ["POST", "PUT"], new RuleUserAgentConfig(["sqlmap", "nikto"], Empty: true))),
                new RuleConfig(RuleAction.Drop, new RuleMatchConfig(UserAgent: new RuleUserAgentConfig(Empty: true))),
            ]);
        HostConfig before = new(HostId.New(), ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200));
        var after = before with { Settings = new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = set.Id } } };
        ConfigDocument older = new(Hosts: [before]);
        ConfigDocument newer = new(RuleSets: [set], Hosts: [after]);

        var groups = ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer));

        var rules = Assert.Single(Assert.Single(groups, group => group.Head.What == ChangedObject.RuleSet).Head.Fields, field => field.Label == "Rules");
        Assert.Equal(
            "1. Allow when path starts with /health; "
            + "2. Deny when path is /admin and method is POST or PUT and user agent contains \"sqlmap\" or \"nikto\" or is empty; "
            + "3. Drop when user agent is empty",
            rules.After);
        var host = Assert.Single(Assert.Single(groups, group => group.Head.What == ChangedObject.Host).Head.Fields, field => field.Label == "Rule set");
        Assert.Equal("Rule set: Scanners", host.After);
        Assert.DoesNotContain(set.Id.ToString(), host.After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerSettings_ReadAsTheirKindsWithTheOldValueAndTheNew()
    {
        ConfigDocument older = new(Settings: new GlobalSettings(Server: new ServerSettings { LimitsMaxConnections = 12_000, AdminSessionIdleTimeout = TimeSpan.FromMinutes(90) }));
        ConfigDocument newer = new(Settings: new GlobalSettings(Server: new ServerSettings
        {
            MetricsRetention = TimeSpan.FromDays(7),
            McpEnabled = true,
            LimitsMaxConnections = 500,
            LogMaxTotalSize = 512L * 1024 * 1024,
            AdminLockoutDuration = TimeSpan.FromMinutes(15),
        }));

        var groups = ChangeView.Build(older, newer, ConfigDiff.Compare(older, newer), Paduraksa.Dashboard.DisplayFormat.Invariant);
        await using DashboardTestContext context = new();
        var list = context.Render<ChangeList>(parameters => parameters.Add(c => c.Groups, groups));

        var fields = Assert.Single(groups).Head.Fields;
        Assert.Equal(
            [
                ("Traffic and security history", "90 days", "7 days"),
                ("Rotated files in total", "1 GiB", "512 MiB"),
                ("Lockout", "15 minutes (default)", "15 minutes"),
                ("Idle timeout", "90 minutes", "2 hours"),
                ("Serve /mcp", "off", "on"),
                ("Open connections", "12,000", "500"),
            ],
            fields.Select(f => (f.Label, f.Before, f.After)));
        var text = list.Find(".pdk-diff-old").ParentElement!.TextContent;
        Assert.Contains("90 days", text, StringComparison.Ordinal);
        Assert.DoesNotContain("00:00", list.Markup, StringComparison.Ordinal);
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
