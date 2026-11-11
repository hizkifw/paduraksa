using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>A rule set's own rules and the references to it. A document that validates always compiles: every pattern is judged
/// the way the compiler reads it, a regex included.</summary>
public sealed class RuleSetValidationTests
{
    private const string Proxy = "/hosts/0";
    private const string SetPath = "/ruleSets/0";
    private const string RulePath = "/ruleSets/0/rules/0";

    [Fact]
    public void AFullSetAndAnEmptyOneAreValid()
    {
        Assert.Empty(ConfigValidator.Validate(WithRules(
            Rule(RuleAction.Allow, Path(RulePathKind.Exact, "/health")),
            Rule(RuleAction.Deny, Path(RulePathKind.Prefix, "/admin/"), methods: ["POST", "put"]),
            Rule(RuleAction.Deny, Path(RulePathKind.Glob, "/**/.env")),
            Rule(RuleAction.Drop, Path(RulePathKind.Regex, @"\.(php|asp)$")),
            Rule(RuleAction.Deny, agent: new RuleUserAgentConfig(["sqlmap"], Empty: true)),
            Rule(RuleAction.Deny, methods: ["TRACE"]))));
        Assert.Empty(ConfigValidator.Validate(WithRules()));
    }

    [Fact]
    public void ARuleThatStatesNoConditionIsRefusedBecauseItWouldMatchEverything() =>
        AssertErrors(
            WithRules(Rule(RuleAction.Deny), Rule(RuleAction.Allow, methods: [])),
            ($"{RulePath}/match", RuleEmpty),
            ("/ruleSets/0/rules/1/match", RuleEmpty));

    [Fact]
    public void AnUndefinedActionOrPathKindIsRefused()
    {
        AssertErrors(WithRules(Rule((RuleAction)99, agent: new RuleUserAgentConfig(Empty: true))), ($"{RulePath}/action", EnumUndefined));
        AssertErrors(WithRules(Rule(RuleAction.Deny, Path((RulePathKind)99, "/x"))), ($"{RulePath}/match/path/kind", EnumUndefined));
    }

    [Theory]
    [InlineData(RulePathKind.Exact, "admin", RulePathInvalid)]
    [InlineData(RulePathKind.Prefix, "", RulePathInvalid)]
    [InlineData(RulePathKind.Glob, "*.php", RulePathInvalid)]
    [InlineData(RulePathKind.Exact, "/a?b=1", RulePathInvalid)]
    [InlineData(RulePathKind.Prefix, "/a#b", RulePathInvalid)]
    [InlineData(RulePathKind.Exact, "/a\tb", RulePathInvalid)]
    [InlineData(RulePathKind.Prefix, "/a%2Fb", RulePathEncoded)]
    [InlineData(RulePathKind.Glob, "/a/%41", RulePathEncoded)]
    [InlineData(RulePathKind.Exact, "/a//b", RulePathNotCanonical)]
    [InlineData(RulePathKind.Prefix, "/a/../b", RulePathNotCanonical)]
    [InlineData(RulePathKind.Exact, "/a;x", RulePathNotCanonical)]
    [InlineData(RulePathKind.Exact, "/a\\b", RulePathNotCanonical)]
    [InlineData(RulePathKind.Prefix, "/a.", RulePathNotCanonical)]
    [InlineData(RulePathKind.Glob, "/a//*", RulePathNotCanonical)]
    [InlineData(RulePathKind.Glob, "/café/*", RulePathGlobInvalid)]
    public void APathPatternMustBeSomethingARequestCanMatch(RulePathKind kind, string pattern, string code) =>
        AssertErrors(WithRules(Rule(RuleAction.Deny, Path(kind, pattern))), ($"{RulePath}/match/path/pattern", code));

    [Theory]
    [InlineData(RulePathKind.Exact, "/café")]
    [InlineData(RulePathKind.Prefix, "/")]
    [InlineData(RulePathKind.Prefix, "/a b/")]
    [InlineData(RulePathKind.Glob, "/**")]
    [InlineData(RulePathKind.Glob, "/a/**/b/*.php")]
    public void APathPatternThatIsCanonicalIsAccepted(RulePathKind kind, string pattern) =>
        Assert.Empty(ConfigValidator.Validate(WithRules(Rule(RuleAction.Deny, Path(kind, pattern)))));

