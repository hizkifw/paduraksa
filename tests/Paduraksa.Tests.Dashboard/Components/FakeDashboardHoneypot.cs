using Paduraksa.Application.Honeypot;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.HoneypotArea;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The honeypot reads over lists a test sets: sessions newest first (by ID), a session's requests oldest first, and the page rules of the real service.</summary>
internal sealed class FakeDashboardHoneypot : IDashboardHoneypot
{
    public static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public HoneypotSummary Summary { get; set; } = new(
        Now,
        MetricsRange.Hour,
        new HoneypotCounts(0, 0, 0, 0, 0, 0),
        new HoneypotBehaviour([], 0, 0, 0, 0, 0, 0),
        [],
        []);

    public List<HoneypotSession> Sessions { get; } = [];

    public Dictionary<HoneypotSessionId, List<HoneypotRequest>> Requests { get; } = [];

    /// <summary>Every list read, for a test to see what was asked.</summary>
    public List<(HoneypotSessionFilter Filter, int Limit, HoneypotSessionId? After)> Lists { get; } = [];

    public List<(HoneypotSessionId Session, int Limit, HoneypotRequestId? After)> RequestReads { get; } = [];

    public int SummaryReads { get; private set; }

    /// <summary>Every read throws, as a database that is down does.</summary>
    public bool Down { get; set; }

    public Task<HoneypotSummary> ReadSummaryAsync(MetricsRange range, HostId? host, CancellationToken cancellationToken = default)
    {
        SummaryReads++;
        return Down ? throw new InvalidOperationException("The database is down.") : Task.FromResult(Summary with { Range = range });
    }

    public Task<HoneypotSessionPage> ListSessionsAsync(HoneypotSessionFilter filter, int limit, HoneypotSessionId? after, CancellationToken cancellationToken = default)
    {
        Lists.Add((filter, limit, after));
        if (Down)
        {
            throw new InvalidOperationException("The database is down.");
        }

        var matching = Sessions
            .Where(session => filter.Query is not { } text
                || session.ClientIp.StartsWith(text, StringComparison.Ordinal)
                || session.UserAgent.Contains(text, StringComparison.OrdinalIgnoreCase)
                || session.Canary.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Where(session => after is not { } cursor || session.Id < cursor)
            .OrderByDescending(session => session.Id)
            .ToList();
        HoneypotSession[] items = [.. matching.Take(limit)];
        return Task.FromResult(new HoneypotSessionPage(items, matching.Count > limit ? items[^1].Id : null));
    }

    public Task<HoneypotRequestPage?> ListRequestsAsync(HoneypotSessionId id, int limit, HoneypotRequestId? after, CancellationToken cancellationToken = default)
    {
        RequestReads.Add((id, limit, after));
        if (Down)
        {
            throw new InvalidOperationException("The database is down.");
        }

        if (!Sessions.Any(session => session.Id == id))
        {
            return Task.FromResult<HoneypotRequestPage?>(null);
        }

        var matching = (Requests.GetValueOrDefault(id) ?? []).Where(request => after is not { } cursor || request.Id > cursor).OrderBy(request => request.Id).ToList();
        HoneypotRequest[] items = [.. matching.Take(limit)];
        return Task.FromResult<HoneypotRequestPage?>(new HoneypotRequestPage(items, matching.Count > limit ? items[^1].Id : null));
    }

    public static HoneypotSession Session(
        string ip = "203.0.113.9",
        string agent = "ExampleBot/1.0",
        string botKind = "none",
        string botName = "",
        string canary = "amber falcon",
        int pages = 3,
        bool robotsFirst = false,
        HostId? host = null) =>
        new(
            HoneypotSessionId.New(), host ?? HostId.New(), ip, agent, botKind, botName, canary, Now.AddMinutes(-20), Now.AddMinutes(-3), robotsFirst,
            Requests: pages + 1, Pages: pages, GuessedUrls: 1, Images: 0, JsRuns: 0, ChallengesShown: 0, ChallengesSolved: 0, ChallengesPassed: 0, MaxDepth: 2, Links: [new HoneypotLinkCount("normal", pages)]);

    public static HoneypotRequest Request(DateTimeOffset time, string path = "/archive-k7f3/", string pageKind = "post", string linkKind = "normal", int status = 200, string pow = "none") =>
        new(HoneypotRequestId.New(), time, "prefix", pageKind, linkKind, 1, 0, path, false, status, pow, 0, "HTTP/2");
}
