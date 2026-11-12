using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.NotificationsArea;

/// <summary>
/// What the notification forms do, as the signed-in user: the same service calls as the API's. Null means the session ended under the page
/// (it is on its way to the sign-in page).
/// </summary>
public sealed class NotificationActions(NotificationService channels, DashboardCaller caller)
{
    public async Task<NotificationChannelResult?> CreateAsync(NotificationChannelBody body, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await channels.CreateAsync(body, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationChannelResult?> UpdateAsync(NotificationChannelId id, NotificationChannelBody body, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await channels.UpdateAsync(id, body, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationChannelResult?> DeleteAsync(NotificationChannelId id, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await channels.DeleteAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NotificationTestResult?> TestAsync(NotificationChannelId id, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await channels.TestAsync(id, origin, cancellationToken).ConfigureAwait(false);
    }
}
