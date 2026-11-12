using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Notifications;

/// <summary>
/// A notification channel, to add or to replace. Which fields apply depends on <see cref="Kind"/>: ntfy needs <c>ServerUrl</c> and
/// <c>Topic</c>; Gotify <c>ServerUrl</c>; Discord and webhook only their secret (the URL); email <c>SmtpHost</c>, <c>SmtpPort</c>,
/// <c>SmtpSecurity</c>, <c>MailFrom</c> and <c>MailTo</c> (and <c>SmtpUsername</c> with a password as the secret). The secrets go into the vault and are never returned.
/// </summary>
/// <param name="Kind">Which service. Fixed once the channel exists.</param>
/// <param name="Label">A display name (required).</param>
/// <param name="Events">What the channel is sent; at least one.</param>
/// <param name="Enabled">False pauses the channel without losing its settings.</param>
/// <param name="ServerUrl">ntfy and Gotify: the server's base URL (http or https).</param>
/// <param name="Topic">ntfy: the topic to publish to.</param>
/// <param name="SmtpHost">Email: the SMTP server.</param>
/// <param name="SmtpPort">Email: its port (587 for STARTTLS, 465 for implicit TLS).</param>
/// <param name="SmtpSecurity">Email: how the connection is secured; StartTls when omitted.</param>
/// <param name="SmtpUsername">Email: the login, when the server asks for one; needs the password as <c>Secret</c>.</param>
/// <param name="MailFrom">Email: the sender address.</param>
/// <param name="MailTo">Email: the recipients (at least one).</param>
/// <param name="Secret">The webhook URL (Discord, webhook), the application token (Gotify), the access token (ntfy, optional) or the SMTP password. On a replacement, null keeps the stored one.</param>
/// <param name="SigningSecret">Webhook: the key that signs each body (HMAC-SHA-256); null on a replacement keeps the stored one, an empty string removes it.</param>
public sealed record NotificationChannelBody(
    NotificationChannelKind Kind,
    string Label,
    IReadOnlyList<NotificationEventKind> Events,
    bool Enabled = true,
    string? ServerUrl = null,
    string? Topic = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    SmtpSecurity? SmtpSecurity = null,
    string? SmtpUsername = null,
    string? MailFrom = null,
    IReadOnlyList<string>? MailTo = null,
    string? Secret = null,
    string? SigningSecret = null)
{
    public override string ToString() => $"{nameof(NotificationChannelBody)} {{ Kind = {Kind}, Label = {Label}, Secret = ***, SigningSecret = *** }}";
}

/// <summary>
/// A channel as listed: its settings and how delivery is going. No secret and no vault reference, only whether one is set.
/// </summary>
/// <param name="Id">The channel.</param>
/// <param name="Kind">Which service.</param>
/// <param name="Label">The display name.</param>
/// <param name="Enabled">False when the channel is paused.</param>
/// <param name="Events">What the channel is sent.</param>
/// <param name="ServerUrl">ntfy and Gotify: the server's base URL.</param>
/// <param name="Topic">ntfy: the topic.</param>
/// <param name="SmtpHost">Email: the SMTP server.</param>
/// <param name="SmtpPort">Email: its port.</param>
/// <param name="SmtpSecurity">Email: how the connection is secured.</param>
/// <param name="SmtpUsername">Email: the login, if any.</param>
/// <param name="MailFrom">Email: the sender address.</param>
/// <param name="MailTo">Email: the recipients.</param>
/// <param name="HasSecret">A secret is stored.</param>
/// <param name="HasSigningSecret">A webhook signing key is stored.</param>
/// <param name="CreatedAt">When the channel was added.</param>
/// <param name="UpdatedAt">When its settings or secret last changed.</param>
/// <param name="LastSentAt">The last time a notification was delivered.</param>
/// <param name="LastErrorAt">The last time a delivery failed (after its retries).</param>
/// <param name="LastError">What went wrong then, in a sentence; cleared by the next delivery.</param>
/// <param name="Delivery">How its deliveries are going; null in the answer to a change, which does not read them.</param>
public sealed record NotificationChannelSummary(
    NotificationChannelId Id,
    NotificationChannelKind Kind,
    string Label,
    bool Enabled,
    IReadOnlyList<NotificationEventKind> Events,
    IReadOnlyList<string> MailTo,
    bool HasSecret,
    bool HasSigningSecret,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ServerUrl = null,
    string? Topic = null,
    string? SmtpHost = null,
    int? SmtpPort = null,
    SmtpSecurity? SmtpSecurity = null,
    string? SmtpUsername = null,
    string? MailFrom = null,
    DateTimeOffset? LastSentAt = null,
    DateTimeOffset? LastErrorAt = null,
    string? LastError = null,
    NotificationChannelFigures? Delivery = null);

