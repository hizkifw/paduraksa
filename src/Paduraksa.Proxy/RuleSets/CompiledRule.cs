using System.Buffers;
using System.Text.RegularExpressions;

using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;

namespace Paduraksa.Proxy.RuleSets;

/// <summary>One rule with its conditions compiled. Exact and prefix paths are tested by the set's index, so a rule that was found
/// through it only has its other conditions left (<see cref="MatchesRest"/>).</summary>
internal sealed class CompiledRule
{
    private readonly string[] _methods;
    private readonly SearchValues<string>? _userAgentTokens;
    private readonly bool _userAgentEmpty;
    private readonly GlobPattern? _glob;
    private readonly Regex? _regex;
    private readonly RuleSetCounters _counters;

    private CompiledRule(RuleSpec spec, string[] methods, SearchValues<string>? tokens, GlobPattern? glob, Regex? regex, RuleSetCounters counters)
    {
        _counters = counters;
        Spec = spec;
        _methods = methods;
        _userAgentTokens = tokens;
        _userAgentEmpty = spec.UserAgentEmpty;
        _glob = glob;
        _regex = regex;
    }

    public RuleSpec Spec { get; }

    public RuleAction Action => Spec.Action;

    /// <summary>The path condition needs a scan of the path (a glob or a regex), not an index lookup.</summary>
    public bool NeedsPathScan => _glob is not null || _regex is not null;

    /// <summary>Whether this rule's regex allocates when it runs, which the set reports so the common path can stay free of it.</summary>
    public bool UsesRegex => _regex is not null;

    /// <summary>Compiles <paramref name="spec"/>; null with the reasons in <paramref name="errors"/> when a pattern can't be used.</summary>
    public static CompiledRule? TryCreate(RuleSpec spec, RuleSetCounters counters, List<string> errors, TimeSpan regexTimeout)
    {
        GlobPattern? glob = null;
        Regex? regex = null;
        var valid = true;
        if (spec.PathKind is { } kind)
        {
            var pattern = spec.Pattern ?? string.Empty;
            switch (kind)
            {
                case RulePathKind.Glob:
                    if (!GlobPattern.TryCompile(pattern, out glob, out var globError))
                    {
                        errors.Add($"the glob '{pattern}': {globError}");
                        valid = false;
                    }

                    break;
                case RulePathKind.Regex:
                    regex = RulePatterns.TryCompileRegex(pattern, regexTimeout, out var regexError);
                    if (regex is null)
                    {
                        errors.Add($"the regex '{pattern}': {regexError}");
                        valid = false;
                    }

                    break;
                case RulePathKind.Exact or RulePathKind.Prefix:
                    if (pattern.Length == 0 || pattern[0] != '/')
                    {
                        errors.Add($"the path '{pattern}' must start with '/'");
                        valid = false;
                    }

                    break;
                default:
                    errors.Add($"unknown path kind {kind}");
                    valid = false;
                    break;
            }
        }

        if (!Enum.IsDefined(spec.Action))
        {
            errors.Add($"unknown rule action {spec.Action}");
            valid = false;
        }

        SearchValues<string>? tokens = null;
        if (spec.UserAgentContains.Count > 0)
        {
            var names = Copy(spec.UserAgentContains);
            if (Array.Exists(names, static token => string.IsNullOrEmpty(token)))
            {
                errors.Add("a user-agent substring can't be empty");
                valid = false;
            }
            else
            {
                tokens = SearchValues.Create(names, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (spec.PathKind is null && spec.Methods.Count == 0 && spec.UserAgentContains.Count == 0 && !spec.UserAgentEmpty)
        {
            errors.Add("a rule needs at least one condition");
            valid = false;
        }

        return valid ? new CompiledRule(spec, Copy(spec.Methods), tokens, glob, regex, counters) : null;
    }

    // The proxy doesn't use LINQ (an architecture test says so), and a spread of a list would.
    private static string[] Copy(IReadOnlyList<string> items)
    {
        var copy = new string[items.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = items[i];
        }

        return copy;
    }

    /// <summary>The method and user-agent conditions, which need no path: what remains once an index has found the path.</summary>
    public bool MatchesRest(string method, StringValues userAgent) => MatchesMethod(method) && MatchesUserAgent(userAgent);

    /// <summary>Every condition, the path scan included.</summary>
    public bool MatchesAll(string method, ReadOnlySpan<char> path, StringValues userAgent) =>
        MatchesMethod(method) && MatchesUserAgent(userAgent) && MatchesScannedPath(path);

    private bool MatchesMethod(string method)
    {
        if (_methods.Length == 0)
        {
            return true;
        }

        foreach (var candidate in _methods)
        {
            if (string.Equals(method, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesUserAgent(StringValues userAgent)
    {
        if (_userAgentTokens is null && !_userAgentEmpty)
        {
            return true;
        }

        // Every header line counts: a request that sends two can't hide a name in the one the upstream reads.
        var empty = true;
        for (var i = 0; i < userAgent.Count; i++)
        {
            var value = userAgent[i].AsSpan();
            if (!value.IsWhiteSpace())
            {
                empty = false;
                if (_userAgentTokens is not null && value.ContainsAny(_userAgentTokens))
                {
                    return true;
                }
            }
        }

        return _userAgentEmpty && empty;
    }

    private bool MatchesScannedPath(ReadOnlySpan<char> path)
    {
        if (_glob is not null)
        {
            return _glob.IsMatch(path);
        }

        if (_regex is not null)
        {
            try
            {
                return _regex.IsMatch(path);
            }
            catch (RegexMatchTimeoutException)
            {
                // An input the engine can't finish in time is decided by what the rule would do: a deny or a drop counts as a match and an
                // allow as none, so the answer is the stricter one either way, and it is counted. The timeout is far past any real path's
                // cost (the regex runs in time linear in the path), so this is for the day something is wrong.
                _counters.CountTimeout();
                return Spec.Action != RuleAction.Allow;
            }
        }

        return true;
    }
}
