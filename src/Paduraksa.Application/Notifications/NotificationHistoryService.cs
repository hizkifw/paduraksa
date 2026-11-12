using Microsoft.EntityFrameworkCore;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// The notification history (PLAN §7.4 "As built: durable delivery and history"): events newest first with what happened to each on every channel, and the
/// manual retry of a failed or expired delivery. Reads need <c>providers:read</c> and a retry <c>providers:write</c>, the permissions of the channels,
/// checked by the caller for reads and here for the retry, which is audited with who asked and which delivery.
/// </summary>
public sealed class NotificationHistoryService
{
    /// <summary>The largest page.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The window of a channel's success figures.</summary>
    public static readonly TimeSpan FigureWindow = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly IPermissionEvaluator _permissions;
    private readonly NotificationStats _stats;
    private readonly IDeliveryWake _wake;
    private readonly TimeProvider _time;

    internal NotificationHistoryService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        IPermissionEvaluator permissions,
        NotificationStats stats,
        IDeliveryWake wake,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(time);
        _contexts = contexts;
        _config = config;
        _permissions = permissions;
        _stats = stats;
        _wake = wake;
        _time = time;
    }

    /// <summary>Events matching <paramref name="filter"/>, newest first, each with its deliveries. <paramref name="after"/> is the last event of the previous page.</summary>
    public async Task<NotificationHistoryPage> QueryAsync(NotificationHistoryFilter filter, int limit, NotificationEventId? after, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var events = context.NotificationEvents.AsNoTracking();
            if (after is { } cursor)
            {
                events = events.Where(e => e.Id < cursor);
            }

            if (filter.From is { } from)
            {
                var utc = from.ToUniversalTime();
                events = events.Where(e => e.CreatedAt >= utc);
            }

            if (filter.To is { } to)
            {
                var utc = to.ToUniversalTime();
                events = events.Where(e => e.CreatedAt < utc);
            }

            if (filter.Kind is { } kind)
            {
                events = events.Where(e => e.Kind == kind);
            }

            if (filter.Channel is { } channel && filter.Outcome is { } outcome)
            {
                events = events.Where(e => context.NotificationDeliveries.Any(d => d.EventId == e.Id && d.ChannelId == channel && d.State == outcome));
            }
            else if (filter.Channel is { } onlyChannel)
            {
                events = events.Where(e => context.NotificationDeliveries.Any(d => d.EventId == e.Id && d.ChannelId == onlyChannel));
            }
            else if (filter.Outcome is { } onlyOutcome)
            {
                events = events.Where(e => context.NotificationDeliveries.Any(d => d.EventId == e.Id && d.State == onlyOutcome));
            }

            // One more than asked: whether it exists says whether older events remain.
            var rows = await events.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
            var more = rows.Count > limit;
            rows = [.. rows.Take(limit)];
            var ids = rows.Select(r => r.Id).ToList();
            var deliveries = ids.Count == 0
                ? []
                : await context.NotificationDeliveries.AsNoTracking().Where(d => ids.Contains(d.EventId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var channels = await ChannelsAsync(context, cancellationToken).ConfigureAwait(false);
            var items = rows.Select(r => ToEvent(r, deliveries.Where(d => d.EventId == r.Id), channels, includeChannel: null)).ToList();
            return new NotificationHistoryPage(items, more && items.Count > 0 ? items[^1].Id : null);
        }
    }

    /// <summary>A channel's latest deliveries, newest first, each as its event with only this channel's delivery (the channel's drawer).</summary>
    public async Task<IReadOnlyList<NotificationHistoryEvent>> RecentAsync(NotificationChannelId channel, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var deliveries = await context.NotificationDeliveries.AsNoTracking().Where(d => d.ChannelId == channel).OrderByDescending(d => d.Id).Take(limit)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var ids = deliveries.Select(d => d.EventId).Distinct().ToList();
            var events = (await context.NotificationEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.Id);
            var channels = await ChannelsAsync(context, cancellationToken).ConfigureAwait(false);
            return [.. deliveries.Where(d => events.ContainsKey(d.EventId)).Select(d => ToEvent(events[d.EventId], [d], channels, channel))];
        }
    }

    /// <summary>Sends a failed or expired delivery again: it goes back to pending, due now, with a full retry schedule. Needs <c>providers:write</c>; audited.</summary>
    public async Task<NotificationRetryResult> RetryDeliveryAsync(NotificationDeliveryId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.delivery.retry", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new NotificationRetryResult(IssuanceChangeStatus.Denied);
        }

        List<NotificationChannelId> woken = [];
        var result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                var row = await context.NotificationDeliveries.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return (new NotificationRetryResult(IssuanceChangeStatus.NotFound), null);
                }

                var outcome = await ResetAsync(context, [row], woken, cancellationToken).ConfigureAwait(false);
                return outcome.Retried == 0
                    ? (outcome.Refusal!, null)
                    : (new NotificationRetryResult(IssuanceChangeStatus.Committed, 1), IssuanceMapping.Entry(origin, "notification.delivery.retry", id, AuditOutcome.Success) with
                    {
                        Details = AuditDetails.Json(w =>
                        {
                            w.WriteString("event", row.EventId.ToString());
                            w.WriteString("channel", row.ChannelId.ToString());
                        }),
                    });
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        Woken(result, woken);
        return result;
    }

    /// <summary>Sends every failed or expired delivery of an event again. Needs <c>providers:write</c>; audited.</summary>
    public async Task<NotificationRetryResult> RetryEventAsync(NotificationEventId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "notification.event.retry", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new NotificationRetryResult(IssuanceChangeStatus.Denied);
        }

        List<NotificationChannelId> woken = [];
        var result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                if (!await context.NotificationEvents.AnyAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false))
                {
                    return (new NotificationRetryResult(IssuanceChangeStatus.NotFound), null);
                }

                var rows = await context.NotificationDeliveries.Where(d => d.EventId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
                var outcome = await ResetAsync(context, rows, woken, cancellationToken).ConfigureAwait(false);
                return outcome.Retried == 0
                    ? (outcome.Refusal!, null)
                    : (new NotificationRetryResult(IssuanceChangeStatus.Committed, outcome.Retried), IssuanceMapping.Entry(origin, "notification.event.retry", id, AuditOutcome.Success) with
                    {
                        Details = AuditDetails.Json(w => w.WriteNumber("deliveries", outcome.Retried)),
                    });
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        Woken(result, woken);
        return result;
    }

    /// <summary>
    /// Puts the failed or expired ones of <paramref name="rows"/> back to pending, due now with a fresh schedule, unless their channel is gone or paused (they
    /// would only be dropped again). A refusal says which of the two it was.
    /// </summary>
    private async Task<(int Retried, NotificationRetryResult? Refusal)> ResetAsync(ConfigDbContext context, IReadOnlyList<NotificationDeliveryRow> rows, List<NotificationChannelId> woken, CancellationToken cancellationToken)
    {
        var eligible = rows.Where(r => r.State is NotificationDeliveryState.Failed or NotificationDeliveryState.Expired).ToList();
        if (eligible.Count == 0)
        {
            return (0, Refused(NotificationErrorCodes.NotRetryable, "/state", "only a failed or expired delivery can be sent again"));
        }

        var channelIds = eligible.Select(r => r.ChannelId).Distinct().ToList();
        var live = (await context.NotificationChannels.AsNoTracking().Where(c => channelIds.Contains(c.Id) && c.Enabled).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var now = _time.GetUtcNow();
        var retried = 0;
        foreach (var row in eligible.Where(r => live.Contains(r.ChannelId)))
        {
            row.State = NotificationDeliveryState.Pending;
            row.Attempts = 0;
            row.NextAttemptAt = now;
            row.DigestId = null;
            woken.Add(row.ChannelId);
            retried++;
        }

        if (retried == 0)
        {
            return (0, Refused(NotificationErrorCodes.ChannelGone, "/channel", "the channel was deleted or is paused; resume or add it again first"));
        }

        try
        {
            _ = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Retention deleted a row between the read and the write: it is gone, as if it had never been retryable.
            woken.Clear();
            return (0, Refused(NotificationErrorCodes.NotRetryable, "/state", "that delivery was removed from the history while it was being sent again"));
        }

        return (retried, null);
    }

    private void Woken(NotificationRetryResult result, List<NotificationChannelId> channels)
    {
        if (result.Status != IssuanceChangeStatus.Committed)
        {
            return;
        }

        _stats.PendingAdded(result.Retried);
        foreach (var channel in channels.Distinct())
        {
            _wake.Wake(channel);
        }
    }

    private static NotificationRetryResult Refused(string code, string path, string message) =>
        new(IssuanceChangeStatus.Invalid, Errors: [new ConfigError(path, code, message)]);

    private static NotificationRetryResult Unavailable(string reason) =>
        new(IssuanceChangeStatus.Unavailable, Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so notification changes are refused: {reason}.")]);

    private static async Task<Dictionary<NotificationChannelId, (string Label, NotificationChannelKind Kind)>> ChannelsAsync(ConfigDbContext context, CancellationToken cancellationToken) =>
        (await context.NotificationChannels.AsNoTracking().Select(c => new { c.Id, c.Label, c.Kind }).ToListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(c => c.Id, c => (c.Label, c.Kind));

    private static NotificationHistoryEvent ToEvent(
        NotificationEventRow row,
        IEnumerable<NotificationDeliveryRow> deliveries,
        Dictionary<NotificationChannelId, (string Label, NotificationChannelKind Kind)> channels,
        NotificationChannelId? includeChannel) => new(
            row.Id,
            row.Kind,
            row.Title,
            row.Body,
            row.OccurredAt,
            row.CreatedAt,
            [.. deliveries.Where(d => includeChannel is not { } only || d.ChannelId == only).OrderBy(d => d.Id).Select(d => new NotificationDeliverySummary(
                d.Id,
                d.ChannelId,
                d.State,
                d.Attempts,
                channels.TryGetValue(d.ChannelId, out var channel) ? channel.Label : null,
                channels.TryGetValue(d.ChannelId, out var known) ? known.Kind : null,
                d.State == NotificationDeliveryState.Pending ? d.NextAttemptAt : null,
                d.LastAttemptAt,
                d.SentAt,
                d.LastError))],
            row.Subject,
            row.LinkPath);
}

/// <summary>The figures a channel's list row shows: what waits, and the outcomes of the last seven days.</summary>
internal static class NotificationFigures
{
    /// <remarks>
    /// "When it ended" is the delivery's <c>NextAttemptAt</c>, which every writer sets to the time of the final state, so the window is a range over
    /// the claim index (channel, state, time) and needs no join with the events.
    /// </remarks>
    public static async Task<Dictionary<NotificationChannelId, NotificationChannelFigures>> ReadAsync(ConfigDbContext context, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var queued = await context.NotificationDeliveries.AsNoTracking()
            .Where(d => d.State == NotificationDeliveryState.Pending)
            .GroupBy(d => d.ChannelId).Select(g => new { Channel = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ended = await context.NotificationDeliveries.AsNoTracking()
            .Where(d => (d.State == NotificationDeliveryState.Sent || d.State == NotificationDeliveryState.Failed || d.State == NotificationDeliveryState.Expired) && d.NextAttemptAt >= since)
            .GroupBy(d => new { d.ChannelId, d.State }).Select(g => new { g.Key.ChannelId, g.Key.State, Count = g.Count() })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<NotificationChannelId, NotificationChannelFigures> figures = [];
        foreach (var id in queued.Select(q => q.Channel).Concat(ended.Select(e => e.ChannelId)).Distinct())
        {
            figures[id] = new NotificationChannelFigures(
                queued.Where(q => q.Channel == id).Sum(q => q.Count),
                ended.Where(e => e.ChannelId == id && e.State == NotificationDeliveryState.Sent).Sum(e => e.Count),
                ended.Where(e => e.ChannelId == id && e.State != NotificationDeliveryState.Sent).Sum(e => e.Count));
        }

        return figures;
    }
}
