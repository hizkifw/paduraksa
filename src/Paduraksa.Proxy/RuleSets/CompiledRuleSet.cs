using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>
/// A rule set compiled for the gate: the first rule that matches decides, and a request no rule matches is <see cref="NoMatch"/>.
/// <para>Rules with an exact or prefix path are not scanned. They sit in sorted arrays of the patterns (ignoring case, like
/// route matching), so a request finds the ones its path can match by binary search: the path itself for an exact pattern, and
/// each segment boundary of it for a prefix. Rules with no path condition, a glob or a regex are scanned in order. The lowest
/// rule index among everything that matches wins, so the order of the document is the order of precedence whatever the index
/// found first, and a scan stops at the best index found so far.</para>
/// <para>Nothing here allocates. A regex rule is the one exception to expect: the non-backtracking engine builds its automaton
/// lazily while it matches, so a regex may allocate as it meets path shapes it hasn't seen (<see cref="HasRegex"/>). It runs
/// last in its rule, after the cheaper conditions, and only a set that has one pays for it.</para>
/// </summary>
public sealed class CompiledRuleSet
{
    /// <summary><see cref="Match"/>'s answer for a request no rule matches.</summary>
    public const int NoMatch = -1;

    private readonly CompiledRule[] _rules;
    private readonly int[] _scan;
    private readonly string[] _exact;
    private readonly int[][] _exactRules;
    private readonly string[] _prefix;
    private readonly int[][] _prefixRules;

    private CompiledRuleSet(RuleSetSpec spec, CompiledRule[] rules, RuleSetCounters counters)
    {
        Id = spec.Id;
        Name = spec.Name;
        Counters = counters;
        _rules = rules;
        List<int> scan = [];
        SortedDictionary<string, List<int>> exact = new(StringComparer.OrdinalIgnoreCase);
        SortedDictionary<string, List<int>> prefix = new(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rules.Length; i++)
        {
            var rule = rules[i];
            if (rule.Spec.PathKind is { } kind && !rule.NeedsPathScan)
            {
                var index = kind == RulePathKind.Exact ? exact : prefix;
                if (!index.TryGetValue(rule.Spec.Pattern!, out var at))
                {
                    index[rule.Spec.Pattern!] = at = [];
                }

                at.Add(i);
            }
            else
            {
                scan.Add(i);
            }

            NeedsPath |= rule.Spec.PathKind is not null;
            HasRegex |= rule.UsesRegex;
        }

        _scan = scan.ToArray();
        (_exact, _exactRules) = Flatten(exact);
        (_prefix, _prefixRules) = Flatten(prefix);
    }

    public RuleSetId Id { get; }

    public string Name { get; }

    public int RuleCount => _rules.Length;

    /// <summary>The rules' effect, counted as the gate applies them.</summary>
    public RuleSetCounters Counters { get; }

    /// <summary>Some rule reads the path, so a caller only has to canonicalise it for a set that does.</summary>
    public bool NeedsPath { get; }

    /// <summary>A rule runs a regular expression, which may allocate (see the type's remarks).</summary>
    public bool HasRegex { get; }

    public RuleAction ActionOf(int rule) => _rules[rule].Action;

    internal RuleSpec SpecOf(int rule) => _rules[rule].Spec;

    /// <summary>Compiles <paramref name="spec"/>; null with the reasons when any rule can't be used. Cold path.</summary>
    public static CompiledRuleSet? TryCreate(RuleSetSpec spec, RuleSetCounters counters, List<string> errors) =>
        TryCreate(spec, counters, errors, RuleSetLimits.RegexMatchTimeout);

    internal static CompiledRuleSet? TryCreate(RuleSetSpec spec, RuleSetCounters counters, List<string> errors, TimeSpan regexTimeout)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(errors);
        if (spec.Rules.Count > RuleSetLimits.MaxRules)
        {
            errors.Add($"a rule set can have at most {RuleSetLimits.MaxRules} rules");
            return null;
        }

