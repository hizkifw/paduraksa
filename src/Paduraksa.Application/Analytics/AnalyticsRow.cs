using Paduraksa.Proxy.Analytics;

namespace Paduraksa.Application.Analytics;

/// <summary>A stored stretch of traffic as the read side takes it from the database or from memory: the figures as numbers and the sketches still encoded.</summary>
/// <param name="Tier">How wide it is.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="HostKey">The host's typed-ID text; empty for coverage and the requests no host owns.</param>
/// <param name="Start">The first second (Unix time).</param>
/// <param name="CoveredSeconds">Seconds the process observed.</param>
/// <param name="Requests">Requests.</param>
/// <param name="BytesOut">Response body bytes written to clients (declared <c>Content-Length</c>s in rows from before they were counted).</param>
/// <param name="BytesIn">Request body bytes read from clients (0 in rows from before they were counted).</param>
/// <param name="ServerErrors">5xx answers.</param>
/// <param name="Bucket">The sketches and tables, or null for coverage.</param>
public sealed record AnalyticsRow(
    AnalyticsTier Tier, AnalyticsSubject Subject, string HostKey, long Start, int CoveredSeconds, long Requests, long BytesOut, long BytesIn, long ServerErrors, EncodedTrafficBucket? Bucket)
{
    /// <summary>The seconds the row spans.</summary>
    public int Width => Tier switch
    {
        AnalyticsTier.Day => AnalyticsRollupStore.DaySeconds,
        AnalyticsTier.Hour => AnalyticsRollupStore.HourSeconds,
        _ => TrafficAnalytics.BucketSeconds,
    };
}

/// <summary>Which of the rows a read wants besides coverage, which it always gets.</summary>
/// <param name="Subject">A host, or the requests no host owns; null for all of them.</param>
/// <param name="HostKey">The host's typed-ID text when <paramref name="Subject"/> is <see cref="AnalyticsSubject.Host"/>.</param>
public readonly record struct AnalyticsScope(AnalyticsSubject? Subject, string HostKey)
{
    public static AnalyticsScope All { get; } = new(null, string.Empty);

    public static AnalyticsScope Host(string hostKey) => new(AnalyticsSubject.Host, hostKey);

    public static AnalyticsScope Unmatched { get; } = new(AnalyticsSubject.Unmatched, string.Empty);
}

/// <summary>What a range read found: the stored rows, and the closed buckets still waiting to be written that fall in the range.</summary>
/// <param name="Rows">Stored rows, hourly where a whole hour is inside the range and rolled up, five-minute for the rest.</param>
/// <param name="Unwritten">Closed buckets not yet confirmed written; none of them is in <paramref name="Rows"/>.</param>
public sealed record AnalyticsRead(IReadOnlyList<AnalyticsRow> Rows, IReadOnlyList<AnalyticsRow> Unwritten);
