using Paduraksa.Contracts.Logging;
using Paduraksa.Proxy.Logging;

namespace Paduraksa.Application.Logging;

/// <summary>
/// The live access-log tail (docs/design/observability.md "The live tail (M9)") for the dashboard and the API: opens a bounded subscription on the hub the access-log
/// drain feeds, with a backlog of the newest matching requests, and maps what it holds to the contract's entries. Never changes
/// anything, and holds nothing of the request path.
/// </summary>
public sealed class LogTailService(AccessLogTail hub)
{
    /// <summary>The most backlog entries a subscription starts with, whatever is asked.</summary>
    public const int MaxBacklog = 1_000;

    /// <summary>
    /// How long a stream to an API client may stay silent before it sends a comment line, so a proxy or a client that times out
    /// idle connections keeps it open, and a vanished client is noticed by the failed write. A test shortens it.
    /// </summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A subscription to the requests <paramref name="filter"/> keeps, or null when the process is serving as many tails as it
    /// allows; the caller disposes the stream. A filter that <see cref="LogTailFilter.Problem"/> faults (the dashboard's input is
    /// as free as the API's) is refused with <see cref="ArgumentException"/>: the callers say it in their own words first.
    /// </summary>
    public LogTailFeed? Open(LogTailFilter filter, int backlog)
    {
        Check(filter);
        return hub.Subscribe(ToHubFilter(filter), Math.Clamp(backlog, 0, MaxBacklog)) is { } subscription ? new LogTailFeed(subscription) : null;
    }

    /// <summary>The newest <paramref name="limit"/> requests the filter keeps, oldest first.</summary>
    public IReadOnlyList<LogTailEntry> Recent(LogTailFilter filter, int limit)
    {
        Check(filter);
        var found = hub.Recent(ToHubFilter(filter), Math.Clamp(limit, 0, MaxBacklog));
        List<LogTailEntry> entries = new(found.Count);
        foreach (var entry in found)
        {
            entries.Add(LogTailFeed.Map(entry));
        }

        return entries;
    }

    /// <summary>How many tails are open and how many the process allows.</summary>
    public (int Open, int Limit) Subscribers => (hub.SubscriberCount, AccessLogTail.MaxSubscribers);

    private static void Check(LogTailFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(filter));
        }
    }

    private static AccessLogTailFilter ToHubFilter(LogTailFilter filter) => new()
    {
        Host = Blank(filter.Host),
        UnmatchedHost = filter.UnmatchedHost,
        StatusClass = filter.StatusClass,
        Status = filter.Status,
        ClientAddressPrefix = Blank(filter.ClientAddress),
        Method = Blank(filter.Method),
        PathContains = Blank(filter.PathContains),
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// One open tail: the backlog it started with, then what arrives, in order. A reader that falls behind loses the oldest entries and
/// is told how many (<see cref="TakeSkipped"/>). Dispose it to stop listening.
/// </summary>
public sealed class LogTailFeed : IDisposable
{
    private readonly AccessLogTailSubscription _subscription;

    internal LogTailFeed(AccessLogTailSubscription subscription)
    {
        _subscription = subscription;
        List<LogTailEntry> backlog = new(subscription.Backlog.Count);
        foreach (var entry in subscription.Backlog)
        {
            backlog.Add(Map(entry));
        }

        Backlog = backlog;
    }

    /// <summary>The newest matching requests when the tail opened, oldest first.</summary>
    public IReadOnlyList<LogTailEntry> Backlog { get; }

    /// <summary>
    /// Completes with true when an entry can be read, false once the stream is disposed. Honours <paramref name="cancellationToken"/>
    /// by throwing <see cref="OperationCanceledException"/>.
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) => _subscription.Reader.WaitToReadAsync(cancellationToken);

    public bool TryRead(out LogTailEntry entry)
    {
        if (_subscription.Reader.TryRead(out var found))
        {
            entry = Map(found);
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>How many entries are waiting to be read.</summary>
    public int Pending => _subscription.Reader.CanCount ? _subscription.Reader.Count : 0;

    /// <summary>The entries dropped since the last call because the reader was behind.</summary>
    public long TakeSkipped() => _subscription.TakeSkipped();

    public void Dispose() => _subscription.Dispose();

    internal static LogTailEntry Map(AccessLogTailEntry entry) => new(
        entry.Sequence,
        new DateTimeOffset(entry.TimestampUtcTicks, TimeSpan.Zero),
        entry.TotalTicks / (double)TimeSpan.TicksPerMillisecond,
        ClientAddress: entry.ClientAddress,
        Host: entry.Host,
        MatchedHost: entry.MatchedHostNames is { Count: > 0 } names ? names[0] : null,
        Method: entry.Method,
        Path: entry.Path,
        Query: entry.Query,
        Protocol: entry.Protocol,
        Status: entry.StatusCode > AccessLogEntry.NoResponse ? entry.StatusCode : null,
        ResponseBytes: entry.ResponseBytes >= 0 ? entry.ResponseBytes : null,
        RequestBytes: entry.RequestBytes >= 0 ? entry.RequestBytes : null,
        UpstreamMs: entry.UpstreamTicks >= 0 ? entry.UpstreamTicks / (double)TimeSpan.TicksPerMillisecond : null,
        Upstream: entry.UpstreamAddress,
        RequestIdentifier: entry.RequestId,
        CloudflareRay: entry.CloudflareRay,
        Referer: entry.Referer,
        UserAgent: entry.UserAgent,
        Timing: new LogTailTiming(
            ConnectionRequest: entry.ConnectionRequest > 0 ? entry.ConnectionRequest : null,
            TlsHandshakeMs: Ms(entry.TlsHandshakeTicks),
            TlsReused: entry.TlsHandshakeTicks == AccessLogEntry.HandshakeReused,
            RequestBodyMs: Ms(entry.RequestBodyTicks),
            GateMs: Ms(entry.GateTicks),
            UpstreamConnectMs: entry.UpstreamTicks >= 0 ? Ms(entry.UpstreamConnectTicks) : null,
            UpstreamWaitMs: Ms(entry.UpstreamWaitTicks),
            ResponseSendMs: Ms(entry.ResponseSendTicks),
            RequestBodyStartMs: Ms(entry.RequestBodyStartMicros),
            UpstreamConnectStartMs: entry.UpstreamConnectTicks > 0 ? Ms(entry.UpstreamConnectStartMicros) : null,
            UpstreamWaitStartMs: Ms(RequestStages.WaitStartTicks(entry.ForwardStartMicros, entry.UpstreamTicks, entry.UpstreamWaitTicks)),
            ResponseSendStartMs: entry.ResponseSendTicks >= 0 ? Ms(Math.Max(0, entry.TotalTicks - entry.ResponseSendTicks)) : null));

    private static double? Ms(long ticks) => ticks >= 0 ? ticks / (double)TimeSpan.TicksPerMillisecond : null;

    private static double? Ms(int micros) => micros >= 0 ? micros / 1000.0 : null;
}
