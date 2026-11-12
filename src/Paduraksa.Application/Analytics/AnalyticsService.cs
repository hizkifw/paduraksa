using Paduraksa.Contracts.Analytics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Metrics;

namespace Paduraksa.Application.Analytics;

/// <summary>
/// The read side of the traffic analytics (PLAN §7.4) that the admin API and the dashboard share: the stored rows of a range, the closed
/// buckets still waiting to be written and the buckets that are open now, merged into one overview. Reads only; never changes anything.
/// </summary>
/// <remarks>
/// <para><b>Merging.</b> Every row's counts add. Visitors are the union of the rows' sketches (a visitor seen on two hosts or in two buckets is
/// one), the top tables are the merge of the rows' Space-Saving sketches into a larger one, so a key keeps the bound the sum of its buckets' errors gives
/// (<see cref="HeavyHitters"/>), and a path's 95th percentile comes from the sum of its histograms, over the requests seen since it was
/// tracked.</para>
/// <para><b>Gaps.</b> The chart runs from the first step the process observed to the last; a step in between that no coverage row touches is
/// a gap (<see cref="AnalyticsStep.CoveredSeconds"/> 0), never a zero. The part of the range before the first observed step is not a gap.</para>
/// <para><b>Cost.</b> A read decodes every row of its range, so a longer range reads the coarser rows: the day's whole hours come from hourly rows
/// and a month of twenty hosts is at most 14 400 of them. A result is kept briefly (the page polls every few seconds) so a page left open
/// doesn't read the table each time.</para>
/// </remarks>
public sealed class AnalyticsService(AnalyticsRollupStore store, TrafficAnalytics live, TimeProvider time)
{
    /// <summary>The rows each table shows.</summary>
    public const int TopRows = 10;

    /// <summary>Fewer requests than this for a path and its percentile is noise: the slowest-paths table leaves it out.</summary>
    public const int MinimumSampleForSlowest = 5;

    /// <summary>How the merged range's tables are sized, as a multiple of a five-minute bucket's: large enough that merging a month's buckets rarely drops a key that matters.</summary>
    internal const int AccumulatorScale = 16;

    private static readonly TimeSpan HourCache = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongCache = TimeSpan.FromSeconds(30);

    /// <summary>The most results kept. The API accepts any well-formed host ID (a deleted host's history is readable), so without a bound every ID a caller tries would stay.</summary>
    internal const int MaxCached = 128;

    private readonly Lock _cacheGate = new();
    private readonly Dictionary<(MetricsRange, AnalyticsScope), (DateTimeOffset At, AnalyticsOverview Overview)> _cache = [];

    /// <summary>Results kept now.</summary>
    internal int CachedCount
    {
        get
        {
            lock (_cacheGate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>How far each range reaches and how wide one chart step is.</summary>
    public static (TimeSpan Span, int StepSeconds) Reach(MetricsRange range) => range switch
    {
        MetricsRange.Hour => (TimeSpan.FromHours(1), 300),
        MetricsRange.Day => (TimeSpan.FromDays(1), 3_600),
        MetricsRange.Week => (TimeSpan.FromDays(7), 3 * 3_600),
        MetricsRange.Month => (TimeSpan.FromDays(30), 86_400),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Undefined metrics range."),
    };

    /// <summary>The analytics of <paramref name="range"/>: for the requests no host owns when <paramref name="unmatched"/>, for <paramref name="host"/> when it is set, otherwise for every host.</summary>
    public async Task<AnalyticsOverview> ReadOverviewAsync(MetricsRange range, HostId? host, bool unmatched, CancellationToken cancellationToken)
    {
        var scope = unmatched ? AnalyticsScope.Unmatched : host is { } id ? AnalyticsScope.Host(id.ToString()) : AnalyticsScope.All;
        var at = time.GetUtcNow();
        if (TryCached(range, scope, at, out var kept))
        {
            return kept;
        }

        var (span, step) = Reach(range);
        var from = at - span;
        var read = await store.ReadAsync(scope, from, at, Coarsest(range), cancellationToken).ConfigureAwait(false);
        var overview = Build(range, step, host, unmatched, at, from, read.Rows, read.Unwritten, live.Live(scope.Subject, host ?? default));
        Keep(range, scope, at, overview);
        return overview;
    }

    private bool TryCached(MetricsRange range, AnalyticsScope scope, DateTimeOffset at, out AnalyticsOverview overview)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue((range, scope), out var cached) && at >= cached.At && at - cached.At < Lifetime(range))
            {
                overview = cached.Overview;
                return true;
            }
        }

        overview = null!;
        return false;
    }

