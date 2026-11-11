using Paduraksa.Application.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>Where a rule set's policy sits in the host chain and the route chains, and what the mapping hands the compiler.</summary>
public sealed class RuleSetMappingTests
{
    private static readonly ConfigRevisionId Revision = Id<ConfigRevisionId>(7);
    private static readonly RuleSetId Blocked = Id<RuleSetId>(1);
    private static readonly RuleSetId Other = Id<RuleSetId>(2);
    private static readonly AccessListConfig Passwords = new(Id<AccessListId>(1), "passwords", BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Id<PasswordCredentialId>(1))]));
    private static readonly AccessListConfig Lan = new(Id<AccessListId>(2), "lan", Ip: new AccessListIp(Allow: ["192.168.0.0/16"]));
    private static readonly AccessListConfig LanAndPasswords = new(Id<AccessListId>(3), "lan-and-passwords", Ip: new AccessListIp(Allow: ["192.168.0.0/16"]), BasicAuth: Passwords.BasicAuth);

    private static RuleSetSettings Use(RuleSetId id) => new() { RuleSetId = id };

    private static AccessListSettings UseList(AccessListConfig list) => new() { AccessListId = list.Id };

    [Fact]
    public void WithoutRouteOverridesTheSetGuardsTheWholeHostFromTheHostChain()
    {
        var host = MapHost(new HostSettings { RuleSet = Use(Blocked) }, new RouteSettings(), new RouteSettings { RuleSet = Use(Blocked) });

        Assert.Equal(new RuleSetPolicySpec(Blocked), Assert.Single(host.Policies));
        Assert.All(host.Routes, route => Assert.Empty(route.Policies));
        Assert.All(host.Routes, route => Assert.Equal(Blocked, route.RuleSet));
        Assert.Equal(new HostRuleSetSpec { Enabled = true, RuleSetId = Blocked }, host.RuleSet);
    }

    [Fact]
    public void ARouteThatNamesAnotherSetOrTurnsTheRulesOffMakesEveryRouteCarryItsOwn()
    {
        var host = MapHost(
            new HostSettings { RuleSet = Use(Blocked) },
            new RouteSettings { RuleSet = Use(Other) },
            new RouteSettings { RuleSet = new RuleSetSettings { Enabled = false } });

        // A route's set replaces the host's and a deny can't be lifted by a union, so the host chain carries none.
        Assert.Empty(host.Policies);
        Assert.Equal(new RuleSetPolicySpec(Other, RouteOverride: true), Assert.Single(host.Routes[0].Policies));
        Assert.Empty(host.Routes[1].Policies);
        Assert.Equal(Other, host.Routes[0].RuleSet);
        Assert.Null(host.Routes[1].RuleSet);
    }

    [Fact]
    public void ARouteThatInheritsKeepsTheHostsSetAndIsNotMarkedAsAnOverride()
    {
        var host = MapHost(new HostSettings { RuleSet = Use(Blocked) }, new RouteSettings(), new RouteSettings { RuleSet = Use(Other) });

        Assert.Equal(new RuleSetPolicySpec(Blocked), Assert.Single(host.Routes[0].Policies));
        Assert.Equal(new RuleSetPolicySpec(Other, RouteOverride: true), Assert.Single(host.Routes[1].Policies));
    }

    [Fact]
    public void ARouteCanGuardAnUnguardedHostAndKeepsAReferenceWhileItIsOff()
    {
        var host = MapHost(null, new RouteSettings(), new RouteSettings { RuleSet = Use(Blocked) });
        Assert.Empty(host.Policies);
        Assert.Empty(host.Routes[0].Policies);
        Assert.Equal(new RuleSetPolicySpec(Blocked, RouteOverride: true), Assert.Single(host.Routes[1].Policies));

        var off = MapHost(null, new RouteSettings(), new RouteSettings { RuleSet = new RuleSetSettings { Enabled = false, RuleSetId = Blocked } });
        Assert.Empty(off.Policies);
        Assert.All(off.Routes, route => Assert.Empty(route.Policies));
    }

    [Fact]
    public void TheSetComesBeforeAPasswordCheckSoARefusalNeverCostsAPbkdf2()
    {
        // Every route resolves to the same list and the same set: the list's password check is the host chain's, and the set
        // is put in front of it.
        var host = MapHost(
            new HostSettings { AccessList = UseList(Passwords), RuleSet = Use(Blocked) },
            new RouteSettings(),
            new RouteSettings(),
            Passwords);

        Assert.Equal(
            [new RuleSetPolicySpec(Blocked)],
            host.Policies.OfType<RuleSetPolicySpec>());
        Assert.Empty(host.Policies.OfType<AccessListPolicySpec>());
        Assert.All(host.Routes, route => Assert.Equal(new AccessListPolicySpec(Passwords.Id), Assert.Single(route.Policies)));
    }

    [Fact]
    public void ARouteWithItsOwnSetPutsItBeforeTheListsPasswordCheck()
    {
        var host = MapHost(
            new HostSettings { AccessList = UseList(Passwords) },
            new RouteSettings { RuleSet = Use(Blocked) },
            new RouteSettings(),
            Passwords);

        Assert.Equal([new RuleSetPolicySpec(Blocked, RouteOverride: true), new AccessListPolicySpec(Passwords.Id)], host.Routes[0].Policies);
        Assert.Equal([new AccessListPolicySpec(Passwords.Id)], host.Routes[1].Policies);
    }

    [Fact]
    public void TheSetFollowsTheAddressOnlyCheckOfAListWithPasswordsAndPrecedesNothingElse()
    {
        var host = MapHost(
            new HostSettings { AccessList = UseList(LanAndPasswords), RuleSet = Use(Blocked) },
            new RouteSettings(),
            new RouteSettings(),
            LanAndPasswords);

        // Host chain: the address rules, then the rules; the route chain keeps the password check.
        Assert.Equal(
            [new AccessListPolicySpec(LanAndPasswords.Id, AccessListStage.AddressOnly), new RuleSetPolicySpec(Blocked)],
            host.Policies);
        Assert.All(host.Routes, route => Assert.Equal(new AccessListPolicySpec(LanAndPasswords.Id), Assert.Single(route.Policies)));
    }

    [Fact]
    public void AListWithoutPasswordsKeepsItsPlaceAndTheSetFollowsIt()
    {
        var host = MapHost(
            new HostSettings { AccessList = UseList(Lan), RuleSet = Use(Blocked) },
            new RouteSettings(),
            new RouteSettings(),
            Lan);

        Assert.Equal([new AccessListPolicySpec(Lan.Id), new RuleSetPolicySpec(Blocked)], host.Policies);
    }

    [Fact]
    public void AHostWithoutRoutesCarriesTheSetBeforeTheWholeListWithItsPasswords()
    {
        var document = Document(null, null, new HostSettings { ForceHttps = true, AccessList = UseList(Passwords), RuleSet = Use(Blocked) });
        var config = document.Hosts[0] with { Kind = HostKind.Respond, Routes = [], Respond = new RespondConfig(503, "soon"), Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } };

        var mapped = Assert.Single(TestMapping.Map(document with { Hosts = [config], AccessLists = [Passwords], RuleSets = [Set(Blocked)] }, Revision, CertificateInventory.Empty).Model.Hosts);

        Assert.Equal([new RuleSetPolicySpec(Blocked), new AccessListPolicySpec(Passwords.Id, AccessListStage.Full, DeferPasswordsOnPlainHttp: true)], mapped.Policies);
    }

    [Fact]
    public void ADocumentsSetsAndOnlyThePresetsSomethingNamesReachTheCompiler()
    {
        var document = Document(
            new HostSettings { RuleSet = Use(RuleSetPresets.AiCrawlers.Id) },
            new HostSettings { RuleSet = Use(RuleSetPresets.KnownScanners.Id) },
            new HostSettings { RuleSet = Use(Blocked) });
        var host = document.Hosts[0] with
        {
            Routes = [new RouteConfig(RootRoute, AppUpstream, Settings: new RouteSettings { RuleSet = Use(RuleSetPresets.EmptyUserAgent.Id) })],
        };

        var model = TestMapping.Map(document with { Hosts = [host], RuleSets = [Set(Blocked)] }, Revision, CertificateInventory.Empty).Model;

        Assert.Equal(
            [Blocked, RuleSetPresets.AiCrawlers.Id, RuleSetPresets.KnownScanners.Id, RuleSetPresets.EmptyUserAgent.Id],
            model.RuleSets.Select(set => set.Id));
        Assert.Equal("Block empty user agent", model.RuleSets.Single(set => set.Id == RuleSetPresets.EmptyUserAgent.Id).Name);

        var unused = TestMapping.Map(Document(null, null, null) with { RuleSets = [Set(Blocked)] }, Revision, CertificateInventory.Empty).Model;
        Assert.Equal([Blocked], unused.RuleSets.Select(set => set.Id));
    }

    [Fact]
    public void ARuleMapsItsConditionsAndTheActionOneToOne()
    {
        var set = new RuleSetConfig(
            Blocked,
            "blocked",
            Rules:
            [
                new RuleConfig(RuleAction.Allow, new RuleMatchConfig(new RulePathConfig(RulePathKind.Prefix, "/health"))),
                new RuleConfig(RuleAction.Deny, new RuleMatchConfig(new RulePathConfig(RulePathKind.Glob, "/**/.env"), ["POST"], new RuleUserAgentConfig(["sqlmap"], Empty: true))),
                new RuleConfig(RuleAction.Drop, new RuleMatchConfig(Methods: ["TRACE"])),
            ]);

        var mapped = Assert.Single(TestMapping.Map(Document(new HostSettings { RuleSet = Use(Blocked) }, null, null) with { RuleSets = [set] }, Revision, CertificateInventory.Empty).Model.RuleSets);

        Assert.Equal("blocked", mapped.Name);
        Assert.Collection(
            mapped.Rules,
            rule => Assert.Equal(new RuleSpec { Action = RuleAction.Allow, PathKind = RulePathKind.Prefix, Pattern = "/health" }, rule),
            rule =>
            {
                Assert.Equal(RuleAction.Deny, rule.Action);
                Assert.Equal(RulePathKind.Glob, rule.PathKind);
                Assert.Equal("/**/.env", rule.Pattern);
                Assert.Equal(["POST"], rule.Methods);
                Assert.Equal(["sqlmap"], rule.UserAgentContains);
                Assert.True(rule.UserAgentEmpty);
            },
            rule =>
            {
                Assert.Equal(RuleAction.Drop, rule.Action);
                Assert.Null(rule.PathKind);
                Assert.Equal(["TRACE"], rule.Methods);
            });
    }

    private static RuleSetConfig Set(RuleSetId id) => new(id, "set " + id, Rules: [new RuleConfig(RuleAction.Deny, new RuleMatchConfig(Methods: ["TRACE"]))]);

    private static HostSpec MapHost(HostSettings? host, RouteSettings first, RouteSettings second, params AccessListConfig[] lists)
    {
        var document = Document(null, null, host) with { AccessLists = lists, RuleSets = [Set(Blocked), Set(Other)] };
        var config = document.Hosts[0] with
        {
            Routes = [new RouteConfig(RootRoute, AppUpstream, Path: "/a", Settings: first), new RouteConfig(ApiRoute, AppUpstream, Path: "/b", Settings: second)],
        };
        return Assert.Single(TestMapping.Map(document with { Hosts = [config] }, Revision, CertificateInventory.Empty).Model.Hosts);
    }

    private static ConfigDocument Document(HostSettings? defaults, HostSettings? profile, HostSettings? host) => new(
        Settings: new GlobalSettings(Defaults: defaults),
        Profiles: profile is null ? [] : [new ProfileConfig(LanProfile, "profile", Settings: profile)],
        Upstreams: [new UpstreamConfig(AppUpstream, "app", Destinations: [new DestinationConfig(AppDestination, "http://app")])],
        Hosts: [new HostConfig(AppHost, ["app.example.com"], ProfileId: profile is null ? null : LanProfile, Settings: host, Routes: [new RouteConfig(RootRoute, AppUpstream)])]);
}
