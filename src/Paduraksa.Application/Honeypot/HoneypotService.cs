using Microsoft.EntityFrameworkCore;

using Paduraksa.Application.Analytics;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Honeypot;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Application.Honeypot;

/// <summary>Which sessions a list or a summary covers.</summary>
/// <param name="Range">Sessions whose latest request is within this range.</param>
/// <param name="Host">Only this host's; null for every host.</param>
/// <param name="Query">An IP address (exact, or a prefix of one), a user agent substring or a canary phrase (or part of one); null for all.</param>
public sealed record HoneypotSessionFilter(MetricsRange Range, HostId? Host = null, string? Query = null);

/// <summary>A page of sessions, newest first. <paramref name="Next"/> is the last session of a page that has older ones after it.</summary>
public sealed record HoneypotSessionPage(IReadOnlyList<HoneypotSession> Items, HoneypotSessionId? Next);

/// <summary>A session with a page of its requests, oldest first. <paramref name="Next"/> is the last request of a page that has later ones after it.</summary>
public sealed record HoneypotSessionRead(HoneypotSession Session, IReadOnlyList<HoneypotRequest> Requests, HoneypotRequestId? Next);

/// <summary>
/// The read side of the honeypot recording (docs/design/waf.md "Honeypot", "Recording") that the admin API shares: reads of the two tables, without authorization
/// of its own (the caller checks <c>metrics:read</c>). Reads the stored rows only, so the newest requests appear after the recording job's next write.
/// </summary>
public sealed class HoneypotService
{
    /// <summary>The user agents the summary lists.</summary>
    public const int TopUserAgents = 10;

    /// <summary>The paths the summary lists.</summary>
    public const int TopGuessedPaths = 10;

    /// <summary>The longest <see cref="HoneypotSessionFilter.Query"/>.</summary>
    public const int MaxQueryLength = 128;

    /// <summary>The largest page of sessions or requests.</summary>
    public const int MaxPageSize = 200;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly HoneypotStats _stats;
    private readonly TimeProvider _time;

    internal HoneypotService(IDbContextFactory<ConfigDbContext> contexts, HoneypotStats stats, TimeProvider time)
    {
        _contexts = contexts;
        _stats = stats;
        _time = time;
    }

