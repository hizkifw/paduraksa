using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Paduraksa.Core.Model;

namespace Paduraksa.Data.Metrics;

/// <summary>
/// One minute of the gate's security counters: the process's, or one auth provider's or rule set's (docs/design/observability.md
/// "Security counters"). Keyed like <see cref="MetricRollupRow"/> by the object's typed ID and the minute. The totals have a row for every
/// minute the process observed; a provider's or set's rows are sparse. A column per counter, named as <c>SecurityCounter</c> names it:
/// a provider's row fills only the sign-in columns and a set's only the rule ones. Int64Ordered ID: high-volume, append-only and admin-only.
/// </summary>
internal sealed class SecurityRollupRow
{
    public SecurityRollupId Id { get; set; }

    public SecurityRollupSubject Subject { get; set; }

    /// <summary>The typed-ID string of the provider or set; empty for the totals.</summary>
    public string SubjectKey { get; set; } = "";

    /// <summary>The minute, as whole minutes since the Unix epoch (UTC).</summary>
    public long Minute { get; set; }

    /// <summary>How many of the minute's seconds the process observed.</summary>
    public int CoveredSeconds { get; set; }

    public long ChallengesServed { get; set; }

    public long Solved { get; set; }

    public long VerifyRejected { get; set; }

    public long BlockedNonBrowser { get; set; }

    public long TokenMalformed { get; set; }

    public long TokenBadTag { get; set; }

    public long TokenExpired { get; set; }

    public long TokenTooEasy { get; set; }

    public long RateLimitAllowed { get; set; }

    public long RateLimited { get; set; }

    public long AddressRefused { get; set; }

    public long Unauthorized { get; set; }

    public long PasswordCacheHits { get; set; }

    public long Verifications { get; set; }

    public long Queued { get; set; }

    public long QueueFull { get; set; }

    public long QueueTimeouts { get; set; }

    public long ConnectionsDenied { get; set; }

    public long ConnectionsOverLimit { get; set; }

    public long ConnectionsUncounted { get; set; }

    public long SignInAllowed { get; set; }

    public long SignInDenied { get; set; }

    public long SignInRedirected { get; set; }

    public long SignInCacheHits { get; set; }

    public long SignInCalls { get; set; }

    public long SignInErrors { get; set; }

    public long SignInTimeouts { get; set; }

    public long SignInBusy { get; set; }

    public long RulesDenied { get; set; }

    public long RulesDropped { get; set; }

    public long RulesAllowed { get; set; }

    public long RulesTimeouts { get; set; }
}

internal sealed class SecurityRollupRowConfiguration : IEntityTypeConfiguration<SecurityRollupRow>
{
    public void Configure(EntityTypeBuilder<SecurityRollupRow> builder)
    {
        builder.ToTable("SecurityRollups", table => table.HasCheckConstraint("CK_SecurityRollups_Subject", "\"Subject\" IN (0, 1, 2)"));
        builder.Property(row => row.Subject).HasConversion<int>();
        builder.Property(row => row.SubjectKey).HasMaxLength(64);

        // One row per object and minute, as for the request rollups; retention deletes by age.
        builder.HasIndex(row => new { row.Subject, row.SubjectKey, row.Minute }).IsUnique();
        builder.HasIndex(row => row.Minute);
    }
}