/// <summary>How a channel's deliveries are going: what waits now, and the outcomes of the last seven days (by when their event happened).</summary>
/// <param name="Queued">Deliveries waiting to be sent or retried.</param>
/// <param name="Sent">Deliveries the service accepted.</param>
/// <param name="Failed">Deliveries given up on, or not sent because their event was too old when the process started. The success rate is <c>sent / (sent + failed)</c>; dropped ones (the channel was deleted or paused) are in neither.</param>
public sealed record NotificationChannelFigures(int Queued, int Sent, int Failed);

/// <summary>The outcome of a channel change.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Channel">The channel after the change; null for a delete, a refusal or not found.</param>
/// <param name="Errors">Why it was refused.</param>
public sealed record NotificationChannelResult(
    IssuanceChangeStatus Status,
    NotificationChannelSummary? Channel = null,
    IReadOnlyList<ConfigError>? Errors = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>The answer of "send test": whether the message was delivered, now, without retries.</summary>
/// <param name="Delivered">The service accepted the message.</param>
/// <param name="Error">What went wrong, in a sentence a person can act on; never a URL or a token.</param>
public sealed record NotificationTestOutcome(bool Delivered, string? Error = null);

/// <summary>The service's answer to a test, with the cases the API answers with a status of their own.</summary>
public sealed record NotificationTestResult(bool Found, bool Delivered, string? Error = null)
{
    /// <summary>The origin may not send tests (it lacks <c>providers:write</c>); nothing was sent.</summary>
    public bool Denied { get; init; }
}

/// <summary>The error codes of a refused channel request.</summary>
public static class NotificationErrorCodes
{
    public const string KindUndefined = "notification.kind.undefined";
    public const string LabelRequired = "notification.label.required";
    public const string TextTooLong = "notification.text.tooLong";
    public const string EventsRequired = "notification.events.required";
    public const string EventUndefined = "notification.event.undefined";
    public const string UrlInvalid = "notification.url.invalid";
    public const string TopicInvalid = "notification.topic.invalid";
    public const string SecretRequired = "notification.secret.required";
    public const string SecretTooLong = "notification.secret.tooLong";
    public const string SmtpInvalid = "notification.smtp.invalid";
    public const string SmtpInsecureLogin = "notification.smtp.insecureLogin";
    public const string AddressInvalid = "notification.address.invalid";
    public const string KindFixed = "notification.kind.fixed";
    public const string FieldNotForKind = "notification.field.notForKind";
    public const string NotRetryable = "notification.delivery.notRetryable";
    public const string ChannelGone = "notification.delivery.channelGone";
}

/// <summary>Whether and how an upstream is serving, as the live proxy sees it.</summary>
[JsonConverter(typeof(ConfigEnumConverter<UpstreamHealthState>))]
public enum UpstreamHealthState
{
    /// <summary>No health check is configured, so there is nothing to say.</summary>
    Unchecked,

    /// <summary>Checks are configured and have not decided yet.</summary>
    Unknown,

    Healthy,

    /// <summary>At least one destination is out and at least one still takes traffic.</summary>
    Degraded,

    /// <summary>No destination takes traffic.</summary>
    Down,
}

/// <summary>One destination's health.</summary>
[JsonConverter(typeof(ConfigEnumConverter<DestinationHealthState>))]
public enum DestinationHealthState
{
    Unknown,
    Healthy,
    Unhealthy,
}

/// <summary>A destination as the proxy sees it.</summary>
/// <param name="Id">The destination.</param>
/// <param name="Address">Its configured address.</param>
/// <param name="State">YARP's combined verdict, after the flap guard.</param>
/// <param name="Reason">Why it is unhealthy ("active probe: HTTP 503", "passive check: too many failed requests"); the last probe's result when healthy.</param>
/// <param name="Since">When it last changed state, if it has since the process started.</param>
/// <param name="Flapping">It changed too often and its changes are not reported until it settles.</param>
public sealed record DestinationHealthView(DestinationId Id, string Address, DestinationHealthState State, string? Reason = null, DateTimeOffset? Since = null, bool Flapping = false);

/// <summary>The most recent state change of an upstream or one of its destinations.</summary>
/// <param name="At">When the change began.</param>
/// <param name="Summary">What changed ("Down", "Back up", "10.0.0.5:8096 down").</param>
/// <param name="Reason">Why, where YARP says.</param>
public sealed record HealthChangeView(DateTimeOffset At, string Summary, string? Reason = null);

/// <summary>An upstream's live health.</summary>
/// <param name="Id">The upstream.</param>
/// <param name="State">The whole upstream's state.</param>
/// <param name="Destinations">Each destination the proxy knows.</param>
/// <param name="LastChange">The latest change of the upstream or any of its destinations since the process started; null if none.</param>
public sealed record UpstreamHealthView(UpstreamId Id, UpstreamHealthState State, IReadOnlyList<DestinationHealthView> Destinations, HealthChangeView? LastChange = null);

/// <summary>One event's delivery to one channel, as the history shows it.</summary>
/// <param name="Id">The delivery.</param>
/// <param name="ChannelId">The channel it went (or goes) to.</param>
/// <param name="State">Pending (waiting, or between retries), sent, failed (given up), expired (the event was too old when the process started) or dropped (the channel was deleted or paused).</param>
/// <param name="Attempts">How many times a send was tried.</param>
/// <param name="ChannelLabel">The channel's display name; absent when the channel has been deleted (the history stays).</param>
/// <param name="ChannelKind">The channel's service; absent when it has been deleted.</param>
/// <param name="NextAttemptAt">When the next try is due; only for a pending delivery.</param>
/// <param name="LastAttemptAt">When it was last tried or dropped.</param>
/// <param name="SentAt">When the service accepted it.</param>
/// <param name="LastError">What went wrong the last time, in the fixed, secret-free wording the senders use.</param>
public sealed record NotificationDeliverySummary(
    NotificationDeliveryId Id,
    NotificationChannelId ChannelId,
    NotificationDeliveryState State,
    int Attempts,
    string? ChannelLabel = null,
    NotificationChannelKind? ChannelKind = null,
    DateTimeOffset? NextAttemptAt = null,
    DateTimeOffset? LastAttemptAt = null,
    DateTimeOffset? SentAt = null,
    string? LastError = null);

/// <summary>An event that was published, with what happened to it on each channel. An event nothing was subscribed to has no deliveries.</summary>
/// <param name="Id">The event; webhooks receive it as <c>eventId</c> (the digest's own ID when several events went out as one message).</param>
/// <param name="Kind">Which event of the catalogue.</param>
/// <param name="Title">One line: what happened to which object.</param>
/// <param name="Body">What, since when, and what to do.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="CreatedAt">When it was published.</param>
/// <param name="Deliveries">Its deliveries, in the order the channels were addressed.</param>
/// <param name="Subject">The object it is about (an upstream, destination, certificate or user ID); absent when it is about nothing in particular.</param>
/// <param name="LinkPath">The dashboard page that shows it (<c>/upstreams</c>).</param>
public sealed record NotificationHistoryEvent(
    NotificationEventId Id,
    NotificationEventKind Kind,
    string Title,
    string Body,
    DateTimeOffset OccurredAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<NotificationDeliverySummary> Deliveries,
    string? Subject = null,
    string? LinkPath = null);

/// <summary>What the history is filtered by; every part is optional and all must match.</summary>
/// <param name="Channel">Only events with a delivery to this channel.</param>
/// <param name="Kind">Only events of this kind.</param>
/// <param name="Outcome">Only events with a delivery in this state (with <paramref name="Channel"/>, to that channel).</param>
/// <param name="From">Only events published at or after this time.</param>
/// <param name="To">Only events published before this time.</param>
public sealed record NotificationHistoryFilter(
    NotificationChannelId? Channel = null,
    NotificationEventKind? Kind = null,
    NotificationDeliveryState? Outcome = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

/// <summary>A page of the history, newest first.</summary>
/// <param name="Items">The events.</param>
/// <param name="Next">The last event of the page when older ones remain; pass it as the cursor.</param>
public sealed record NotificationHistoryPage(IReadOnlyList<NotificationHistoryEvent> Items, NotificationEventId? Next = null);

/// <summary>The outcome of sending failed or expired deliveries again.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Retried">How many deliveries were put back to pending.</param>
/// <param name="Errors">Why it was refused.</param>
public sealed record NotificationRetryResult(IssuanceChangeStatus Status, int Retried = 0, IReadOnlyList<ConfigError>? Errors = null)
{
    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];
}

/// <summary>The answer of a retry: how many deliveries are pending again; each is sent within seconds.</summary>
/// <param name="Retried">The deliveries put back to pending.</param>
public sealed record NotificationRetryOutcome(int Retried);
