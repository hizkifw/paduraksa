using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Notifications;

/// <summary>
/// Something worth telling a person, recorded once (docs/design/observability.md "Durable delivery and history (notify-outbox)"): the notification history's backbone and
/// everything a sender needs to build the message again after a restart. Written by the flusher together with its deliveries, so an event with no
/// subscribed channel is still here, with none. Int64Ordered ID: admin-only, append-only, and creation order is the history's order.
/// </summary>
internal sealed class NotificationEventRow
{
    public NotificationEventId Id { get; set; }

    public NotificationEventKind Kind { get; set; }

    /// <summary>The object it is about (an upstream, destination, certificate or user ID) as text; null for an event about nothing in particular.</summary>
    public string? Subject { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>The value of <c>NotificationSeverity</c> (a Notifications-layer enum this layer cannot name).</summary>
    public int Severity { get; set; }

    /// <summary>The dashboard page that shows it (<c>/upstreams</c>); joined to the public admin URL when a message is built.</summary>
    public string? LinkPath { get; set; }

    /// <summary>A webhook's machine-readable facts as a JSON array of <c>[key, value]</c> pairs; null for none.</summary>
    public string? Data { get; set; }

    /// <summary>When the thing happened (a health change is reported a debounce after it began).</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>When it was published: the age that expiry and retention count from.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One event's delivery to one channel: the outbox row the channel's worker claims. <b>No foreign key to the channel</b>, so history outlives a
/// deleted channel (pending rows are marked <see cref="NotificationDeliveryState.Dropped"/> when it goes). The error text is the senders' fixed,
/// secret-free wording.
/// </summary>
internal sealed class NotificationDeliveryRow
{
    public NotificationDeliveryId Id { get; set; }

    public NotificationEventId EventId { get; set; }

    public NotificationChannelId ChannelId { get; set; }

    public NotificationDeliveryState State { get; set; } = NotificationDeliveryState.Pending;

    /// <summary>How many times a send was tried; the retry schedule is read off it.</summary>
    public int Attempts { get; set; }

    /// <summary>When the worker may take it: now for a new row, later after a transient failure. A row in a final state keeps the time it ended here, which is what the channel's seven-day figures range over.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Set when the row is first taken as part of a digest, and kept through its retries, so a resend after a crash or a failure is the same message.</summary>
    public NotificationDigestId? DigestId { get; set; }
}

internal sealed class NotificationEventRowConfiguration : IEntityTypeConfiguration<NotificationEventRow>
{
    public void Configure(EntityTypeBuilder<NotificationEventRow> builder)
    {
        builder.ToTable("NotificationEvents", table => table.HasCheckConstraint("CK_NotificationEvents_Kind", NameChecks.NotificationEventKind("Kind")));
        builder.Property(e => e.Subject).HasMaxLength(64);

        // The history's time range and retention walk events by age.
        builder.HasIndex(e => e.CreatedAt);
    }
}

internal sealed class NotificationDeliveryRowConfiguration : IEntityTypeConfiguration<NotificationDeliveryRow>
{
    public void Configure(EntityTypeBuilder<NotificationDeliveryRow> builder)
    {
        builder.ToTable("NotificationDeliveries", table => table.HasCheckConstraint("CK_NotificationDeliveries_State", NameChecks.NotificationDeliveryState("State")));
        builder.HasOne<NotificationEventRow>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);

        // The worker's claim: one channel's pending rows that are due, in due order; and "is anything of this channel waiting for a retry" is the
        // same index with the time on the other side of now.
        builder.HasIndex(d => new { d.ChannelId, d.State, d.NextAttemptAt });
        // An event is addressed to a channel once. It also serves the event's deliveries, the history's channel and outcome filters (probed per
        // event) and retention's "has this event a row still pending".
        builder.HasIndex(d => new { d.EventId, d.ChannelId }).IsUnique();
        // A channel's recent deliveries, newest first (its drawer, and the history filtered to it).
        builder.HasIndex(d => new { d.ChannelId, d.Id });
    }
}
