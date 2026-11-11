using System.Text.Json.Serialization;

using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Metrics;

/// <summary>How far back the overview looks. The hour comes from the sampler's memory, the rest from the one-minute rollups.</summary>
[JsonConverter(typeof(ConfigEnumConverter<MetricsRange>))]
public enum MetricsRange
{
    Hour = 0,
    Day = 1,
    Week = 2,
    Month = 3,
}

/// <summary>Latency percentiles in milliseconds; all 0 when nothing was timed.</summary>
public sealed record LatencyFigures(double P50Ms, double P95Ms, double P99Ms);

/// <summary>The proxy's pace over the last half minute.</summary>
/// <param name="RequestsPerSecond">Requests answered a second, the connections the gate dropped included.</param>
/// <param name="ErrorRate">The share of requests answered with a 5xx, 0 to 1.</param>
/// <param name="RefusedPerSecond">Requests the gate refused by a policy or a limit, a second.</param>
/// <param name="BytesPerSecond">Response bytes a second, over the responses that declare a length.</param>
/// <param name="Total">Time from the request's first byte to the end of the response.</param>
/// <param name="Upstream">Time spent forwarding and waiting for the upstream; the difference to <paramref name="Total"/> is the proxy's own.</param>
public sealed record MetricsFigures(double RequestsPerSecond, double ErrorRate, double RefusedPerSecond, double BytesPerSecond, LatencyFigures Total, LatencyFigures Upstream);

/// <summary>One step of the chart: the figures over <see cref="MetricsOverview.BucketWidth"/> starting at <paramref name="Start"/>.</summary>
public sealed record MetricsSeriesPoint(
    DateTimeOffset Start,
    double RequestsPerSecond,
    double ErrorsPerSecond,
    double RefusedPerSecond,
    LatencyFigures Total,
    LatencyFigures Upstream);

/// <summary>A host's traffic over the range.</summary>
/// <param name="Id">The host; <see cref="HostId.Empty"/> is the requests no host owns (an unknown name).</param>
/// <param name="Name">The host's first name in the live config, or its ID when it is gone.</param>
/// <param name="Requests">Requests answered over the range.</param>
/// <param name="ErrorRate">The share answered with a 5xx, 0 to 1.</param>
/// <param name="Refused">Requests the gate refused.</param>
/// <param name="P95Ms">The 95th-percentile total time; over a longer range it is the mean of the minutes' percentiles, weighted by their requests.</param>
public sealed record HostTraffic(HostId Id, string Name, long Requests, double ErrorRate, long Refused, double P95Ms);

/// <summary>An upstream's forwards over the range.</summary>
/// <param name="Id">The upstream.</param>
/// <param name="Requests">Requests forwarded to it.</param>
/// <param name="Errors">Forwards that failed on its side: a refused or timed-out connection, a broken response, no destination to try.</param>
/// <param name="ErrorRate"><paramref name="Errors"/> over <paramref name="Requests"/>, 0 to 1.</param>
/// <param name="P95Ms">The 95th-percentile upstream time, as <see cref="HostTraffic.P95Ms"/>.</param>
public sealed record UpstreamTraffic(UpstreamId Id, long Requests, long Errors, double ErrorRate, double P95Ms);

/// <summary>
/// The overview page's data: the pace now, a chart over the range, the busiest hosts and the upstreams' errors. Read from the
/// sampler's rings for the hour and from the persisted one-minute rollups for longer.
/// </summary>
/// <param name="At">When it was read.</param>
/// <param name="Range">How far back <paramref name="Series"/> and the tables reach.</param>
/// <param name="BucketWidth">The width of one <paramref name="Series"/> step.</param>
/// <param name="Now">The last half minute.</param>
/// <param name="Series">The chart, oldest first.</param>
/// <param name="Hosts">The busiest hosts over the range, most requests first.</param>
/// <param name="Upstreams">The upstreams that were forwarded to over the range, most errors first.</param>
public sealed record MetricsOverview(
    DateTimeOffset At,
    MetricsRange Range,
    TimeSpan BucketWidth,
    MetricsFigures Now,
    IReadOnlyList<MetricsSeriesPoint> Series,
    IReadOnlyList<HostTraffic> Hosts,
    IReadOnlyList<UpstreamTraffic> Upstreams);
