namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// How one sampler tick's interval divides over the one or two ten-second buckets it touches, and how its gain divides with it. A
/// tick whose reading arrives a few milliseconds past a boundary, or a pause that makes it late, then files each window the share
/// of the gain the window's part of the interval is worth, and the pieces add up to the gain exactly. Two pieces at most, because
/// <see cref="MetricSampler.MaxTickGap"/> is shorter than a bucket.
/// </summary>
internal readonly struct TickSplit
{
    private readonly long _first;
    private readonly long _second;

    private TickSplit(int segments, long first, long second, long firstBucket, long endBucket)
    {
        Segments = segments;
        _first = first;
        _second = second;
        FirstBucket = firstBucket;
        EndBucket = endBucket;
    }

    /// <summary>How many buckets the interval touches: 1 or 2.</summary>
    public int Segments { get; }

    /// <summary>The bucket the first piece belongs to; the second, if any, is the next one.</summary>
    public long FirstBucket { get; }

    /// <summary>The bucket the clock is in at the end of the interval: the one after the last piece's when the interval ended exactly on a boundary.</summary>
    public long EndBucket { get; }

    /// <summary>
    /// The interval from <paramref name="fromMillis"/> to <paramref name="toMillis"/> cut at the bucket boundaries. An interval that
    /// counts as unobserved (a stall or a clock that didn't move) is one piece of no length, in the bucket <paramref name="toMillis"/> is in.
    /// </summary>
    public static TickSplit Of(long fromMillis, long toMillis, bool observed, long bucketMillis)
    {
        if (!observed || toMillis <= fromMillis)
        {
            var current = toMillis / bucketMillis;
            return new TickSplit(1, 0, 0, current, current);
        }

        var firstBucket = fromMillis / bucketMillis;
        var endBucket = toMillis / bucketMillis;
        var lastBucket = (toMillis - 1) / bucketMillis;
        if (lastBucket == firstBucket)
        {
            return new TickSplit(1, toMillis - fromMillis, 0, firstBucket, endBucket);
        }

        System.Diagnostics.Debug.Assert(lastBucket - firstBucket == 1, "A tick no longer than a bucket crosses one boundary at most.");
        var boundary = lastBucket * bucketMillis;
        return new TickSplit(2, boundary - fromMillis, toMillis - boundary, firstBucket, endBucket);
    }

    /// <summary>The milliseconds of the interval the piece observed.</summary>
    public long Millis(int segment) => segment == 0 ? _first : _second;

    /// <summary>
    /// The piece of <paramref name="gain"/> that goes with the piece: proportional to its length, in whole counts, the leftover count going
    /// to the piece with the larger fractional part (the earlier one on a tie), so the pieces sum to <paramref name="gain"/> exactly. An
    /// interval of no length has one piece that takes it all.
    /// </summary>
    public long Share(long gain, int segment)
    {
        if (Segments == 1 || gain == 0)
        {
            return segment == 0 ? gain : 0;
        }

        var length = _first + _second;
        var firstExact = (Int128)gain * _first;
        var secondExact = (Int128)gain * _second;
        var firstFloor = (long)(firstExact / length);
        var secondFloor = (long)(secondExact / length);
        var leftover = gain - firstFloor - secondFloor;
        var firstGetsIt = leftover > 0 && firstExact % length >= secondExact % length;
        return segment == 0 ? firstFloor + (firstGetsIt ? leftover : 0) : secondFloor + (leftover > 0 && !firstGetsIt ? leftover : 0);
    }
}
