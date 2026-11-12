using System.Globalization;

using MudBlazor;

using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.NotificationsArea;

/// <summary>A group of events in the channel form.</summary>
/// <param name="Title">The group's name.</param>
/// <param name="Events">Its events.</param>
public sealed record EventGroup(string Title, IReadOnlyList<NotificationEventKind> Events);

/// <summary>How the notification pages and forms name channel kinds and events.</summary>
public static class NotificationDisplay
{
    public static IReadOnlyList<NotificationChannelKind> Kinds { get; } =
        [NotificationChannelKind.Ntfy, NotificationChannelKind.Gotify, NotificationChannelKind.Discord, NotificationChannelKind.Webhook, NotificationChannelKind.Email];

    public static IReadOnlyList<EventGroup> Groups { get; } =
    [
        new("Upstreams", [NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp, NotificationEventKind.DestinationDown, NotificationEventKind.DestinationUp]),
        new("Certificates", [NotificationEventKind.CertificateExpiring, NotificationEventKind.AcmeRenewalFailed, NotificationEventKind.AcmeRenewalRecovered]),
        new("Sign-in", [NotificationEventKind.SignInLockout, NotificationEventKind.SignInNewAddress]),
    ];

    /// <summary>What a new channel is subscribed to: everything but the per-destination events, which a one-destination upstream says twice.</summary>
    public static IReadOnlyList<NotificationEventKind> DefaultEvents { get; } =
    [
        NotificationEventKind.UpstreamDown, NotificationEventKind.UpstreamUp, NotificationEventKind.CertificateExpiring, NotificationEventKind.AcmeRenewalFailed,
        NotificationEventKind.AcmeRenewalRecovered, NotificationEventKind.SignInLockout, NotificationEventKind.SignInNewAddress,
    ];

    public static string KindLabel(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Ntfy => "ntfy",
        NotificationChannelKind.Gotify => "Gotify",
        NotificationChannelKind.Discord => "Discord",
        NotificationChannelKind.Webhook => "Webhook",
        _ => "Email",
    };

    public static string KindIcon(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Ntfy => Icons.Material.Outlined.NotificationsActive,
        NotificationChannelKind.Gotify => Icons.Material.Outlined.Notifications,
        NotificationChannelKind.Discord => Icons.Material.Outlined.Forum,
        NotificationChannelKind.Webhook => Icons.Material.Outlined.Webhook,
        _ => Icons.Material.Outlined.Email,
    };

    public static string EventLabel(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.UpstreamDown => "An upstream goes down",
        NotificationEventKind.UpstreamUp => "An upstream is back up",
        NotificationEventKind.DestinationDown => "One destination goes down",
        NotificationEventKind.DestinationUp => "A destination is back up",
        NotificationEventKind.CertificateExpiring => "A certificate is about to expire (30, 14, 7 and 1 days)",
        NotificationEventKind.AcmeRenewalFailed => "Issuing or renewing a certificate fails",
        NotificationEventKind.AcmeRenewalRecovered => "A failed issuance recovers",
        NotificationEventKind.SignInLockout => "An account is locked by failed sign-ins",
        NotificationEventKind.SignInNewAddress => "An Owner or Admin signs in from a new address",
        _ => "A test",
    };

    /// <summary>The label of the secret field.</summary>
    public static string SecretLabel(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Ntfy => "Access token (optional)",
        NotificationChannelKind.Gotify => "Application token",
        NotificationChannelKind.Discord => "Webhook URL",
        NotificationChannelKind.Webhook => "Webhook URL",
        _ => "SMTP password",
    };

    /// <summary>What the secret is for, when that helps (a URL that embeds a token; the one thing that needs an https URL).</summary>
    public static string? SecretHint(NotificationChannelKind kind) => kind switch
    {
        NotificationChannelKind.Ntfy => "Only for a topic that needs a login. Sent as a bearer token.",
        NotificationChannelKind.Gotify => "From Apps in Gotify. Sent in the X-Gotify-Key header.",
        NotificationChannelKind.Discord => "Channel settings, Integrations, Webhooks. The URL embeds its token.",
        NotificationChannelKind.Webhook => "Receives a JSON POST. The URL may embed a secret.",
        _ => null,
    };

    /// <summary>One line under a channel's name: the service and where it goes.</summary>
    public static string Target(NotificationChannelSummary channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.Kind switch
        {
            NotificationChannelKind.Ntfy => $"{Host(channel.ServerUrl)}/{channel.Topic}",
            NotificationChannelKind.Gotify => Host(channel.ServerUrl),
            NotificationChannelKind.Email => $"{string.Join(", ", channel.MailTo.Take(2))}{(channel.MailTo.Count > 2 ? $" +{(channel.MailTo.Count - 2).ToString(CultureInfo.InvariantCulture)}" : string.Empty)} via {channel.SmtpHost}",
            NotificationChannelKind.Discord => "Discord webhook",
            _ => channel.HasSigningSecret ? "Signed JSON webhook" : "JSON webhook",
        };
    }

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url ?? string.Empty;

    public static string EventCount(int count) => count == 1 ? "1 event" : $"{count.ToString(CultureInfo.InvariantCulture)} events";
}
