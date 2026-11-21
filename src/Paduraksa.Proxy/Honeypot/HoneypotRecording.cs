using Paduraksa.Core.Model;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// A session as the recording writes it: the running totals at a point in time. The counters cover the whole session, so a later record
/// of the same <see cref="Id"/> replaces an earlier one.
/// </summary>
/// <param name="Id">The session.</param>
/// <param name="Host">The host whose trap it is.</param>
/// <param name="ClientIp">The client address in full.</param>
/// <param name="UserAgent">The user agent, cut to <see cref="HoneypotLimits.RecordedUserAgentLength"/>.</param>
/// <param name="Canary">The visitor's canary phrase.</param>
/// <param name="StartedAt">The first request.</param>
/// <param name="LastSeen">The latest request.</param>
/// <param name="ReadRobotsFirst">The visitor fetched robots.txt before the session's first request.</param>
/// <param name="Requests">Every request the trap answered for the session.</param>
/// <param name="Pages">Requests for a page of the site: not the sitemap, the feed, an image, the beacon, a URL that is no page, or the challenge's verify request.</param>
/// <param name="GuessedUrls">Requests for a URL that is no page.</param>
/// <param name="Images">Requests for an image.</param>
/// <param name="JsRuns">Requests only a client that runs the page's script makes: the beacon and the script link.</param>
/// <param name="ChallengesShown">Requests answered with the challenge instead of the page.</param>
/// <param name="ChallengesSolved">Verify requests that carried a valid solution.</param>
/// <param name="ChallengesPassed">Pages behind the challenge served on a valid cookie.</param>
/// <param name="MaxDepth">The most links followed from the index to reach a page served to the session; 0 when it only had the index.</param>
/// <param name="LinkCounts">Requests by the kind of link they followed, indexed by <see cref="HoneypotLinkKind"/>; the verify request follows none.</param>
public sealed record HoneypotSessionRecord(
    HoneypotSessionId Id,
    HostId Host,
    IpKey ClientIp,
    string UserAgent,
    string Canary,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeen,
    bool ReadRobotsFirst,
    int Requests,
    int Pages,
    int GuessedUrls,
    int Images,
    int JsRuns,
    int ChallengesShown,
    int ChallengesSolved,
    int ChallengesPassed,
    int MaxDepth,
    IReadOnlyList<int> LinkCounts);

/// <summary>One request of a session, with the hit's strings cut to the lengths the tables keep.</summary>
/// <param name="SessionId">The session it belongs to.</param>
/// <param name="Id">The request.</param>
/// <param name="Hit">What the gate reported.</param>
public sealed record HoneypotRequestRecord(HoneypotSessionId SessionId, HoneypotRequestId Id, HoneypotHit Hit);

/// <summary>What the recording has to write: the sessions that changed (their latest totals) and the requests since the last write.</summary>
/// <param name="Sessions">Sessions that started or changed, one record each.</param>
/// <param name="Requests">Requests in the order they were recorded.</param>
public sealed record HoneypotBatch(IReadOnlyList<HoneypotSessionRecord> Sessions, IReadOnlyList<HoneypotRequestRecord> Requests)
{
    public bool IsEmpty => Sessions.Count == 0 && Requests.Count == 0;
}
