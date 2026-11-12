using Paduraksa.Core.Model;
using Paduraksa.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// An event that channels may be subscribed to: the composed message and where it happened, before it is addressed to any channel.
/// Composed by the part that noticed; the dispatcher adds the dashboard link and sends it to each subscribed channel.
/// </summary>
/// <param name="Kind">Which event of the catalogue.</param>
/// <param name="Title">One line: what happened to which object.</param>
/// <param name="Body">What, since when, and what to do.</param>
/// <param name="Severity">How urgent.</param>
/// <param name="At">When it happened.</param>
/// <param name="LinkPath">The dashboard page that shows it (<c>/upstreams</c>); joined to the public admin URL when that is known.</param>
/// <param name="Data">Machine-readable facts for a webhook.</param>
public sealed record NotificationEvent(
    NotificationEventKind Kind,
    string Title,
    string Body,
    NotificationSeverity Severity,
    DateTimeOffset At,
    string? LinkPath = null,
    IReadOnlyList<KeyValuePair<string, string>>? Data = null);

/// <summary>Where the parts of the product that notice something hand it over; never blocks and never throws.</summary>
public interface INotificationEvents
{
    /// <summary>Queues <paramref name="notification"/> for every enabled channel subscribed to its kind. Drops the oldest queued event when the queue is full.</summary>
    void Publish(NotificationEvent notification);

    /// <summary>Whether at least one enabled channel is subscribed to <paramref name="kind"/>.</summary>
    Task<bool> HasSubscribersAsync(NotificationEventKind kind, CancellationToken cancellationToken);
}

/// <summary>The knobs of the notification system (<c>Paduraksa:Notifications</c>).</summary>
public sealed class NotificationOptions
{
    /// <summary>The address the dashboard is reached at (<c>https://admin.example.com</c>), for the links in a message; null sends none.</summary>
    public Uri? AdminUrl { get; init; }

    /// <summary>Events waiting for the dispatcher before the oldest is dropped (and counted).</summary>
    public int EventCapacity { get; init; } = 1000;

    /// <summary>Messages waiting for one channel before its oldest is dropped (and counted).</summary>
    public int ChannelCapacity { get; init; } = 100;

    /// <summary>How long a channel waits after the first message for more of the same kind, so a storm becomes one digest.</summary>
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The wait before each retry of a transient failure; the message is given up after the last.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } = [TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(16)];

    /// <summary>A health state must hold this long before it is reported.</summary>
    public TimeSpan HealthHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the watcher reads YARP's state.</summary>
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The window in which <see cref="FlapThreshold"/> changes make an object "flapping".</summary>
    public TimeSpan FlapWindow { get; init; } = TimeSpan.FromMinutes(10);

    public int FlapThreshold { get; init; } = 4;

    /// <summary>A flapping object is reported again once it has been still for this long.</summary>
    public TimeSpan FlapSettle { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>The expiry thresholds in days, largest first.</summary>
    public IReadOnlyList<int> ExpiryThresholdDays { get; init; } = [30, 14, 7, 1];

    public TimeSpan ExpiryCheckInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long an address stays "known" for a user after they last signed in from it.</summary>
    /// <summary>How far back a user's earlier sign-in counts as "has signed in before" (so the very first sign-in is not an anomaly); bounded so the check seeks an index, not the audit table.</summary>
    public TimeSpan SignedInBeforeWindow { get; init; } = TimeSpan.FromDays(365);

    public TimeSpan KnownAddressWindow { get; init; } = TimeSpan.FromDays(30);
}
