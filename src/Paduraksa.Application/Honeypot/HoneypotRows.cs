using Paduraksa.Contracts.Honeypot;
using Paduraksa.Core.Model;
using Paduraksa.Data.Honeypot;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Application.Honeypot;

/// <summary>Between the recorder's records, the rows and the API's contracts.</summary>
internal static class HoneypotRows
{
    private static readonly HoneypotLinkKind[] LinkKinds = Enum.GetValues<HoneypotLinkKind>();

    public static void Fill(HoneypotSessionRow row, HoneypotSessionRecord record)
    {
        row.HostKey = record.Host.ToString();
        row.ClientIp = record.ClientIp.ToString();
        row.UserAgent = record.UserAgent;
        row.Canary = record.Canary;
        row.StartedAt = record.StartedAt.ToUnixTimeMilliseconds();
        row.LastSeen = record.LastSeen.ToUnixTimeMilliseconds();
        row.ReadRobotsFirst = record.ReadRobotsFirst;
        row.Requests = record.Requests;
        row.Pages = record.Pages;
        row.GuessedUrls = record.GuessedUrls;
        row.Images = record.Images;
        row.JsRuns = record.JsRuns;
        row.ChallengesShown = record.ChallengesShown;
        row.ChallengesSolved = record.ChallengesSolved;
        row.ChallengesPassed = record.ChallengesPassed;
        row.MaxDepth = record.MaxDepth;
        row.LinkDirect = record.LinkCounts[(int)HoneypotLinkKind.Direct];
        row.LinkNormal = record.LinkCounts[(int)HoneypotLinkKind.Normal];
        row.LinkNoFollow = record.LinkCounts[(int)HoneypotLinkKind.NoFollow];
        row.LinkUgc = record.LinkCounts[(int)HoneypotLinkKind.Ugc];
        row.LinkSponsored = record.LinkCounts[(int)HoneypotLinkKind.Sponsored];
        row.LinkHidden = record.LinkCounts[(int)HoneypotLinkKind.Hidden];
        row.LinkComment = record.LinkCounts[(int)HoneypotLinkKind.Comment];
        row.LinkScript = record.LinkCounts[(int)HoneypotLinkKind.Script];
        row.LinkBeacon = record.LinkCounts[(int)HoneypotLinkKind.Beacon];
        row.LinkSitemap = record.LinkCounts[(int)HoneypotLinkKind.Sitemap];
        row.LinkFeed = record.LinkCounts[(int)HoneypotLinkKind.Feed];
        row.LinkNoIndexPage = record.LinkCounts[(int)HoneypotLinkKind.NoIndexPage];
        row.LinkImage = record.LinkCounts[(int)HoneypotLinkKind.Image];
    }

    public static HoneypotRequestRow RequestOf(HoneypotRequestRecord record)
    {
        var hit = record.Hit;
        return new HoneypotRequestRow
        {
            Id = record.Id,
            SessionId = record.SessionId,
            Time = hit.Time.ToUnixTimeMilliseconds(),
            Entry = (int)hit.Entry,
            PageKind = (int)hit.PageKind,
            LinkKind = (int)hit.LinkKind,
            PageKey = (long)hit.PageId,
            ParentKey = (long)hit.ParentId,
            Status = hit.Status,
            Pow = (int)hit.Pow,
            SolveMilliseconds = hit.SolveMilliseconds,
            Protocol = hit.Protocol,
            TlsVersion = (int)hit.TlsVersion,
            Accept = hit.Accept,
            AcceptLanguage = hit.AcceptLanguage,
            SecFetchSite = hit.SecFetchSite,
            SecFetchMode = hit.SecFetchMode,
            SecFetchDest = hit.SecFetchDest,
            SecFetchUser = hit.SecFetchUser,
            Referer = hit.Referer,
            Path = hit.Path,
            PathTruncated = hit.PathTruncated,
        };
    }

    public static HoneypotSession ToSession(HoneypotSessionRow row)
    {
        var agent = UserAgentClassifier.Classify(row.UserAgent);
        return new HoneypotSession(
            row.Id,
            HostId.TryParse(row.HostKey, null, out var host) ? host : default,
            row.ClientIp,
            row.UserAgent,
            CamelCase(agent.Bot.ToString()),
            agent.BotName ?? string.Empty,
            row.Canary,
            DateTimeOffset.FromUnixTimeMilliseconds(row.StartedAt),
            DateTimeOffset.FromUnixTimeMilliseconds(row.LastSeen),
            row.ReadRobotsFirst,
            row.Requests,
            row.Pages,
            row.GuessedUrls,
            row.Images,
            row.JsRuns,
            row.ChallengesShown,
            row.ChallengesSolved,
            row.ChallengesPassed,
            row.MaxDepth,
            [.. LinkKinds.Select(kind => new HoneypotLinkCount(LinkName(kind), LinkOf(row, kind))).Where(static count => count.Count > 0)]);
    }

    public static HoneypotRequest ToRequest(HoneypotRequestRow row) => new(
        row.Id,
        DateTimeOffset.FromUnixTimeMilliseconds(row.Time),
        CamelCase(((HoneypotEntry)row.Entry).ToString()),
        CamelCase(((HoneypotPageKind)row.PageKind).ToString()),
        LinkName((HoneypotLinkKind)row.LinkKind),
        row.PageKey,
        row.ParentKey,
        row.Path,
        row.PathTruncated,
        row.Status,
        CamelCase(((HoneypotPowOutcome)row.Pow).ToString()),
        row.SolveMilliseconds,
        row.Protocol,
        row.TlsVersion == 0 ? null : ((System.Security.Authentication.SslProtocols)row.TlsVersion).ToString().ToLowerInvariant(),
        row.Accept,
        row.AcceptLanguage,
        row.SecFetchSite,
        row.SecFetchMode,
        row.SecFetchDest,
        row.SecFetchUser,
        row.Referer);

    /// <summary>The kind as the API names it: the enum's name in camel case, except where the design names it otherwise (<c>nofollow</c>, <c>noindexPage</c>).</summary>
    public static string LinkName(HoneypotLinkKind kind) => kind switch
    {
        HoneypotLinkKind.NoFollow => "nofollow",
        HoneypotLinkKind.NoIndexPage => "noindexPage",
        _ => CamelCase(kind.ToString()),
    };

    public static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static int LinkOf(HoneypotSessionRow row, HoneypotLinkKind kind) => kind switch
    {
        HoneypotLinkKind.Direct => row.LinkDirect,
        HoneypotLinkKind.Normal => row.LinkNormal,
        HoneypotLinkKind.NoFollow => row.LinkNoFollow,
        HoneypotLinkKind.Ugc => row.LinkUgc,
        HoneypotLinkKind.Sponsored => row.LinkSponsored,
        HoneypotLinkKind.Hidden => row.LinkHidden,
        HoneypotLinkKind.Comment => row.LinkComment,
        HoneypotLinkKind.Script => row.LinkScript,
        HoneypotLinkKind.Beacon => row.LinkBeacon,
        HoneypotLinkKind.Sitemap => row.LinkSitemap,
        HoneypotLinkKind.Feed => row.LinkFeed,
        HoneypotLinkKind.NoIndexPage => row.LinkNoIndexPage,
        HoneypotLinkKind.Image => row.LinkImage,
        _ => 0,
    };
}
