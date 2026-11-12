using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Data.Notifications;
using Paduraksa.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>Rows to what leaves the Data layer: the summary a surface shows (no vault ID) and the target a sender uses.</summary>
internal static class NotificationMapping
{
    public static NotificationChannelSummary ToSummary(NotificationChannelRow row) => new(
        row.Id,
        row.Kind,
        row.Label,
        row.Enabled,
        EventsOf(row),
        [.. row.MailTo],
        row.SecretId is not null,
        row.SigningSecretId is not null,
        row.CreatedAt,
        row.UpdatedAt,
        row.ServerUrl,
        row.Topic,
        row.SmtpHost,
        row.SmtpPort,
        row.SmtpSecurity,
        row.SmtpUsername,
        row.MailFrom,
        row.LastSentAt,
        row.LastErrorAt,
        row.LastError);

    public static ChannelTarget ToTarget(NotificationChannelRow row) => new(
        row.Id,
        row.Kind,
        row.ServerUrl,
        row.Topic,
        row.Kind == NotificationChannelKind.Email && row.SmtpHost is { } host && row.SmtpPort is { } port && row.MailFrom is { } from
            ? new SmtpTarget(host, port, row.SmtpSecurity ?? SmtpSecurity.StartTls, row.SmtpUsername, from, [.. row.MailTo])
            : null,
        row.SecretId,
        row.SigningSecretId);

    /// <summary>The events a row subscribes to; a name this build does not know (a downgrade) is skipped.</summary>
    public static IReadOnlyList<NotificationEventKind> EventsOf(NotificationChannelRow row)
    {
        List<NotificationEventKind> events = [];
        foreach (var name in row.Events)
        {
            try
            {
                events.Add(ModelNames.ParseNotificationEventKind(name));
            }
            catch (FormatException)
            {
                // Written by a newer build; not one this build can send.
            }
        }

        return events;
    }

    public static bool Subscribes(NotificationChannelRow row, NotificationEventKind kind) => row.Events.Contains(kind.ToName());
}
