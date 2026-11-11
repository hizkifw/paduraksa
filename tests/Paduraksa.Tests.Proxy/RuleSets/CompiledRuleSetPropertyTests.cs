using System.Text.RegularExpressions;

using CsCheck;

using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;

namespace Paduraksa.Tests.Proxy.RuleSets;

/// <summary>A compiled rule set (indexed exact and prefix paths, scanned globs and regexes) against the plain reading of its rules: the first rule whose conditions all hold.</summary>
public sealed class CompiledRuleSetPropertyTests
{
    private static readonly string[] PathPatterns = ["/", "/a", "/a/", "/A/b", "/a/b", "/b", "/ab", "/a/b/", "/a/**", "/*", "/**/b", "/**/**/a", "/a*", "/*.php", "/**.php", "/a/*/b", "/**", "/a/b/**"];

    private static readonly string[] RegexPatterns = [@"\.php$", "^/a(/|$)", "b+$", "^/[ab]{2}"];

    private static readonly string[] Methods = ["GET", "POST", "PUT", "get"];

    // Case folding that isn't ASCII's (the Kelvin sign, dotted and dotless i, long s) is where two comparers can part ways.
    private static readonly string[] AgentTokens = ["curl", "sqlmap", "Bot", "k", "i", "s", "é", "bot "];

    private static readonly string[] AgentValues = ["", " ", "curl/8", "SQLMAP", "Mozilla Bot", "K", "İ", "ı", "ſ", "ISS", "É", "bot", "x"];

    private static readonly Gen<RuleSpec> Rules = Gen.Select(
        Property.Pick(RuleAction.Allow, RuleAction.Deny, RuleAction.Drop),
        Gen.Int[0, 5],
        Property.Pick(PathPatterns),
        Property.Pick(RegexPatterns),
        Property.Pick(Methods).List[0, 2],
        Property.Pick(AgentTokens).List[0, 2],
        Gen.Bool).Select(
        static (action, kind, pattern, regex, methods, tokens, empty) =>
        {
            RulePathKind? pathKind = kind switch
            {
                0 => null,
                1 => RulePathKind.Exact,
                2 => RulePathKind.Prefix,
                3 or 4 => RulePathKind.Glob,
                _ => RulePathKind.Regex,
            };
            var text = kind == 5 ? regex : pattern;
            if (pathKind is RulePathKind.Exact or RulePathKind.Prefix or RulePathKind.Glob && text[0] != '/')
            {
                text = "/" + text;
            }

            var matchesEmpty = empty && kind != 5;

            // A rule with no condition is refused when the set is compiled (the second property checks that).
            string[] stated = pathKind is null && methods.Count == 0 && tokens.Count == 0 && !matchesEmpty ? ["GET"] : [.. methods];
            return Rule(action, pathKind, pathKind is null ? null : text, stated, [.. tokens], matchesEmpty);
        });

    private static readonly Gen<(string Method, string Path, string[] Agents)> Requests = Gen.Select(
        Property.Pick(Methods),
        Property.StringOf("/abAB.", 0, 7),
        Property.Pick(AgentValues).List[0, 2]).Select(static (method, path, agents) => (method, path, agents.ToArray()));

    [Fact]
    public void TheSetPicksTheFirstRuleTheRequestMatchesInTheDocumentsOrder() =>
        Gen.Select(Rules.List[1, 7], Requests.List[1, 12]).Check(
            8_000,
            t =>
            {
                var rules = t.Item1;
                var set = Compile(rules);
                foreach (var (method, path, agents) in t.Item2)
                {
                    var expected = Reference.FirstMatch(rules, method, path, agents);
                    var actual = set.Match(method, path, new StringValues(agents));

                    Assert.True(
                        expected == actual,
                        $"{method} '{path}' agents [{string.Join("|", agents)}]: the set says rule {actual}, the model rule {expected}, over {string.Join(" ; ", rules.Select(Describe))}");
                }
            });

    [Fact]
    public void ARuleSetCompilesExactlyWhenEveryRuleCan() =>
        Gen.Select(Property.StringOf("/*a(\\", 0, 5), Gen.Int[0, 3], Property.Pick("curl", "")).Check(
            4_000,
            t =>
            {
                var (pattern, kind, agent) = t;
                RulePathKind[] kinds = [RulePathKind.Exact, RulePathKind.Prefix, RulePathKind.Glob, RulePathKind.Regex];
                var rule = Rule(RuleAction.Deny, kinds[kind], pattern, agents: agent.Length == 0 ? null : [agent]);
                List<string> errors = [];

                var set = CompiledRuleSet.TryCreate(new RuleSetSpec { Id = TestIds.RuleSet("set"), Name = "set", Rules = [rule] }, new RuleSetCounters(), errors);

                Assert.Equal(set is not null, errors.Count == 0);
                if (set is not null)
                {
                    _ = set.Match("GET", "/", StringValues.Empty);
                }
            });

    private static CompiledRuleSet Compile(List<RuleSpec> rules)
    {
        List<string> errors = [];
        var set = CompiledRuleSet.TryCreate(new RuleSetSpec { Id = TestIds.RuleSet("set"), Name = "set", Rules = rules }, new RuleSetCounters(), errors);
        Assert.True(set is not null, string.Join("; ", errors));
        return set;
    }

    private static string Describe(RuleSpec rule) =>
        $"{rule.Action} {rule.PathKind} '{rule.Pattern}' [{string.Join(",", rule.Methods)}] ua[{string.Join(",", rule.UserAgentContains)}]{(rule.UserAgentEmpty ? " empty" : string.Empty)}";

    private static class Reference
    {
        public static int FirstMatch(List<RuleSpec> rules, string method, string path, string[] agents)
        {
            for (var i = 0; i < rules.Count; i++)
            {
                if (Matches(rules[i], method, path, agents))
                {
                    return i;
                }
            }

            return CompiledRuleSet.NoMatch;
        }

        private static bool Matches(RuleSpec rule, string method, string path, string[] agents) =>
            PathMatches(rule, path) && MethodMatches(rule, method) && AgentMatches(rule, agents);

        private static bool PathMatches(RuleSpec rule, string path)
        {
            switch (rule.PathKind)
            {
                case null:
                    return true;
                case RulePathKind.Exact:
                    return string.Equals(path, rule.Pattern, StringComparison.OrdinalIgnoreCase);
                case RulePathKind.Prefix:
                    // As a route takes a path: the pattern is the path or a leading run of its segments; an empty path is "/".
                    var target = path.Length == 0 ? "/" : path;
                    var prefix = rule.Pattern!;
                    return target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && (target.Length == prefix.Length || prefix.EndsWith('/') || target[prefix.Length] == '/');
                case RulePathKind.Glob:
                    return GlobReference.IsMatch(rule.Pattern!, path);
                default:
                    return Regex.IsMatch(path, rule.Pattern!, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        private static bool MethodMatches(RuleSpec rule, string method) =>
            rule.Methods.Count == 0 || rule.Methods.Any(m => string.Equals(m, method, StringComparison.OrdinalIgnoreCase));

        private static bool AgentMatches(RuleSpec rule, string[] agents)
        {
            if (rule.UserAgentContains.Count == 0 && !rule.UserAgentEmpty)
            {
                return true;
            }

            var present = agents.Where(a => !string.IsNullOrWhiteSpace(a)).ToArray();
            return present.Any(a => rule.UserAgentContains.Any(token => a.Contains(token, StringComparison.OrdinalIgnoreCase)))
                || (rule.UserAgentEmpty && present.Length == 0);
        }
    }
}
