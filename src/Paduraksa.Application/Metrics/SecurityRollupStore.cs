using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Metrics;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// The one-minute security rollups (docs/design/observability.md "Security counters"): written by the rollup job next to the request
/// rollups, read by the security statistics for ranges the sampler's memory doesn't reach. Keyed by the object's typed ID and the minute.
/// </summary>
public sealed class SecurityRollupStore
{
    private const int MinuteSeconds = 60;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    internal SecurityRollupStore(IDbContextFactory<ConfigDbContext> contexts) => _contexts = contexts;

    /// <summary>Writes the minutes. A minute already stored for the same object (a restart inside it) is merged into its row: the counts and the observed seconds add up, the seconds capped at 60.</summary>
    public async Task WriteAsync(IReadOnlyList<SecurityRollup> rollups, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rollups);
        if (rollups.Count == 0)
        {
            return;
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var first = rollups.Min(static rollup => rollup.Point.Start) / 60;
            var last = rollups.Max(static rollup => rollup.Point.Start) / 60;
            var existing = await context.SecurityRollups
                .Where(row => row.Minute >= first && row.Minute <= last)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<(SecurityRollupSubject, string, long), SecurityRollupRow> stored = existing.ToDictionary(static row => (row.Subject, row.SubjectKey, row.Minute));
            foreach (var rollup in rollups.OrderBy(static rollup => rollup.Point.Start))
            {
                var minute = rollup.Point.Start / 60;
                var key = (rollup.Subject, rollup.SubjectId, minute);
                if (stored.TryGetValue(key, out var row))
                {
                    Apply(row, SecurityPoint.Combine([PointOf(row), rollup.Point], minute * 60, MinuteSeconds));
                }
                else
                {
                    row = new SecurityRollupRow { Id = SecurityRollupId.New(), Subject = rollup.Subject, SubjectKey = rollup.SubjectId, Minute = minute };
                    Apply(row, rollup.Point);
                    stored[key] = row;
                    context.SecurityRollups.Add(row);
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes the minutes that started before <paramref name="cutoff"/>; returns how many rows went.</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var minute = cutoff.ToUnixTimeSeconds() / 60;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.SecurityRollups.Where(row => row.Minute < minute).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Every minute of every series from <paramref name="from"/> to <paramref name="to"/>, as stored, for the sampler's seed: a range of the Minute index.</summary>
    public async Task<IReadOnlyList<SecurityRollup>> ReadMinutesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var fromMinute = from.ToUnixTimeSeconds() / 60;
        var toMinute = to.ToUnixTimeSeconds() / 60;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await MinutesQuery(context, fromMinute, toMinute).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(static row => new SecurityRollup(row.Subject, row.SubjectKey, PointOf(row)))];
        }
    }

    internal static IQueryable<SecurityRollupRow> MinutesQuery(ConfigDbContext context, long fromMinute, long toMinute) =>
        context.SecurityRollups.AsNoTracking().Where(row => row.Minute >= fromMinute && row.Minute <= toMinute);

