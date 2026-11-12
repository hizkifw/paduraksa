using Microsoft.EntityFrameworkCore;

using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Analytics;
using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Application.Analytics;

/// <summary>
/// The analytics' rollups in the config database (PLAN §7.4): five-minute rows written from the drain's backlog, hourly rows made from them
/// once an hour has passed, and the reads the dashboard and the API make. Keyed by the host's typed ID and the stretch's start, never by
/// the in-memory slot.
/// </summary>
/// <remarks>
/// <para><b>A read never has a hole or a double.</b> A closed bucket is in the backlog until its write is confirmed. A write takes the
/// backlog's batch, writes it and acknowledges it under one gate, and a read takes the stored rows and the backlog's remainder under the
/// same gate, so a bucket is in exactly one of the two.</para>
/// <para><b>Two tiers.</b> The day range reads five-minute rows (288 a host). The longer ranges read the hourly rows (168 a host a week,
/// 720 a month), so the cost of a month is not twelve times a day's. A range's edges, which do not fall on an hour, and the hours
/// not rolled up yet are read from the five-minute rows.</para>
/// </remarks>
public sealed class AnalyticsRollupStore : IDisposable
{
    internal const int HourSeconds = 3_600;
    internal const int DaySeconds = 86_400;

    // An hour is rolled up once it ended this long ago, so a bucket that closes a little late, or a restart that flushes one, is in it.
    private static readonly TimeSpan RollUpDelay = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly AnalyticsBacklog _backlog;
    private readonly SemaphoreSlim _consistency = new(1, 1);

    internal AnalyticsRollupStore(IDbContextFactory<ConfigDbContext> contexts, AnalyticsBacklog backlog)
    {
        _contexts = contexts;
        _backlog = backlog;
    }

    public void Dispose() => _consistency.Dispose();

