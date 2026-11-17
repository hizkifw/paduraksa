// Linked into the test projects that build analytics rollups by hand (<Compile Include Link>), so it compiles into each assembly as
// an internal type of its own.
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Analytics;
using Paduraksa.Proxy.Logging;

namespace Paduraksa.Tests.Shared;

internal static class AnalyticsTestData
{
    /// <summary>A visitor hash that differs in every bit from its neighbours', as the real one does.</summary>
    public static ulong Mix(ulong index)
    {
        var z = index + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public static ulong Visitor(int i) => Mix((ulong)i);

    /// <summary>The five-minute rollup of a host (or, for an empty id, of the requests no host took) holding <paramref name="entries"/>.</summary>
    public static AnalyticsRollup Rollup(HostId host, DateTimeOffset at, params (AccessLogEntry Entry, int Visitor, bool Ignored)[] entries)
    {
        TrafficBucket bucket = new();
        foreach (var (entry, visitor, ignored) in entries)
        {
            var copy = entry;
            bucket.Record(in copy, Visitor(visitor), UserAgentClassifier.Classify(entry.UserAgent), ignored);
        }

        return new AnalyticsRollup(
            host.IsEmpty ? AnalyticsSubject.Unmatched : AnalyticsSubject.Host,
            host.IsEmpty ? string.Empty : host.ToString(),
            at.ToUnixTimeSeconds(),
            300,
            bucket.Requests,
            bucket.BytesOut,
            bucket.BytesIn,
            bucket.ServerErrors,
            TrafficBucketCodec.Encode(bucket));
    }

    public static AnalyticsRollup Coverage(DateTimeOffset at, int seconds = 300) =>
        new(AnalyticsSubject.Coverage, string.Empty, at.ToUnixTimeSeconds(), seconds, 0, 0, 0, 0, null);
}
