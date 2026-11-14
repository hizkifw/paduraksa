using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Metrics;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// The one-minute rollups in the config database (docs/design/config.md "State that survives a publish"): written by the rollup job, read by the overview for ranges the
/// sampler's memory doesn't reach. Rows are keyed by the object's typed ID and the minute; the in-memory slot never appears.
/// </summary>
public sealed class MetricRollupStore
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    private const int MinuteSeconds = 60;

    internal MetricRollupStore(IDbContextFactory<ConfigDbContext> contexts) => _contexts = contexts;

    /// <summary>
    /// Writes the minutes. A minute already stored for the same object (a restart inside it, a stop that flushed a partial minute and
    /// a start that went on to fill it) is merged into its row, never doubled and never dropped: counts, sums and the observed seconds
    /// add up, and the percentiles are the mean of the two weighted by their requests. The two halves never overlap in time, so the
    /// observed seconds only need capping at the minute's 60.
    /// </summary>
    public async Task WriteAsync(IReadOnlyList<MetricRollup> rollups, CancellationToken cancellationToken)
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
            var existing = await context.MetricRollups
                .Where(row => row.Minute >= first && row.Minute <= last)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<(MetricRollupSubject, string, long), MetricRollupRow> stored = existing.ToDictionary(static row => (row.Subject, row.SubjectKey, row.Minute));
            foreach (var rollup in rollups.OrderBy(static rollup => rollup.Point.Start))
            {
                var key = (rollup.Subject, rollup.SubjectId, rollup.Point.Start / 60);
                if (stored.TryGetValue(key, out var row))
                {
                    Apply(row, MetricPoints.Combine([PointOf(row), rollup.Point], rollup.Point.Start / 60 * 60, MinuteSeconds));
                }
                else
                {
                    row = RowOf(rollup);
                    stored[key] = row;
                    context.MetricRollups.Add(row);
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
            return await context.MetricRollups.Where(row => row.Minute < minute).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One series between <paramref name="from"/> and <paramref name="to"/>, grouped into steps of <paramref name="bucketMinutes"/>
    /// (a minute is one row, so 1 returns them as stored), oldest first, each step combined as <see cref="MetricPoints.Combine"/> does.
    /// The grouping and the sums are done by SQLite on the series' index range, so only a step's worth of numbers is read, never
    /// the minutes themselves.
    /// </summary>
    public async Task<IReadOnlyList<MetricPoint>> ReadSeriesAsync(
        MetricRollupSubject subject, string subjectId, DateTimeOffset from, DateTimeOffset to, int bucketMinutes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketMinutes, 1);
        var fromMinute = from.ToUnixTimeSeconds() / 60;
        var toMinute = to.ToUnixTimeSeconds() / 60;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var steps = await SeriesQuery(context, subject, subjectId, fromMinute, toMinute, bucketMinutes).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. steps.Select(step => PointOf(step, step.Key * bucketMinutes * 60, bucketMinutes * 60))];
        }
    }

    /// <summary>
    /// Every minute of every series from <paramref name="from"/> to <paramref name="to"/>, as stored, for the sampler to start from. It is
    /// a range of the Minute index; an hour of 150 series is a few thousand rows, read once at startup.
    /// </summary>
    public async Task<IReadOnlyList<MetricRollup>> ReadMinutesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await MinutesQuery(context, from.ToUnixTimeSeconds() / 60, to.ToUnixTimeSeconds() / 60).ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(static row => new MetricRollup(row.Subject, row.SubjectKey, PointOf(row)))];
        }
    }

    internal static IQueryable<MetricRollupRow> MinutesQuery(ConfigDbContext context, long fromMinute, long toMinute) =>
        context.MetricRollups.AsNoTracking().Where(row => row.Minute >= fromMinute && row.Minute <= toMinute);

    /// <summary>What every object of <paramref name="subject"/> did between <paramref name="from"/> and <paramref name="to"/>, one combined point per object, keyed by its typed-ID string.</summary>
    public async Task<IReadOnlyDictionary<string, MetricPoint>> SummariseAsync(
        MetricRollupSubject subject, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var fromMinute = from.ToUnixTimeSeconds() / 60;
        var toMinute = to.ToUnixTimeSeconds() / 60;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var sums = await SummaryQuery(context, subject, fromMinute, toMinute).ToListAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, MetricPoint> summaries = [];
            foreach (var sum in sums)
            {
                summaries[sum.Key] = PointOf(sum, fromMinute * 60, (int)Math.Max((toMinute - fromMinute) * 60, 60));
            }

            return summaries;
        }
    }

    // The two aggregates the overview reads. Sums only, in SQL: a month of 150 series is millions of rows, which must never become
    // entities. SummaryQuery filters on (Subject, Minute) and groups by the key, so it walks the subject's range of the
    // (Subject, SubjectKey, Minute) index; SeriesQuery names one series and a minute range, which is a seek on the same index.
    internal static IQueryable<RollupSums<string>> SummaryQuery(ConfigDbContext context, MetricRollupSubject subject, long fromMinute, long toMinute) =>
        Aggregate(context.MetricRollups.AsNoTracking().Where(row => row.Subject == subject && row.Minute >= fromMinute && row.Minute <= toMinute), row => row.SubjectKey);

    internal static IQueryable<RollupSums<long>> SeriesQuery(ConfigDbContext context, MetricRollupSubject subject, string subjectKey, long fromMinute, long toMinute, int bucketMinutes) =>
        Aggregate(
            context.MetricRollups.AsNoTracking().Where(row => row.Subject == subject && row.SubjectKey == subjectKey && row.Minute >= fromMinute && row.Minute <= toMinute),
            row => row.Minute / bucketMinutes)
        .OrderBy(sums => sums.Key);

    private static IQueryable<RollupSums<TKey>> Aggregate<TKey>(IQueryable<MetricRollupRow> rows, Expression<Func<MetricRollupRow, TKey>> key) =>
        rows.GroupBy(key).Select(group => new RollupSums<TKey>
        {
            Key = group.Key,
            CoveredSeconds = group.Sum(row => (long)row.CoveredSeconds),
            Requests = group.Sum(row => row.Requests),
            Status1xx = group.Sum(row => row.Status1xx),
            Status2xx = group.Sum(row => row.Status2xx),
            Status3xx = group.Sum(row => row.Status3xx),
            Status4xx = group.Sum(row => row.Status4xx),
            Status5xx = group.Sum(row => row.Status5xx),
            NoResponse = group.Sum(row => row.NoResponse),
            Refused = group.Sum(row => row.Refused),
            RefusedAccessList = group.Sum(row => row.RefusedAccessList),
            RefusedRuleSet = group.Sum(row => row.RefusedRuleSet),
            RefusedRateLimit = group.Sum(row => row.RefusedRateLimit),
            RefusedProofOfWork = group.Sum(row => row.RefusedProofOfWork),
            RefusedForwardAuth = group.Sum(row => row.RefusedForwardAuth),
            RefusedRequestLimits = group.Sum(row => row.RefusedRequestLimits),
            RefusedConnection = group.Sum(row => row.RefusedConnection),
            RefusedOther = group.Sum(row => row.RefusedOther),
            NoRoute = group.Sum(row => row.NoRoute),
            Unavailable = group.Sum(row => row.Unavailable),
            Dropped = group.Sum(row => row.Dropped),
            UnknownHost = group.Sum(row => row.UnknownHost),
            Errors = group.Sum(row => row.Errors),
            BytesOut = group.Sum(row => row.BytesOut),
            BytesIn = group.Sum(row => row.BytesIn),
            TotalMicrosSum = group.Sum(row => row.TotalMicrosSum),
            UpstreamMicrosSum = group.Sum(row => row.UpstreamMicrosSum),
            UpstreamTimed = group.Sum(row => row.UpstreamTimed),
            WeightedTotalP50 = group.Sum(row => (double)row.TotalP50 * row.Requests),
            WeightedTotalP95 = group.Sum(row => (double)row.TotalP95 * row.Requests),
            WeightedTotalP99 = group.Sum(row => (double)row.TotalP99 * row.Requests),
            WeightedUpstreamP50 = group.Sum(row => (double)row.UpstreamP50 * row.UpstreamTimed),
            WeightedUpstreamP95 = group.Sum(row => (double)row.UpstreamP95 * row.UpstreamTimed),
            WeightedUpstreamP99 = group.Sum(row => (double)row.UpstreamP99 * row.UpstreamTimed),
        });

    /// <summary>What SQLite sums for one group of minutes; percentiles are summed already weighted (by requests, or by the upstream-timed ones), as <see cref="MetricPoints.Combine"/> weighs them.</summary>
    internal sealed class RollupSums<TKey>
    {
        public TKey Key { get; set; } = default!;

        public long CoveredSeconds { get; set; }

        public long Requests { get; set; }

        public long Status1xx { get; set; }

        public long Status2xx { get; set; }

        public long Status3xx { get; set; }

        public long Status4xx { get; set; }

        public long Status5xx { get; set; }

        public long NoResponse { get; set; }

        public long Refused { get; set; }

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

        public long BytesOut { get; set; }

        public long BytesIn { get; set; }

        public long TotalMicrosSum { get; set; }

        public long UpstreamMicrosSum { get; set; }

        public long UpstreamTimed { get; set; }

        public double WeightedTotalP50 { get; set; }

        public double WeightedTotalP95 { get; set; }

        public double WeightedTotalP99 { get; set; }

        public double WeightedUpstreamP50 { get; set; }

        public double WeightedUpstreamP95 { get; set; }

        public double WeightedUpstreamP99 { get; set; }
    }

    private static MetricPoint PointOf<TKey>(RollupSums<TKey> sums, long start, int width) => new()
    {
        Start = start,
        Width = width,
        CoveredSeconds = (int)Math.Min(sums.CoveredSeconds, int.MaxValue),
        Requests = sums.Requests,
        Status1xx = sums.Status1xx,
        Status2xx = sums.Status2xx,
        Status3xx = sums.Status3xx,
        Status4xx = sums.Status4xx,
        Status5xx = sums.Status5xx,
        NoResponse = sums.NoResponse,
        Refused = sums.Refused,
        Refusals = new RefusalCounts
        {
            AccessList = sums.RefusedAccessList,
            RuleSet = sums.RefusedRuleSet,
            RateLimit = sums.RefusedRateLimit,
            ProofOfWork = sums.RefusedProofOfWork,
            ForwardAuth = sums.RefusedForwardAuth,
            RequestLimits = sums.RefusedRequestLimits,
            Connection = sums.RefusedConnection,
            Other = sums.RefusedOther,
            NoRoute = sums.NoRoute,
            Unavailable = sums.Unavailable,
            Dropped = sums.Dropped,
            UnknownHost = sums.UnknownHost,
        },
        Errors = sums.Errors,
        BytesOut = sums.BytesOut,
        BytesIn = sums.BytesIn,
        TotalMicrosSum = sums.TotalMicrosSum,
        UpstreamMicrosSum = sums.UpstreamMicrosSum,
        UpstreamTimed = sums.UpstreamTimed,
        TotalP50 = MetricPoints.WeightedMean(sums.WeightedTotalP50, sums.Requests),
        TotalP95 = MetricPoints.WeightedMean(sums.WeightedTotalP95, sums.Requests),
        TotalP99 = MetricPoints.WeightedMean(sums.WeightedTotalP99, sums.Requests),
        UpstreamP50 = MetricPoints.WeightedMean(sums.WeightedUpstreamP50, sums.UpstreamTimed),
        UpstreamP95 = MetricPoints.WeightedMean(sums.WeightedUpstreamP95, sums.UpstreamTimed),
        UpstreamP99 = MetricPoints.WeightedMean(sums.WeightedUpstreamP99, sums.UpstreamTimed),
    };

    private static MetricRollupRow RowOf(MetricRollup rollup)
    {
        MetricRollupRow row = new()
        {
            Id = MetricRollupId.New(),
            Subject = rollup.Subject,
            SubjectKey = rollup.SubjectId,
            Minute = rollup.Point.Start / 60,
        };
        Apply(row, rollup.Point);
        return row;
    }

    private static MetricPoint PointOf(MetricRollupRow row) => new()
    {
        Start = row.Minute * 60,
        Width = MinuteSeconds,
        CoveredSeconds = row.CoveredSeconds,
        Requests = row.Requests,
        Status1xx = row.Status1xx,
        Status2xx = row.Status2xx,
        Status3xx = row.Status3xx,
        Status4xx = row.Status4xx,
        Status5xx = row.Status5xx,
        NoResponse = row.NoResponse,
        Refused = row.Refused,
        Refusals = new RefusalCounts
        {
            AccessList = row.RefusedAccessList,
            RuleSet = row.RefusedRuleSet,
            RateLimit = row.RefusedRateLimit,
            ProofOfWork = row.RefusedProofOfWork,
            ForwardAuth = row.RefusedForwardAuth,
            RequestLimits = row.RefusedRequestLimits,
            Connection = row.RefusedConnection,
            Other = row.RefusedOther,
            NoRoute = row.NoRoute,
            Unavailable = row.Unavailable,
            Dropped = row.Dropped,
            UnknownHost = row.UnknownHost,
        },
        Errors = row.Errors,
        BytesOut = row.BytesOut,
        BytesIn = row.BytesIn,
        TotalMicrosSum = row.TotalMicrosSum,
        UpstreamMicrosSum = row.UpstreamMicrosSum,
        UpstreamTimed = row.UpstreamTimed,
        TotalP50 = row.TotalP50,
        TotalP95 = row.TotalP95,
        TotalP99 = row.TotalP99,
        UpstreamP50 = row.UpstreamP50,
        UpstreamP95 = row.UpstreamP95,
        UpstreamP99 = row.UpstreamP99,
    };

    private static void Apply(MetricRollupRow row, MetricPoint point)
    {
        row.CoveredSeconds = Math.Min(point.CoveredSeconds, MinuteSeconds);
        row.Requests = point.Requests;
        row.Status1xx = point.Status1xx;
        row.Status2xx = point.Status2xx;
        row.Status3xx = point.Status3xx;
        row.Status4xx = point.Status4xx;
        row.Status5xx = point.Status5xx;
        row.NoResponse = point.NoResponse;
        row.Refused = point.Refused;
        row.RefusedAccessList = point.Refusals.AccessList;
        row.RefusedRuleSet = point.Refusals.RuleSet;
        row.RefusedRateLimit = point.Refusals.RateLimit;
        row.RefusedProofOfWork = point.Refusals.ProofOfWork;
        row.RefusedForwardAuth = point.Refusals.ForwardAuth;
        row.RefusedRequestLimits = point.Refusals.RequestLimits;
        row.RefusedConnection = point.Refusals.Connection;
        row.RefusedOther = point.Refusals.Other;
        row.NoRoute = point.Refusals.NoRoute;
        row.Unavailable = point.Refusals.Unavailable;
        row.Dropped = point.Refusals.Dropped;
        row.UnknownHost = point.Refusals.UnknownHost;
        row.Errors = point.Errors;
        row.BytesOut = point.BytesOut;
        row.BytesIn = point.BytesIn;
        row.TotalMicrosSum = point.TotalMicrosSum;
        row.UpstreamMicrosSum = point.UpstreamMicrosSum;
        row.UpstreamTimed = point.UpstreamTimed;
        row.TotalP50 = point.TotalP50;
        row.TotalP95 = point.TotalP95;
        row.TotalP99 = point.TotalP99;
        row.UpstreamP50 = point.UpstreamP50;
        row.UpstreamP95 = point.UpstreamP95;
        row.UpstreamP99 = point.UpstreamP99;
    }
}
