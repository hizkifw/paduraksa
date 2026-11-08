using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Conversion;

namespace Paduraksa.Data.Revisions;

/// <summary>
/// A committed config revision (PLAN §5.2, §6.4): the whole config document as canonical JSON, Brotli-compressed.
/// Append-only; history is linear (each revision's parent is the one before it).
/// </summary>
internal sealed class ConfigRevision
{
    public ConfigRevisionId Id { get; set; }

    /// <summary>1, 2, 3, ... in commit order; shown as "#42". Unique.</summary>
    public long Seq { get; set; }

    /// <summary>The revision this one was made on top of; null exactly for the first (Seq 1). Unique (no forks).</summary>
    public ConfigRevisionId? ParentId { get; set; }

    public ActorKind ActorKind { get; set; }

    public UserId? ActorUserId { get; set; }

    public ApiTokenId? ActorTokenId { get; set; }

    public Channel Channel { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The canonical document bytes (UTF-8 JSON), Brotli-compressed.</summary>
    public byte[] Document { get; set; } = [];

    /// <summary>SHA-256 of the uncompressed document bytes (32 bytes).</summary>
    public byte[] DocumentHash { get; set; } = [];

    /// <summary>The change list against the parent, as JSON text produced by the Application layer.</summary>
    public string Summary { get; set; } = "";
}

internal sealed class ConfigRevisionConfiguration : IEntityTypeConfiguration<ConfigRevision>
{
    public void Configure(EntityTypeBuilder<ConfigRevision> builder)
    {
        builder.ToTable("ConfigRevisions", table =>
        {
            table.HasCheckConstraint("CK_ConfigRevisions_Seq", "\"Seq\" >= 1");
            // One root: NULLs are distinct in a unique index, so the unique ParentId alone would allow several.
            table.HasCheckConstraint("CK_ConfigRevisions_Root", "(\"Seq\" = 1) = (\"ParentId\" IS NULL)");
            table.HasCheckConstraint("CK_ConfigRevisions_DocumentHash", "length(\"DocumentHash\") = 32");
            table.HasCheckConstraint("CK_ConfigRevisions_Actor", ActorColumns.CheckSql);
            table.HasCheckConstraint("CK_ConfigRevisions_Channel", NameChecks.Channel("Channel"));
        });

        builder.HasIndex(r => r.Seq).IsUnique();

        // One-to-one with itself: the FK's index is unique, so two revisions can't share a parent.
        builder.HasOne<ConfigRevision>().WithOne().HasForeignKey<ConfigRevision>(r => r.ParentId).OnDelete(DeleteBehavior.Restrict);
    }
}
