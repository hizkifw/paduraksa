using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// The rule sets that ship with Paduraksa: read-only, named by a reserved ID in a host's settings like any other set, and
/// never stored in a document. Their rules are part of the build and are refreshed with each release, like the robots.txt
/// crawler list; an operator who wants different ones copies a preset into a set of their own.
/// </summary>
public static class RuleSetPresets
{
    /// <summary>The AI crawlers of <see cref="RobotsText.AiCrawlers"/>, refused by user agent. robots.txt asks them to stay away
    /// and only the polite ones listen; this refuses the rest.</summary>
    public static RuleSetConfig AiCrawlers { get; } = new(
        RuleSetId.FromValue(new Guid("0190a000-0000-7000-8000-72756c650001")),
        "Block AI crawlers",
        Notes: "Refuses the AI crawlers this release knows about by user agent: the list robots.txt's \"block AI crawlers\" mode uses.",
        Rules: [Deny(new RuleUserAgentConfig([.. RobotsText.AiCrawlers.Order(StringComparer.Ordinal)]))]);

    /// <summary>Scanners that announce themselves. A real attacker changes the header, so this only removes the noise.</summary>
    public static RuleSetConfig KnownScanners { get; } = new(
        RuleSetId.FromValue(new Guid("0190a000-0000-7000-8000-72756c650002")),
        "Block known scanners",
        Notes: "Refuses security scanners that send their own name as the user agent: sqlmap (SQL injection), nikto (web server "
            + "scanner), nmap (its scripting engine), masscan and zgrab (internet-wide port and banner scans) and nuclei "
            + "(template-based scanner). Each one's default user agent contains that name and no browser's does.",
        Rules: [Deny(new RuleUserAgentConfig(["masscan", "nikto", "nmap", "nuclei", "sqlmap", "zgrab"]))]);

    /// <summary>Requests that send no user agent: nearly always a script, and no browser or well-behaved crawler.</summary>
    public static RuleSetConfig EmptyUserAgent { get; } = new(
        RuleSetId.FromValue(new Guid("0190a000-0000-7000-8000-72756c650003")),
        "Block empty user agent",
        Notes: "Refuses a request with no user agent or an empty one. Some health checks and command-line tools send none: allow them with an earlier rule in a set of your own.",
        Rules: [Deny(new RuleUserAgentConfig(Empty: true))]);

    /// <summary>Every preset, in the order the dashboard lists them.</summary>
    public static IReadOnlyList<RuleSetConfig> All { get; } = [AiCrawlers, KnownScanners, EmptyUserAgent];

    /// <summary>The preset <paramref name="id"/> names, or false when it names none.</summary>
    public static bool TryGet(RuleSetId id, out RuleSetConfig preset)
    {
        foreach (var candidate in All)
        {
            if (candidate.Id == id)
            {
                preset = candidate;
                return true;
            }
        }

        preset = null!;
        return false;
    }

    public static bool IsPreset(RuleSetId id) => TryGet(id, out _);

    private static RuleConfig Deny(RuleUserAgentConfig userAgent) => new(RuleAction.Deny, new RuleMatchConfig(UserAgent: userAgent));
}