        List<CompiledRule> rules = new(spec.Rules.Count);
        var valid = true;
        for (var i = 0; i < spec.Rules.Count; i++)
        {
            List<string> ruleErrors = [];
            if (CompiledRule.TryCreate(spec.Rules[i], counters, ruleErrors, regexTimeout) is { } rule)
            {
                rules.Add(rule);
            }
            else
            {
                valid = false;
                foreach (var error in ruleErrors)
                {
                    errors.Add($"rule {i + 1}: {error}");
                }
            }
        }

        return valid ? new CompiledRuleSet(spec, rules.ToArray(), counters) : null;
    }

    // The proxy doesn't use LINQ (an architecture test says so): the patterns in order, and each one's rule indexes.
    private static (string[] Patterns, int[][] Rules) Flatten(SortedDictionary<string, List<int>> index)
    {
        var patterns = new string[index.Count];
        var rules = new int[index.Count][];
        var at = 0;
        foreach (var (pattern, indexes) in index)
        {
            patterns[at] = pattern;
            rules[at++] = indexes.ToArray();
        }

        return (patterns, rules);
    }

    /// <summary>
    /// The index of the first rule the request matches, or <see cref="NoMatch"/>.
    /// </summary>
    /// <param name="method">The request method.</param>
    /// <param name="path">The canonical matching path (<see cref="Snapshot.RoutePath"/>); ignored by a set that doesn't read it.</param>
    /// <param name="userAgent">The request's <c>User-Agent</c> values.</param>
    public int Match(string method, ReadOnlySpan<char> path, StringValues userAgent)
    {
        var best = int.MaxValue;
        foreach (var index in _scan)
        {
            if (_rules[index].MatchesAll(method, path, userAgent))
            {
                best = index;
                break;
            }
        }

        if (_exact.Length > 0)
        {
            var at = Find(_exact, path);
            if (at >= 0)
            {
                best = Lowest(_exactRules[at], best, method, userAgent);
            }
        }

        if (_prefix.Length > 0)
        {
            best = MatchPrefixes(path, best, method, userAgent);
        }

        return best == int.MaxValue ? NoMatch : best;
    }

    // A prefix pattern P takes a path X as a route's prefix does: X starts with P, and X is P, or P ends with '/', or the next
    // character of X is '/'. So the candidates are X itself and, at each '/' of X, the text before it and the text through it.
    private int MatchPrefixes(ReadOnlySpan<char> path, int best, string method, StringValues userAgent)
    {
        if (path.IsEmpty)
        {
            path = "/";
        }

        var at = Find(_prefix, path);
        if (at >= 0)
        {
            best = Lowest(_prefixRules[at], best, method, userAgent);
        }

        for (var slash = path.IndexOf('/'); slash >= 0;)
        {
            var through = slash + 1;
            if (slash > 0 && (at = Find(_prefix, path[..slash])) >= 0)
            {
                best = Lowest(_prefixRules[at], best, method, userAgent);
            }

            if (through < path.Length && (at = Find(_prefix, path[..through])) >= 0)
            {
                best = Lowest(_prefixRules[at], best, method, userAgent);
            }

            var next = path[through..].IndexOf('/');
            slash = next < 0 ? -1 : through + next;
        }

        return best;
    }

    // The lowest of rules (ascending) below best whose other conditions hold.
    private int Lowest(int[] rules, int best, string method, StringValues userAgent)
    {
        foreach (var index in rules)
        {
            if (index >= best)
            {
                break;
            }

            if (_rules[index].MatchesRest(method, userAgent))
            {
                return index;
            }
        }

        return best;
    }

    private static int Find(string[] sorted, ReadOnlySpan<char> key)
    {
        var low = 0;
        var high = sorted.Length - 1;
        while (low <= high)
        {
            var mid = (int)(((uint)low + (uint)high) >> 1);
            var order = sorted[mid].AsSpan().CompareTo(key, StringComparison.OrdinalIgnoreCase);
            if (order == 0)
            {
                return mid;
            }

            if (order < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return -1;
    }
}
