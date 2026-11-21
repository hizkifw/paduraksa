using Paduraksa.Contracts.Honeypot;
using Paduraksa.Dashboard.Pages;

namespace Paduraksa.Dashboard.HoneypotArea;

/// <summary>The honeypot's words: the contract's names as the dashboard says them. Whatever a visitor sent (a user agent, a path, a canary) is never one of these.</summary>
public static class HoneypotText
{
    /// <summary>The sessions asked for at a time in the list, and the requests in the drawer's timeline.</summary>
    public const int PageSize = 25;

    /// <summary>A kind of link as a behaviour: how the visitor found the URL.</summary>
    public static string LinkKind(string kind) => kind switch
    {
        "direct" => "Direct URL",
        "normal" => "Normal link",
        "nofollow" => "nofollow link",
        "ugc" => "ugc link",
        "sponsored" => "sponsored link",
        "hidden" => "Hidden link",
        "comment" => "Link in a comment",
        "script" => "Script link",
        "beacon" => "Beacon",
        "sitemap" => "Sitemap",
        "feed" => "Feed",
        "noindexPage" => "Link on a noindex page",
        "image" => "Image",
        _ => kind,
    };

    /// <summary>What a URL named.</summary>
    public static string PageKind(string kind) => kind switch
    {
        "index" => "Index",
        "indexPage" => "Index page",
        "post" => "Post",
        "category" => "Category",
        "tag" => "Tag",
        "author" => "Author",
        "sitemap" => "Sitemap",
        "feed" => "Feed",
        "image" => "Image",
        "beacon" => "Beacon",
        "landing" => "Landing",
        "notFound" => "Not a page",
        _ => kind,
    };

    /// <summary>How a request got into the trap.</summary>
    public static string Entry(string entry) => entry switch
    {
        "robots" => "robots.txt",
        "prefix" => "Prefix",
        "rule" => "Rule",
        _ => entry,
    };

    /// <summary>What the challenge did for one request.</summary>
    public static string ProofOfWork(HoneypotRequest request) => request.ProofOfWork switch
    {
        "shown" => "Shown",
        "solved" => request.SolveMilliseconds > 0 ? $"Solved in {request.SolveMilliseconds / 1000} s" : "Solved",
        "passed" => "Passed",
        _ => "-",
    };

    /// <summary>What the challenge did for a session, in a word: the furthest it got.</summary>
    public static string ProofOfWork(HoneypotSession session) =>
        session.ChallengesSolved > 0 ? "Solved" : session.ChallengesPassed > 0 ? "Passed" : session.ChallengesShown > 0 ? "Shown" : "-";

    /// <summary>The session's challenge counters, for a tooltip.</summary>
    public static string ProofOfWorkDetail(HoneypotSession session) =>
        $"Shown {session.ChallengesShown}, solved {session.ChallengesSolved}, passed {session.ChallengesPassed}";

    /// <summary>The bot class of a user agent: "GPTBot, AI crawler"; null for a client that isn't one.</summary>
    public static string? Bot(string kind, string name) => kind == "none"
        ? null
        : string.IsNullOrEmpty(name) ? AnalyticsFigures.Kind(kind) : $"{name}, {AnalyticsFigures.Kind(kind)}";

    /// <summary>A user agent for a cell: "(none)" for a client that sent no user agent.</summary>
    public static string UserAgent(string userAgent) => userAgent.Length == 0 ? "(none)" : userAgent;

    /// <summary>A path as the table shows it: the beginning, with an ellipsis when the API cut it.</summary>
    public static string Path(string path, bool truncated) => truncated ? path + "..." : path;

    /// <summary>A path for a tooltip: the whole of what was kept, and that it was cut.</summary>
    public static string PathTitle(string path, bool truncated) => truncated ? "Cut at 512 characters: " + path : path;

    /// <summary>What a session did beyond reading pages, as short tags with their detail for a tooltip: read robots.txt first, ran the page's script, fetched images and the furthest the challenge got.</summary>
    public static IReadOnlyList<(string Label, string Detail)> Did(HoneypotSession session)
    {
        List<(string, string)> did = [];
        if (session.ReadRobotsFirst)
        {
            did.Add(("robots.txt", "Read robots.txt before its first request"));
        }

        if (session.JsRuns > 0)
        {
            did.Add(("JS", $"{session.JsRuns} requests only a client that runs the page's script makes"));
        }

        if (session.Images > 0)
        {
            did.Add(("Images", $"{session.Images} image requests"));
        }

        if (ProofOfWork(session) is not "-" and var pow)
        {
            did.Add(($"PoW {pow.ToLowerInvariant()}", ProofOfWorkDetail(session)));
        }

        return did;
    }
}
