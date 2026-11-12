using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Unit.Config;

using Xunit;

using static Paduraksa.Tests.Unit.Yaml.YamlTestSupport;

namespace Paduraksa.Tests.Unit.Yaml;

public sealed class ConfigYamlMapperTests
{
    private const string Header = "apiVersion: paduraksa.dev/v1\nkind: Config\n";

    private static ConfigError OnlyError(string yaml, ConfigDocument? stored = null, ConfigImportMode mode = ConfigImportMode.Replace)
    {
        var result = Read(Header + yaml, stored, mode);
        Assert.False(result.Succeeded, "the YAML was accepted");
        return Assert.Single(result.Errors);
    }

    [Fact]
    public void AnEmptyConfigIsTheDefaultDocument()
    {
        var document = Mapped(Header);

        Assert.Empty(document.Hosts);
        Assert.Equal(new ConfigDocument().Settings, document.Settings);
    }

    [Fact]
    public void ObjectsWithoutIdsGetOneAndRoutesAndDestinationsToo()
    {
        var document = Mapped(Header + """
            upstreams:
              - name: app
                destinations:
                  - address: http://app:8080
            hosts:
              - names: [app.example.com]
                routes:
                  - upstream: app
                    path: /
            """);

        var upstream = Assert.Single(document.Upstreams);
        var destination = Assert.Single(upstream.Destinations);
        var host = Assert.Single(document.Hosts);
        Assert.Equal(upstream.Id, Assert.Single(host.Routes).UpstreamId);
        Assert.False(host.Id.IsEmpty);
        Assert.False(destination.Id.IsEmpty);
        Assert.NotEqual(host.Id.ToString(), Assert.Single(host.Routes).Id.ToString());
    }

    [Fact]
    public void AGivenIdIsKept()
    {
        var id = ConfigSamples.Id<HostId>(7);

        var document = Mapped(Header + $"hosts:\n  - id: {id}\n    names: [a.example.com]\n    kind: respond\n    respond: {{statusCode: 200}}\n");

        Assert.Equal(id, Assert.Single(document.Hosts).Id);
    }

    [Fact]
    public void ReferencesResolveByNameIgnoringCaseAndByIdAndTheBuiltInPresetsByName()
    {
        var policy = ConfigSamples.Id<RateLimitPolicyId>(3);
        var document = Mapped(Header + $$"""
            profiles:
              - name: Media
            rateLimitPolicies:
              - id: {{policy}}
                name: Strict
            accessLists:
              - name: office
            upstreams:
              - name: jelly
            hosts:
              - names: [a.example.com]
                profile: media
                routes:
                  - upstream: JELLY
                    settings:
                      rateLimit: {policy: {{policy}}}
                      accessList: {accessList: office}
                      ruleSet: {ruleSet: Block AI crawlers}
            """);

        var host = Assert.Single(document.Hosts);
        Assert.Equal(Assert.Single(document.Profiles).Id, host.ProfileId);
        var route = Assert.Single(host.Routes);
        Assert.Equal(document.Upstreams[0].Id, route.UpstreamId);
        Assert.Equal(policy, route.Settings.RateLimit!.PolicyId);
        Assert.Equal(document.AccessLists[0].Id, route.Settings.AccessList!.AccessListId);
        Assert.Equal(RuleSetPresets.AiCrawlers.Id, route.Settings.RuleSet!.RuleSetId);
    }

