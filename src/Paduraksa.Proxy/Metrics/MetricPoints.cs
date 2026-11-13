namespace Paduraksa.Proxy.Metrics;

/// <summary>Arithmetic over <see cref="MetricPoint"/>s: how a longer stretch of buckets becomes one.</summary>
public static class MetricPoints
{
    /// <summary>
    /// One point covering all of <paramref name="points"/>: counts, sums and the seconds they observed add up, and a percentile is
    /// the mean of the points' percentiles weighted by the requests they describe (every request for the total time, the timed ones
    /// for the upstream time). A histogram can't be merged back from percentiles, so a long range's p95 is an approximation of the
    /// real one that is exact when the load is steady. <paramref name="width"/> is the span the result stands for.
    /// </summary>
    public static MetricPoint Combine(ReadOnlySpan<MetricPoint> points, long start, int width)
    {
        long covered = 0;
        long requests = 0, s1 = 0, s2 = 0, s3 = 0, s4 = 0, s5 = 0, none = 0, refused = 0, errors = 0, bytes = 0, bytesIn = 0, total = 0, upstream = 0, timed = 0;
        double t50 = 0, t95 = 0, t99 = 0, u50 = 0, u95 = 0, u99 = 0;
        foreach (var point in points)
        {
            covered += point.CoveredSeconds;
            requests += point.Requests;
            s1 += point.Status1xx;
            s2 += point.Status2xx;
            s3 += point.Status3xx;
            s4 += point.Status4xx;
            s5 += point.Status5xx;
            none += point.NoResponse;
            refused += point.Refused;
            errors += point.Errors;
            bytes += point.BytesOut;
            bytesIn += point.BytesIn;
            total += point.TotalMicrosSum;
            upstream += point.UpstreamMicrosSum;
            timed += point.UpstreamTimed;
            t50 += (double)point.TotalP50 * point.Requests;
            t95 += (double)point.TotalP95 * point.Requests;
            t99 += (double)point.TotalP99 * point.Requests;
            u50 += (double)point.UpstreamP50 * point.UpstreamTimed;
            u95 += (double)point.UpstreamP95 * point.UpstreamTimed;
            u99 += (double)point.UpstreamP99 * point.UpstreamTimed;
        }

        return new MetricPoint
        {
            Start = start,
            Width = width,
            CoveredSeconds = (int)Math.Min(covered, int.MaxValue),
            Requests = requests,
            Status1xx = s1,
            Status2xx = s2,
            Status3xx = s3,
            Status4xx = s4,
            Status5xx = s5,
            NoResponse = none,
            Refused = refused,
            Errors = errors,
            BytesOut = bytes,
            BytesIn = bytesIn,
            TotalMicrosSum = total,
            UpstreamMicrosSum = upstream,
            UpstreamTimed = timed,
            TotalP50 = WeightedMean(t50, requests),
            TotalP95 = WeightedMean(t95, requests),
            TotalP99 = WeightedMean(t99, requests),
            UpstreamP50 = WeightedMean(u50, timed),
            UpstreamP95 = WeightedMean(u95, timed),
            UpstreamP99 = WeightedMean(u99, timed),
        };
    }

    /// <summary>The weighted percentile's mean over <paramref name="weight"/> requests, 0 when there were none.</summary>
    public static int WeightedMean(double weighted, long weight) => weight <= 0 ? 0 : (int)Math.Round(weighted / weight);
}
