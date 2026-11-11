using System.Collections.Concurrent;
using System.Globalization;

using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Metrics;

/// <summary>
/// The read side of the metrics (PLAN §7.4) that the admin API and the dashboard share: the Prometheus text for the scrape, and the
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
        var (span, bucket) = Reach(range);
        var at = time.GetUtcNow();
        var names = HostNames(snapshots.Current);
        var now = Figures(sampler.SummariseTotal(NowWindow));

        List<MetricsSeriesPoint> series = [];
        List<HostTraffic> hosts = [];
        List<UpstreamTraffic> upstreams = [];
        if (range == MetricsRange.Hour)
        {
            if (sampler.Read(MetricRollupSubject.Total, Guid.Empty) is { } view)
            {
                foreach (var point in view.Points)
                {
                    series.Add(SeriesPoint(point));
                }

                if (view.Open.Requests > 0 || view.Points.Length > 0)
                {
                    series.Add(SeriesPoint(view.Open));
                }
            }

            foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Host, span))
            {
                var id = owner == Guid.Empty ? default : HostId.FromValue(owner);
                hosts.Add(HostRow(id, names, point));
            }

            foreach (var (owner, point) in sampler.Summarise(MetricRollupSubject.Upstream, span))
            {
                upstreams.Add(UpstreamRow(UpstreamId.FromValue(owner), point));
            }
        }
        else
        {
            var read = await RollupsAsync(range, at, span, bucket, cancellationToken).ConfigureAwait(false);
            foreach (var point in read.Series)
            {
                series.Add(SeriesPoint(point));
            }

            foreach (var (text, point) in read.Hosts)
            {
                hosts.Add(HostRow(text.Length == 0 ? default : HostId.TryParse(text, null, out var host) ? host : default, names, point));
            }

            foreach (var (text, point) in read.Upstreams)
            {
                if (UpstreamId.TryParse(text, null, out var upstream))
                {
                    upstreams.Add(UpstreamRow(upstream, point));
                }
            }
        }

        return new MetricsOverview(
            at,
            range,
            bucket,
            now,
            series,
            [.. hosts.Where(static host => host.Requests > 0).OrderByDescending(static host => host.Requests).ThenBy(static host => host.Name, StringComparer.OrdinalIgnoreCase).Take(TopRows)],
            [.. upstreams.Where(static upstream => upstream.Requests > 0).OrderByDescending(static upstream => upstream.Errors).ThenByDescending(static upstream => upstream.Requests).Take(TopRows)]);
    }

    /// <summary>How long a longer range's rollup read is kept. The rollups gain a row per series a minute, and a month is millions of rows to sum, so a page polling every twenty seconds must not read them every time.</summary>
    private static readonly TimeSpan RollupCacheFor = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<MetricsRange, RollupRead> _rollupCache = new();

    private async Task<RollupRead> RollupsAsync(MetricsRange range, DateTimeOffset at, TimeSpan span, TimeSpan bucket, CancellationToken cancellationToken)
    {
        if (_rollupCache.TryGetValue(range, out var cached) && at - cached.At < RollupCacheFor && at >= cached.At)
        {
            return cached;
        }

        var from = at - span;
        var read = new RollupRead(
            at,
            await rollups.ReadSeriesAsync(MetricRollupSubject.Total, string.Empty, from, at, (int)bucket.TotalMinutes, cancellationToken).ConfigureAwait(false),
            await rollups.SummariseAsync(MetricRollupSubject.Host, from, at, cancellationToken).ConfigureAwait(false),
            await rollups.SummariseAsync(MetricRollupSubject.Upstream, from, at, cancellationToken).ConfigureAwait(false));
        _rollupCache[range] = read;
        return read;
    }

    private sealed record RollupRead(DateTimeOffset At, IReadOnlyList<MetricPoint> Series, IReadOnlyDictionary<string, MetricPoint> Hosts, IReadOnlyDictionary<string, MetricPoint> Upstreams);

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
        point.Seconds <= 0 ? 0 : (double)point.Status5xx / point.Seconds,
        point.Seconds <= 0 ? 0 : (double)point.Refused / point.Seconds,
        new LatencyFigures(Milliseconds(point.TotalP50), Milliseconds(point.TotalP95), Milliseconds(point.TotalP99)),
        new LatencyFigures(Milliseconds(point.UpstreamP50), Milliseconds(point.UpstreamP95), Milliseconds(point.UpstreamP99)));

    private static MetricsFigures Figures(MetricPoint point) => new(
        point.RequestsPerSecond,
        point.ErrorRate,
        point.Seconds <= 0 ? 0 : (double)point.Refused / point.Seconds,
        point.Seconds <= 0 ? 0 : (double)point.BytesOut / point.Seconds,
        new LatencyFigures(Milliseconds(point.TotalP50), Milliseconds(point.TotalP95), Milliseconds(point.TotalP99)),
        new LatencyFigures(Milliseconds(point.UpstreamP50), Milliseconds(point.UpstreamP95), Milliseconds(point.UpstreamP99)));

    private static double Milliseconds(int micros) => micros / 1000.0;
}
