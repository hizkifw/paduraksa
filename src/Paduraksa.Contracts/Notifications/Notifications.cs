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
    string? LastError = null);

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
    public const string TextTooLong = "notification.text.too_long";
    public const string EventsRequired = "notification.events.required";
    public const string EventUndefined = "notification.event.undefined";
    public const string UrlInvalid = "notification.url.invalid";
    public const string TopicInvalid = "notification.topic.invalid";
    public const string SecretRequired = "notification.secret.required";
    public const string SecretTooLong = "notification.secret.too_long";
    public const string SmtpInvalid = "notification.smtp.invalid";
    public const string SmtpInsecureLogin = "notification.smtp.insecure_login";
    public const string AddressInvalid = "notification.address.invalid";
    public const string KindFixed = "notification.kind.fixed";
    public const string FieldNotForKind = "notification.field.not_for_kind";
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