    /// <summary>What every object of <paramref name="subject"/> counted between <paramref name="from"/> and <paramref name="to"/>, one point per object keyed by its typed-ID string (the totals: empty).</summary>
    public async Task<IReadOnlyDictionary<string, SecurityPoint>> SummariseAsync(
        SecurityRollupSubject subject, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var fromMinute = from.ToUnixTimeSeconds() / 60;
        var toMinute = to.ToUnixTimeSeconds() / 60;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var sums = await SummaryQuery(context, subject, fromMinute, toMinute).ToListAsync(cancellationToken).ConfigureAwait(false);
            var width = (int)Math.Max((toMinute - fromMinute) * 60, 60);
            return sums.ToDictionary(static sum => sum.Key, sum => PointOf(sum, fromMinute * 60, width));
        }
    }

    // Sums only, in SQL, grouped by the key: it walks the subject's range of the (Subject, SubjectKey, Minute) index, as the request summary does.
    internal static IQueryable<SecuritySums> SummaryQuery(ConfigDbContext context, SecurityRollupSubject subject, long fromMinute, long toMinute) =>
        context.SecurityRollups.AsNoTracking()
            .Where(row => row.Subject == subject && row.Minute >= fromMinute && row.Minute <= toMinute)
            .GroupBy(row => row.SubjectKey)
            .Select(group => new SecuritySums
            {
                Key = group.Key,
                CoveredSeconds = group.Sum(row => (long)row.CoveredSeconds),
                ChallengesServed = group.Sum(row => row.ChallengesServed),
                Solved = group.Sum(row => row.Solved),
                VerifyRejected = group.Sum(row => row.VerifyRejected),
                BlockedNonBrowser = group.Sum(row => row.BlockedNonBrowser),
                TokenMalformed = group.Sum(row => row.TokenMalformed),
                TokenBadTag = group.Sum(row => row.TokenBadTag),
                TokenExpired = group.Sum(row => row.TokenExpired),
                TokenTooEasy = group.Sum(row => row.TokenTooEasy),
                RateLimitAllowed = group.Sum(row => row.RateLimitAllowed),
                RateLimited = group.Sum(row => row.RateLimited),
                AddressRefused = group.Sum(row => row.AddressRefused),
                Unauthorized = group.Sum(row => row.Unauthorized),
                PasswordCacheHits = group.Sum(row => row.PasswordCacheHits),
                Verifications = group.Sum(row => row.Verifications),
                Queued = group.Sum(row => row.Queued),
                QueueFull = group.Sum(row => row.QueueFull),
                QueueTimeouts = group.Sum(row => row.QueueTimeouts),
                ConnectionsDenied = group.Sum(row => row.ConnectionsDenied),
                ConnectionsOverLimit = group.Sum(row => row.ConnectionsOverLimit),
                ConnectionsUncounted = group.Sum(row => row.ConnectionsUncounted),
                SignInAllowed = group.Sum(row => row.SignInAllowed),
                SignInDenied = group.Sum(row => row.SignInDenied),
                SignInRedirected = group.Sum(row => row.SignInRedirected),
                SignInCacheHits = group.Sum(row => row.SignInCacheHits),
                SignInCalls = group.Sum(row => row.SignInCalls),
                SignInErrors = group.Sum(row => row.SignInErrors),
                SignInTimeouts = group.Sum(row => row.SignInTimeouts),
                SignInBusy = group.Sum(row => row.SignInBusy),
                RulesDenied = group.Sum(row => row.RulesDenied),
                RulesDropped = group.Sum(row => row.RulesDropped),
                RulesAllowed = group.Sum(row => row.RulesAllowed),
                RulesTimeouts = group.Sum(row => row.RulesTimeouts),
            });

    /// <summary>What SQLite sums for one object over a range.</summary>
    internal sealed class SecuritySums
    {
        public string Key { get; set; } = string.Empty;

        public long CoveredSeconds { get; set; }

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

    private static SecurityPoint PointOf(SecuritySums sums, long start, int width)
    {
        Span<long> counts = stackalloc long[SecurityPoint.Count];
        counts[(int)SecurityCounter.ChallengesServed] = sums.ChallengesServed;
        counts[(int)SecurityCounter.Solved] = sums.Solved;
        counts[(int)SecurityCounter.VerifyRejected] = sums.VerifyRejected;
        counts[(int)SecurityCounter.BlockedNonBrowser] = sums.BlockedNonBrowser;
        counts[(int)SecurityCounter.TokenMalformed] = sums.TokenMalformed;
        counts[(int)SecurityCounter.TokenBadTag] = sums.TokenBadTag;
        counts[(int)SecurityCounter.TokenExpired] = sums.TokenExpired;
        counts[(int)SecurityCounter.TokenTooEasy] = sums.TokenTooEasy;
        counts[(int)SecurityCounter.RateLimitAllowed] = sums.RateLimitAllowed;
        counts[(int)SecurityCounter.RateLimited] = sums.RateLimited;
        counts[(int)SecurityCounter.AddressRefused] = sums.AddressRefused;
        counts[(int)SecurityCounter.Unauthorized] = sums.Unauthorized;
        counts[(int)SecurityCounter.PasswordCacheHits] = sums.PasswordCacheHits;
        counts[(int)SecurityCounter.Verifications] = sums.Verifications;
        counts[(int)SecurityCounter.Queued] = sums.Queued;
        counts[(int)SecurityCounter.QueueFull] = sums.QueueFull;
        counts[(int)SecurityCounter.QueueTimeouts] = sums.QueueTimeouts;
        counts[(int)SecurityCounter.ConnectionsDenied] = sums.ConnectionsDenied;
        counts[(int)SecurityCounter.ConnectionsOverLimit] = sums.ConnectionsOverLimit;
        counts[(int)SecurityCounter.ConnectionsUncounted] = sums.ConnectionsUncounted;
        counts[(int)SecurityCounter.SignInAllowed] = sums.SignInAllowed;
        counts[(int)SecurityCounter.SignInDenied] = sums.SignInDenied;
        counts[(int)SecurityCounter.SignInRedirected] = sums.SignInRedirected;
        counts[(int)SecurityCounter.SignInCacheHits] = sums.SignInCacheHits;
        counts[(int)SecurityCounter.SignInCalls] = sums.SignInCalls;
        counts[(int)SecurityCounter.SignInErrors] = sums.SignInErrors;
        counts[(int)SecurityCounter.SignInTimeouts] = sums.SignInTimeouts;
        counts[(int)SecurityCounter.SignInBusy] = sums.SignInBusy;
        counts[(int)SecurityCounter.RulesDenied] = sums.RulesDenied;
        counts[(int)SecurityCounter.RulesDropped] = sums.RulesDropped;
        counts[(int)SecurityCounter.RulesAllowed] = sums.RulesAllowed;
        counts[(int)SecurityCounter.RulesTimeouts] = sums.RulesTimeouts;
        return new SecurityPoint(start, width, (int)Math.Min(sums.CoveredSeconds, int.MaxValue), counts);
    }

    private static SecurityPoint PointOf(SecurityRollupRow row)
    {
        Span<long> counts = stackalloc long[SecurityPoint.Count];
        counts[(int)SecurityCounter.ChallengesServed] = row.ChallengesServed;
        counts[(int)SecurityCounter.Solved] = row.Solved;
        counts[(int)SecurityCounter.VerifyRejected] = row.VerifyRejected;
        counts[(int)SecurityCounter.BlockedNonBrowser] = row.BlockedNonBrowser;
        counts[(int)SecurityCounter.TokenMalformed] = row.TokenMalformed;
        counts[(int)SecurityCounter.TokenBadTag] = row.TokenBadTag;
        counts[(int)SecurityCounter.TokenExpired] = row.TokenExpired;
        counts[(int)SecurityCounter.TokenTooEasy] = row.TokenTooEasy;
        counts[(int)SecurityCounter.RateLimitAllowed] = row.RateLimitAllowed;
        counts[(int)SecurityCounter.RateLimited] = row.RateLimited;
        counts[(int)SecurityCounter.AddressRefused] = row.AddressRefused;
        counts[(int)SecurityCounter.Unauthorized] = row.Unauthorized;
        counts[(int)SecurityCounter.PasswordCacheHits] = row.PasswordCacheHits;
        counts[(int)SecurityCounter.Verifications] = row.Verifications;
        counts[(int)SecurityCounter.Queued] = row.Queued;
        counts[(int)SecurityCounter.QueueFull] = row.QueueFull;
        counts[(int)SecurityCounter.QueueTimeouts] = row.QueueTimeouts;
        counts[(int)SecurityCounter.ConnectionsDenied] = row.ConnectionsDenied;
        counts[(int)SecurityCounter.ConnectionsOverLimit] = row.ConnectionsOverLimit;
        counts[(int)SecurityCounter.ConnectionsUncounted] = row.ConnectionsUncounted;
        counts[(int)SecurityCounter.SignInAllowed] = row.SignInAllowed;
        counts[(int)SecurityCounter.SignInDenied] = row.SignInDenied;
        counts[(int)SecurityCounter.SignInRedirected] = row.SignInRedirected;
        counts[(int)SecurityCounter.SignInCacheHits] = row.SignInCacheHits;
        counts[(int)SecurityCounter.SignInCalls] = row.SignInCalls;
        counts[(int)SecurityCounter.SignInErrors] = row.SignInErrors;
        counts[(int)SecurityCounter.SignInTimeouts] = row.SignInTimeouts;
        counts[(int)SecurityCounter.SignInBusy] = row.SignInBusy;
        counts[(int)SecurityCounter.RulesDenied] = row.RulesDenied;
        counts[(int)SecurityCounter.RulesDropped] = row.RulesDropped;
        counts[(int)SecurityCounter.RulesAllowed] = row.RulesAllowed;
        counts[(int)SecurityCounter.RulesTimeouts] = row.RulesTimeouts;
        return new SecurityPoint(row.Minute * 60, MinuteSeconds, row.CoveredSeconds, counts);
    }

    private static void Apply(SecurityRollupRow row, SecurityPoint point)
    {
        row.CoveredSeconds = Math.Min(point.CoveredSeconds, MinuteSeconds);
        row.ChallengesServed = point[SecurityCounter.ChallengesServed];
        row.Solved = point[SecurityCounter.Solved];
        row.VerifyRejected = point[SecurityCounter.VerifyRejected];
        row.BlockedNonBrowser = point[SecurityCounter.BlockedNonBrowser];
        row.TokenMalformed = point[SecurityCounter.TokenMalformed];
        row.TokenBadTag = point[SecurityCounter.TokenBadTag];
        row.TokenExpired = point[SecurityCounter.TokenExpired];
        row.TokenTooEasy = point[SecurityCounter.TokenTooEasy];
        row.RateLimitAllowed = point[SecurityCounter.RateLimitAllowed];
        row.RateLimited = point[SecurityCounter.RateLimited];
        row.AddressRefused = point[SecurityCounter.AddressRefused];
        row.Unauthorized = point[SecurityCounter.Unauthorized];
        row.PasswordCacheHits = point[SecurityCounter.PasswordCacheHits];
        row.Verifications = point[SecurityCounter.Verifications];
        row.Queued = point[SecurityCounter.Queued];
        row.QueueFull = point[SecurityCounter.QueueFull];
        row.QueueTimeouts = point[SecurityCounter.QueueTimeouts];
        row.ConnectionsDenied = point[SecurityCounter.ConnectionsDenied];
        row.ConnectionsOverLimit = point[SecurityCounter.ConnectionsOverLimit];
        row.ConnectionsUncounted = point[SecurityCounter.ConnectionsUncounted];
        row.SignInAllowed = point[SecurityCounter.SignInAllowed];
        row.SignInDenied = point[SecurityCounter.SignInDenied];
        row.SignInRedirected = point[SecurityCounter.SignInRedirected];
        row.SignInCacheHits = point[SecurityCounter.SignInCacheHits];
        row.SignInCalls = point[SecurityCounter.SignInCalls];
        row.SignInErrors = point[SecurityCounter.SignInErrors];
        row.SignInTimeouts = point[SecurityCounter.SignInTimeouts];
        row.SignInBusy = point[SecurityCounter.SignInBusy];
        row.RulesDenied = point[SecurityCounter.RulesDenied];
        row.RulesDropped = point[SecurityCounter.RulesDropped];
        row.RulesAllowed = point[SecurityCounter.RulesAllowed];
        row.RulesTimeouts = point[SecurityCounter.RulesTimeouts];
    }
}