    public async Task<HoneypotSummary> ReadSummaryAsync(MetricsRange range, HostId? host, CancellationToken cancellationToken)
    {
        var at = _time.GetUtcNow();
        var from = (at - AnalyticsService.Reach(range).Span).ToUnixTimeMilliseconds();
        var hostKey = host?.ToString();
        return await _contexts.UseAsync(async context =>
        {
            var sessions = Sessions(context, from, hostKey);
            var totals = await sessions.GroupBy(static _ => 1).Select(static group => new
            {
                Sessions = group.Count(),
                Requests = group.Sum(static s => (long)s.Requests),
                Pages = group.Sum(static s => (long)s.Pages),
                GuessedUrls = group.Sum(static s => (long)s.GuessedUrls),
                RanJs = group.Count(static s => s.JsRuns > 0),
                FetchedImages = group.Count(static s => s.Images > 0),
                Shown = group.Count(static s => s.ChallengesShown > 0),
                Solved = group.Count(static s => s.ChallengesSolved > 0),
                Passed = group.Count(static s => s.ChallengesPassed > 0),
                Robots = group.Count(static s => s.ReadRobotsFirst),
                Direct = group.Count(static s => s.LinkDirect > 0),
                Normal = group.Count(static s => s.LinkNormal > 0),
                NoFollow = group.Count(static s => s.LinkNoFollow > 0),
                Ugc = group.Count(static s => s.LinkUgc > 0),
                Sponsored = group.Count(static s => s.LinkSponsored > 0),
                Hidden = group.Count(static s => s.LinkHidden > 0),
                Comment = group.Count(static s => s.LinkComment > 0),
                Script = group.Count(static s => s.LinkScript > 0),
                Beacon = group.Count(static s => s.LinkBeacon > 0),
                Sitemap = group.Count(static s => s.LinkSitemap > 0),
                Feed = group.Count(static s => s.LinkFeed > 0),
                NoIndexPage = group.Count(static s => s.LinkNoIndexPage > 0),
                Image = group.Count(static s => s.LinkImage > 0),
            }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

            var agents = await sessions.GroupBy(static s => s.UserAgent)
                .Select(static group => new { UserAgent = group.Key, Sessions = group.Count(), Requests = group.Sum(static s => (long)s.Requests) })
                .OrderByDescending(static agent => agent.Requests).ThenBy(static agent => agent.UserAgent)
                .Take(TopUserAgents)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var pageKinds = new[] { (int)HoneypotPageKind.NotFound, (int)HoneypotPageKind.Landing };
            var guessed = context.HoneypotRequests.AsNoTracking().Where(request => request.Time >= from && pageKinds.Contains(request.PageKind));
            if (hostKey is not null)
            {
                guessed = guessed.Where(request => context.HoneypotSessions.Any(session => session.Id == request.SessionId && session.HostKey == hostKey));
            }

            var paths = await guessed.GroupBy(static request => new { request.Path, request.PathTruncated })
                .Select(static group => new { group.Key.Path, group.Key.PathTruncated, Requests = group.Count(), LastSeen = group.Max(static request => request.Time) })
                .OrderByDescending(static path => path.Requests).ThenByDescending(static path => path.LastSeen).ThenBy(static path => path.Path)
                .Take(TopGuessedPaths)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            HoneypotLinkCount[] links =
            [
                Link(HoneypotLinkKind.Direct, totals?.Direct),
                Link(HoneypotLinkKind.Normal, totals?.Normal),
                Link(HoneypotLinkKind.NoFollow, totals?.NoFollow),
                Link(HoneypotLinkKind.Ugc, totals?.Ugc),
                Link(HoneypotLinkKind.Sponsored, totals?.Sponsored),
                Link(HoneypotLinkKind.Hidden, totals?.Hidden),
                Link(HoneypotLinkKind.Comment, totals?.Comment),
                Link(HoneypotLinkKind.Script, totals?.Script),
                Link(HoneypotLinkKind.Beacon, totals?.Beacon),
                Link(HoneypotLinkKind.Sitemap, totals?.Sitemap),
                Link(HoneypotLinkKind.Feed, totals?.Feed),
                Link(HoneypotLinkKind.NoIndexPage, totals?.NoIndexPage),
                Link(HoneypotLinkKind.Image, totals?.Image),
            ];
            return new HoneypotSummary(
                at,
                range,
                host,
                new HoneypotCounts(totals?.Sessions ?? 0, totals?.Requests ?? 0, totals?.Pages ?? 0, totals?.GuessedUrls ?? 0, _stats.Shed, _stats.Dropped),
                new HoneypotBehaviour(links, totals?.RanJs ?? 0, totals?.FetchedImages ?? 0, totals?.Shown ?? 0, totals?.Solved ?? 0, totals?.Passed ?? 0, totals?.Robots ?? 0),
                [.. agents.Select(static agent => AgentOf(agent.UserAgent, agent.Sessions, agent.Requests))],
                [.. paths.Select(static path => new HoneypotPathCount(path.Path, path.PathTruncated, path.Requests, DateTimeOffset.FromUnixTimeMilliseconds(path.LastSeen)))]);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sessions matching <paramref name="filter"/>, newest first; <paramref name="after"/> is the last session of the previous page.</summary>
    public async Task<HoneypotSessionPage> ListSessionsAsync(HoneypotSessionFilter filter, int limit, HoneypotSessionId? after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var from = (_time.GetUtcNow() - AnalyticsService.Reach(filter.Range).Span).ToUnixTimeMilliseconds();
        return await _contexts.UseAsync(async context =>
        {
            var query = Sessions(context, from, filter.Host?.ToString());
            if (filter.Query is { Length: > 0 } text)
            {
                query = Matching(query, text);
            }

            if (after is { } cursor)
            {
                query = query.Where(row => row.Id < cursor);
            }

            // One more than asked: whether it exists says whether older sessions remain.
            var rows = await query.OrderByDescending(row => row.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
            var more = rows.Count > limit;
            HoneypotSession[] items = [.. rows.Take(limit).Select(HoneypotRows.ToSession)];
            return new HoneypotSessionPage(items, more ? items[^1].Id : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One session and a page of its requests, oldest first; null when there is no such session (it may be past retention).</summary>
    public async Task<HoneypotSessionRead?> ReadSessionAsync(HoneypotSessionId id, int limit, HoneypotRequestId? after, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        return await _contexts.UseAsync(async context =>
        {
            var row = await context.HoneypotSessions.AsNoTracking().FirstOrDefaultAsync(session => session.Id == id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return null;
            }

            var requests = context.HoneypotRequests.AsNoTracking().Where(request => request.SessionId == id);
            if (after is { } cursor)
            {
                requests = requests.Where(request => request.Id > cursor);
            }

            var rows = await requests.OrderBy(request => request.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
            var more = rows.Count > limit;
            HoneypotRequest[] items = [.. rows.Take(limit).Select(HoneypotRows.ToRequest)];
            return new HoneypotSessionRead(HoneypotRows.ToSession(row), items, more ? items[^1].Id : null);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<HoneypotSessionRow> Sessions(ConfigDbContext context, long from, string? hostKey)
    {
        var sessions = context.HoneypotSessions.AsNoTracking().Where(row => row.LastSeen >= from);
        return hostKey is null ? sessions : sessions.Where(row => row.HostKey == hostKey);
    }

    private static IQueryable<HoneypotSessionRow> Matching(IQueryable<HoneypotSessionRow> sessions, string text)
    {
        var escaped = EscapeLike(text);
        var prefix = escaped + "%";
        var inside = "%" + escaped + "%";
        // An address typed in another spelling (a shortened IPv6 address) is still the same one.
        var address = IpKey.TryParse(text, out var parsed) ? parsed.ToString() : null;
        return sessions.Where(row =>
            EF.Functions.Like(row.ClientIp, prefix, "\\")
            || (address != null && row.ClientIp == address)
            || EF.Functions.Like(row.UserAgent, inside, "\\")
            || EF.Functions.Like(row.Canary, inside, "\\"));
    }

    private static string EscapeLike(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static HoneypotLinkCount Link(HoneypotLinkKind kind, int? sessions) => new(HoneypotRows.LinkName(kind), sessions ?? 0);

    private static HoneypotUserAgent AgentOf(string userAgent, int sessions, long requests)
    {
        var info = UserAgentClassifier.Classify(userAgent);
        return new HoneypotUserAgent(userAgent, HoneypotRows.CamelCase(info.Bot.ToString()), info.BotName, sessions, requests);
    }
}