    [Fact]
    public void APatternHasAtMostTheLimitOfCharacters()
    {
        AssertErrors(
            WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Prefix, "/" + new string('a', RuleSetLimits.MaxPatternLength)))),
            ($"{RulePath}/match/path/pattern", RulePathInvalid));
        AssertErrors(
            WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Regex, new string('a', RuleSetLimits.MaxPatternLength + 1)))),
            ($"{RulePath}/match/path/pattern", RulePathRegexInvalid));
        Assert.Empty(ConfigValidator.Validate(WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Prefix, "/" + new string('a', RuleSetLimits.MaxPatternLength - 1))))));
    }

    [Fact]
    public void AGlobHasAtMostTheLimitOfTokens() =>
        AssertErrors(
            WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Glob, "/" + new string('a', RuleSetLimits.MaxGlobTokens)))),
            ($"{RulePath}/match/path/pattern", RulePathGlobInvalid));

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("[a-")]
    [InlineData("*leading")]
    [InlineData("(?<=/a)b")]
    [InlineData(@"(a)\1")]
    [InlineData("")]
    public void ARegexThatCannotBeBuiltIsRefusedAtTheFieldWithTheReason(string pattern)
    {
        var errors = ConfigValidator.Validate(WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Regex, pattern))));

        var error = Assert.Single(errors);
        Assert.Equal($"{RulePath}/match/path/pattern", error.Path);
        Assert.Equal(RulePathRegexInvalid, error.Code);
        Assert.True(error.Message.Length > 30, error.Message);
    }

    [Theory]
    [InlineData("^(a+)+$")]
    [InlineData("(a|aa)+$")]
    [InlineData("(.*a){20}")]
    public void ACatastrophicLookingRegexIsAcceptedBecauseItCostsNoMoreThanAPlainOne(string pattern) =>
        Assert.Empty(ConfigValidator.Validate(WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Regex, pattern)))));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("GE T")]
    [InlineData("G(ET)")]
    public void AMethodMustBeAToken(string method) =>
        AssertErrors(WithRules(Rule(RuleAction.Deny, methods: [method])), ($"{RulePath}/match/methods/0", RuleMethodsInvalid));

    [Fact]
    public void AtMostTheLimitOfMethodsAreNamed()
    {
        var methods = Enumerable.Range(0, RuleSetLimits.MaxMethods + 1).Select(i => $"M{i}").ToArray();

        AssertErrors(WithRules(Rule(RuleAction.Deny, methods: methods)), ($"{RulePath}/match/methods", RuleMethodsInvalid));
    }

    [Fact]
    public void AUserAgentConditionNeedsSubstringsOrTheEmptyFlag() =>
        AssertErrors(
            WithRules(Rule(RuleAction.Deny, Path(RulePathKind.Prefix, "/a"), agent: new RuleUserAgentConfig())),
            ($"{RulePath}/match/userAgent", RuleUserAgentInvalid));

    [Fact]
    public void AUserAgentSubstringIsPrintableAsciiOfBoundedLength()
    {
        AssertErrors(
            WithRules(Rule(RuleAction.Deny, agent: new RuleUserAgentConfig(["", "café", new string('x', RuleSetLimits.MaxUserAgentTokenLength + 1), "ta\tb", "fine"]))),
            ($"{RulePath}/match/userAgent/contains/0", RuleUserAgentInvalid),
            ($"{RulePath}/match/userAgent/contains/1", RuleUserAgentInvalid),
            ($"{RulePath}/match/userAgent/contains/2", RuleUserAgentInvalid),
            ($"{RulePath}/match/userAgent/contains/3", RuleUserAgentInvalid));
        Assert.Empty(ConfigValidator.Validate(WithRules(Rule(RuleAction.Deny, agent: new RuleUserAgentConfig(["Mozilla/5.0 (compatible; X11)"])))));
    }

    [Fact]
    public void AtMostTheLimitOfUserAgentSubstringsAreNamed()
    {
        var tokens = Enumerable.Range(0, RuleSetLimits.MaxUserAgentTokens + 1).Select(i => $"bot{i}").ToArray();

        AssertErrors(WithRules(Rule(RuleAction.Deny, agent: new RuleUserAgentConfig(tokens))), ($"{RulePath}/match/userAgent/contains", RuleUserAgentInvalid));
    }

    [Fact]
    public void ASetHasAtMostTheLimitOfRules()
    {
        var rules = Enumerable.Range(0, RuleSetLimits.MaxRules + 1).Select(i => Rule(RuleAction.Deny, Path(RulePathKind.Exact, $"/p{i}"))).ToArray();

        AssertErrors(WithRules(rules), ($"{SetPath}/rules", RuleSetRulesTooMany));
        Assert.Empty(ConfigValidator.Validate(WithRules(rules[..RuleSetLimits.MaxRules])));
    }

    [Fact]
    public void ANameIsRequiredAndNamesAndIdsAreUnique()
    {
        AssertErrors(Valid() with { RuleSets = [new RuleSetConfig(Scanners, "  ")] }, ($"{SetPath}/name", NameRequired));
        AssertErrors(
            Valid() with { RuleSets = [new RuleSetConfig(Scanners, "Scanners"), new RuleSetConfig(Id<RuleSetId>(2), "scanners")] },
            ("/ruleSets/1/name", NameDuplicate));
        AssertErrors(
            Valid() with { RuleSets = [new RuleSetConfig(Scanners, "one"), new RuleSetConfig(Scanners, "two")] },
            ("/ruleSets/1/id", IdDuplicate));
    }

    [Fact]
    public void ABuiltInPresetsIdCannotBeUsedByADocumentsSet() =>
        AssertErrors(
            Valid() with { RuleSets = [new RuleSetConfig(RuleSetPresets.AiCrawlers.Id, "mine")] },
            ($"{SetPath}/id", RuleSetIdReserved));

    [Fact]
    public void ThePresetsAreValidSetsThemselves()
    {
        // A preset that failed the validator couldn't be copied into a set of the operator's own.
        foreach (var preset in RuleSetPresets.All)
        {
            var copy = Valid() with { RuleSets = [preset with { Id = Id<RuleSetId>(77) }] };
            Assert.Empty(ConfigValidator.Validate(copy));

            // Already canonical, so a copy keeps the preset's rules exactly.
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(preset.Rules, ConfigJsonContext.Default.Options),
                System.Text.Json.JsonSerializer.Serialize(CanonicalConfigJson.Canonicalize(copy).RuleSets[0].Rules, ConfigJsonContext.Default.Options));
        }
    }

    [Fact]
    public void AnUnknownHostSetIsReportedWhereTheHostResolvesIt() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = Id<RuleSetId>(9) } } }),
            },
            ($"{Proxy}/settings/ruleSet/ruleSetId", HostRuleSetUnknown));

    [Fact]
    public void AnUnknownRouteSetIsReportedAtTheRoute() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with
                {
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { RuleSet = new RuleSetSettings { RuleSetId = Id<RuleSetId>(9) } } }, h.Routes[1]],
                }),
            },
            ($"{Proxy}/routes/0/settings/ruleSet/ruleSetId", RouteRuleSetUnknown));

    [Fact]
    public void ASetTheDefaultsNameIsReportedAtEachHostThatInheritsItWithTheLayer()
    {
        var document = Valid() with
        {
            Settings = new GlobalSettings(Defaults: new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = Id<RuleSetId>(9) } }),
            Profiles = [],
            Hosts = [.. Valid().Hosts.Select(h => h with { ProfileId = null })],
        };

        var errors = ConfigValidator.Validate(document);

        Assert.Equal(
            ["/hosts/0/settings/ruleSet/ruleSetId", "/hosts/1/settings/ruleSet/ruleSetId", "/hosts/2/settings/ruleSet/ruleSetId"],
            errors.Select(e => e.Path));
        Assert.All(errors, e => Assert.Contains("global defaults", e.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ANamedSetIsCheckedEvenWhileTheSwitchIsOff() =>
        AssertErrors(
            Valid() with
            {
                Hosts = MapHost(Valid(), AppHost, h => h with { Settings = new HostSettings { RuleSet = new RuleSetSettings { Enabled = false, RuleSetId = Id<RuleSetId>(9) } } }),
            },
            ($"{Proxy}/settings/ruleSet/ruleSetId", HostRuleSetUnknown));

    [Fact]
    public void ADocumentsSetAndEveryPresetAreValidReferencesInEveryLayer()
    {
        var document = Valid() with
        {
            RuleSets = [new RuleSetConfig(Scanners, "scanners", Rules: [Rule(RuleAction.Deny, methods: ["TRACE"])])],
            Settings = new GlobalSettings(Defaults: new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = RuleSetPresets.AiCrawlers.Id } }),
            Profiles = [new ProfileConfig(LanProfile, "LAN only", Settings: new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = RuleSetPresets.KnownScanners.Id } })],
            Hosts = MapHost(
                Valid(),
                AppHost,
                h => h with
                {
                    Settings = new HostSettings { RuleSet = new RuleSetSettings { RuleSetId = Scanners } },
                    Routes = [h.Routes[0] with { Settings = new RouteSettings { RuleSet = new RuleSetSettings { RuleSetId = RuleSetPresets.EmptyUserAgent.Id } } }, h.Routes[1]],
                }),
        };

        Assert.Empty(ConfigValidator.Validate(document));
    }

    private static RuleConfig Rule(RuleAction action, RulePathConfig? path = null, string[]? methods = null, RuleUserAgentConfig? agent = null) =>
        new(action, new RuleMatchConfig(path, methods, agent));

    private static RulePathConfig Path(RulePathKind kind, string pattern) => new(kind, pattern);

    private static ConfigDocument WithRules(params RuleConfig[] rules) =>
        Valid() with { RuleSets = [new RuleSetConfig(Scanners, "scanners", Rules: rules)] };

    private static void AssertErrors(ConfigDocument document, params (string Path, string Code)[] expected) =>
        Assert.Equal(expected, ConfigValidator.Validate(document).Select(e => (e.Path, e.Code)));

    private static HostConfig[] MapHost(ConfigDocument document, HostId id, Func<HostConfig, HostConfig> change) =>
        [.. document.Hosts.Select(h => h.Id == id ? change(h) : h)];
}
