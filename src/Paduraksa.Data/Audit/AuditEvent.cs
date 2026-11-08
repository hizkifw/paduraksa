using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;
using Paduraksa.Data.Revisions;

namespace Paduraksa.Data.Audit;

/// <summary>
/// An audit-log entry (PLAN §5.2, §6.4). Append-only and hash-chained: <see cref="Hash"/> covers
/// <see cref="PrevHash"/> (the previous event's <see cref="Hash"/>, by ID) and every other column, so editing,
/// deleting or reordering rows breaks the chain. The Application layer computes and verifies the hashes.
/// </summary>
internal sealed class AuditEvent
{
    /// <summary>Int64Ordered, so the table is a rowid B-tree in time order; the chain runs in ID order.</summary>
    public AuditEventId Id { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public ActorKind ActorKind { get; set; }

    public UserId? ActorUserId { get; set; }

    public ApiTokenId? ActorTokenId { get; set; }

    public Channel Channel { get; set; }

    /// <summary>A stable dotted name such as <c>config.host.update</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary>The typed-ID string (<c>host_...</c>) of what was acted on, of any type, or null.</summary>
    public string? Target { get; set; }

    public AuditOutcome Outcome { get; set; }

    /// <summary>The client address as text, as resolved by the admin app; null for system and CLI actions.</summary>
    public string? ClientIp { get; set; }

    /// <summary>Free-form JSON details, stored exactly as given (the chain hashes this text).</summary>
    public string? Details { get; set; }

    /// <summary>The config revision this action committed, if any.</summary>
    public ConfigRevisionId? RevisionId { get; set; }

    /// <summary>The previous event's <see cref="Hash"/>, or 32 zero bytes for the first event. Unique (no forks).</summary>
    public byte[] PrevHash { get; set; } = [];

    /// <summary>SHA-256 over <see cref="PrevHash"/> and the canonical encoding of this event (32 bytes).</summary>
    public byte[] Hash { get; set; } = [];
}

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents", table =>
        {
            table.HasCheckConstraint("CK_AuditEvents_Hashes", "length(\"PrevHash\") = 32 AND length(\"Hash\") = 32");
            table.HasCheckConstraint("CK_AuditEvents_Actor", ActorColumns.CheckSql);
            table.HasCheckConstraint("CK_AuditEvents_Channel", NameChecks.Channel("Channel"));
            table.HasCheckConstraint("CK_AuditEvents_Outcome", NameChecks.AuditOutcome("Outcome"));
        });

        // Every event names its predecessor, and no two may name the same one: if two writers ever race, the second
        // append fails here instead of silently forking the chain.
        builder.HasIndex(e => e.PrevHash).IsUnique();
        builder.HasIndex(e => e.Timestamp);
        builder.HasIndex(e => e.Target);

        builder.HasOne<ConfigRevision>().WithMany().HasForeignKey(e => e.RevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}
