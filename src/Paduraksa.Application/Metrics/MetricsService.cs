using System.Collections.Concurrent;
using System.Globalization;

using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// The read side of the metrics (docs/design/observability.md "Metrics (M9)") that the admin API and the dashboard share: the Prometheus text for the scrape, and the
/// overview with its chart, busiest hosts and upstream errors. Reads the registry, the sampler's rings and the rollup store; never
/// changes anything.
/// </summary>
public sealed class MetricsService(
    MetricsRegistry registry, SnapshotStore snapshots, MetricSampler sampler, MetricRollupStore rollups, ProcessCounters process, TimeProvider time)
{
    /// <summary>The media type of <see cref="PrometheusText"/>.</summary>
    public const string PrometheusContentType = PrometheusWriter.ContentType;

    /// <summary>The pace is read over this long, so a rate is steady and a quiet second doesn't read as zero.</summary>
    public static readonly TimeSpan NowWindow = TimeSpan.FromSeconds(30);

    /// <summary>The rows the tables show.</summary>
    public const int TopRows = 8;

    /// <summary>How far each range reaches, and the width of one chart step (a ring bucket for the hour, minutes of rollups for the rest).</summary>
    public static (TimeSpan Span, TimeSpan Bucket) Reach(MetricsRange range) => range switch
    {
        MetricsRange.Hour => (TimeSpan.FromHours(1), TimeSpan.FromSeconds(10)),
        MetricsRange.Day => (TimeSpan.FromDays(1), TimeSpan.FromMinutes(6)),
        MetricsRange.Week => (TimeSpan.FromDays(7), TimeSpan.FromMinutes(42)),
        MetricsRange.Month => (TimeSpan.FromDays(30), TimeSpan.FromHours(3)),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Undefined metrics range."),
    };

    /// <summary>The counters in the Prometheus text format, labelled from the live config.</summary>
    public string PrometheusText()
    {
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        PrometheusWriter.Write(writer, registry, snapshots.Current, process);
        return writer.ToString();
    }

    public async Task<MetricsOverview> ReadOverviewAsync(MetricsRange range, CancellationToken cancellationToken)
    {
        var (_, bucket) = Reach(range);
        var at = time.GetUtcNow();
        var names = HostNames(snapshots.Current);
        var now = Figures(sampler.SummariseTotal(NowWindow));
        var read = await ReadRangeAsync(range, at, cancellationToken).ConfigureAwait(false);
        var hosts = read.Hosts.Select(pair => HostRow(pair.Key, names, pair.Value));
        var upstreams = read.Upstreams.Select(static pair => UpstreamRow(pair.Key, pair.Value));

        return new MetricsOverview(
            at,
            range,
            bucket,
            now,
            [.. read.Series.Select(SeriesPoint)],
            [.. hosts.Where(static host => host.Requests > 0).OrderByDescending(static host => host.Requests).ThenBy(static host => host.Name, StringComparer.OrdinalIgnoreCase).Take(TopRows)],
            [.. upstreams.Where(static upstream => upstream.Requests > 0).OrderByDescending(static upstream => upstream.Errors).ThenByDescending(static upstream => upstream.Requests).Take(TopRows)]);
    }

    /// <summary>
    /// The range's figures as the sampler and the rollups have them: the totals' steps laid out in time (<see cref="MetricTimeline"/>),
    /// and one point per host (the unmatched requests under the empty ID) and per upstream. The hour is the seed and the ring; the
    /// longer ranges are the rollups, read at most once every 30 s. The overview and the security statistics both read this.
    /// <para>A longer range's hosts and upstreams take their last hour from the sampler (<see cref="RecentFrom"/>) and only what is before
    /// it from the rollups, so a request is in them at once rather than after its minute is written. The steps stay the rollups', and their
    /// last minute or two lag by up to a minute plus the write interval.</para>
    /// </summary>
    internal async Task<RangeFigures> ReadRangeAsync(MetricsRange range, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (span, bucket) = Reach(range);
        Dictionary<HostId, MetricPoint> hosts = [];
        Dictionary<UpstreamId, MetricPoint> upstreams = [];
        if (range == MetricsRange.Hour)
        {
            var series = MetricTimeline.Hour(sampler.History(MetricRollupSubject.Total, Guid.Empty), sampler.Read(MetricRollupSubject.Total, Guid.Empty), at);
            foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Host, span))
            {
                hosts[owner == Guid.Empty ? default : HostId.FromValue(owner)] = point;
            }

            foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Upstream, span))
            {
                upstreams[UpstreamId.FromValue(owner)] = point;
            }

            return new RangeFigures(series, hosts, upstreams);
        }

        var recent = RecentFrom(at);
        var read = await RollupsAsync(range, at, span, bucket, recent, cancellationToken).ConfigureAwait(false);
        foreach (var (text, point) in read.Hosts)
        {
            // A key that isn't a host ID can only be the unmatched requests' empty one.
            Add(hosts, text.Length > 0 && HostId.TryParse(text, null, out var parsed) ? parsed : default, point);
        }

        foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Host, at - recent))
        {
            Add(hosts, owner == Guid.Empty ? default : HostId.FromValue(owner), point);
        }

        foreach (var (text, point) in read.Upstreams)
        {
            if (UpstreamId.TryParse(text, null, out var upstream))
            {
                Add(upstreams, upstream, point);
            }
        }

        foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Upstream, at - recent))
        {
            Add(upstreams, UpstreamId.FromValue(owner), point);
        }

        return new RangeFigures(MetricTimeline.Steps(read.Series, (int)bucket.TotalSeconds), hosts, upstreams);
    }

    /// <summary>
    /// Where a longer range's figures switch from the rollups to the sampler: the first whole minute of the last hour. The sampler holds every
    /// minute from there on (the seed reaches an hour before the boot, the ring and its minutes the rest), and the rollups are read only to
    /// the minute before, so no minute comes from both.
    /// </summary>
    internal static DateTimeOffset RecentFrom(DateTimeOffset at) =>
        DateTimeOffset.FromUnixTimeSeconds((at.ToUnixTimeSeconds() - MetricSampler.RingSeconds + 59) / 60 * 60);

    private static void Add<TKey>(Dictionary<TKey, MetricPoint> points, TKey key, MetricPoint point)
        where TKey : notnull =>
        points[key] = points.TryGetValue(key, out var other) ? MetricPoints.Combine([other, point], other.Start, other.Width) : point;

    internal sealed record RangeFigures(IReadOnlyList<MetricPoint> Series, IReadOnlyDictionary<HostId, MetricPoint> Hosts, IReadOnlyDictionary<UpstreamId, MetricPoint> Upstreams);

    /// <summary>How long a longer range's rollup read is kept. The rollups gain a row per series a minute, and a month is millions of rows to sum, so a page polling every twenty seconds must not read them every time.</summary>
    private static readonly TimeSpan RollupCacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<MetricsRange, RollupRead> _rollupCache = new();

    // A cached read is kept only while its split minute is still the current one: past it, a minute would be in neither half.
    private async Task<RollupRead> RollupsAsync(MetricsRange range, DateTimeOffset at, TimeSpan span, TimeSpan bucket, DateTimeOffset recent, CancellationToken cancellationToken)
    {
        if (_rollupCache.TryGetValue(range, out var cached) && at - cached.At < RollupCacheFor && at >= cached.At && cached.Recent == recent)
        {
            return cached;
        }

        var from = at - span;
        var before = recent.AddMinutes(-1);
        var read = new RollupRead(
            at,
            recent,
            await rollups.ReadSeriesAsync(MetricRollupSubject.Total, string.Empty, from, at, (int)bucket.TotalMinutes, cancellationToken).ConfigureAwait(false),
            await rollups.SummariseAsync(MetricRollupSubject.Host, from, before, cancellationToken).ConfigureAwait(false),
            await rollups.SummariseAsync(MetricRollupSubject.Upstream, from, before, cancellationToken).ConfigureAwait(false));
        _rollupCache[range] = read;
        return read;
    }

    private sealed record RollupRead(DateTimeOffset At, DateTimeOffset Recent, IReadOnlyList<MetricPoint> Series, IReadOnlyDictionary<string, MetricPoint> Hosts, IReadOnlyDictionary<string, MetricPoint> Upstreams);

    private static Dictionary<HostId, string> HostNames(RuntimeSnapshot snapshot)
    {
        Dictionary<HostId, string> names = [];
        foreach (var host in snapshot.HostsByIndex)
        {
            names[host.Id] = host.Names.Count > 0 ? host.Names[0] : host.Id.ToString();
        }

        return names;
    }

    private static HostTraffic HostRow(HostId id, Dictionary<HostId, string> names, MetricPoint point) => new(
        id,
        id.IsEmpty ? "No host matched" : names.TryGetValue(id, out var name) ? name : id.ToString(),
        point.Requests,
        point.ErrorRate,
        point.Refused,
        Milliseconds(point.TotalP95));

    private static UpstreamTraffic UpstreamRow(UpstreamId id, MetricPoint point) => new(
        id, point.Requests, point.Errors, point.Requests == 0 ? 0 : (double)point.Errors / point.Requests, Milliseconds(point.UpstreamP95));

    private static MetricsSeriesPoint SeriesPoint(MetricPoint point) => new(
        DateTimeOffset.FromUnixTimeSeconds(point.Start),
        point.RequestsPerSecond,
        point.PerSecond(point.Status5xx),
        point.PerSecond(point.Refused),
        new LatencyFigures(Milliseconds(point.TotalP50), Milliseconds(point.TotalP95), Milliseconds(point.TotalP99)),
        new LatencyFigures(Milliseconds(point.UpstreamP50), Milliseconds(point.UpstreamP95), Milliseconds(point.UpstreamP99)),
        point.Width,
        point.CoveredSeconds);

    private static MetricsFigures Figures(MetricPoint point) => new(
        point.RequestsPerSecond,
        point.ErrorRate,
        point.PerSecond(point.Refused),
        point.PerSecond(point.BytesOut),
        point.PerSecond(point.BytesIn),
        new LatencyFigures(Milliseconds(point.TotalP50), Milliseconds(point.TotalP95), Milliseconds(point.TotalP99)),
        new LatencyFigures(Milliseconds(point.UpstreamP50), Milliseconds(point.UpstreamP95), Milliseconds(point.UpstreamP99)));

    private static double Milliseconds(int micros) => micros / 1000.0;
}
