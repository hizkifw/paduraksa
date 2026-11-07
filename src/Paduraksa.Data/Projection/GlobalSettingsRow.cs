using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;
using Paduraksa.Data.Revisions;

namespace Paduraksa.Data.Projection;

/// <summary>
/// The global settings (PLAN §5.1): a single row (<see cref="Key"/> 1), which also records the revision the whole
/// projection reflects.
/// </summary>
/// <remarks>
/// <para>The <c>*Row</c> entities are a projection of the latest revision's config document (PLAN §6.4), written by
/// the command pipeline in the same transaction as the revision. They serve queries (dashboard lists, "which hosts use
/// this upstream") and database-level invariants (unique names, foreign keys); nothing reads them to rebuild config,
/// which always comes from a revision's document. They are lean on purpose: the queryable fields, plus settings layers
/// as the document's JSON text (queryable with <c>json_extract</c>). Enum-valued columns hold the document's camelCase
/// names; Data doesn't know the Contracts enums, so those columns carry no check constraint.</para>
/// <para>Foreign keys between projection rows are <c>NO ACTION</c>, not <c>RESTRICT</c>: the pipeline defers
/// foreign-key checks to the commit (<c>PRAGMA defer_foreign_keys</c>) so it can rewrite an object's rows in any
/// order, and SQLite checks <c>RESTRICT</c> at once even then.</para>
/// </remarks>
internal sealed class GlobalSettingsRow
{
    public const int SingletonKey = 1;

    /// <summary>Always <see cref="SingletonKey"/>.</summary>
    public int Key { get; set; } = SingletonKey;

    /// <summary>The revision the whole projection reflects.</summary>
    public ConfigRevisionId RevisionId { get; set; }

    public long Version { get; set; }

    public string UnknownHost { get; set; } = "";

    public CertificateId? DefaultCertificateId { get; set; }

    public string TrustedProxyMode { get; set; } = "";

    /// <summary>The defaults layer (<c>HostSettings</c>) as JSON.</summary>
    public string Defaults { get; set; } = "{}";
}

internal sealed class GlobalSettingsRowConfiguration : IEntityTypeConfiguration<GlobalSettingsRow>
{
    public void Configure(EntityTypeBuilder<GlobalSettingsRow> builder)
    {
        builder.ToTable("GlobalSettings", table =>
            table.HasCheckConstraint("CK_GlobalSettings_Singleton", $"\"Key\" = {GlobalSettingsRow.SingletonKey}"));
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).ValueGeneratedNever();
        builder.HasOne<ConfigRevision>().WithMany().HasForeignKey(s => s.RevisionId).OnDelete(DeleteBehavior.Restrict);
    }
}
