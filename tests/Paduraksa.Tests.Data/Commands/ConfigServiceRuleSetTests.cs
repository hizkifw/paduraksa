using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>Rule sets through the command pipeline: stored canonically, versioned and audited like the other objects, every reference
/// resolved (a built-in preset included), and a set that something names can't be deleted.</summary>
public sealed class ConfigServiceRuleSetTests : IAsyncLifetime
{
    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static RuleSetConfig Set(RuleSetId id, string name, params RuleConfig[] rules) => new(id, name, Rules: rules);

    private static RuleConfig Deny(string prefix) => new(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, prefix)));

    private static HostSettings Names(RuleSetId id) => new() { RuleSet = new RuleSetSettings { RuleSetId = id } };

    [Fact]
    public async Task ASetIsStoredCanonicallyVersionedAndAudited()
    {
        var id = RuleSetId.New();
        var messy = Set(
            id,
            "  Block admin  ",
            new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/admin"), [" post", "GET", "get"], new RuleUserAgentConfig([" sqlmap", "SQLMAP", "Nikto"]))));

        var created = await _h.RunAsync(new UpsertRuleSetCommand(messy));

        Assert.Equal(ConfigCommandStatus.Committed, created.Status);
        var stored = Assert.Single((await _h.CurrentAsync()).RuleSets);
        Assert.Equal("Block admin", stored.Name);
        var match = Assert.Single(stored.Rules).Match;
        Assert.Equal(["GET", "POST"], match.Methods);
        Assert.Equal(["Nikto", "sqlmap"], match.UserAgent!.Contains);
        var createdVersion = stored.Version;
        Assert.True(createdVersion > 0);
        Assert.Equal("config.rule_set.create", (await _h.WaitForAuditAsync(e => e.Action == "config.rule_set.create")).Action);

        var changed = await _h.RunAsync(new UpsertRuleSetCommand(stored with { Rules = [Deny("/admin"), Deny("/wp-admin")] }));

        Assert.Equal(ConfigCommandStatus.Committed, changed.Status);
        Assert.True(Assert.Single((await _h.CurrentAsync()).RuleSets).Version > createdVersion);
        Assert.Equal("config.rule_set.update", (await _h.WaitForAuditAsync(e => e.Action == "config.rule_set.update")).Action);

        var stale = await _h.RunAsync(new UpsertRuleSetCommand(stored with { Notes = "late" }));
        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ASetThatCantBeUsedIsRefusedWithThePointerOfTheFieldInsideIt()
    {
        var bad = Set(
            RuleSetId.New(),
            "bad",
            Deny("/ok"),
            new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Regex, "(unclosed"))),
            new RuleConfig(RuleAction.Allow));

        var result = await _h.RunAsync(new UpsertRuleSetCommand(bad));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Collection(
            result.Errors,
            error => Assert.Equal(("/rules/1/match/path/pattern", ConfigErrorCodes.RulePathRegexInvalid), (error.Path, error.Code)),
            error => Assert.Equal(("/rules/2/match", ConfigErrorCodes.RuleEmpty), (error.Path, error.Code)));
        Assert.Empty((await _h.CurrentAsync()).RuleSets);
    }

    [Fact]
    public async Task ABuiltInPresetsIdCantBeWrittenAndIsNeverPartOfTheDocument()
    {
        var refused = await _h.RunAsync(new UpsertRuleSetCommand(Set(RuleSetPresets.AiCrawlers.Id, "mine", Deny("/x"))));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Equal(ConfigErrorCodes.RuleSetIdReserved, Assert.Single(refused.Errors).Code);

        // Naming a preset needs no set in the document.
        var (upstream, host) = (UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        var named = await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(RuleSetPresets.AiCrawlers.Id) }));

        Assert.Equal(ConfigCommandStatus.Committed, named.Status);
        Assert.Empty((await _h.CurrentAsync()).RuleSets);
    }

    [Fact]
    public async Task AHostCantNameASetThatDoesntExist()
    {
        var (upstream, host) = (UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));

        var refused = await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(RuleSetId.New()) }));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Equal(ConfigErrorCodes.HostRuleSetUnknown, Assert.Single(refused.Errors).Code);
    }

    [Fact]
    public async Task ASetInUseCantBeDeletedAndAnUnusedOneCan()
    {
        var (set, upstream, host) = (RuleSetId.New(), UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertRuleSetCommand(Set(set, "blocked", Deny("/admin"))));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = Names(set) }));
        var version = Assert.Single((await _h.CurrentAsync()).RuleSets).Version;

        var refused = await _h.RunAsync(new DeleteRuleSetCommand(set, version));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Contains(refused.RelatedErrors, e => e.Code == ConfigErrorCodes.HostRuleSetUnknown);
        Assert.Single((await _h.CurrentAsync()).RuleSets);

        var hostVersion = Assert.Single((await _h.CurrentAsync()).Hosts).Version;
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Version = hostVersion }));
        var deleted = await _h.RunAsync(new DeleteRuleSetCommand(set, version));

        Assert.Equal(ConfigCommandStatus.Committed, deleted.Status);
        Assert.Empty((await _h.CurrentAsync()).RuleSets);
        Assert.Equal("config.rule_set.delete", (await _h.WaitForAuditAsync(e => e.Action == "config.rule_set.delete")).Action);
    }

    [Fact]
    public async Task ASetAHostReachesThroughItsProfileTheDefaultsOrARouteCantBeDeleted()
    {
        var (set, upstream, profile, host, route) = (RuleSetId.New(), UpstreamId.New(), ProfileId.New(), HostId.New(), RouteId.New());
        await _h.RunAsync(new UpsertRuleSetCommand(Set(set, "blocked", Deny("/admin"))));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        var version = Assert.Single((await _h.CurrentAsync()).RuleSets).Version;

        // Through the profile the host uses.
        await _h.RunAsync(new UpsertProfileCommand(Profile(profile, "lan") with { Settings = Names(set) }));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { ProfileId = profile }));
        var viaProfile = await _h.RunAsync(new DeleteRuleSetCommand(set, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaProfile.Status);
        Assert.Contains("inherited from its profile", Assert.Single(viaProfile.RelatedErrors).Message, StringComparison.Ordinal);

        // Through the global defaults.
        var current = await _h.CurrentAsync();
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Version = current.Hosts[0].Version }));
        await _h.RunAsync(new UpdateSettingsCommand(current.Settings with { Defaults = Names(set) }));
        var viaDefaults = await _h.RunAsync(new DeleteRuleSetCommand(set, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaDefaults.Status);
        Assert.Contains("inherited from the global defaults", Assert.Single(viaDefaults.RelatedErrors).Message, StringComparison.Ordinal);

        // Through a route of its own.
        await _h.RunAsync(new UpdateSettingsCommand((await _h.CurrentAsync()).Settings with { Defaults = new HostSettings() }));
        var withRoute = ProxyHost(host, "app.example.com", upstream, route);
        await _h.RunAsync(new UpsertHostCommand(withRoute with
        {
            Version = (await _h.CurrentAsync()).Hosts[0].Version,
            Routes = [withRoute.Routes[0] with { Settings = new RouteSettings { RuleSet = new RuleSetSettings { RuleSetId = set } } }],
        }));
        var viaRoute = await _h.RunAsync(new DeleteRuleSetCommand(set, version));
        Assert.Equal(ConfigCommandStatus.Invalid, viaRoute.Status);
        Assert.Equal(ConfigErrorCodes.RouteRuleSetUnknown, Assert.Single(viaRoute.RelatedErrors).Code);
    }
}
