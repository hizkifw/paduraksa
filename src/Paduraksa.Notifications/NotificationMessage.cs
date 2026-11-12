using Paduraksa.Core.Model;

namespace Paduraksa.Notifications;

/// <summary>How much a person should care; each service maps it to its own notion (ntfy priority, an embed colour).</summary>
public enum NotificationSeverity
{
    Info,

    /// <summary>Something that was wrong is fine again.</summary>
    Good,

    Warning,

    Critical,
}

/// <summary>
/// What is sent: one short, plain message. The text is composed by the caller; it can carry names a user chose (a host, an upstream), so
/// every format here escapes them for its own medium and none of them trusts the text to be harmless.
/// </summary>
/// <param name="Kind">The event it reports.</param>
/// <param name="Title">One line: what happened to which object.</param>
/// <param name="Body">What, since when, and what to do; may have several lines.</param>
/// <param name="Severity">How urgent.</param>
/// <param name="At">When it happened (UTC).</param>
/// <param name="Link">The dashboard page to look at, absolute; null when the instance's public admin URL is not known.</param>
/// <param name="Data">Machine-readable facts for a webhook's <c>data</c> object (the object's ID, a threshold), in order.</param>
/// <param name="EventId">
/// What a webhook receiver de-duplicates on: delivery is at-least-once, so a message may arrive twice, and both carry the same ID. The event's ID, or
/// the digest's when several events are one message; null for a message that is not in the outbox (the test button).
/// </param>
public sealed record NotificationMessage(
    NotificationEventKind Kind,
    string Title,
    string Body,
    NotificationSeverity Severity,
    DateTimeOffset At,
    string? Link = null,
    IReadOnlyList<KeyValuePair<string, string>>? Data = null,
    string? EventId = null);

/// <summary>An SMTP mailbox's connection and addresses.</summary>
/// <param name="Host">The server.</param>
/// <param name="Port">Its port.</param>
/// <param name="Security">How the connection is secured.</param>
/// <param name="Username">The login; null for a relay that takes none.</param>
/// <param name="From">The sender address.</param>
/// <param name="To">The recipients (at least one).</param>
public sealed record SmtpTarget(string Host, int Port, SmtpSecurity Security, string? Username, string From, IReadOnlyList<string> To);

/// <summary>A channel as a sender needs it: the non-secret settings and the vault IDs of its secrets. No value of a secret is held.</summary>
/// <param name="Id">The channel.</param>
/// <param name="Kind">Which service.</param>
/// <param name="ServerUrl">The base URL of an ntfy or Gotify server.</param>
/// <param name="Topic">The ntfy topic.</param>
/// <param name="Smtp">The mailbox settings of an email channel.</param>
/// <param name="Secret">The webhook URL, token or SMTP password, as a vault notification secret.</param>
/// <param name="SigningSecret">A webhook's body-signing key (an HMAC-SHA-256 key in the vault).</param>
public sealed record ChannelTarget(
    NotificationChannelId Id,
    NotificationChannelKind Kind,
    string? ServerUrl,
    string? Topic,
    SmtpTarget? Smtp,
    SecretId? Secret,
    SecretId? SigningSecret);

/// <summary>A delivery that did not work. The message is one short, fixed-wording line a person can act on; it never carries a URL, a token or what the server said.</summary>
public sealed class NotificationDeliveryException : Exception
{
    public NotificationDeliveryException(string message, bool transient)
        : base(message) => Transient = transient;

    public NotificationDeliveryException()
        : this("The notification could not be delivered.", transient: true)
    {
    }

    public NotificationDeliveryException(string message)
        : this(message, transient: true)
    {
    }

    public NotificationDeliveryException(string message, Exception innerException)
        : base(message, innerException) => Transient = true;

    /// <summary>A retry may succeed (the network, a 5xx, a rate limit); false for a refusal that only a change will fix (a 401, a missing secret).</summary>
    public bool Transient { get; }
}

/// <summary>Sends a <see cref="NotificationMessage"/> through one kind of service.</summary>
public interface INotificationSender
{
    NotificationChannelKind Kind { get; }

    /// <exception cref="NotificationDeliveryException">The message was not delivered.</exception>
    Task SendAsync(ChannelTarget channel, NotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>The sender for each <see cref="NotificationChannelKind"/>.</summary>
public interface INotificationSenders
{
    /// <summary>The sender of <paramref name="kind"/>, or null for a kind this build cannot send.</summary>
    INotificationSender? Find(NotificationChannelKind kind);
}
