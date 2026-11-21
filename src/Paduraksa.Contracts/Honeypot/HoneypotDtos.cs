using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Honeypot;

/// <summary>A kind of link and how many times it was followed or by how many sessions.</summary>
/// <param name="Kind">How the link was published: <c>direct</c> (a URL no link carries: the index, a guessed URL, a landing), <c>normal</c>, <c>nofollow</c>, <c>ugc</c>, <c>sponsored</c>, <c>hidden</c> (an anchor hidden with CSS), <c>comment</c> (a URL in an HTML comment), <c>script</c> (a link that exists only after the page's script ran), <c>beacon</c> (a request the script makes on load), <c>sitemap</c>, <c>feed</c>, <c>noindexPage</c> (a link on a page marked noindex) or <c>image</c>.</param>
/// <param name="Count">Requests that followed it, in a session; sessions that followed it, in the summary's behaviour.</param>
public sealed record HoneypotLinkCount(string Kind, long Count);

/// <summary>
/// One visitor's session in a host's honeypot: the host, the client address in full and the user agent, from the first request until 30 idle minutes
/// pass (docs/design/waf.md "Honeypot"). The counters cover the whole session, so a session that began before the range of a query is counted
/// in full. A session is written a few seconds after its requests, so the newest figures lag by about that.
/// </summary>
/// <param name="Id">The session.</param>
/// <param name="Host">The host whose trap it was (it may have been deleted since).</param>
/// <param name="ClientIp">The client address, in full: unlike the rest of the analytics, the honeypot keeps it.</param>
/// <param name="UserAgent">The <c>User-Agent</c>, cut to 512 characters; empty when the client sent none.</param>
/// <param name="BotKind">What kind of automated client the user agent names: <c>none</c>, <c>searchEngine</c>, <c>aiCrawler</c>, <c>monitor</c>, <c>socialPreview</c>, <c>scanner</c>, <c>library</c> or <c>unknown</c>.</param>
/// <param name="BotName">The bot's name (<c>GPTBot</c>, <c>curl</c>); empty when the agent isn't one.</param>
/// <param name="Canary">The visitor's canary phrase: two made-up words woven into the posts it was served. If they turn up in a search engine or a model's answer, they name this visitor.</param>
/// <param name="StartedAt">The first request.</param>
/// <param name="LastSeen">The latest request.</param>
/// <param name="ReadRobotsFirst">The visitor fetched robots.txt (up to a day) before its first request: a crawler that read the file and still came in.</param>
/// <param name="Requests">Requests the trap answered for it.</param>
/// <param name="Pages">Requests for a page of the site (the index, a post, a category, a tag, an author, an index page or a landing), including one answered with the challenge; not the sitemap, the feed, an image, the beacon, a URL that is no page, or the challenge's verify request.</param>
/// <param name="GuessedUrls">Requests for a URL under the prefix that is no page: a token that doesn't verify, or any other path.</param>
/// <param name="Images">Requests for an image.</param>
/// <param name="JsRuns">Requests that only a client that runs the page's script makes: the beacon and the script link.</param>
/// <param name="ChallengesShown">Requests answered with the proof-of-work challenge instead of the page.</param>
/// <param name="ChallengesSolved">Verify requests that carried a valid solution.</param>
/// <param name="ChallengesPassed">Pages behind the challenge served on a valid cookie.</param>
/// <param name="MaxDepth">The most links followed from the index to reach a page served to this session, counted from the parent pages it requested itself. A page whose parent it never requested is one link deep. 0 when it only had the index.</param>
/// <param name="Links">Requests by the kind of link they followed, for the kinds followed at least once. The verify request follows none.</param>
public sealed record HoneypotSession(
    HoneypotSessionId Id,
    HostId Host,
    string ClientIp,
    string UserAgent,
    string BotKind,
    string BotName,
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
    IReadOnlyList<HoneypotLinkCount> Links);

