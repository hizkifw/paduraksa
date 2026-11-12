namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// How the interval one sampler tick observed divides over the ten-second buckets it touches, and how the tick's gain divides with
/// it. Usually the interval is a second and touches one bucket; a tick a few milliseconds past a boundary touches two; a late tick
/// that was serving all along (a stall) touches as many as it was late for. Each bucket gets the share of the gain its part of the
/// interval is worth, in whole counts, and the shares add up to the gain exactly.
/// </summary>
internal readonly struct TickSplit
{
    private readonly long _start;
    private readonly long _end;
    private readonly long _bucketMillis;

    private TickSplit(long start, long end, long bucketMillis)
    {
        _start = start;
        _end = end;
        _bucketMillis = bucketMillis;
    }

    /// <summary>How many buckets the interval touches; 1 for an interval of no length.</summary>
    public int Segments => _end <= _start ? 1 : (int)(((_end - 1) / _bucketMillis) - (_start / _bucketMillis)) + 1;

    /// <summary>The bucket the first piece belongs to; the next piece is the bucket after it, and so on.</summary>
    public long FirstBucket => (_end <= _start ? _end : _start) / _bucketMillis;

    /// <summary>The bucket the clock is in at the end of the interval: the one after the last piece's when the interval ended exactly on a boundary.</summary>
    public long EndBucket => _end / _bucketMillis;

    /// <summary>The interval from <paramref name="startMillis"/> to <paramref name="endMillis"/>, all of it observed.</summary>
    public static TickSplit Of(long startMillis, long endMillis, long bucketMillis) => new(startMillis, Math.Max(startMillis, endMillis), bucketMillis);

    /// <summary>An interval of no length at <paramref name="atMillis"/>: nothing observed, and any gain goes into that moment's bucket.</summary>
    public static TickSplit None(long atMillis, long bucketMillis) => new(atMillis, atMillis, bucketMillis);

    /// <summary>The milliseconds of the interval the piece observed.</summary>
    public long Millis(int segment)
    {
        if (_end <= _start)
        {
            return 0;
        }

        var bucket = FirstBucket + segment;
        return Math.Min(_end, (bucket + 1) * _bucketMillis) - Math.Max(_start, bucket * _bucketMillis);
    }

    /// <summary>
    /// The piece of <paramref name="gain"/> that goes with the piece: the gain rounded to the nearest whole count at the end of the
    /// piece, less the same at the end of the one before. The pieces therefore sum to <paramref name="gain"/> exactly and none is
    /// off its proportional share by more than one count; a tick 4 ms late keeps its three requests in the bucket they happened in,
    /// and an even split of an odd count gives the leftover to the earlier piece. An interval of no length has one piece that takes it all.
    /// </summary>
    public long Share(long gain, int segment)
    {
        if (_end <= _start || gain == 0)
        {
            return segment == 0 ? gain : 0;
        }

        return Cumulative(gain, segment) - (segment == 0 ? 0 : Cumulative(gain, segment - 1));
    }

    private long Cumulative(long gain, int segment)
    {
        var length = _end - _start;
        var through = Math.Min(_end, (FirstBucket + segment + 1) * _bucketMillis) - _start;
        return (long)((((Int128)2 * gain * through) + length) / (2 * (Int128)length));
    }
}
