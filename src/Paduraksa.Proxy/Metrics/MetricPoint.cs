using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// What one series did during one bucket of the sampler's ring or one minute of a rollup: counts, the sum of the durations and the
/// percentiles the bucket's histogram gave (docs/design/observability.md "Metrics (M9)"), and how many of the bucket's seconds the process was there to see.
/// Percentiles are whole microseconds, 0 when nothing was timed.
/// A host series fills every field; an upstream series fills the requests, <see cref="Errors"/>, the bytes and the upstream-time fields;
/// the totals series is the sum over every host, the unmatched slot included.
/// </summary>
public readonly record struct MetricPoint
{
    /// <summary>The bucket's first second, as Unix time.</summary>
    public long Start { get; init; }

    /// <summary>The bucket's length: 10 s in the ring, 60 s in a rollup, more in a grouped range. A summary's is the span it was asked for.</summary>
    public int Width { get; init; }

    /// <summary>
    /// How many seconds of <see cref="Width"/> the process observed. Rates divide by this and never by <see cref="Width"/>, so a half-observed
    /// minute isn't diluted; 0 means nobody was looking (the process was down), which is not the same as a covered bucket that saw no request.
    /// </summary>
    public int CoveredSeconds { get; init; }

    /// <summary>Requests answered (a host or total series) or forwarded (an upstream).</summary>
    public long Requests { get; init; }

    /// <summary>1xx answers: mostly the 101 of a WebSocket upgrade.</summary>
    public long Status1xx { get; init; }

    public long Status2xx { get; init; }

    public long Status3xx { get; init; }

    public long Status4xx { get; init; }

    public long Status5xx { get; init; }

    /// <summary>Requests that got no response at all: a connection the gate dropped on purpose.</summary>
    public long NoResponse { get; init; }

    /// <summary>Requests the gate refused by a policy or a limit (<see cref="MetricNames.IsRefusal"/>).</summary>
    public long Refused { get; init; }

    /// <summary>Forwards that failed on the upstream's side (<see cref="ForwarderErrors.IsUpstreamFault"/>); upstream series only.</summary>
    public long Errors { get; init; }

    /// <summary>Response body bytes written to clients (docs/design/observability.md "Counted body bytes", counted body bytes).</summary>
    public long BytesOut { get; init; }

    /// <summary>Request body bytes read from clients.</summary>
    public long BytesIn { get; init; }

    public long TotalMicrosSum { get; init; }

    public long UpstreamMicrosSum { get; init; }

    /// <summary>Requests that were timed upstream: what <see cref="UpstreamMicrosSum"/> is a sum over.</summary>
    public long UpstreamTimed { get; init; }

    public int TotalP50 { get; init; }

    public int TotalP95 { get; init; }

    public int TotalP99 { get; init; }

    public int UpstreamP50 { get; init; }

    public int UpstreamP95 { get; init; }

    public int UpstreamP99 { get; init; }

    /// <summary>The share of requests answered with a 5xx: the proxy's own 502, 503 and 504 and an upstream's. 0 when there were no requests.</summary>
    public double ErrorRate => Requests == 0 ? 0 : (double)Status5xx / Requests;

    /// <summary>Requests a second over the seconds that were observed; 0 for a bucket nobody observed.</summary>
    public double RequestsPerSecond => PerSecond(Requests);

    /// <summary>A count as a rate over <see cref="CoveredSeconds"/>.</summary>
    public double PerSecond(long count) => CoveredSeconds <= 0 ? 0 : (double)count / CoveredSeconds;
}

/// <summary>One finished minute of one series, keyed by the object's typed ID and the minute, never a slot: what the rollup job writes.</summary>
/// <param name="Subject">What the minute is about.</param>
/// <param name="SubjectId">The typed-ID string (<c>host_...</c>, <c>ups_...</c>); empty for <see cref="MetricRollupSubject.Total"/>.</param>
/// <param name="Point">The minute's figures: <see cref="MetricPoint.Width"/> 60, <see cref="MetricPoint.CoveredSeconds"/> the part of it the process observed.</param>
public readonly record struct MetricRollup(MetricRollupSubject Subject, string SubjectId, MetricPoint Point);