    // Expired results go first; if every one is still fresh (a caller walking host IDs), the oldest goes.
    private void Keep(MetricsRange range, AnalyticsScope scope, DateTimeOffset at, AnalyticsOverview overview)
    {
        lock (_cacheGate)
        {
            if (_cache.Count >= MaxCached && !_cache.ContainsKey((range, scope)))
            {
                foreach (var key in new List<(MetricsRange, AnalyticsScope)>(_cache.Keys))
                {
                    if (at - _cache[key].At >= Lifetime(key.Item1) || at < _cache[key].At)
                    {
                        _cache.Remove(key);
                    }
                }

                if (_cache.Count >= MaxCached)
                {
                    var oldest = default((MetricsRange, AnalyticsScope));
                    var oldestAt = DateTimeOffset.MaxValue;
                    foreach (var (key, value) in _cache)
                    {
                        if (value.At < oldestAt)
                        {
                            oldest = key;
                            oldestAt = value.At;
                        }
                    }

                    _cache.Remove(oldest);
                }
            }

            _cache[(range, scope)] = (at, overview);
        }
    }

    // The coarsest rows a range can draw as a step: the hour's steps are five minutes, the day's and the week's are whole hours or more, the month's a day.
    private static AnalyticsTier Coarsest(MetricsRange range) => range switch
    {
        MetricsRange.Hour => AnalyticsTier.Fine,
        MetricsRange.Month => AnalyticsTier.Day,
        _ => AnalyticsTier.Hour,
    };

    private static TimeSpan Lifetime(MetricsRange range) => range == MetricsRange.Hour ? HourCache : LongCache;

    private static AnalyticsOverview Build(
        MetricsRange range, int step, HostId? host, bool unmatched, DateTimeOffset at, DateTimeOffset from,
        IReadOnlyList<AnalyticsRow> rows, IReadOnlyList<AnalyticsRow> unwritten, IReadOnlyList<LiveBucket> open)
    {
        var fromSeconds = from.ToUnixTimeSeconds();
        TrafficBucket total = new(AccumulatorScale);
        SortedDictionary<long, StepAccumulator> steps = [];
        long covered = 0;

        void Add(AnalyticsRow row, TrafficBucket? decoded)
        {
            var stepStart = row.Start - (row.Start % step);
            if (!steps.TryGetValue(stepStart, out var accumulator))
            {
                accumulator = new StepAccumulator();
                steps[stepStart] = accumulator;
            }

            if (row.Subject == AnalyticsSubject.Coverage)
            {
                accumulator.Covered += row.CoveredSeconds;
                covered += row.CoveredSeconds;
                return;
            }

            if (decoded is null)
            {
                return;
            }

            accumulator.Requests += decoded.Requests;
            accumulator.BytesOut += decoded.BytesOut;
            accumulator.ServerErrors += decoded.ServerErrors;
            accumulator.Visitors.Merge(decoded.Visitors);
            total.MergeFrom(decoded);
        }

        foreach (var row in rows)
        {
            Add(row, Decode(row));
        }

        foreach (var row in unwritten)
        {
            Add(row, Decode(row));
        }

        foreach (var bucket in open)
        {
            if (bucket.Start + TrafficAnalytics.BucketSeconds <= fromSeconds)
            {
                continue;
            }

            Add(
                new AnalyticsRow(AnalyticsTier.Fine, bucket.Subject, bucket.Host.IsEmpty ? string.Empty : bucket.Host.ToString(), bucket.Start, bucket.CoveredSeconds, 0, 0, 0, null),
                bucket.Bucket);
        }

        var series = Layout(steps, step);
        var bots = 0L;
        for (var kind = 1; kind < total.BotClasses.Length; kind++)
        {
            bots += total.BotClasses[kind];
        }

        AnalyticsTotals totals = new(
            total.Requests,
            total.BytesOut,
            total.Visitors.Estimate(),
            total.ServerErrors,
            total.Requests == 0 ? 0 : (double)total.ServerErrors / total.Requests,
            bots,
            covered);
        var paths = total.Paths.Top(int.MaxValue);
        return new AnalyticsOverview(
            at,
            range,
            step,
            totals,
            series,
            Classes(total.StatusClasses),
            Counts(total.Statuses, TopRows),
            Counts(total.Methods, TopRows),
            Protocols(total.Protocols),
            [.. paths.Take(TopRows).Select(Path)],
            [.. total.Referers.Top(TopRows).Select(Count)],
            [.. total.UserAgents.Top(TopRows).Select(Count)],
            [.. paths.Where(static path => path.Requests >= MinimumSampleForSlowest).OrderByDescending(static path => path.P95Micros).ThenBy(static path => path.Key, StringComparer.Ordinal).Take(TopRows).Select(Path)],
            [.. total.Largest.Top(TopRows).Select(Count)],
            Families(total.Browsers),
            Counts(total.Browsers, TopRows),
            Counts(total.OperatingSystems, TopRows),
            Devices(total.Devices),
            BotKinds(total.BotClasses),
            Bots(total.BotNames, TopRows),
            Bots(total.IgnoredRobots, TopRows),
            host,
            unmatched);
    }

    private static TrafficBucket? Decode(AnalyticsRow row) => row.Bucket is null ? null : TrafficBucketCodec.Decode(row.Bucket);