    [Fact]
    public void AReferenceToNothingIsAnErrorWithItsPathAndLine()
    {
        var error = OnlyError("hosts:\n  - names: [a.example.com]\n    profile: nope\n");

        Assert.Equal("/hosts/0/profile", error.Path);
        Assert.Equal(YamlErrorCodes.ReferenceUnknown, error.Code);
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameTwoObjectsShareIsAmbiguousNotAGuess()
    {
        var result = Read(Header + "profiles:\n  - name: dup\n  - name: DUP\nhosts:\n  - names: [a.example.com]\n    profile: dup\n");

        var error = Assert.Single(result.Errors);
        Assert.Equal(YamlErrorCodes.ReferenceAmbiguous, error.Code);
        Assert.Equal("/hosts/0/profile", error.Path);
    }

    [Fact]
    public void AnIdThatNoObjectHasIsKeptForTheValidatorToJudge()
    {
        var missing = ConfigSamples.Id<ProfileId>(999);

        var document = Mapped(Header + $"hosts:\n  - names: [a.example.com]\n    profile: {missing}\n");

        Assert.Equal(missing, document.Hosts[0].ProfileId);
    }

    [Fact]
    public void MergingResolvesNamesAmongTheStoredObjectsToo_ButReplacingDoesNot()
    {
        var stored = ConfigSamples.Valid();
        const string Body = "hosts:\n  - names: [new.example.com]\n    profile: LAN only\n";

        var merged = Mapped(Header + Body, stored, ConfigImportMode.Merge);
        var replaced = Read(Header + Body, stored, ConfigImportMode.Replace);

        Assert.Equal(ConfigSamples.LanProfile, merged.Hosts[0].ProfileId);
        Assert.Equal(YamlErrorCodes.ReferenceUnknown, Assert.Single(replaced.Errors).Code);
    }

    [Fact]
    public void WithoutIdsAnEntryIsTheStoredObjectOfTheSameName()
    {
        var stored = ConfigSamples.Valid();

        var document = Mapped(Header + """
            profiles:
              - name: lan only
            upstreams:
              - name: APP
                destinations:
                  - address: HTTP://app:8080
            rateLimitPolicies:
              - name: api
            hosts:
              - names: [OLD.example.com]
                kind: redirect
                redirect: {target: "https://app.example.com"}
              - names: [app.example.com]
                routes:
                  - upstream: app
                    path: /api
                  - upstream: app
                    path: /
            """, stored);

        Assert.Equal(ConfigSamples.LanProfile, document.Profiles[0].Id);
        Assert.Equal(ConfigSamples.AppUpstream, document.Upstreams[0].Id);
        Assert.Equal(ConfigSamples.AppDestination, document.Upstreams[0].Destinations[0].Id);
        Assert.Equal(ConfigSamples.ApiPolicy, document.RateLimitPolicies[0].Id);
        Assert.Equal(ConfigSamples.RedirectHost, document.Hosts[0].Id);
        Assert.Equal(ConfigSamples.AppHost, document.Hosts[1].Id);
        Assert.Equal(ConfigSamples.ApiRoute, document.Hosts[1].Routes[0].Id);
        Assert.Equal(ConfigSamples.RootRoute, document.Hosts[1].Routes[1].Id);
    }

    [Fact]
    public void AHostIsMatchedByItsFirstNameAmongAnyOfTheStoredHostsNames()
    {
        var document = Mapped(Header + "hosts:\n  - names: ['*.app.example.com']\n    kind: respond\n    respond: {statusCode: 200}\n", ConfigSamples.Valid());

        Assert.Equal(ConfigSamples.AppHost, Assert.Single(document.Hosts).Id);
    }

    [Fact]
    public void AnEntryNeverTakesTheIdOfAnObjectAnotherEntryClaimsOrAnEarlierOneTook()
    {
        var stored = ConfigSamples.Valid();
        var claimed = ConfigSamples.LanProfile;

        var document = Mapped(Header + $"profiles:\n  - id: {claimed}\n    name: renamed\n  - name: LAN only\n  - name: LAN only\n", stored);

        Assert.Equal(claimed, document.Profiles[0].Id);
        Assert.NotEqual(claimed, document.Profiles[1].Id);
        Assert.NotEqual(document.Profiles[1].Id, document.Profiles[2].Id);
    }

    [Fact]
    public void TheSameIdTwiceIsRefusedWhereItIsRepeated()
    {
        var id = ConfigSamples.Id<ProfileId>(1);

        var error = OnlyError($"profiles:\n  - id: {id}\n    name: a\n  - id: {id}\n    name: b\n");

        Assert.Equal("/profiles/1/id", error.Path);
        Assert.Equal(YamlErrorCodes.Duplicate, error.Code);
    }

    [Fact]
    public void AnIdOfTheWrongKindIsRefused()
    {
        var error = OnlyError($"hosts:\n  - id: {ConfigSamples.Id<ProfileId>(1)}\n    names: [a.example.com]\n");

        Assert.Equal("/hosts/0/id", error.Path);
        Assert.Equal(YamlErrorCodes.IdInvalid, error.Code);
        Assert.Contains("host_", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownKeyIsAnErrorThatSaysWhatWasMeant()
    {
        var typo = OnlyError("hosts:\n  - names: [a.example.com]\n    tag: [x]\n");
        Assert.Equal("/hosts/0/tag", typo.Path);
        Assert.Equal(YamlErrorCodes.KeyUnknown, typo.Code);
        Assert.Contains("did you mean 'tags'", typo.Message, StringComparison.Ordinal);

        var byId = OnlyError("hosts:\n  - names: [a.example.com]\n    profileId: x\n");
        Assert.Contains("'profile'", byId.Message, StringComparison.Ordinal);

        var version = OnlyError("profiles:\n  - name: a\n    version: 3\n");
        Assert.Contains("assigns versions", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPasswordTravelsInYamlEvenIfTheDocumentsWriteFieldHasOne()
    {
        var error = OnlyError("accessLists:\n  - name: a\n    basicAuth:\n      users:\n        - username: u\n          password: secret\n");

        Assert.Equal("/accessLists/0/basicAuth/users/0/password", error.Path);
        Assert.Equal(YamlErrorCodes.KeyUnknown, error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequiredKeyIsReportedAtItsObject()
    {
        var host = OnlyError("hosts:\n  - kind: proxy\n");
        Assert.Equal("/hosts/0", host.Path);
        Assert.Equal(YamlErrorCodes.KeyRequired, host.Code);
        Assert.Contains("names", host.Message, StringComparison.Ordinal);

        var route = OnlyError("hosts:\n  - names: [a.example.com]\n    routes:\n      - path: /\n");
        Assert.Equal("/hosts/0/routes/0", route.Path);
        Assert.Contains("upstream", route.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kind: Cfg\n", "/kind")]
    [InlineData("apiVersion: paduraksa.dev/v2\nkind: Config\n", "/apiVersion")]
    public void TheHeaderIsChecked(string header, string path)
    {
        var result = Read(header);

        Assert.Contains(result.Errors, e => e.Path == path);
    }

    [Fact]
    public void ANullKindOrApiVersionIsMissing()
    {
        var result = Read("settings: {}\n");

        Assert.Contains(result.Errors, static e => e.Path == "/kind");
        Assert.Contains(result.Errors, static e => e.Path == "/apiVersion");
    }

    [Theory]
    [InlineData("hosts:\n  - names: [a.example.com]\n    enabled: yes\n", "/hosts/0/enabled", "true or false")]
    [InlineData("hosts:\n  - names: [a.example.com]\n    kind: redirect\n    redirect: {target: x, statusCode: \"301\"}\n", "/hosts/0/redirect/statusCode", "whole number")]
    [InlineData("hosts:\n  - names: [a.example.com]\n    kind: redirect\n    redirect: {target: x, statusCode: 30.1}\n", "/hosts/0/redirect/statusCode", "whole number")]
    [InlineData("hosts:\n  - names: a.example.com\n", "/hosts/0/names", "list")]
    [InlineData("hosts:\n  - names: [a.example.com]\n    settings: [x]\n", "/hosts/0/settings", "mapping")]
    [InlineData("hosts: {a: b}\n", "/hosts", "list")]
    [InlineData("hosts:\n  - names: [[a]]\n", "/hosts/0/names/0", "text")]
    [InlineData("hosts:\n  - names: [~]\n", "/hosts/0/names/0", "empty")]
    [InlineData("hosts:\n  - x\n", "/hosts/0", "mapping")]
    public void AValueOfTheWrongShapeIsAnErrorAtItsPath(string yaml, string path, string said)
    {
        var result = Read(Header + yaml);

        var error = Assert.Single(result.Errors);
        Assert.Equal(path, error.Path);
        Assert.Contains(said, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABadEnumNameOrTimeSpanIsReportedAtItsProperty()
    {
        var kind = OnlyError("hosts:\n  - names: [a.example.com]\n    kind: tunnel\n");
        Assert.Equal("/hosts/0/kind", kind.Path);
        Assert.Equal(YamlErrorCodes.ValueInvalid, kind.Code);

        var interval = OnlyError("upstreams:\n  - name: a\n    activeHealth: {path: /, interval: soon}\n");
        Assert.Equal("/upstreams/0/activeHealth/interval", interval.Path);
    }

    [Fact]
    public void PlainScalarsThatLookLikeOtherTypesAreTextWhereTextIsExpected()
    {
        var document = Mapped(Header + """
            accessLists:
              - name: 123
                ip: {allow: [10.0.0.1]}
                countries: [NO]
            hosts:
              - names: [true]
                tags: ['null', 007]
                kind: respond
                respond: {statusCode: 200, body: 42}
            """.Replace("countries: [NO]", "satisfy: any"));

        Assert.Equal("123", document.AccessLists[0].Name);
        Assert.Equal(["true"], document.Hosts[0].Names);
        Assert.Equal(["null", "007"], document.Hosts[0].Tags);
        Assert.Equal("42", document.Hosts[0].Respond!.Body);
    }

    [Fact]
    public void CountryCodesThatAreYaml11BooleansStayText()
    {
        var document = Mapped(Header + "accessLists:\n  - name: a\n    ip:\n      countries:\n        allow: [NO, 'ID', DE]\n");

        Assert.Equal(["NO", "ID", "DE"], document.AccessLists[0].Ip!.Countries!.Allow);
    }

    [Fact]
    public void NullMeansAbsentForAnOptionalKeyAndIsAnErrorForARequiredOne()
    {
        var document = Mapped(Header + "hosts:\n  - names: [a.example.com]\n    profile: ~\n    notes:\n");
        Assert.Null(document.Hosts[0].ProfileId);
        Assert.Null(document.Hosts[0].Notes);

        var error = OnlyError("profiles:\n  - name: ~\n");
        Assert.Equal(YamlErrorCodes.KeyRequired, error.Code);
    }

    [Fact]
    public void ManyErrorsAreReportedTogetherAndCapped()
    {
        var result = Read(Header + "hosts:\n  - names: [a.example.com]\n    tag: 1\n    kindd: 2\n  - kind: proxy\n    profile: gone\n");

        Assert.Equal(3, result.Errors.Count);

        var many = Read(Header + "profiles:\n" + string.Concat(Enumerable.Range(0, 300).Select(static i => $"  - nam: {i}\n")));
        Assert.True(many.Errors.Count <= 101);
        Assert.Contains("more errors", many.Errors[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedIdsAreTheCallersToChooseSoTheSameBytesGiveTheSameDocument()
    {
        const string Body = "hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n";

        var first = Read(Header + Body, null, ConfigImportMode.Replace, CountingIds());
        var second = Read(Header + Body, null, ConfigImportMode.Replace, CountingIds());

        Assert.Equal(first.Mapped!.Document.Hosts[0].Id, second.Mapped!.Document.Hosts[0].Id);
    }

    [Theory]
    [InlineData("settings: null\n", false)]
    [InlineData("settings: ~\n", false)]
    [InlineData("settings:\n", false)]
    [InlineData("settings: {}\n", true)]
    [InlineData("settings:\n  unknownHost: close\n", true)]
    public void ANullSettingsIsNoSettingsAndAnEmptyMappingIsSettings(string yaml, bool present) =>
        Assert.Equal(present, Read(Header + yaml).Mapped!.HasSettings);

    [Fact]
    public void AHostWithSeveralStoredTwinsIsAmbiguousUntilItsIdIsGiven()
    {
        var stored = ConfigSamples.Valid();
        var twin = ConfigSamples.Id<HostId>(77);
        stored = stored with { Hosts = [.. stored.Hosts, new HostConfig(twin, ["app.example.com"], Kind: HostKind.Respond, Enabled: false, Respond: new RespondConfig(200))] };
        const string Body = "hosts:\n  - names: [app.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n";

        var ambiguous = Read(Header + Body, stored, ConfigImportMode.Merge);
        var named = Mapped(Header + $"hosts:\n  - id: {twin}\n    names: [app.example.com]\n    kind: respond\n    respond: {{statusCode: 200}}\n", stored, ConfigImportMode.Merge);

        var error = Assert.Single(ambiguous.Errors);
        Assert.Equal("/hosts/0", error.Path);
        Assert.Equal(YamlErrorCodes.ReferenceAmbiguous, error.Code);
        Assert.Contains("add the id", error.Message, StringComparison.Ordinal);
        Assert.Equal(twin, Assert.Single(named.Hosts).Id);
    }

    [Fact]
    public void ASectionTheYamlLeavesOutIsEmptyAndSettingsPresenceIsReported()
    {
        var without = Read(Header);
        var with = Read(Header + "settings:\n  unknownHost: close\n");

        Assert.False(without.Mapped!.HasSettings);
        Assert.True(with.Mapped!.HasSettings);
        Assert.Equal(UnknownHostBehaviour.Close, with.Mapped.Document.Settings.UnknownHost);
    }

    [Fact]
    public void PositionsOfEveryNodeAreKept()
    {
        var mapped = Read(Header + "hosts:\n  - names: [a.example.com]\n    kind: respond\n    respond: {statusCode: 200}\n").Mapped!;

        Assert.Equal(4, mapped.Positions["/hosts"].Line);
        Assert.Equal(5, mapped.Positions["/hosts/0/kind"].Line);
    }

    [Fact]
    public void ARootThatIsNotAMappingIsRefused()
    {
        var result = ConfigYamlImporter.Read("- a\n- b\n"u8, null, ConfigImportMode.Replace, CountingIds());

        Assert.Equal(YamlErrorCodes.TypeMismatch, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void SyntaxAndLimitErrorsAreReportedWithTheirCodesAndNoPath()
    {
        var result = ConfigYamlImporter.Read("a: !!str x\n"u8, null, ConfigImportMode.Replace, CountingIds());

        var error = Assert.Single(result.Errors);
        Assert.Equal(YamlErrorCodes.Tag, error.Code);
        Assert.Equal(string.Empty, error.Path);
        Assert.Contains("line 1", error.Message, StringComparison.Ordinal);
    }
}
