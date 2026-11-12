using Paduraksa.Core.Model;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// The outbox's process-lifetime figures, read by <c>/metrics</c> through <c>ProcessCounters</c>: deliveries that ended in each state by the kind of
/// channel (never its name: a label per channel would grow with the config), the pending count, and the events the in-memory buffer dropped.
/// Plain interlocked counters, written by the workers and read by a scrape.
/// </summary>
public sealed class NotificationStats
{
    private static readonly NotificationDeliveryState[] Outcomes =
        [NotificationDeliveryState.Sent, NotificationDeliveryState.Failed, NotificationDeliveryState.Expired, NotificationDeliveryState.Dropped];

    private static readonly NotificationChannelKind[] Kinds = [.. Enum.GetValues<NotificationChannelKind>()];

    private readonly long[] _ended = new long[Kinds.Length * Outcomes.Length];
    private long _pending;
    private long _bufferDropped;

    /// <summary>Deliveries that are waiting for a worker or for their retry time: the queue depth.</summary>
    public long Pending => Math.Max(0, Interlocked.Read(ref _pending));

    /// <summary>Events dropped because the in-memory buffer was full.</summary>
    public long BufferDropped => Interlocked.Read(ref _bufferDropped);

    /// <summary>Deliveries to channels of <paramref name="kind"/> that ended in <paramref name="outcome"/> (sent, failed, expired or dropped), since the process started.</summary>
    public long Ended(NotificationChannelKind kind, NotificationDeliveryState outcome) =>
        Slot(kind, outcome) is >= 0 and var slot ? Interlocked.Read(ref _ended[slot]) : 0;

    /// <summary>The kinds a counter is kept for, in order.</summary>
    public static IReadOnlyList<NotificationChannelKind> ChannelKinds => Kinds;

    /// <summary>The outcomes a counter is kept for, in order.</summary>
    public static IReadOnlyList<NotificationDeliveryState> EndedStates => Outcomes;

    internal void Ended(NotificationChannelKind kind, NotificationDeliveryState outcome, long count)
    {
        if (count > 0 && Slot(kind, outcome) is >= 0 and var slot)
        {
            _ = Interlocked.Add(ref _ended[slot], count);
        }
    }

    internal void PendingAdded(long count) => Interlocked.Add(ref _pending, count);

    internal void PendingSet(long count) => Interlocked.Exchange(ref _pending, count);

    internal void BufferDrop() => Interlocked.Increment(ref _bufferDropped);

    private static int Slot(NotificationChannelKind kind, NotificationDeliveryState outcome)
    {
        var k = Array.IndexOf(Kinds, kind);
        var o = Array.IndexOf(Outcomes, outcome);
        return k < 0 || o < 0 ? -1 : (k * Outcomes.Length) + o;
    }
}

/// <summary>The outbox's figures as process counters for <c>/metrics</c> and the OpenTelemetry meter: by channel kind, never by channel.</summary>
public static class NotificationMetrics
{
    /// <summary>One counter family per outcome (<c>paduraksa.notification.sent</c>, <c>failed</c>, <c>expired</c>, <c>dropped</c>, each with a <c>kind</c> label), the queue depth as a gauge and the buffer's drops.</summary>
    public static IReadOnlyList<Paduraksa.Proxy.Metrics.ProcessCounter> Counters(NotificationStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        List<Paduraksa.Proxy.Metrics.ProcessCounter> counters = [];
        foreach (var outcome in NotificationStats.EndedStates)
        {
            var name = "paduraksa.notification." + outcome.ToName();
            var help = outcome switch
            {
                NotificationDeliveryState.Sent => "Notification deliveries the service accepted, by kind of channel.",
                NotificationDeliveryState.Failed => "Notification deliveries given up on after their retries (or refused for good), by kind of channel.",
                NotificationDeliveryState.Expired => "Notification deliveries not sent because their event was too old when the process started, by kind of channel.",
                _ => "Notification deliveries not sent because their channel was deleted or paused, by kind of channel.",
            };
            foreach (var kind in NotificationStats.ChannelKinds)
            {
                var captured = (kind, outcome);
                counters.Add(new(name, help, "kind", kind.ToName(), () => stats.Ended(captured.kind, captured.outcome)));
            }
        }

        counters.Add(new("paduraksa.notification.queue_depth", "Notification deliveries waiting to be sent or retried.", null, null, () => stats.Pending, Gauge: true));
        counters.Add(new("paduraksa.notification.buffer_dropped", "Notification events dropped because the in-memory buffer in front of the outbox was full.", null, null, () => stats.BufferDropped));
        return counters;
    }
}
