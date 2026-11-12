using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Identity;

namespace Paduraksa.Data.Drafts;

/// <summary>
/// A user's open draft (PLAN §12, "As built (M10): drafts"). One per user. <b>No secret here:</b> a password typed into a drafted access list is a
/// vault value (a credential no config names yet) and the entries carry only its ID.
/// </summary>
internal sealed class DraftRow
{
    public DraftId Id { get; set; }

    public UserId OwnerUserId { get; set; }

    /// <summary>1 on creation, +1 with every change; apply names the one it reviewed.</summary>
    public long Version { get; set; }

    /// <summary>The revision the draft was started on (0, with a null <see cref="StartedRevisionId"/>: there was none).</summary>
    public long StartedSeq { get; set; }

    public ConfigRevisionId? StartedRevisionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set by every change; an untouched draft is dropped 30 days after it.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The next entry's number; entries are numbered in the order added and numbers aren't reused.</summary>
    public int NextNumber { get; set; } = 1;

    /// <summary>The entries as JSON: an array of stored entries (number, time, command).</summary>
    public string Entries { get; set; } = "[]";

    /// <summary>The credentials the draft's entries name that the vault holds before any config does, as a JSON array of their IDs.</summary>
    public string PendingCredentials { get; set; } = "[]";
}

/// <summary>A dropped draft's notice, kept until its owner sees it.</summary>
internal sealed class DraftNoticeRow
{
    public DraftId DraftId { get; set; }

    public UserId OwnerUserId { get; set; }

    public DateTimeOffset DroppedAt { get; set; }

    public int Entries { get; set; }
}

internal sealed class DraftRowConfiguration : IEntityTypeConfiguration<DraftRow>, IEntityTypeConfiguration<DraftNoticeRow>
{
    public void Configure(EntityTypeBuilder<DraftRow> builder)
    {
        builder.ToTable("ConfigDrafts", table =>
        {
            table.HasCheckConstraint("CK_ConfigDrafts_Version", "\"Version\" >= 1");
            table.HasCheckConstraint("CK_ConfigDrafts_NextNumber", "\"NextNumber\" >= 1");
        });
        builder.HasKey(d => d.Id);
        builder.HasIndex(d => d.OwnerUserId).IsUnique();
        builder.HasIndex(d => d.UpdatedAt);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(d => d.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<DraftNoticeRow> builder)
    {
        builder.ToTable("ConfigDraftNotices");
        builder.HasKey(n => n.DraftId);
        builder.HasIndex(n => n.OwnerUserId);
        builder.HasOne<UserRow>().WithMany().HasForeignKey(n => n.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
    }
}
