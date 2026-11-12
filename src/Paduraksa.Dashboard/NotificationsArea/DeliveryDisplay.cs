using System.Globalization;

using MudBlazor;

using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Components;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.NotificationsArea;

/// <summary>How the history words a delivery: a short label for its chip, the longer detail as the tooltip, a tone and an icon.</summary>
public static class DeliveryDisplay
{
    /// <summary>The outcome filter: any, then each state. The tones are the chips' own.</summary>
    public static IReadOnlyList<SegmentOption<NotificationDeliveryState?>> OutcomeOptions { get; } =
    [
        new(null, "Any outcome", Key: "any"),
        new(NotificationDeliveryState.Sent, "Sent", Tone: "success"),
        new(NotificationDeliveryState.Failed, "Failed", Tone: "error"),
        new(NotificationDeliveryState.Pending, "Retrying", Tooltip: "Waiting to be sent or between retries", Tone: "info"),
        new(NotificationDeliveryState.Expired, "Expired", Tooltip: "Not sent: the event was too old when Paduraksa started", Tone: "warning"),
        new(NotificationDeliveryState.Dropped, "Dropped", Tooltip: "Not sent: the channel was deleted or paused"),
    ];

    /// <summary>The channel's name, or what is known of a deleted one.</summary>
    public static string Channel(NotificationDeliverySummary delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return delivery.ChannelLabel ?? (delivery.ChannelKind is { } kind ? $"Deleted {NotificationDisplay.KindLabel(kind)} channel" : "Deleted channel");
    }

    public static Tone ToneOf(NotificationDeliveryState state) => state switch
    {
        NotificationDeliveryState.Sent => Tone.Success,
        NotificationDeliveryState.Failed => Tone.Error,
        NotificationDeliveryState.Pending => Tone.Info,
        NotificationDeliveryState.Expired => Tone.Warning,
        _ => Tone.Default,
    };

    public static string IconOf(NotificationDeliveryState state) => state switch
    {
        NotificationDeliveryState.Sent => Icons.Material.Rounded.Check,
        NotificationDeliveryState.Failed => Icons.Material.Rounded.ErrorOutline,
        NotificationDeliveryState.Pending => Icons.Material.Rounded.Schedule,
        NotificationDeliveryState.Expired => Icons.Material.Rounded.HourglassBottom,
        _ => Icons.Material.Rounded.Block,
    };

    /// <summary>What the chip says, after the channel's name (when it has one): the outcome, and for a delivery that is waiting, when it is tried next.</summary>
    public static string Label(NotificationDeliverySummary delivery, bool withChannel)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var outcome = delivery.State switch
        {
            NotificationDeliveryState.Sent => null,
            NotificationDeliveryState.Failed => "failed",
            NotificationDeliveryState.Expired => "expired",
            NotificationDeliveryState.Dropped => "dropped",
            _ => delivery.Attempts == 0 ? "queued" : delivery.NextAttemptAt is { } next ? $"retrying, next {Time(next)}" : "retrying",
        };
        var channel = withChannel ? Channel(delivery) : null;
        return (channel, outcome) switch
        {
            (null, null) => "Sent",
            (null, _) => char.ToUpperInvariant(outcome![0]) + outcome[1..],
            (_, null) => channel,
            _ => $"{channel} {outcome}",
        };
    }

    /// <summary>The tooltip: the error a failure ended with, when it was sent, how many times it was tried.</summary>
    public static string Detail(NotificationDeliverySummary delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        var tries = delivery.Attempts == 1 ? "1 attempt" : $"{delivery.Attempts.ToString(CultureInfo.InvariantCulture)} attempts";
        return delivery.State switch
        {
            NotificationDeliveryState.Sent => delivery.SentAt is { } at ? $"Sent {RevisionText.Exact(at)} ({tries})" : "Sent",
            NotificationDeliveryState.Pending when delivery.Attempts == 0 => "Waiting for its turn",
            NotificationDeliveryState.Pending => $"{delivery.LastError ?? "The last try failed"} ({tries}; next {delivery.NextAttemptAt.Map(RevisionText.Exact)})",
            _ => delivery.LastError is { Length: > 0 } error ? (delivery.Attempts > 0 ? $"{error} ({tries})" : error) : "Not sent",
        };
    }

    /// <summary>Whether a person can send it again: it was given up or expired, and its channel still exists.</summary>
    public static bool CanRetry(NotificationDeliverySummary delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        return delivery.State is NotificationDeliveryState.Failed or NotificationDeliveryState.Expired && delivery.ChannelLabel is not null;
    }

    public static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Map(this DateTimeOffset? value, Func<DateTimeOffset, string> format) => value is { } at ? format(at) : "soon";

    /// <summary>The event's kind as a person says it (the catalogue's wording, shortened).</summary>
    public static string KindLabel(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.UpstreamDown => "Upstream down",
        NotificationEventKind.UpstreamUp => "Upstream up",
        NotificationEventKind.DestinationDown => "Destination down",
        NotificationEventKind.DestinationUp => "Destination up",
        NotificationEventKind.CertificateExpiring => "Certificate expiring",
        NotificationEventKind.AcmeRenewalFailed => "Issuance failed",
        NotificationEventKind.AcmeRenewalRecovered => "Issuance recovered",
        NotificationEventKind.SignInLockout => "Account locked",
        NotificationEventKind.SignInNewAddress => "New sign-in address",
        _ => "Test",
    };

    public static Tone KindTone(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.UpstreamDown or NotificationEventKind.DestinationDown or NotificationEventKind.AcmeRenewalFailed => Tone.Error,
        NotificationEventKind.CertificateExpiring or NotificationEventKind.SignInLockout => Tone.Warning,
        NotificationEventKind.UpstreamUp or NotificationEventKind.DestinationUp or NotificationEventKind.AcmeRenewalRecovered => Tone.Success,
        _ => Tone.Default,
    };
}
