using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>An event waiting in memory for the flusher, with the ID and creation time it will be stored under (kept across a failed write, so a retry is the same event).</summary>
internal readonly record struct BufferedEvent(NotificationEventId Id, NotificationEvent Event, DateTimeOffset CreatedAt);

/// <summary>
/// Deliveries taken together by a worker: one event as itself, or several events of a digestible kind as one digest. All rows of a group are
/// sent, retried and ended together.
/// </summary>
/// <param name="Rows">The deliveries.</param>
/// <param name="Message">What is sent: the event, or the digest of them.</param>
/// <param name="EventId">What a webhook de-duplicates on: the event's ID, or the digest's.</param>
/// <param name="Attempts">How many times the group has been sent already (the most of its rows).</param>
internal sealed record DeliveryGroup(IReadOnlyList<NotificationDeliveryId> Rows, NotificationEvent Message, string EventId, int Attempts);

/// <summary>
/// The outbox's database operations (PLAN §7.4 "As built: durable delivery and history"): the flusher stores events with their deliveries, a
/// channel's worker takes what is due and records how it went, and the process start, a deleted channel and retention clean up. One statement or one
/// small transaction each, never the config writer's lock: the notification tables are not config.
/// </summary>
internal sealed class NotificationOutbox(IDbContextFactory<ConfigDbContext> contexts, NotificationStats stats)
{
    private static readonly HashSet<NotificationEventKind> Digestible =
    [
        NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp, NotificationEventKind.DestinationDown, NotificationEventKind.DestinationUp,
        NotificationEventKind.CertificateExpiring, NotificationEventKind.AcmeRenewalFailed, NotificationEventKind.AcmeRenewalRecovered,
    ];

    public const string ChannelDeleted = "The channel was deleted.";

    public const string ChannelPaused = "The channel is paused.";

    public const string TooOld = "Not sent: the event was too old when Paduraksa started.";

