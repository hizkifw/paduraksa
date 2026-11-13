namespace Paduraksa.Proxy.Analytics;

/// <summary>What a rollup row is about.</summary>
public enum AnalyticsSubject
{
    /// <summary>The process was observing this stretch of time (no traffic data): the record of when there is a gap and when there is only quiet.</summary>
    Coverage = 0,

    /// <summary>A configured host's traffic.</summary>
    Host = 1,

    /// <summary>The requests no host owns.</summary>
    Unmatched = 2,
}

/// <summary>How wide a rollup row is.</summary>
public enum AnalyticsTier
{
    /// <summary>The five-minute buckets the drain closes.</summary>
    Fine = 0,

    /// <summary>An hour of them merged, kept for the longer ranges.</summary>
    Hour = 1,

    /// <summary>A day of hours merged, which is what a month is read from.</summary>
    Day = 2,
}

/// <summary>
/// One closed stretch of one host's traffic, ready to be stored (PLAN §7.4, analytics): the figures a chart needs as plain numbers and
/// the rest as <see cref="EncodedTrafficBucket"/>. A <see cref="AnalyticsSubject.Coverage"/> row has no bucket and says only how many
/// seconds the process watched.
/// </summary>
/// <param name="Subject">What the row is about.</param>
/// <param name="HostKey">The host's typed-ID text; empty for coverage and for the requests no host owns.</param>
/// <param name="Start">The first second (Unix time) of the stretch, a multiple of its width.</param>
/// <param name="CoveredSeconds">How many of the stretch's seconds the process observed.</param>
/// <param name="Requests">Requests in it.</param>
/// <param name="BytesOut">Response body bytes written to clients.</param>
/// <param name="BytesIn">Request body bytes read from clients.</param>
/// <param name="ServerErrors">5xx answers.</param>
/// <param name="Bucket">The rest of the traffic, or null for coverage.</param>
public sealed record AnalyticsRollup(
    AnalyticsSubject Subject, string HostKey, long Start, int CoveredSeconds, long Requests, long BytesOut, long BytesIn, long ServerErrors, EncodedTrafficBucket? Bucket);
