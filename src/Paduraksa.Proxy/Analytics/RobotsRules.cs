using System.Text;

namespace Paduraksa.Proxy.Analytics;

/// <summary>
/// A host's <c>robots.txt</c> read back into rules, to tell a crawler that ignores it from one that doesn't (docs/design/observability.md "Traffic analytics"). The
/// proxy serves these bytes itself, so the rules the analytics judge by are the ones the crawler was shown. RFC 9309: a group names
/// one or more agents; the group whose agent name is the longest match inside the crawler's <c>User-Agent</c> applies, else
/// <c>*</c>; inside it the longest matching rule wins and <c>Allow</c> beats <c>Disallow</c> on a tie; <c>*</c> and a trailing
/// <c>$</c> are the only pattern characters.
/// </summary>
public sealed class RobotsRules
{
    /// <summary>The path a crawler may always read, whatever the rules say.</summary>
    public const string RobotsPath = "/robots.txt";

    private const int MaxRules = 2_048;

    private readonly Group[] _groups;

    private RobotsRules(Group[] groups) => _groups = groups;

    /// <summary>Rules that disallow nothing.</summary>
    public static RobotsRules Empty { get; } = new([]);

    /// <summary>Reads <paramref name="text"/>; unknown directives, comments and malformed lines are skipped, and at most <see cref="MaxRules"/> rules are kept.</summary>
    public static RobotsRules Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<Group> groups = [];
        List<string> agents = [];
        List<Rule> rules = [];
        var collecting = false;
        var total = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.AsSpan();
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().ToString();
            if (name.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (!collecting)
                {
                    Close(groups, agents, rules);
                    collecting = true;
                }

                agents.Add(value);
            }
            else if (name.Equals("allow", StringComparison.OrdinalIgnoreCase) || name.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                collecting = false;
                if (agents.Count > 0 && value.Length > 0 && total++ < MaxRules)
                {
                    rules.Add(new Rule(value, name.Equals("allow", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        Close(groups, agents, rules);
        return new RobotsRules([.. groups]);
    }

    private static void Close(List<Group> groups, List<string> agents, List<Rule> rules)
    {
        if (agents.Count > 0)
        {
            groups.Add(new Group([.. agents], [.. rules]));
        }

        agents.Clear();
        rules.Clear();
    }

    /// <summary>Whether a crawler sending <paramref name="userAgent"/> may fetch <paramref name="path"/>.</summary>
    public bool IsAllowed(string userAgent, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Resolve(userAgent).IsAllowed(path);
    }

    /// <summary>
    /// The rules that apply to a crawler sending <paramref name="userAgent"/>: the group whose agent name is the longest one inside it, else
    /// the groups for everyone together. Resolved once, a caller that sees the same agent often keeps it and asks it about each path.
    /// </summary>
    public Applicable Resolve(string userAgent)
    {
        ArgumentNullException.ThrowIfNull(userAgent);
        var best = -1;
        var bestLength = -1;
        for (var g = 0; g < _groups.Length; g++)
        {
            foreach (var agent in _groups[g].Agents)
            {
                if (agent.Length > bestLength && agent != "*" && userAgent.Contains(agent, StringComparison.OrdinalIgnoreCase))
                {
                    best = g;
                    bestLength = agent.Length;
                }
            }
        }

        if (best >= 0)
        {
            return new Applicable(_groups[best].Rules);
        }

        List<Rule> everyone = [];
        foreach (var group in _groups)
        {
            if (Array.IndexOf(group.Agents, "*") >= 0)
            {
                everyone.AddRange(group.Rules);
            }
        }

        return new Applicable([.. everyone]);
    }

    /// <summary>The rules one crawler is held to, from <see cref="Resolve"/>.</summary>
    public sealed class Applicable
    {
        private readonly Rule[] _rules;

        internal Applicable(Rule[] rules) => _rules = rules;

        /// <summary>Whether the crawler may fetch <paramref name="path"/>; <c>/robots.txt</c> is always allowed.</summary>
        public bool IsAllowed(ReadOnlySpan<char> path)
        {
            if (path.SequenceEqual(RobotsPath))
            {
                return true;
            }

            var matched = -1;
            var allowed = true;
            foreach (var rule in _rules)
            {
                if (Matches(rule, path) && (rule.Pattern.Length > matched || (rule.Pattern.Length == matched && rule.Allow)))
                {
                    matched = rule.Pattern.Length;
                    allowed = rule.Allow;
                }
            }

            return allowed;
        }
    }

    // A pattern is a prefix of the path; '*' is any run of characters and a trailing '$' pins the end. With only '*' for a
    // wildcard, taking each piece at its first possible place is enough, so the match is linear however many stars a custom file has.
    // The parts of a pattern that don't depend on the path (the pinned end, the literal before the first star) are worked out once, in the
    // rule, so a rule that can't match is turned away by one prefix comparison.
    private static bool Matches(Rule rule, ReadOnlySpan<char> text)
    {
        var body = rule.Body.AsSpan();
        var star = rule.FirstStar;
        if (star < 0)
        {
            return rule.Anchored ? text.SequenceEqual(body) : text.StartsWith(body, StringComparison.Ordinal);
        }

        if (!text.StartsWith(body[..star], StringComparison.Ordinal))
        {
            return false;
        }

        var rest = body[(star + 1)..];
        text = text[star..];
        while (true)
        {
            star = rest.IndexOf('*');
            if (star < 0)
            {
                // The last piece: anywhere after the stars, or at the very end when the pattern is pinned.
                return rule.Anchored ? text.EndsWith(rest, StringComparison.Ordinal) : text.Contains(rest, StringComparison.Ordinal);
            }

            var found = text.IndexOf(rest[..star], StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            text = text[(found + star)..];
            rest = rest[(star + 1)..];
        }
    }

    /// <summary>Reads the rules of a compiled <c>robots.txt</c> body.</summary>
    public static RobotsRules FromBody(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Parse(Encoding.UTF8.GetString(body));
    }

    private sealed record Group(string[] Agents, Rule[] Rules);

    internal sealed class Rule
    {
        public Rule(string pattern, bool allow)
        {
            Pattern = pattern;
            Allow = allow;
            Anchored = pattern.EndsWith('$');
            Body = Anchored ? pattern[..^1] : pattern;
            FirstStar = Body.IndexOf('*', StringComparison.Ordinal);
        }

        public string Pattern { get; }

        public bool Allow { get; }

        /// <summary>The pattern ends with <c>$</c>: it must match to the end of the path.</summary>
        public bool Anchored { get; }

        /// <summary>The pattern without its <c>$</c>.</summary>
        public string Body { get; }

        /// <summary>Where the first <c>*</c> is, or -1.</summary>
        public int FirstStar { get; }
    }
}
