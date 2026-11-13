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
/// <param name="BytesPerSecond">Response body bytes written to clients a second.</param>
/// <param name="BytesInPerSecond">Request body bytes read from clients a second.</param>
/// <param name="Total">Time from the request's first byte to the end of the response.</param>
/// <param name="Upstream">Time spent forwarding and waiting for the upstream; the difference to <paramref name="Total"/> is the proxy's own.</param>
public sealed record MetricsFigures(double RequestsPerSecond, double ErrorRate, double RefusedPerSecond, double BytesPerSecond, double BytesInPerSecond, LatencyFigures Total, LatencyFigures Upstream);

/// <summary>
/// One step of the chart: the figures over the <paramref name="WidthSeconds"/> starting at <paramref name="Start"/>. A step whose
/// <paramref name="CoveredSeconds"/> is 0 has no data: Paduraksa was not running (every figure is 0, and a chart should break its
/// line there rather than draw a zero). A step that was covered and saw no requests has a rate of 0 and no latency (0).
/// </summary>
/// <param name="Start">The step's first second.</param>
/// <param name="RequestsPerSecond">Requests answered a second over the seconds that were observed, not the step's whole width.</param>
/// <param name="ErrorsPerSecond">5xx answers a second, over the observed seconds.</param>
/// <param name="RefusedPerSecond">Requests the gate refused by a policy or a limit, a second over the observed seconds.</param>
/// <param name="Total">Latency percentiles of the requests answered in the step.</param>
/// <param name="Upstream">Upstream time percentiles of the requests forwarded in the step.</param>
/// <param name="WidthSeconds">How long the step is. Usually <see cref="MetricsOverview.BucketWidth"/>; the hour's steps from before the server started are a minute wide (the one it started in is cut short where the ten-second steps begin).</param>
/// <param name="CoveredSeconds">How many of those seconds the server was running to count them; less than the width for a step the server started or stopped in, and 0 for no data.</param>
public sealed record MetricsSeriesPoint(
    DateTimeOffset Start,
    double RequestsPerSecond,
    double ErrorsPerSecond,
    double RefusedPerSecond,
    LatencyFigures Total,
    LatencyFigures Upstream,
    int WidthSeconds,
    int CoveredSeconds);

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
/// sampler's memory (and the last hour of rollups it started from) for the hour and from the persisted one-minute rollups for longer.
/// </summary>
/// <param name="At">When it was read.</param>
/// <param name="Range">How far back <paramref name="Series"/> and the tables reach.</param>
/// <param name="BucketWidth">The usual width of one <paramref name="Series"/> step; see <see cref="MetricsSeriesPoint.WidthSeconds"/> for each step's own.</param>
/// <param name="Now">The last half minute.</param>
/// <param name="Series">
/// The chart, oldest first, from the first step the server observed to the last: every step in between is there, and one the server
/// was not running for has <see cref="MetricsSeriesPoint.CoveredSeconds"/> 0 (no data), so downtime is a gap and not a line through it.
/// </param>
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
