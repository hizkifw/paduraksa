using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Analytics;

/// <summary>
/// One stretch of one host's traffic, as the analytics keep it (PLAN §7.4): five minutes as the drain closes them, or an hour of them merged
/// (the <see cref="Tier"/>). Keyed by the host's typed ID and the start of the stretch, never by the in-memory slot, so history stays with
/// the host. The figures a chart needs are columns; the sketches and tables are two blobs the proxy's codec reads. A
/// <see cref="Subject"/> of coverage has no blobs: it is the record that the process watched the stretch, so a stretch without one is a gap.
/// Int64Ordered ID: high-volume, append-only and admin-only.
/// </summary>
internal sealed class AnalyticsRollupRow
{
    public AnalyticsRollupId Id { get; set; }

    /// <summary>0: five minutes. 1: an hour. 2: a day.</summary>
    public int Tier { get; set; }

    /// <summary>0: coverage. 1: a host. 2: the requests no host owns.</summary>
    public int Subject { get; set; }

    /// <summary>The host's typed-ID text (<c>host_...</c>); empty for coverage and for the requests no host owns.</summary>
    public string HostKey { get; set; } = "";

    /// <summary>The stretch's first second as Unix time, a multiple of its width.</summary>
    public long Start { get; set; }

    /// <summary>How many of the stretch's seconds the process observed.</summary>
    public int CoveredSeconds { get; set; }

    public long Requests { get; set; }

    /// <summary>Response body bytes written to clients; rows from before bodies were counted hold the declared <c>Content-Length</c>s.</summary>
    public long BytesOut { get; set; }

    /// <summary>Request body bytes read from clients; 0 in rows from before they were counted.</summary>
    public long BytesIn { get; set; }

    public long ServerErrors { get; set; }

    /// <summary>The unique-visitors sketch, on its own so a chart that draws a sketch per step doesn't read the rest.</summary>
    public byte[]? Visitors { get; set; }

    /// <summary>The compressed counts and top tables.</summary>
    public byte[]? Data { get; set; }
}

internal sealed class AnalyticsRollupRowConfiguration : IEntityTypeConfiguration<AnalyticsRollupRow>
{
    public void Configure(EntityTypeBuilder<AnalyticsRollupRow> builder)
    {
        builder.ToTable("AnalyticsRollups", table =>
        {
            table.HasCheckConstraint("CK_AnalyticsRollups_Tier", "\"Tier\" IN (0, 1, 2)");
            table.HasCheckConstraint("CK_AnalyticsRollups_Subject", "\"Subject\" IN (0, 1, 2)");
        });
        builder.Property(row => row.HostKey).HasMaxLength(64);

        // One row per tier, subject, host and stretch: the job writes a closed bucket once, and a restart inside it merges into the row.
        // It also serves one host's range read as a seek on (Tier, Subject, HostKey) and a Start range.
        builder.HasIndex(row => new { row.Tier, row.Subject, row.HostKey, row.Start }).IsUnique();
        // Every host's range read, the hourly roll-up and retention walk a tier's stretches in time order.
        builder.HasIndex(row => new { row.Tier, row.Start });
    }
}
