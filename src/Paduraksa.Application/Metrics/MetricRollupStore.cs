using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Metrics;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// The one-minute rollups in the config database (PLAN §6.3): written by the rollup job, read by the overview for ranges the
/// sampler's memory doesn't reach. Rows are keyed by the object's typed ID and the minute; the in-memory slot never appears.
/// </summary>
public sealed class MetricRollupStore
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    internal MetricRollupStore(IDbContextFactory<ConfigDbContext> contexts) => _contexts = contexts;

    /// <summary>Writes the minutes. A minute already stored for the same object (a restart inside it, say) is skipped, never doubled.</summary>
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
                .Select(row => new { row.Subject, row.SubjectKey, row.Minute })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            HashSet<(MetricRollupSubject, string, long)> stored = [.. existing.Select(static row => (row.Subject, row.SubjectKey, row.Minute))];
            foreach (var rollup in rollups.OrderBy(static rollup => rollup.Point.Start))
            {
                if (stored.Add((rollup.Subject, rollup.SubjectId, rollup.Point.Start / 60)))
                {
                    context.MetricRollups.Add(RowOf(rollup));
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
            Requests = group.Sum(row => row.Requests),
            Status1xx = group.Sum(row => row.Status1xx),
            Status2xx = group.Sum(row => row.Status2xx),
            Status3xx = group.Sum(row => row.Status3xx),
            Status4xx = group.Sum(row => row.Status4xx),
            Status5xx = group.Sum(row => row.Status5xx),
            NoResponse = group.Sum(row => row.NoResponse),
            Refused = group.Sum(row => row.Refused),
            Errors = group.Sum(row => row.Errors),
            BytesOut = group.Sum(row => row.BytesOut),
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

        public double WeightedTotalP50 { get; set; }

        public double WeightedTotalP95 { get; set; }

        public double WeightedTotalP99 { get; set; }

        public double WeightedUpstreamP50 { get; set; }

        public double WeightedUpstreamP95 { get; set; }

        public double WeightedUpstreamP99 { get; set; }
    }

    private static MetricPoint PointOf<TKey>(RollupSums<TKey> sums, long start, int seconds) => new()
    {
        Start = start,
        Seconds = seconds,
        Requests = sums.Requests,
        Status1xx = sums.Status1xx,
        Status2xx = sums.Status2xx,
        Status3xx = sums.Status3xx,
        Status4xx = sums.Status4xx,
        Status5xx = sums.Status5xx,
        NoResponse = sums.NoResponse,
        Refused = sums.Refused,
        Errors = sums.Errors,
        BytesOut = sums.BytesOut,
        TotalMicrosSum = sums.TotalMicrosSum,
        UpstreamMicrosSum = sums.UpstreamMicrosSum,
        UpstreamTimed = sums.UpstreamTimed,
        TotalP50 = Mean(sums.WeightedTotalP50, sums.Requests),
        TotalP95 = Mean(sums.WeightedTotalP95, sums.Requests),
        TotalP99 = Mean(sums.WeightedTotalP99, sums.Requests),
        UpstreamP50 = Mean(sums.WeightedUpstreamP50, sums.UpstreamTimed),
        UpstreamP95 = Mean(sums.WeightedUpstreamP95, sums.UpstreamTimed),
        UpstreamP99 = Mean(sums.WeightedUpstreamP99, sums.UpstreamTimed),
    };

    private static int Mean(double weighted, long weight) => weight <= 0 ? 0 : (int)Math.Round(weighted / weight);

    private static MetricRollupRow RowOf(MetricRollup rollup)
    {
        var point = rollup.Point;
        return new MetricRollupRow
        {
            Id = MetricRollupId.New(),
            Subject = rollup.Subject,
            SubjectKey = rollup.SubjectId,
            Minute = point.Start / 60,
            Requests = point.Requests,
            Status1xx = point.Status1xx,
            Status2xx = point.Status2xx,
            Status3xx = point.Status3xx,
            Status4xx = point.Status4xx,
            Status5xx = point.Status5xx,
            NoResponse = point.NoResponse,
            Refused = point.Refused,
            Errors = point.Errors,
            BytesOut = point.BytesOut,
            TotalMicrosSum = point.TotalMicrosSum,
            UpstreamMicrosSum = point.UpstreamMicrosSum,
            UpstreamTimed = point.UpstreamTimed,
            TotalP50 = point.TotalP50,
            TotalP95 = point.TotalP95,
            TotalP99 = point.TotalP99,
            UpstreamP50 = point.UpstreamP50,
            UpstreamP95 = point.UpstreamP95,
            UpstreamP99 = point.UpstreamP99,
        };
    }
}
