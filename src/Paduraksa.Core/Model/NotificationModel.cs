using Paduraksa.Core.Ids;

namespace Paduraksa.Core.Model;

/// <summary>A notification channel: one place events are sent to (an ntfy topic, a Discord webhook, a mailbox). Its secrets are vault <see cref="SecretId"/>s.</summary>
[TypedId("ntf")]
public readonly partial record struct NotificationChannelId;

/// <summary>The services a notification can be sent through. Persisted by name (<see cref="ModelNames"/>); adding a kind needs a migration for the column's check.</summary>
public enum NotificationChannelKind
{
    /// <summary>An ntfy server and topic (ntfy.sh or self-hosted); the secret is an access token, optional.</summary>
    Ntfy = 1,

    /// <summary>A Gotify server; the secret is the application token.</summary>
    Gotify = 2,

    /// <summary>A Discord channel webhook; the secret is the webhook URL, which embeds its token.</summary>
    Discord = 3,

    /// <summary>A generic JSON webhook; the secret is the URL (it may embed one), and an optional signing key signs each body.</summary>
    Webhook = 4,

    /// <summary>A mailbox over SMTP; the secret is the SMTP password, optional.</summary>
    Email = 5,
}

/// <summary>How an SMTP connection is secured.</summary>
public enum SmtpSecurity
{
    /// <summary>Plain connection upgraded with STARTTLS (usually port 587); refused when the server does not offer it.</summary>
    StartTls = 1,

    /// <summary>TLS from the first byte (usually port 465).</summary>
    ImplicitTls = 2,

    /// <summary>No encryption: only for a relay on a trusted network.</summary>
    None = 3,
}

/// <summary>
/// The fixed catalogue of what a channel can subscribe to. Persisted by name (<see cref="ModelNames"/>), so a name is never reused;
/// <see cref="Test"/> is sent by hand and is not a subscription.
/// </summary>
public enum NotificationEventKind
{
    /// <summary>An upstream has no destination left to send traffic to.</summary>
    UpstreamDown = 1,

    /// <summary>An upstream that was down has a working destination again.</summary>
    UpstreamUp = 2,

    /// <summary>One destination of an upstream became unhealthy while others still work.</summary>
    DestinationDown = 3,

    /// <summary>One destination of an upstream is healthy again.</summary>
    DestinationUp = 4,

    /// <summary>A certificate crossed an expiry threshold (30, 14, 7 or 1 days).</summary>
    CertificateExpiring = 5,

    /// <summary>An ACME order failed after a working state: the first failure of a streak.</summary>
    AcmeRenewalFailed = 6,

    /// <summary>An ACME order succeeded after a failure.</summary>
    AcmeRenewalRecovered = 7,

    /// <summary>An account was locked out by failed sign-ins, or sign-ins from one address were throttled.</summary>
    SignInLockout = 8,

    /// <summary>An Owner or Admin signed in from an address not seen for that user in the last 30 days.</summary>
    SignInNewAddress = 9,

    /// <summary>The "send test" button.</summary>
    Test = 10,
}
