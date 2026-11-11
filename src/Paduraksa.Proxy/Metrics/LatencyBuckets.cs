using System.Numerics;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The latency histogram's fixed log-linear bucket scheme (PLAN §7.4), in whole microseconds: 0 to 3 µs get a bucket each,
/// then every power of two is split into four equal steps (so a bucket is at most 25 % wide), up to 2^26 µs (67 s); anything
/// longer is the overflow bucket. 101 buckets cover 1 µs to 60 s and beyond; a histogram's stride is rounded up to a whole
/// number of cache lines.
/// </summary>
/// <remarks>
/// The index is a leading-zero count and two shifts, so recording a latency is one array increment. Bucket bounds are
/// exclusive at the top: bucket <c>i</c> holds values in <c>[Lower(i), Lower(i + 1))</c>.
/// </remarks>
public static class LatencyBuckets
{
    private const int SubBucketBits = 2;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int FirstLogValue = SubBuckets;

    /// <summary>The octave the overflow bucket starts at: 2^26 µs is 67.1 s.</summary>
    public const int OverflowOctave = 26;

    /// <summary>The overflow bucket's index; values of <c>2^26</c> µs and more land here.</summary>
    public const int Overflow = FirstLogValue + ((OverflowOctave - SubBucketBits) * SubBuckets);

    /// <summary>Buckets in use.</summary>
    public const int Count = Overflow + 1;

    /// <summary>Longs a histogram occupies in a table: <see cref="Count"/> rounded up to a multiple of 8 (64 bytes).</summary>
    public const int Stride = (Count + 7) & ~7;

    /// <summary>The bucket a latency of <paramref name="micros"/> falls in. Negative values count as zero.</summary>
    public static int IndexOf(long micros)
    {
        if (micros < FirstLogValue)
        {
            return micros < 0 ? 0 : (int)micros;
        }

        var octave = 63 - BitOperations.LeadingZeroCount((ulong)micros);
        if (octave >= OverflowOctave)
        {
            return Overflow;
        }

        var sub = (int)((micros >> (octave - SubBucketBits)) & (SubBuckets - 1));
        return FirstLogValue + ((octave - SubBucketBits) * SubBuckets) + sub;
    }

    /// <summary>The smallest value (µs) in bucket <paramref name="index"/>.</summary>
    public static long LowerBound(int index)
    {
        if (index < FirstLogValue)
        {
            return Math.Max(index, 0);
        }

        if (index >= Overflow)
        {
            return 1L << OverflowOctave;
        }

        var octave = ((index - FirstLogValue) / SubBuckets) + SubBucketBits;
        var sub = (index - FirstLogValue) % SubBuckets;
        return (long)(SubBuckets + sub) << (octave - SubBucketBits);
    }

    /// <summary>The first value (µs) past bucket <paramref name="index"/>; <see cref="long.MaxValue"/> for the overflow bucket.</summary>
    public static long UpperBound(int index) => index >= Overflow ? long.MaxValue : LowerBound(index + 1);

    /// <summary>
    /// The value (µs) below which a fraction <paramref name="quantile"/> of the <paramref name="counts"/> fall, interpolated
    /// linearly inside the bucket that holds it. 0 for an empty histogram; the overflow bucket reports its lower bound.
    /// </summary>
    public static long Quantile(ReadOnlySpan<long> counts, double quantile)
    {
        long total = 0;
        for (var i = 0; i < Count && i < counts.Length; i++)
        {
            total += counts[i];
        }

        if (total == 0)
        {
            return 0;
        }

        var target = Math.Clamp(quantile, 0, 1) * total;
        double seen = 0;
        for (var i = 0; i < Count && i < counts.Length; i++)
        {
            var next = seen + counts[i];
            if (counts[i] > 0 && next >= target)
            {
                if (i >= Overflow)
                {
                    return LowerBound(i);
                }

                var lower = LowerBound(i);
                var width = UpperBound(i) - lower;
                return lower + (long)(width * ((target - seen) / counts[i]));
            }

            seen = next;
        }

        return LowerBound(Overflow);
    }

    /// <summary>
    /// The count of observations below <c>2^octave</c> µs: a cumulative bucket boundary that falls exactly on a bucket edge,
    /// which is how the Prometheus <c>le</c> series stay exact. <paramref name="octave"/> is 2 to <see cref="OverflowOctave"/>.
    /// </summary>
    public static long CountBelowOctave(ReadOnlySpan<long> counts, int octave)
    {
        var end = octave >= OverflowOctave ? Overflow : FirstLogValue + ((octave - SubBucketBits) * SubBuckets);
        long total = 0;
        for (var i = 0; i < end && i < counts.Length; i++)
        {
            total += counts[i];
        }

        return total;
    }
}