    /// <summary>
    /// Writes what the backlog holds and confirms it. A bucket for a stretch that already has a row (a restart inside it) is merged into the
    /// row, never doubled and never dropped. A failed write puts the batch back, ahead of newer ones, and throws.
    /// </summary>
    public async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _consistency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var batch = _backlog.Take();
            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await WriteAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _backlog.Requeue();
                throw;
            }

            _backlog.Acknowledge(batch);
        }
        finally
        {
            _consistency.Release();
        }
    }

    internal async Task WriteAsync(IReadOnlyList<AnalyticsRollup> rollups, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var first = rollups.Min(static rollup => rollup.Start);
            var last = rollups.Max(static rollup => rollup.Start);
            var tier = (int)AnalyticsTier.Fine;
            var existing = await context.AnalyticsRollups
                .Where(row => row.Tier == tier && row.Start >= first && row.Start <= last)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<(int, string, long), AnalyticsRollupRow> stored = existing.ToDictionary(static row => (row.Subject, row.HostKey, row.Start));
            foreach (var rollup in rollups.OrderBy(static rollup => rollup.Start))
            {
                var key = ((int)rollup.Subject, rollup.HostKey, rollup.Start);
                if (!stored.TryGetValue(key, out var row))
                {
                    row = RowOf(AnalyticsTier.Fine, rollup);
                    stored[key] = row;
                    context.AnalyticsRollups.Add(row);
                    continue;
                }

                // The two halves of a bucket never overlap in time, so their observed seconds add, up to the bucket's width.
                row.CoveredSeconds = Math.Min(TrafficAnalytics.BucketSeconds, row.CoveredSeconds + rollup.CoveredSeconds);
                if (rollup.Bucket is not null)
                {
                    Merge(row, rollup);
                }
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Merge(AnalyticsRollupRow row, AnalyticsRollup added)
    {
        var current = row.Visitors is not null && row.Data is not null ? TrafficBucketCodec.Decode(new EncodedTrafficBucket(row.Visitors, row.Data)) : null;
        var incoming = TrafficBucketCodec.Decode(added.Bucket!);
        if (incoming is null)
        {
            return;
        }

        if (current is null)
        {
            // A row that can't be read is replaced by what is known, rather than kept forever as a hole.
            Replace(row, added);
            return;
        }

        current.MergeFrom(incoming);
        var encoded = TrafficBucketCodec.Encode(current);
        row.Requests += added.Requests;
        row.BytesOut += added.BytesOut;
        row.ServerErrors += added.ServerErrors;
        row.Visitors = encoded.Visitors;
        row.Data = encoded.Data;
    }

    private static void Replace(AnalyticsRollupRow row, AnalyticsRollup added)
    {
        row.Requests = added.Requests;
        row.BytesOut = added.BytesOut;
        row.ServerErrors = added.ServerErrors;
        row.Visitors = added.Bucket!.Visitors;
        row.Data = added.Bucket.Data;
    }

    private static AnalyticsRollupRow RowOf(AnalyticsTier tier, AnalyticsRollup rollup) => new()
    {
        Id = AnalyticsRollupId.New(),
        Tier = (int)tier,
        Subject = (int)rollup.Subject,
        HostKey = rollup.HostKey,
        Start = rollup.Start,
        CoveredSeconds = rollup.CoveredSeconds,
        Requests = rollup.Requests,
        BytesOut = rollup.BytesOut,
        ServerErrors = rollup.ServerErrors,
        Visitors = rollup.Bucket?.Visitors,
        Data = rollup.Bucket?.Data,
    };

    private static AnalyticsRow ToRow(AnalyticsRollupRow row) => new(
        (AnalyticsTier)row.Tier,
        (AnalyticsSubject)row.Subject,
        row.HostKey,
        row.Start,
        row.CoveredSeconds,
        row.Requests,
        row.BytesOut,
        row.ServerErrors,
        row.Visitors is not null && row.Data is not null ? new EncodedTrafficBucket(row.Visitors, row.Data) : null);

    internal static AnalyticsRow ToRow(AnalyticsRollup rollup) => new(
        AnalyticsTier.Fine, rollup.Subject, rollup.HostKey, rollup.Start, rollup.CoveredSeconds, rollup.Requests, rollup.BytesOut, rollup.ServerErrors, rollup.Bucket);

    /// <summary>
    /// The rows of <paramref name="scope"/> (and every coverage row) from <paramref name="from"/> up to <paramref name="to"/>, hourly rows being
    /// the coarsest used: see the overload that names the tier.
    /// </summary>
    public Task<AnalyticsRead> ReadAsync(AnalyticsScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ReadAsync(scope, from, to, AnalyticsTier.Hour, cancellationToken);

    /// <summary>
    /// The rows of <paramref name="scope"/> (and every coverage row) from <paramref name="from"/> up to <paramref name="to"/>. Rows of the
    /// <paramref name="coarsest"/> tier or finer are used: daily rows for the whole days inside the range that have been rolled up (only when the
    /// caller can draw a day as a step), hourly rows for the whole rolled-up hours of what is left, five-minute rows for the rest. The first
    /// five-minute row is the one <paramref name="from"/> falls in, so a range is never short of its edge by more than a bucket.
    /// </summary>
    public async Task<AnalyticsRead> ReadAsync(AnalyticsScope scope, DateTimeOffset from, DateTimeOffset to, AnalyticsTier coarsest, CancellationToken cancellationToken)
    {
        var fromSeconds = Floor(from.ToUnixTimeSeconds(), TrafficAnalytics.BucketSeconds);
        var toSeconds = to.ToUnixTimeSeconds();
        await _consistency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                List<AnalyticsRow> rows = [];
                (long From, long To)[] pieces = [(fromSeconds, toSeconds + 1)];
                if (coarsest >= AnalyticsTier.Day)
                {
                    var dayFrom = Ceiling(fromSeconds, DaySeconds);
                    var dayTo = Math.Min(Floor(toSeconds, DaySeconds), await RolledEndAsync(context, AnalyticsTier.Day, cancellationToken).ConfigureAwait(false));
                    if (dayFrom < dayTo)
                    {
                        rows.AddRange(await QueryAsync(context, scope, AnalyticsTier.Day, dayFrom, dayTo, cancellationToken).ConfigureAwait(false));
                        pieces = [(fromSeconds, dayFrom), (dayTo, toSeconds + 1)];
                    }
                }

                var rolledHours = coarsest >= AnalyticsTier.Hour ? await RolledEndAsync(context, AnalyticsTier.Hour, cancellationToken).ConfigureAwait(false) : 0;
                foreach (var (pieceFrom, pieceTo) in pieces)
                {
                    if (pieceTo <= pieceFrom)
                    {
                        continue;
                    }

                    var hourFrom = Ceiling(pieceFrom, HourSeconds);
                    var hourTo = Math.Min(Floor(pieceTo, HourSeconds), rolledHours);
                    if (hourFrom < hourTo)
                    {
                        rows.AddRange(await QueryAsync(context, scope, AnalyticsTier.Hour, hourFrom, hourTo, cancellationToken).ConfigureAwait(false));
                        rows.AddRange(await QueryAsync(context, scope, AnalyticsTier.Fine, pieceFrom, hourFrom, cancellationToken).ConfigureAwait(false));
                        rows.AddRange(await QueryAsync(context, scope, AnalyticsTier.Fine, hourTo, pieceTo, cancellationToken).ConfigureAwait(false));
                    }
                    else
                    {
                        rows.AddRange(await QueryAsync(context, scope, AnalyticsTier.Fine, pieceFrom, pieceTo, cancellationToken).ConfigureAwait(false));
                    }
                }

                var unwritten = _backlog.Unwritten()
                    .Where(rollup => rollup.Start >= fromSeconds && rollup.Start <= toSeconds && Matches(scope, rollup))
                    .Select(ToRow)
                    .ToList();
                return new AnalyticsRead(rows, unwritten);
            }
        }
        finally
        {
            _consistency.Release();
        }
    }

    private static bool Matches(AnalyticsScope scope, AnalyticsRollup rollup) =>
        rollup.Subject == AnalyticsSubject.Coverage
        || scope.Subject is null
        || (scope.Subject == rollup.Subject && (scope.Subject != AnalyticsSubject.Host || scope.HostKey == rollup.HostKey));

    /// <summary>
    /// The rows a scope wants in a range of one tier. All hosts are one walk of the (Tier, Start) index; one host, or the requests no host owns,
    /// is a seek on the (Tier, Subject, HostKey, Start) index, so a host's month doesn't read every other host's rows to throw them away.
    /// </summary>
    internal static IQueryable<AnalyticsRollupRow> RangeQuery(ConfigDbContext context, AnalyticsScope scope, AnalyticsTier tier, long from, long to)
    {
        var tierValue = (int)tier;
        var rows = context.AnalyticsRollups.AsNoTracking().Where(row => row.Tier == tierValue && row.Start >= from && row.Start < to);
        if (scope.Subject is not { } subject)
        {
            return rows;
        }

        var wanted = (int)subject;
        var key = scope.HostKey;
        return rows.Where(row => row.Subject == wanted && row.HostKey == key);
    }

    /// <summary>The coverage rows of a range of one tier, which every read wants whatever its scope: a seek on the same index.</summary>
    internal static IQueryable<AnalyticsRollupRow> CoverageQuery(ConfigDbContext context, AnalyticsTier tier, long from, long to)
    {
        var tierValue = (int)tier;
        var coverage = (int)AnalyticsSubject.Coverage;
        return context.AnalyticsRollups.AsNoTracking().Where(row => row.Tier == tierValue && row.Subject == coverage && row.HostKey == string.Empty && row.Start >= from && row.Start < to);
    }

    private static async Task<List<AnalyticsRow>> QueryAsync(ConfigDbContext context, AnalyticsScope scope, AnalyticsTier tier, long from, long to, CancellationToken cancellationToken)
    {
        if (to <= from)
        {
            return [];
        }

        var rows = await RangeQuery(context, scope, tier, from, to).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (scope.Subject is not null)
        {
            rows.AddRange(await CoverageQuery(context, tier, from, to).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        return [.. rows.Select(ToRow)];
    }

    // The end of the last stretch of a tier that was rolled up: its newest coverage row plus its width. 0 while nothing has been rolled up.
    internal static async Task<long> RolledEndAsync(ConfigDbContext context, AnalyticsTier tier, CancellationToken cancellationToken)
    {
        var tierValue = (int)tier;
        var coverage = (int)AnalyticsSubject.Coverage;
        var newest = await context.AnalyticsRollups.AsNoTracking()
            .Where(row => row.Tier == tierValue && row.Subject == coverage)
            .OrderByDescending(row => row.Start)
            .Select(row => (long?)row.Start)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return newest is { } start ? start + (tier == AnalyticsTier.Day ? DaySeconds : HourSeconds) : 0;
    }

    internal static Task<long> RolledEndAsync(ConfigDbContext context, CancellationToken cancellationToken) => RolledEndAsync(context, AnalyticsTier.Hour, cancellationToken);

    /// <summary>
    /// Merges the five-minute rows of every hour that ended at least ten minutes ago and has none yet into hourly rows, one transaction an
    /// hour, and then the hourly rows of every day whose hours are all rolled up into daily rows. Returns how many hours and days were rolled up.
    /// Idempotent: a stretch that has its rows is skipped.
    /// </summary>
    public async Task<int> RollUpAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var limit = Floor((now - RollUpDelay).ToUnixTimeSeconds(), HourSeconds);
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rolledEnd = await RolledEndAsync(context, AnalyticsTier.Hour, cancellationToken).ConfigureAwait(false);
            var fine = (int)AnalyticsTier.Fine;
            var starts = await context.AnalyticsRollups.AsNoTracking()
                .Where(row => row.Tier == fine && row.Start >= rolledEnd && row.Start < limit)
                .Select(row => row.Start)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var hours = starts.Select(static start => Floor(start, HourSeconds)).Distinct().Order().ToList();
            foreach (var hour in hours)
            {
                await RollUpPeriodAsync(context, AnalyticsTier.Fine, AnalyticsTier.Hour, hour, HourSeconds, scale: 2, cancellationToken).ConfigureAwait(false);
            }

            // A day is rolled up once every hour of it is: the last hourly coverage row is at or past its end.
            var rolledHours = hours.Count > 0 ? await RolledEndAsync(context, AnalyticsTier.Hour, cancellationToken).ConfigureAwait(false) : rolledEnd;
            var rolledDays = await RolledEndAsync(context, AnalyticsTier.Day, cancellationToken).ConfigureAwait(false);
            var hourTier = (int)AnalyticsTier.Hour;
            var hourStarts = await context.AnalyticsRollups.AsNoTracking()
                .Where(row => row.Tier == hourTier && row.Start >= rolledDays && row.Start < rolledHours)
                .Select(row => row.Start)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var days = hourStarts.Select(static start => Floor(start, DaySeconds)).Distinct().Where(day => day + DaySeconds <= rolledHours).Order().ToList();
            foreach (var day in days)
            {
                await RollUpPeriodAsync(context, AnalyticsTier.Hour, AnalyticsTier.Day, day, DaySeconds, scale: 4, cancellationToken).ConfigureAwait(false);
            }

            return hours.Count + days.Count;
        }
    }

    // One stretch of the finer tier merged into one row per host of the coarser: counts add, visitors union, the sketches merge at `scale` times a
    // five-minute bucket's size. The coverage row is the seconds of the finer rows' coverage, capped at the stretch.
    private static async Task RollUpPeriodAsync(ConfigDbContext context, AnalyticsTier from, AnalyticsTier to, long start, int width, int scale, CancellationToken cancellationToken)
    {
        var fromTier = (int)from;
        var rows = await context.AnalyticsRollups.AsNoTracking()
            .Where(row => row.Tier == fromTier && row.Start >= start && row.Start < start + width)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var covered = (int)Math.Min(width, rows.Where(static row => row.Subject == (int)AnalyticsSubject.Coverage).Sum(static row => (long)row.CoveredSeconds));
        // A bucket may carry rows without its coverage row only if the process was never ticked for it; the stretch is still worth keeping.
        context.AnalyticsRollups.Add(new AnalyticsRollupRow
        {
            Id = AnalyticsRollupId.New(),
            Tier = (int)to,
            Subject = (int)AnalyticsSubject.Coverage,
            HostKey = string.Empty,
            Start = start,
            CoveredSeconds = covered,
        });
        foreach (var group in rows.Where(static row => row.Subject != (int)AnalyticsSubject.Coverage).GroupBy(static row => (row.Subject, row.HostKey)))
        {
            TrafficBucket merged = new(scale);
            foreach (var row in group)
            {
                if (row.Visitors is not null && row.Data is not null && TrafficBucketCodec.Decode(new EncodedTrafficBucket(row.Visitors, row.Data)) is { } bucket)
                {
                    merged.MergeFrom(bucket);
                }
            }

            var encoded = TrafficBucketCodec.Encode(merged);
            context.AnalyticsRollups.Add(new AnalyticsRollupRow
            {
                Id = AnalyticsRollupId.New(),
                Tier = (int)to,
                Subject = group.Key.Subject,
                HostKey = group.Key.HostKey,
                Start = start,
                CoveredSeconds = covered,
                Requests = group.Sum(static row => row.Requests),
                BytesOut = group.Sum(static row => row.BytesOut),
                ServerErrors = group.Sum(static row => row.ServerErrors),
                Visitors = encoded.Visitors,
                Data = encoded.Data,
            });
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Deletes what is past retention: any row older than <paramref name="retentionCutoff"/>, and five-minute rows older than
    /// <paramref name="fineCutoff"/> whose hour has been rolled up. Returns how many rows went.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset retentionCutoff, DateTimeOffset fineCutoff, CancellationToken cancellationToken)
    {
        var retention = retentionCutoff.ToUnixTimeSeconds();
        var fineLimit = fineCutoff.ToUnixTimeSeconds();
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var fine = (int)AnalyticsTier.Fine;
            var rolledEnd = await RolledEndAsync(context, cancellationToken).ConfigureAwait(false);
            var rolledLimit = Math.Min(fineLimit, rolledEnd);
            var removed = await context.AnalyticsRollups.Where(row => row.Start < retention).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            return removed + await context.AnalyticsRollups.Where(row => row.Tier == fine && row.Start < rolledLimit).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static long Floor(long value, int width) => value - (((value % width) + width) % width);

    private static long Ceiling(long value, int width) => Floor(value + width - 1, width);
}
