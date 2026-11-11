using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;

namespace Paduraksa.Tests.Proxy.RuleSets;

public sealed class CompiledRuleSetTests
{
    private static readonly StringValues Browser = "Mozilla/5.0 (X11; Linux x86_64) Firefox/130.0";

    [Theory]
    [InlineData("/admin", true)]
    [InlineData("/ADMIN", true)]
    [InlineData("/admin/", false)]
    [InlineData("/admin/x", false)]
    [InlineData("/", false)]
    public void AnExactRuleTakesTheWholePath(string path, bool matches) =>
        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, Set(Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/admin")).Match("GET", path, Browser));

    [Theory]
    [InlineData("/admin", true)]
    [InlineData("/Admin/", true)]
    [InlineData("/admin/users/1", true)]
    [InlineData("/administrator", false)]
    [InlineData("/adm", false)]
    [InlineData("/x/admin", false)]
    [InlineData("/", false)]
    public void APrefixRuleTakesTheSegmentAndWhatLiesBelowItLikeARoute(string path, bool matches) =>
        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, Set(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin")).Match("GET", path, Browser));

    [Theory]
    [InlineData("/admin/", true)]
    [InlineData("/admin/x", true)]
    [InlineData("/admin", false)]
    public void APrefixThatEndsInASlashNeedsTheSlash(string path, bool matches) =>
        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, Set(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin/")).Match("GET", path, Browser));

    [Theory]
    [InlineData("/")]
    [InlineData("/a")]
    [InlineData("/a/b/c/")]
    public void TheRootPrefixTakesEveryPath(string path) =>
        Assert.Equal(0, Set(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/")).Match("GET", path, Browser));

    [Fact]
    public void ARuleStatesEveryConditionAndAllMustHold()
    {
        var set = Set(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/api", methods: ["POST", "PUT"], agents: ["curl"]));

        Assert.Equal(0, set.Match("POST", "/api/x", "curl/8.5.0"));
        Assert.Equal(0, set.Match("put", "/api", "Mozilla CURL"));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/api/x", "curl/8.5.0"));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("POST", "/other", "curl/8.5.0"));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("POST", "/api/x", Browser));
    }

    [Theory]
    [InlineData("Mozilla/5.0 sqlmap/1.7", true)]
    [InlineData("SQLMAP", true)]
    [InlineData("sqlmapper", true)]
    [InlineData("sqlma", false)]
    [InlineData("", false)]
    public void UserAgentSubstringsAreMatchedIgnoringCase(string agent, bool matches) =>
        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, Set(Rule(RuleAction.Deny, agents: ["sqlmap", "nikto"])).Match("GET", "/", agent));

    [Fact]
    public void EveryUserAgentHeaderLineCounts()
    {
        var set = Set(Rule(RuleAction.Deny, agents: ["sqlmap"]));

        Assert.Equal(0, set.Match("GET", "/", new StringValues(["Mozilla/5.0", "sqlmap/1.7"])));
        Assert.Equal(0, set.Match("GET", "/", new StringValues(["sqlmap/1.7", "Mozilla/5.0"])));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("curl/8.5.0", false)]
    public void TheEmptyFlagTakesAMissingOrBlankUserAgent(string? agent, bool matches)
    {
        var set = Set(Rule(RuleAction.Deny, emptyAgent: true));

        StringValues values = agent is null ? StringValues.Empty : agent;
        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, set.Match("GET", "/", values));
    }

    [Fact]
    public void EmptyAndSubstringsInOneRuleAreEitherOr()
    {
        var set = Set(Rule(RuleAction.Deny, agents: ["sqlmap"], emptyAgent: true));

        Assert.Equal(0, set.Match("GET", "/", StringValues.Empty));
        Assert.Equal(0, set.Match("GET", "/", "sqlmap"));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/", Browser));
    }

    [Fact]
    public void TheFirstMatchingRuleWinsSoAnAllowCanCarveAnException()
    {
        var set = Set(
            Rule(RuleAction.Allow, kind: RulePathKind.Prefix, pattern: "/admin/public"),
            Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin"),
            Rule(RuleAction.Drop, agents: ["sqlmap"]));

        Assert.Equal(0, set.Match("GET", "/admin/public/logo.png", Browser));
        Assert.Equal(RuleAction.Allow, set.ActionOf(0));
        Assert.Equal(1, set.Match("GET", "/admin/users", Browser));
        Assert.Equal(1, set.Match("GET", "/admin", "sqlmap"));
        Assert.Equal(2, set.Match("GET", "/", "sqlmap"));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/", Browser));
    }

    [Fact]
    public void ThePrecedenceIsTheDocumentsOrderWhateverIndexFindsAMatchFirst()
    {
        // The scan (a glob) and both indexes (exact, prefix) all match this request: the first rule in the document decides, in
        // every order the three can be written in.
        RuleSpec[] rules =
        [
            Rule(RuleAction.Allow, kind: RulePathKind.Glob, pattern: "/a/**"),
            Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/a/b"),
            Rule(RuleAction.Drop, kind: RulePathKind.Prefix, pattern: "/a"),
        ];
        foreach (var order in new[] { new[] { 0, 1, 2 }, [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0] })
        {
            var set = Set([.. order.Select(i => rules[i])]);
            Assert.Equal(0, set.Match("GET", "/a/b", Browser));
            Assert.Equal(rules[order[0]].Action, set.ActionOf(0));
        }
    }

    [Fact]
    public void ARuleThatMatchesThePathButNotItsOtherConditionsDoesNotBlockALaterOne()
    {
        var set = Set(
            Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/api", methods: ["DELETE"]),
            Rule(RuleAction.Drop, kind: RulePathKind.Prefix, pattern: "/api"));

        Assert.Equal(1, set.Match("GET", "/api/x", Browser));
        Assert.Equal(0, set.Match("DELETE", "/api/x", Browser));
    }

    [Fact]
    public void ARegexMatchesSomewhereInThePathIgnoringCase()
    {
        var set = Set(Rule(RuleAction.Deny, kind: RulePathKind.Regex, pattern: @"\.(php|asp|jsp)$"));

        Assert.Equal(0, set.Match("GET", "/x/index.PHP", Browser));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/x/index.php/more", Browser));
        Assert.True(set.HasRegex);
    }

    [Fact]
    public void ACatastrophicLookingRegexRunsInLinearTime()
    {
        // (a+)+$ takes exponential time on a backtracking engine; NonBacktracking doesn't backtrack at all.
        var set = Set(Rule(RuleAction.Deny, kind: RulePathKind.Regex, pattern: "^(a+)+$"));
        var path = "/" + new string('a', 5000) + "!";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", path, Browser));
        Assert.Equal(0, set.Match("GET", new string('a', 5000), Browser));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(RuleAction.Deny, true)]
    [InlineData(RuleAction.Drop, true)]
    [InlineData(RuleAction.Allow, false)]
    public void ARegexThatRunsOutOfTimeIsDecidedByItsActionAndCounted(RuleAction action, bool matches)
    {
        // A one-millisecond limit over a path far longer than a request line (the engine is linear, so only size can make it late):
        // deny and drop fail closed and an allow doesn't fire, so the stricter answer either way.
        RuleSetCounters counters = new();
        var set = CompiledRuleSet.TryCreate(
            new RuleSetSpec { Id = TestIds.RuleSet("slow"), Name = "slow", Rules = [Rule(action, kind: RulePathKind.Regex, pattern: "^/a+b$")] },
            counters,
            [],
            TimeSpan.FromMilliseconds(1))!;
        var path = "/" + new string('a', 60_000_000);

        Assert.Equal(matches ? 0 : CompiledRuleSet.NoMatch, set.Match("GET", path, Browser));
        Assert.Equal(1, counters.Timeouts);

        // An ordinary path is judged on the pattern, and counts no timeout.
        Assert.Equal(0, set.Match("GET", "/aab", Browser));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/aab-", Browser));
        Assert.Equal(1, counters.Timeouts);
    }

    [Fact]
    public void ARegexThatNeedsABacktrackingEngineIsRefusedAtCompileTime()
    {
        Assert.Null(RulePatterns.TryCompileRegex(@"(?<=/a)b", out var lookbehind));
        Assert.NotNull(lookbehind);
        Assert.Null(RulePatterns.TryCompileRegex(@"(a)\1", out var backreference));
        Assert.NotNull(backreference);
        Assert.Null(RulePatterns.TryCompileRegex("(unclosed", out var unclosed));
        Assert.Contains("unclosed", unclosed, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RulePatterns.TryCompileRegex(string.Empty, out _));
        Assert.Null(RulePatterns.TryCompileRegex(new string('a', RuleSetLimits.MaxPatternLength + 1), out _));
    }

    [Fact]
    public void ASetOfOnlyIndexedRulesHasNoRegexAndANonPathSetNeedsNoPath()
    {
        Assert.False(Set(Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/a")).HasRegex);
        Assert.False(Set(Rule(RuleAction.Deny, emptyAgent: true)).NeedsPath);
        Assert.True(Set(Rule(RuleAction.Deny, kind: RulePathKind.Glob, pattern: "/a/**")).NeedsPath);
    }

    [Fact]
    public void ABadRuleFailsTheWholeSetNamingTheRule()
    {
        List<string> errors = [];
        var spec = new RuleSetSpec
        {
            Id = TestIds.RuleSet("bad"),
            Name = "bad",
            Rules =
            [
                Rule(RuleAction.Deny, emptyAgent: true),
                Rule(RuleAction.Deny, kind: RulePathKind.Regex, pattern: "(oops"),
                Rule(RuleAction.Deny),
                Rule(RuleAction.Deny, kind: RulePathKind.Glob, pattern: "no-slash"),
            ],
        };

        Assert.Null(CompiledRuleSet.TryCreate(spec, new RuleSetCounters(), errors));
        Assert.Collection(
            errors,
            error => Assert.StartsWith("rule 2: the regex '(oops'", error, StringComparison.Ordinal),
            error => Assert.Equal("rule 3: a rule needs at least one condition", error),
            error => Assert.StartsWith("rule 4: the glob 'no-slash'", error, StringComparison.Ordinal));
    }

    [Fact]
    public void ASetHasAtMostTheLimitOfRules()
    {
        var rules = Enumerable.Range(0, RuleSetLimits.MaxRules + 1).Select(i => Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: $"/{i}")).ToArray();
        List<string> errors = [];

        Assert.Null(CompiledRuleSet.TryCreate(new RuleSetSpec { Id = TestIds.RuleSet("big"), Name = "big", Rules = rules }, new RuleSetCounters(), errors));
        Assert.Single(errors);
        Assert.NotNull(CompiledRuleSet.TryCreate(new RuleSetSpec { Id = TestIds.RuleSet("big"), Name = "big", Rules = rules[..RuleSetLimits.MaxRules] }, new RuleSetCounters(), []));
    }

    [Fact]
    public void ManyIndexedPatternsAreFoundByBinarySearchWhateverTheirOrder()
    {
        var rules = Enumerable.Range(0, 150).Reverse().Select(i => Rule(RuleAction.Deny, kind: i % 2 == 0 ? RulePathKind.Exact : RulePathKind.Prefix, pattern: $"/p{i:D3}")).ToArray();
        var set = Set(rules);

        for (var i = 0; i < 150; i++)
        {
            var rule = 149 - i;
            Assert.Equal(rule, set.Match("GET", $"/p{i:D3}", Browser));
        }

        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/p150", Browser));
        Assert.Equal(CompiledRuleSet.NoMatch, set.Match("GET", "/p000/x", Browser));
        Assert.Equal(148, set.Match("GET", "/p001/x", Browser));
    }

    private static CompiledRuleSet Set(params RuleSpec[] rules)
    {
        List<string> errors = [];
        var set = CompiledRuleSet.TryCreate(new RuleSetSpec { Id = TestIds.RuleSet("set"), Name = "set", Rules = rules }, new RuleSetCounters(), errors);
        Assert.True(set is not null, string.Join("; ", errors));
        return set;
    }
}
