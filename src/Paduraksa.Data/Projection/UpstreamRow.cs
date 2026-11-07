using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;

namespace Paduraksa.Data.Projection;

/// <summary>An upstream (PLAN §5.1); a projection row (see <see cref="GlobalSettingsRow"/>).</summary>
internal sealed class UpstreamRow
{
    public UpstreamId Id { get; set; }

    /// <summary>Unique ignoring case.</summary>
    public string Name { get; set; } = "";

    public long Version { get; set; }

    public string? Notes { get; set; }
}

/// <summary>A destination of an upstream; deleted with it.</summary>
internal sealed class DestinationRow
{
    public DestinationId Id { get; set; }

    public UpstreamId UpstreamId { get; set; }

    public string Address { get; set; } = "";

    public bool Enabled { get; set; }
}

internal sealed class UpstreamRowConfiguration : IEntityTypeConfiguration<UpstreamRow>, IEntityTypeConfiguration<DestinationRow>
{
    public void Configure(EntityTypeBuilder<UpstreamRow> builder)
    {
        builder.ToTable("Upstreams");
        builder.Property(u => u.Name).UseCollation(ProjectionColumns.NoCase);
        builder.HasIndex(u => u.Name).IsUnique();
    }

    public void Configure(EntityTypeBuilder<DestinationRow> builder)
    {
        builder.ToTable("Destinations");
        builder.HasOne<UpstreamRow>().WithMany().HasForeignKey(d => d.UpstreamId).OnDelete(DeleteBehavior.Cascade);
    }
}
