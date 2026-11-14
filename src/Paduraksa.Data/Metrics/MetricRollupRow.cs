using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Metrics;

/// <summary>
/// One minute of one series: a host's, an upstream's or the process's figures (docs/design/config.md "State that survives a publish", docs/design/observability.md "Metrics (M9)"). Keyed by the object's typed ID
/// and the minute, never by the in-memory slot, so history stays attached to the object across restarts and slot reuse.
/// The process totals have a row for every minute the process observed, idle ones included; a minute with no row was not observed.
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

    /// <summary>How many of the minute's seconds the process observed; a restart or a stop inside the minute leaves it under 60.</summary>
    public int CoveredSeconds { get; set; }

    public long Requests { get; set; }

    public long Status1xx { get; set; }

    public long Status2xx { get; set; }

    public long Status3xx { get; set; }

    public long Status4xx { get; set; }

    public long Status5xx { get; set; }

    public long NoResponse { get; set; }

    public long Refused { get; set; }

    // The refusals by kind (MetricPoint.Refusals). A row from before they were kept has 0 in each, and its Refused is all of an unknown reason.

    public long RefusedAccessList { get; set; }

    public long RefusedRuleSet { get; set; }

    public long RefusedRateLimit { get; set; }

    public long RefusedProofOfWork { get; set; }

    public long RefusedForwardAuth { get; set; }

    public long RefusedRequestLimits { get; set; }

    public long RefusedConnection { get; set; }

    public long RefusedOther { get; set; }

    public long NoRoute { get; set; }

    public long Unavailable { get; set; }

    public long Dropped { get; set; }

    public long UnknownHost { get; set; }

    public long Errors { get; set; }

    /// <summary>Response body bytes written to clients. Rows from before counted body bytes hold the declared <c>Content-Length</c>s instead.</summary>
    public long BytesOut { get; set; }

    /// <summary>Request body bytes read from clients; 0 in rows from before they were counted.</summary>
    public long BytesIn { get; set; }

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
