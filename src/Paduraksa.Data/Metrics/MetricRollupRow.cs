using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Metrics;

/// <summary>
/// One minute of one series: a host's, an upstream's or the process's figures (PLAN §6.3, §7.4). Keyed by the object's typed ID
/// and the minute, never by the in-memory slot, so history stays attached to the object across restarts and slot reuse.
/// Int64Ordered ID: high-volume, append-only and admin-only.
/// </summary>
internal sealed class MetricRollupRow
{
    public MetricRollupId Id { get; set; }

    public MetricRollupSubject Subject { get; set; }

    /// <summary>The typed-ID string (<c>host_...</c>, <c>ups_...</c>) of the object; empty for the process totals and the requests no host owns.</summary>
    public string SubjectKey { get; set; } = "";

    /// <summary>The minute, as whole minutes since the Unix epoch (UTC): a plain integer, so ranges and buckets are arithmetic in SQL.</summary>
    public long Minute { get; set; }

    public long Requests { get; set; }

    public long Status1xx { get; set; }

    public long Status2xx { get; set; }

    public long Status3xx { get; set; }

    public long Status4xx { get; set; }

    public long Status5xx { get; set; }

    public long NoResponse { get; set; }

    public long Refused { get; set; }

    public long Errors { get; set; }

    public long BytesOut { get; set; }

    public long TotalMicrosSum { get; set; }

    public long UpstreamMicrosSum { get; set; }

    public long UpstreamTimed { get; set; }

    public int TotalP50 { get; set; }

    public int TotalP95 { get; set; }

    public int TotalP99 { get; set; }

    public int UpstreamP50 { get; set; }

    public int UpstreamP95 { get; set; }

    public int UpstreamP99 { get; set; }
}

internal sealed class MetricRollupRowConfiguration : IEntityTypeConfiguration<MetricRollupRow>
{
    public void Configure(EntityTypeBuilder<MetricRollupRow> builder)
    {
        builder.ToTable("MetricRollups", table => table.HasCheckConstraint("CK_MetricRollups_Subject", "\"Subject\" IN (0, 1, 2)"));
        builder.Property(row => row.Subject).HasConversion<int>();
        builder.Property(row => row.SubjectKey).HasMaxLength(64);

        // One row per object and minute: the rollup job writes each finished minute once, and a restart can't write it twice.
        builder.HasIndex(row => new { row.Subject, row.SubjectKey, row.Minute }).IsUnique();
        // Retention deletes by age across every series.
        builder.HasIndex(row => row.Minute);
    }
}