    // The series from the first observed step to the last, the steps nobody observed in between present as gaps.
    private static List<AnalyticsStep> Layout(SortedDictionary<long, StepAccumulator> steps, int width)
    {
        long? first = null;
        long? last = null;
        foreach (var (start, accumulator) in steps)
        {
            if (accumulator.Covered > 0 || accumulator.Requests > 0)
            {
                first ??= start;
                last = start;
            }
        }

        List<AnalyticsStep> series = [];
        if (first is null || last is null)
        {
            return series;
        }

        for (var start = first.Value; start <= last.Value; start += width)
        {
            if (!steps.TryGetValue(start, out var accumulator) || (accumulator.Covered == 0 && accumulator.Requests == 0))
            {
                series.Add(new AnalyticsStep(DateTimeOffset.FromUnixTimeSeconds(start), width, 0, 0, 0, 0, 0));
                continue;
            }

            series.Add(new AnalyticsStep(
                DateTimeOffset.FromUnixTimeSeconds(start),
                width,
                // A step with requests and no coverage row (entries filed in a stretch the drain didn't tick through) is data, not a gap: it is
                // drawn, as observed for the moment it is known to have had, so the chart and the totals agree.
                (int)Math.Max(1, Math.Min(width, accumulator.Covered)),
                accumulator.Requests,
                accumulator.BytesOut,
                accumulator.Visitors.Estimate(),
                accumulator.ServerErrors));
        }

        return series;
    }

    private static AnalyticsPath Path(HeavyHitter path) => new(
        path.Key,
        path.Count,
        path.Error,
        path.Bytes,
        path.P95Micros / 1000.0,
        path.Requests == 0 ? 0 : (double)path.ServerErrors / path.Requests);

    private static AnalyticsCount Count(HeavyHitter hitter) => new(hitter.Key, hitter.Count, hitter.Error);

    private static List<AnalyticsCount> Counts(BoundedCounter counter, int limit)
    {
        List<AnalyticsCount> counts = [];
        foreach (var (label, count) in counter.Ranked())
        {
            if (counts.Count == limit)
            {
                break;
            }

            counts.Add(new AnalyticsCount(label, count, 0));
        }

        if (counter.Other > 0)
        {
            counts.Add(new AnalyticsCount("other", counter.Other, 0));
        }

        return counts;
    }

    private static List<AnalyticsCount> Classes(long[] classes)
    {
        List<AnalyticsCount> counts = [];
        string[] labels = ["none", "1xx", "2xx", "3xx", "4xx", "5xx"];
        for (var i = 0; i < labels.Length; i++)
        {
            counts.Add(new AnalyticsCount(labels[i], classes[i], 0));
        }

        return counts;
    }

    private static List<AnalyticsCount> Protocols(long[] protocols)
    {
        string[] labels = ["other", "HTTP/1.0", "HTTP/1.1", "HTTP/2", "HTTP/3"];
        List<AnalyticsCount> counts = [];
        for (var i = 1; i < labels.Length; i++)
        {
            counts.Add(new AnalyticsCount(labels[i], protocols[i], 0));
        }

        counts.Add(new AnalyticsCount(labels[0], protocols[0], 0));
        return counts;
    }

    private static List<AnalyticsCount> Devices(long[] devices)
    {
        List<AnalyticsCount> counts = [];
        foreach (var device in Enum.GetValues<DeviceClass>())
        {
            counts.Add(new AnalyticsCount(CamelCase(device.ToString()), devices[(int)device], 0));
        }

        return counts;
    }

    private static List<AnalyticsBot> BotKinds(long[] kinds)
    {
        List<AnalyticsBot> counts = [];
        foreach (var kind in Enum.GetValues<BotClass>())
        {
            if (kind != BotClass.None)
            {
                counts.Add(new AnalyticsBot(CamelCase(kind.ToString()), string.Empty, kinds[(int)kind]));
            }
        }

        return counts;
    }

    private static List<AnalyticsBot> Bots(BoundedCounter counter, int limit)
    {
        List<AnalyticsBot> bots = [];
        foreach (var (key, count) in counter.Ranked())
        {
            if (bots.Count == limit)
            {
                break;
            }

            var (kind, name) = UserAgentInfo.ParseBotKey(key);
            bots.Add(new AnalyticsBot(CamelCase(kind.ToString()), name, count));
        }

        return bots;
    }

    // "Chrome 131" and "Chrome 130" are one family.
    private static List<AnalyticsCount> Families(BoundedCounter versions)
    {
        Dictionary<string, long> families = new(StringComparer.Ordinal);
        foreach (var (label, count) in versions.Counts)
        {
            var space = label.LastIndexOf(' ');
            var family = space > 0 && int.TryParse(label.AsSpan(space + 1), out _) ? label[..space] : label;
            families[family] = families.GetValueOrDefault(family) + count;
        }

        List<AnalyticsCount> counts = [.. families.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal).Take(TopRows).Select(static pair => new AnalyticsCount(pair.Key, pair.Value, 0))];
        if (versions.Other > 0)
        {
            counts.Add(new AnalyticsCount("other", versions.Other, 0));
        }

        return counts;
    }

    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private sealed class StepAccumulator
    {
        public double Covered { get; set; }

        public long Requests { get; set; }

        public long BytesOut { get; set; }

        public long ServerErrors { get; set; }

        public HyperLogLog Visitors { get; } = new();
    }
}
