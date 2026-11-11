namespace Paduraksa.Proxy.Metrics;

/// <summary>Where each counter sits inside a host's or a route's slot (the two share one layout). Stable: the Prometheus and OpenTelemetry names are built on it.</summary>
public static class RequestMetric
{
    /// <summary>1xx, 2xx, 3xx, 4xx, 5xx, then "no response" (a dropped connection, or a status outside 100-599): <see cref="StatusClasses"/> counters.</summary>
    public const int Status = 0;

    public const int StatusClasses = 6;

    /// <summary>The "no response" class's offset inside the status counters.</summary>
    public const int NoResponse = 5;

    /// <summary>One counter per <see cref="GateOutcome"/>, from here.</summary>
    public const int Outcome = Status + StatusClasses;

    public const int Outcomes = 15;

    /// <summary>Bytes of response bodies whose <c>Content-Length</c> is known; a chunked or streamed body adds nothing (the proxy can't see its size for free).</summary>
    public const int BytesOut = Outcome + Outcomes;

    /// <summary>Sum of total request durations in microseconds, for the histogram's <c>_sum</c>.</summary>
    public const int TotalMicros = BytesOut + 1;

    /// <summary>Sum of upstream durations in microseconds, over the requests that were timed upstream (their count is the upstream histogram's).</summary>
    public const int UpstreamMicros = TotalMicros + 1;

    /// <summary>Longs per slot and stripe: a multiple of 8.</summary>
    public const int Stride = 32;

    /// <summary>The offset of the upstream-time histogram inside a host's latency slot; the total-time histogram is at 0.</summary>
    public const int UpstreamHistogram = LatencyBuckets.Stride;

    /// <summary>Longs per host slot and stripe in the latency table: the two histograms.</summary>
    public const int LatencyStride = 2 * LatencyBuckets.Stride;

    /// <summary>The status class's counter offset: 0 for 1xx to 4 for 5xx, <see cref="NoResponse"/> otherwise.</summary>
    public static int ClassOf(int statusCode) => statusCode is >= 100 and <= 599 ? (statusCode / 100) - 1 : NoResponse;
}

/// <summary>Where each counter sits inside an upstream's slot.</summary>
public static class UpstreamMetric
{
    /// <summary>Requests the gate forwarded to this upstream.</summary>
    public const int Requests = 0;

    /// <summary>One counter per YARP <c>ForwarderError</c> value from 1 (<c>None</c>, 0, is not an error), at <c>Errors + (int)error</c>.</summary>
    public const int Errors = 1;

    public const int ErrorKinds = 20;

    public const int UpstreamMicros = Errors + ErrorKinds + 1;

    public const int Stride = 24;

    /// <summary>The upstream-time histogram is the whole latency slot.</summary>
    public const int LatencyStride = LatencyBuckets.Stride;
}

/// <summary>Where each counter sits inside a destination's slot.</summary>
public static class DestinationMetric
{
    public const int Requests = 0;

    /// <summary>Forwards to this destination that failed for a reason on its side of the connection (<see cref="ForwarderErrors.IsUpstreamFault"/>).</summary>
    public const int Errors = 1;

    public const int Stride = 8;
}
