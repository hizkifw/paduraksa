using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.NotificationsArea;

/// <summary>
/// What the notification history page and a channel's drawer ask of the Application layer: pages of the history, a channel's latest deliveries, and
/// sending a failed one again as the signed-in user. A seam so the page can be tested without a database; <see cref="DashboardNotificationHistory"/>
/// is the real one. Reads are not authorized here, as on the API: the page checks <c>providers:read</c> first.
/// </summary>
public interface IDashboardNotificationHistory
{
    Task<NotificationHistoryPage> QueryAsync(NotificationHistoryFilter filter, int limit, NotificationEventId? after, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationHistoryEvent>> RecentAsync(NotificationChannelId channel, int limit, CancellationToken cancellationToken = default);

    /// <summary>The channels, with their delivery figures, to filter by.</summary>
    Task<IReadOnlyList<NotificationChannelSummary>> ChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Null means the session ended under the page (it is on its way to the sign-in page).</summary>
    Task<NotificationRetryResult?> RetryDeliveryAsync(NotificationDeliveryId id, CancellationToken cancellationToken = default);

    /// <summary>Null means the session ended under the page.</summary>
    Task<NotificationRetryResult?> RetryEventAsync(NotificationEventId id, CancellationToken cancellationToken = default);
}

/// <summary><see cref="NotificationHistoryService"/> behind <see cref="IDashboardNotificationHistory"/>.</summary>
internal sealed class DashboardNotificationHistory(NotificationHistoryService history, NotificationService channels, DashboardCaller caller) : IDashboardNotificationHistory
{
    public Task<NotificationHistoryPage> QueryAsync(NotificationHistoryFilter filter, int limit, NotificationEventId? after, CancellationToken cancellationToken = default) =>
        history.QueryAsync(filter, limit, after, cancellationToken);

    public Task<IReadOnlyList<NotificationHistoryEvent>> RecentAsync(NotificationChannelId channel, int limit, CancellationToken cancellationToken = default) =>
        history.RecentAsync(channel, limit, cancellationToken);

    public Task<IReadOnlyList<NotificationChannelSummary>> ChannelsAsync(CancellationToken cancellationToken = default) => channels.ListAsync(cancellationToken);

    public async Task<NotificationRetryResult?> RetryDeliveryAsync(NotificationDeliveryId id, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await history.RetryDeliveryAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationRetryResult?> RetryEventAsync(NotificationEventId id, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await history.RetryEventAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }
}