/// <summary>One request the trap answered, as the gate saw it, read as a page of a session's requests, oldest first.</summary>
/// <param name="Id">The request.</param>
/// <param name="Time">When it arrived.</param>
/// <param name="Entry">How it got in: <c>robots</c> (the host serves the robots.txt <c>Disallow</c> line, so a crawler that read it would not have come), <c>prefix</c> (the host's robots.txt passes through) or <c>rule</c> (a rule set's honeypot action answered a path outside the prefix).</param>
/// <param name="PageKind">What the URL named: <c>index</c>, <c>indexPage</c>, <c>post</c>, <c>category</c>, <c>tag</c>, <c>author</c>, <c>sitemap</c>, <c>feed</c>, <c>image</c>, <c>beacon</c>, <c>landing</c> or <c>notFound</c>. A verify request carries the page the visitor was sent back to.</param>
/// <param name="LinkKind">The kind of link the URL was published as, as in <see cref="HoneypotLinkCount.Kind"/>.</param>
/// <param name="PageKey">The page's ID, the seed of its content: its kind in the bits above the 28 of the ID itself, so one number names one page across kinds. Called a key because the API's IDs are typed (<c>hps_...</c>) and this one is the trap's own number.</param>
/// <param name="ParentKey">The key of the page whose link this URL is; 0 when none (the index is 0 too).</param>
/// <param name="Path">The canonical path the gate matched on, without the query string: the trap's verify path for a verify request, and the path that was probed (<c>/wp-login.php</c>) for a landing page a rule set's honeypot action answered. At most 512 characters. It is what the client sent, so it may hold any characters.</param>
/// <param name="PathTruncated">The path was longer than 512 characters and <paramref name="Path"/> is its beginning.</param>
/// <param name="Status">The status answered.</param>
/// <param name="ProofOfWork">What the challenge did: <c>none</c> (the page wasn't behind one), <c>shown</c> (the challenge was answered instead of the page), <c>solved</c> (a verify request with a valid solution, status 303) or <c>passed</c> (the page served on a valid cookie).</param>
/// <param name="SolveMilliseconds">With <c>solved</c>: from the challenge's issue to its verification, to the second; else 0.</param>
/// <param name="Protocol">The HTTP version: <c>HTTP/1.1</c>, <c>HTTP/2</c>, <c>HTTP/3</c>.</param>
/// <param name="TlsVersion">The negotiated TLS version (<c>tls12</c>, <c>tls13</c>); null on plain HTTP.</param>
/// <param name="Accept">The <c>Accept</c> header, cut to 256 characters.</param>
/// <param name="AcceptLanguage">The <c>Accept-Language</c> header, cut to 128 characters.</param>
/// <param name="SecFetchSite">The <c>Sec-Fetch-Site</c> header.</param>
/// <param name="SecFetchMode">The <c>Sec-Fetch-Mode</c> header.</param>
/// <param name="SecFetchDest">The <c>Sec-Fetch-Dest</c> header.</param>
/// <param name="SecFetchUser">The <c>Sec-Fetch-User</c> header.</param>
/// <param name="Referer">The <c>Referer</c> header, cut to 512 characters.</param>
public sealed record HoneypotRequest(
    HoneypotRequestId Id,
    DateTimeOffset Time,
    string Entry,
    string PageKind,
    string LinkKind,
    long PageKey,
    long ParentKey,
    string Path,
    bool PathTruncated,
    int Status,
    string ProofOfWork,
    int SolveMilliseconds,
    string Protocol,
    string? TlsVersion = null,
    string? Accept = null,
    string? AcceptLanguage = null,
    string? SecFetchSite = null,
    string? SecFetchMode = null,
    string? SecFetchDest = null,
    string? SecFetchUser = null,
    string? Referer = null);

/// <summary>A path that was asked for and is no page of the trap, with how often.</summary>
/// <param name="Path">The canonical path, without the query string, at most 512 characters.</param>
/// <param name="PathTruncated">The path was longer than that and <paramref name="Path"/> is its beginning.</param>
/// <param name="Requests">Requests for it in the range.</param>
/// <param name="LastSeen">The latest of them.</param>
public sealed record HoneypotPathCount(string Path, bool PathTruncated, long Requests, DateTimeOffset LastSeen);

/// <summary>The sessions of a range, and what they did.</summary>
/// <param name="Sessions">Sessions with a request in the range.</param>
/// <param name="Requests">Their requests (whole sessions: see <see cref="HoneypotSession"/>).</param>
/// <param name="Pages">Their requests for a page of the site.</param>
/// <param name="GuessedUrls">Their requests for a URL that is no page.</param>
/// <param name="Shed">Requests the trap answered <c>503</c> because it was already generating its cap of answers, since the server started (not per range).</param>
/// <param name="Dropped">Requests the recording had no room for, since the server started (not per range): not in any figure here.</param>
public sealed record HoneypotCounts(long Sessions, long Requests, long Pages, long GuessedUrls, long Shed, long Dropped);

/// <summary>What the sessions of a range did, as the number of sessions that did it.</summary>
/// <param name="Links">For every kind of link, how many sessions followed one at least once.</param>
/// <param name="RanJs">Sessions that made a request only a client that runs the page's script makes.</param>
/// <param name="FetchedImages">Sessions that fetched an image.</param>
/// <param name="ChallengesShown">Sessions that were shown the proof-of-work challenge.</param>
/// <param name="ChallengesSolved">Sessions that solved it.</param>
/// <param name="ChallengesPassed">Sessions served a page behind it on a valid cookie.</param>
/// <param name="ReadRobotsFirst">Sessions whose visitor fetched robots.txt before its first request.</param>
public sealed record HoneypotBehaviour(
    IReadOnlyList<HoneypotLinkCount> Links,
    long RanJs,
    long FetchedImages,
    long ChallengesShown,
    long ChallengesSolved,
    long ChallengesPassed,
    long ReadRobotsFirst);

/// <summary>A user agent among the sessions of a range.</summary>
/// <param name="UserAgent">The user agent, cut to 512 characters; empty for a client that sent none.</param>
/// <param name="BotKind">As in <see cref="HoneypotSession.BotKind"/>.</param>
/// <param name="BotName">The bot's name, or empty when it isn't one.</param>
/// <param name="Sessions">Sessions with it.</param>
/// <param name="Requests">Their requests.</param>
public sealed record HoneypotUserAgent(string UserAgent, string BotKind, string BotName, long Sessions, long Requests);

/// <summary>The honeypot over a range, for every host or for one (docs/design/waf.md "Honeypot", "Recording").</summary>
/// <param name="At">When it was read.</param>
/// <param name="Range">How far back it reaches: a session counts when its latest request is in the range.</param>
/// <param name="Counts">The headline figures.</param>
/// <param name="Behaviour">What the sessions did.</param>
/// <param name="TopUserAgents">The user agents with the most requests, at most ten.</param>
/// <param name="TopGuessedPaths">The paths asked for most that are no page: a URL under the prefix that doesn't verify, and the paths a rule set's honeypot action answered with a landing page. At most ten, by requests and then by latest. Counted by request time within the range, not by session.</param>
/// <param name="Host">The host asked for; absent for every host.</param>
public sealed record HoneypotSummary(
    DateTimeOffset At,
    MetricsRange Range,
    HoneypotCounts Counts,
    HoneypotBehaviour Behaviour,
    IReadOnlyList<HoneypotUserAgent> TopUserAgents,
    IReadOnlyList<HoneypotPathCount> TopGuessedPaths,
    HostId? Host = null);