    /// <summary>
    /// Stores <paramref name="events"/> and, for each, one pending delivery per enabled channel subscribed to its kind, in one transaction: the
    /// channels are read once per call, not once per event. An event nothing subscribes to is stored with no delivery, so the history shows it.
    /// </summary>
    /// <returns>The channels that have new deliveries, to be woken.</returns>
    public async Task<IReadOnlyCollection<NotificationChannelId>> StoreAsync(IReadOnlyList<BufferedEvent> events, CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var channels = await context.NotificationChannels.AsNoTracking().Where(c => c.Enabled).ToListAsync(cancellationToken).ConfigureAwait(false);
            HashSet<NotificationChannelId> touched = [];
            var deliveries = 0;
            foreach (var buffered in events)
            {
                var notification = buffered.Event;
                _ = context.NotificationEvents.Add(new NotificationEventRow
                {
                    Id = buffered.Id,
                    Kind = notification.Kind,
                    Subject = notification.Subject,
                    Title = notification.Title,
                    Body = notification.Body,
                    Severity = (int)notification.Severity,
                    LinkPath = notification.LinkPath,
                    Data = WriteData(notification.Data),
                    OccurredAt = notification.At,
                    CreatedAt = buffered.CreatedAt,
                });
                foreach (var channel in channels.Where(c => NotificationMapping.Subscribes(c, notification.Kind)))
                {
                    _ = context.NotificationDeliveries.Add(new NotificationDeliveryRow
                    {
                        Id = NotificationDeliveryId.New(),
                        EventId = buffered.Id,
                        ChannelId = channel.Id,
                        NextAttemptAt = buffered.CreatedAt,
                    });
                    _ = touched.Add(channel.Id);
                    deliveries++;
                }
            }

            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            stats.PendingAdded(deliveries);
            return touched;
        }
    }

    /// <summary>
    /// What a process start does to what the last one left: deliveries of a channel that no longer exists are dropped, and the rest that are pending for an
    /// event older than <paramref name="oldest"/> are expired, so a long outage does not end in a burst of stale messages.
    /// </summary>
    public async Task<IReadOnlyList<NotificationChannelId>> RecoverAsync(DateTimeOffset oldest, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var orphans = await context.NotificationDeliveries
                .Where(d => d.State == NotificationDeliveryState.Pending && !context.NotificationChannels.Any(c => c.Id == d.ChannelId))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, NotificationDeliveryState.Dropped).SetProperty(d => d.LastError, ChannelDeleted).SetProperty(d => d.LastAttemptAt, now), cancellationToken)
                .ConfigureAwait(false);
            stats.PendingAdded(-orphans);

            var channels = await context.NotificationChannels.AsNoTracking().Select(c => new { c.Id, c.Kind }).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var channel in channels)
            {
                var channelId = channel.Id;
                var expired = await context.NotificationDeliveries
                    .Where(d => d.ChannelId == channelId && d.State == NotificationDeliveryState.Pending
                        && context.NotificationEvents.Any(e => e.Id == d.EventId && e.CreatedAt < oldest))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, NotificationDeliveryState.Expired).SetProperty(d => d.LastError, TooOld).SetProperty(d => d.LastAttemptAt, now), cancellationToken)
                    .ConfigureAwait(false);
                stats.PendingAdded(-expired);
                stats.Ended(channel.Kind, NotificationDeliveryState.Expired, expired);
            }

            return await context.NotificationDeliveries.AsNoTracking()
                .Where(d => d.State == NotificationDeliveryState.Pending)
                .Select(d => d.ChannelId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Counts the pending deliveries again, so the queue-depth gauge cannot drift from what the table holds (a channel deleted through the service, a manual retry).</summary>
    public async Task RefreshPendingAsync(CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            stats.PendingSet(await context.NotificationDeliveries.AsNoTracking().CountAsync(d => d.State == NotificationDeliveryState.Pending, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>When the channel's earliest retry that is not due yet comes due; null when it has none. While there is one, the channel's newer deliveries wait behind it, as its messages always have.</summary>
    public async Task<DateTimeOffset?> RetryWaitingUntilAsync(NotificationChannelId channel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.NotificationDeliveries.AsNoTracking()
                .Where(d => d.ChannelId == channel && d.State == NotificationDeliveryState.Pending && d.NextAttemptAt > now)
                .OrderBy(d => d.NextAttemptAt)
                .Select(d => (DateTimeOffset?)d.NextAttemptAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The channel's due deliveries as the messages to send: each event on its own, or events of one digestible kind as one digest. A digest's rows get
    /// its ID here, before anything is sent, and keep it through retries, so a resend after a failure or a crash is the same message with the same ID.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryGroup>> ClaimAsync(NotificationChannelId channel, DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.NotificationDeliveries
                .Where(d => d.ChannelId == channel && d.State == NotificationDeliveryState.Pending && d.NextAttemptAt <= now)
                .OrderBy(d => d.NextAttemptAt).Take(limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return [];
            }

            var eventIds = rows.Select(r => r.EventId).Distinct().ToList();
            var events = (await context.NotificationEvents.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToDictionary(e => e.Id);

            var groups = Group(rows.Where(r => events.ContainsKey(r.EventId)).OrderBy(r => r.EventId).ToList(), events, out var assigned);
            if (assigned)
            {
                _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return groups;
        }
    }

    /// <summary>The rows in event order, grouped: those already in a digest stay together; the rest of a digestible kind become a digest when there are several.</summary>
    private static List<DeliveryGroup> Group(List<NotificationDeliveryRow> rows, Dictionary<NotificationEventId, NotificationEventRow> events, out bool assigned)
    {
        assigned = false;
        List<(NotificationEventId First, DeliveryGroup Group)> found = [];
        foreach (var batch in rows.Where(r => r.DigestId is not null).GroupBy(r => r.DigestId!.Value))
        {
            var members = batch.ToList();
            found.Add((members[0].EventId, Digest(members, events, batch.Key)));
        }

        var fresh = rows.Where(r => r.DigestId is null).ToList();
        foreach (var kind in fresh.Select(r => events[r.EventId].Kind).Distinct().Where(Digestible.Contains).ToList())
        {
            var members = fresh.Where(r => events[r.EventId].Kind == kind).ToList();
            if (members.Count < 2)
            {
                continue;
            }

            var digest = NotificationDigestId.New();
            foreach (var member in members)
            {
                member.DigestId = digest;
            }

            assigned = true;
            found.Add((members[0].EventId, Digest(members, events, digest)));
            _ = fresh.RemoveAll(members.Contains);
        }

        foreach (var row in fresh)
        {
            found.Add((row.EventId, new DeliveryGroup([row.Id], ToNotification(events[row.EventId]), row.EventId.ToString(), row.Attempts)));
        }

        return [.. found.OrderBy(f => f.First).Select(f => f.Group)];
    }

    private static DeliveryGroup Digest(List<NotificationDeliveryRow> members, Dictionary<NotificationEventId, NotificationEventRow> events, NotificationDigestId digest) => new(
        [.. members.Select(m => m.Id)],
        members.Count == 1 ? ToNotification(events[members[0].EventId]) : NotificationText.Digest([.. members.Select(m => ToNotification(events[m.EventId]))]),
        digest.ToString(),
        members.Max(m => m.Attempts));

    /// <summary>Records how a group's send went. Only rows still pending are touched, so a delivery dropped meanwhile (its channel was deleted or paused) stays dropped.</summary>
    /// <returns>How many rows changed.</returns>
    public async Task<int> RecordAsync(DeliveryGroup group, NotificationDeliveryState state, string? error, DateTimeOffset now, DateTimeOffset? retryAt, CancellationToken cancellationToken)
    {
        var rows = group.Rows;
        var attempts = group.Attempts + 1;
        DateTimeOffset? sentAt = state == NotificationDeliveryState.Sent ? now : null;
        var next = retryAt ?? now;
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var changed = await context.NotificationDeliveries
                .Where(d => rows.Contains(d.Id) && d.State == NotificationDeliveryState.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.State, state).SetProperty(d => d.Attempts, attempts).SetProperty(d => d.LastAttemptAt, now)
                        .SetProperty(d => d.SentAt, sentAt).SetProperty(d => d.LastError, error).SetProperty(d => d.NextAttemptAt, next),
                    cancellationToken).ConfigureAwait(false);
            if (state != NotificationDeliveryState.Pending)
            {
                stats.PendingAdded(-changed);
            }

            return changed;
        }
    }

    /// <summary>Marks a channel's pending deliveries dropped (it was deleted or paused), in the transaction of whoever changed the channel.</summary>
    /// <returns>How many rows changed.</returns>
    public static async Task<int> DropPendingAsync(ConfigDbContext context, NotificationChannelId channel, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.NotificationDeliveries
            .Where(d => d.ChannelId == channel && d.State == NotificationDeliveryState.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.State, NotificationDeliveryState.Dropped).SetProperty(d => d.LastError, reason).SetProperty(d => d.LastAttemptAt, now), cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Marks a deleted channel's pending deliveries dropped (a worker found its channel gone).</summary>
    public async Task<int> DropPendingAsync(NotificationChannelId channel, NotificationChannelKind? kind, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var dropped = await DropPendingAsync(context, channel, reason, now, cancellationToken).ConfigureAwait(false);
            stats.PendingAdded(-dropped);
            if (kind is { } known)
            {
                stats.Ended(known, NotificationDeliveryState.Dropped, dropped);
            }

            return dropped;
        }
    }

    /// <summary>
    /// Deletes events older than <paramref name="cutoff"/> that have no pending delivery, with their deliveries, a batch at a time. A failed delivery is
    /// final (it was given up), so a pending one is the only thing that keeps an old event.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, int batch, CancellationToken cancellationToken)
    {
        var removed = 0;
        while (true)
        {
            var context = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var ids = await context.NotificationEvents.AsNoTracking()
                    .Where(e => e.CreatedAt < cutoff && !context.NotificationDeliveries.Any(d => d.EventId == e.Id && d.State == NotificationDeliveryState.Pending))
                    .OrderBy(e => e.CreatedAt).Take(batch).Select(e => e.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (ids.Count == 0)
                {
                    return removed;
                }

                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    _ = await context.NotificationDeliveries.Where(d => ids.Contains(d.EventId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    removed += await context.NotificationEvents.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    internal static NotificationEvent ToNotification(NotificationEventRow row) => new(
        row.Kind,
        row.Title,
        row.Body,
        (NotificationSeverity)row.Severity,
        row.OccurredAt,
        row.LinkPath,
        ReadData(row.Data),
        row.Subject);

    /// <summary>A webhook's facts as a JSON array of <c>[key, value]</c> pairs, the order kept.</summary>
    internal static string? WriteData(IReadOnlyList<KeyValuePair<string, string>>? data)
    {
        if (data is not { Count: > 0 })
        {
            return null;
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartArray();
            foreach (var (key, value) in data)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(key);
                writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static IReadOnlyList<KeyValuePair<string, string>>? ReadData(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return [.. document.RootElement.EnumerateArray().Select(pair => new KeyValuePair<string, string>(pair[0].GetString() ?? string.Empty, pair[1].GetString() ?? string.Empty))];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // Written by this code as valid JSON; an edited row sends without its facts rather than not at all.
            return null;
        }
    }
}
