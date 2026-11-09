using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Paduraksa.Core.Model;

namespace Paduraksa.Data.Projection;

/// <summary>A host; a projection row (see <see cref="GlobalSettingsRow"/>).</summary>
internal sealed class HostRow
{
    public HostId Id { get; set; }

    public long Version { get; set; }

    public string Kind { get; set; } = "";

    public bool Enabled { get; set; }

    public ProfileId? ProfileId { get; set; }

    public string CertificateMode { get; set; } = "";

    public CertificateId? CertificateId { get; set; }

    /// <summary>The host layer (<c>HostSettings</c>) as JSON.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>The normalised tags (a JSON array column).</summary>
    public List<string> Tags { get; set; } = [];

    public string? Notes { get; set; }
}

/// <summary>
/// One name of a host: a natural key, stored normalised, with no ID of its own; deleted with the host. Every host's
/// names are projected, but only enabled hosts' names must be unique (a disabled host may stage a replacement under the
/// same names, as the validator and compiler allow), so <see cref="HostEnabled"/> copies the host's flag for a partial
/// unique index.
/// </summary>
internal sealed class HostNameRow
{
    public HostId HostId { get; set; }

    /// <summary>Normalised (lowercase, punycode, no trailing dot); compared ignoring case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The host's <see cref="HostRow.Enabled"/>, kept equal by the pipeline.</summary>
    public bool HostEnabled { get; set; }
}

/// <summary>A route of a host; deleted with it.</summary>
internal sealed class RouteRow
{
    public RouteId Id { get; set; }

    public HostId HostId { get; set; }

    public UpstreamId UpstreamId { get; set; }

    /// <summary>0, 1, 2, ... in the host's evaluation order (the canonical route order).</summary>
    public int Position { get; set; }

    public int Order { get; set; }

    public string Match { get; set; } = "";

    public string Path { get; set; } = "";

    /// <summary>The route layer (<c>RouteSettings</c>) as JSON.</summary>
    public string Settings { get; set; } = "{}";
}

internal sealed class HostRowConfiguration :
    IEntityTypeConfiguration<HostRow>, IEntityTypeConfiguration<HostNameRow>, IEntityTypeConfiguration<RouteRow>
{
    public void Configure(EntityTypeBuilder<HostRow> builder)
    {
        builder.ToTable("Hosts");
        builder.HasOne<ProfileRow>().WithMany().HasForeignKey(h => h.ProfileId).OnDelete(DeleteBehavior.NoAction);
    }

    public void Configure(EntityTypeBuilder<HostNameRow> builder)
    {
        builder.ToTable("HostNames");
        builder.HasKey(n => new { n.HostId, n.Name });
        builder.Property(n => n.Name).UseCollation(ProjectionColumns.NoCase);
        builder.HasIndex(n => n.Name).IsUnique().HasFilter("\"HostEnabled\" = 1").HasDatabaseName("IX_HostNames_Name_Enabled");
        builder.HasOne<HostRow>().WithMany().HasForeignKey(n => n.HostId).OnDelete(DeleteBehavior.Cascade);
    }

    public void Configure(EntityTypeBuilder<RouteRow> builder)
    {
        builder.ToTable("Routes");
        builder.HasIndex(r => new { r.HostId, r.Position }).IsUnique();
        builder.HasOne<HostRow>().WithMany().HasForeignKey(r => r.HostId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<UpstreamRow>().WithMany().HasForeignKey(r => r.UpstreamId).OnDelete(DeleteBehavior.NoAction);
    }
}
