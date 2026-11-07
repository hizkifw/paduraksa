using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;

namespace Paduraksa.Data.Projection;

/// <summary>A profile (PLAN §5.1); a projection row (see <see cref="GlobalSettingsRow"/>).</summary>
internal sealed class ProfileRow
{
    public ProfileId Id { get; set; }

    /// <summary>Unique ignoring case.</summary>
    public string Name { get; set; } = "";

    public long Version { get; set; }

    /// <summary>The profile layer (<c>HostSettings</c>) as JSON.</summary>
    public string Settings { get; set; } = "{}";

    public string? Notes { get; set; }
}

internal sealed class ProfileRowConfiguration : IEntityTypeConfiguration<ProfileRow>
{
    public void Configure(EntityTypeBuilder<ProfileRow> builder)
    {
        builder.ToTable("Profiles");
        builder.Property(p => p.Name).UseCollation(ProjectionColumns.NoCase);
        builder.HasIndex(p => p.Name).IsUnique();
    }
}

internal static class ProjectionColumns
{
    /// <summary>SQLite's ASCII case-insensitive collation; names are normalised or ASCII-folded on write.</summary>
    public const string NoCase = "NOCASE";
}
