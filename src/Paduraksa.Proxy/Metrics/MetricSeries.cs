using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The figures a series gained inside one window (a ten-second ring bucket or a minute): counters in the registry's layout and the
/// two histograms, so a percentile is taken over exactly the requests the window saw.
/// </summary>
internal sealed class MetricWindow(MetricRollupSubject kind)
{
    private readonly long[] _counters = new long[kind == MetricRollupSubject.Upstream ? UpstreamMetric.Stride : RequestMetric.Stride];
    private readonly long[] _total = new long[LatencyBuckets.Stride];
    private readonly long[] _upstream = new long[LatencyBuckets.Stride];
    private long _coveredMillis;

    /// <summary>
    /// Adds the share of a tick's gain that belongs to this window: the <paramref name="segment"/>th of the pieces the tick's interval
    /// was cut into at the window boundaries (<see cref="TickSplit"/>), and the milliseconds that piece observed.
    /// </summary>
    public void Add(ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream, in TickSplit split, int segment)
    {
        for (var i = 0; i < _counters.Length && i < counters.Length; i++)
        {
            _counters[i] += split.Share(counters[i], segment);
        }

        for (var i = 0; i < LatencyBuckets.Stride; i++)
        {
            _total[i] += split.Share(total[i], segment);
            _upstream[i] += split.Share(upstream[i], segment);
        }

        _coveredMillis += split.Millis(segment);
    }

    public void Clear()
    {
        Array.Clear(_counters);
        Array.Clear(_total);
        Array.Clear(_upstream);
        _coveredMillis = 0;
    }

    // The requests timed upstream are the upstream histogram's observations: one fewer counter on the request path.
    private static long Sum(long[] histogram)
    {
        long total = 0;
        foreach (var count in histogram)
        {
            total += count;
        }

        return total;
    }

    public MetricPoint ToPoint(long start, int width)
    {
        // A window that saw any part of a second counts that second: 0 is reserved for "nobody was looking".
        var covered = _coveredMillis <= 0 ? 0 : Math.Clamp((int)((_coveredMillis + 500) / 1000), 1, width);
        if (kind == MetricRollupSubject.Upstream)
        {
            long errors = 0;
            for (var error = 1; error < UpstreamMetric.ErrorKinds; error++)
            {
                if (ForwarderErrors.IsUpstreamFault((global::Yarp.ReverseProxy.Forwarder.ForwarderError)error))
                {
                    errors += _counters[UpstreamMetric.Errors + error];
                }
            }

            return new MetricPoint
            {
                Start = start,
                Width = width,
                CoveredSeconds = covered,
                Requests = _counters[UpstreamMetric.Requests],
                Errors = errors,
                UpstreamMicrosSum = _counters[UpstreamMetric.UpstreamMicros],
                UpstreamTimed = Sum(_upstream),
                UpstreamP50 = (int)LatencyBuckets.Quantile(_upstream, 0.50),
                UpstreamP95 = (int)LatencyBuckets.Quantile(_upstream, 0.95),
                UpstreamP99 = (int)LatencyBuckets.Quantile(_upstream, 0.99),
            };
        }

        long requests = 0;
        for (var c = 0; c < RequestMetric.StatusClasses; c++)
        {
            requests += _counters[RequestMetric.Status + c];
        }

        long refused = 0;
        foreach (var outcome in MetricNames.CountedOutcomes)
        {
            if (MetricNames.IsRefusal(outcome))
            {
                refused += _counters[RequestMetric.Outcome + (int)outcome];
            }
        }

        return new MetricPoint
        {
            Start = start,
            Width = width,
            CoveredSeconds = covered,
            Requests = requests,
            Status1xx = _counters[RequestMetric.Status],
            Status2xx = _counters[RequestMetric.Status + 1],
            Status3xx = _counters[RequestMetric.Status + 2],
            Status4xx = _counters[RequestMetric.Status + 3],
            Status5xx = _counters[RequestMetric.Status + 4],
            NoResponse = _counters[RequestMetric.Status + RequestMetric.NoResponse],
            Refused = refused,
            BytesOut = _counters[RequestMetric.BytesOut],
            TotalMicrosSum = _counters[RequestMetric.TotalMicros],
            UpstreamMicrosSum = _counters[RequestMetric.UpstreamMicros],
            UpstreamTimed = Sum(_upstream),
            TotalP50 = (int)LatencyBuckets.Quantile(_total, 0.50),
            TotalP95 = (int)LatencyBuckets.Quantile(_total, 0.95),
            TotalP99 = (int)LatencyBuckets.Quantile(_total, 0.99),
            UpstreamP50 = (int)LatencyBuckets.Quantile(_upstream, 0.50),
            UpstreamP95 = (int)LatencyBuckets.Quantile(_upstream, 0.95),
            UpstreamP99 = (int)LatencyBuckets.Quantile(_upstream, 0.99),
        };
    }
}

/// <summary>What the sampler keeps of one object: the cumulative values it last saw, the windows being filled, and the ring of finished ten-second buckets.</summary>
internal sealed class MetricSeries
{
    /// <summary>The ring's length: an hour of ten-second buckets.</summary>
    public const int RingPoints = 360;

    /// <summary>A bucket's width in seconds.</summary>
    public const int BucketSeconds = 10;

    private const int MinuteSeconds = 60;
    private const int BucketsPerMinute = MinuteSeconds / BucketSeconds;

    private readonly Lock _gate = new();
    private readonly MetricRollupSubject _kind;
    private readonly string _subjectId;
    private readonly long[] _previousCounters;
    private readonly long[] _previousTotal = new long[LatencyBuckets.Stride];
    private readonly long[] _previousUpstream = new long[LatencyBuckets.Stride];
    private readonly long[] _deltaCounters;
    private readonly long[] _deltaTotal = new long[LatencyBuckets.Stride];
    private readonly long[] _deltaUpstream = new long[LatencyBuckets.Stride];
    private readonly MetricWindow _bucket;
    private readonly MetricWindow _minute;
    private readonly MetricPoint[] _ring = new MetricPoint[RingPoints];
    private long _bucketId = -1;
    private long _minuteId = -1;
    private int _head;
    private int _count;

    public MetricSeries(MetricRollupSubject kind, Guid owner, int version, string subjectId)
    {
        _kind = kind;
        Owner = owner;
        Version = version;
        _subjectId = subjectId;
        var stride = kind == MetricRollupSubject.Upstream ? UpstreamMetric.Stride : RequestMetric.Stride;
        _previousCounters = new long[stride];
        _deltaCounters = new long[stride];
        _bucket = new MetricWindow(kind);
        _minute = new MetricWindow(kind);
    }

    public Guid Owner { get; }

    /// <summary>The slot's version when the owner got it: a different one means the slot was rented again.</summary>
    public int Version { get; }

    /// <summary>What the last <see cref="Sample"/> found the series had gained (valid until the next call): the sampler adds it to the totals.</summary>
    public ReadOnlySpan<long> DeltaCounters => _deltaCounters;

    public ReadOnlySpan<long> DeltaTotal => _deltaTotal;

    public ReadOnlySpan<long> DeltaUpstream => _deltaUpstream;

    /// <summary>Diffs the cumulative values the sampler read against the ones before; the gain is then in <see cref="DeltaCounters"/> and the others, to be filed by Absorb.</summary>
    public void Sample(ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream)
    {
        Diff(counters, _previousCounters, _deltaCounters);
        Diff(total, _previousTotal, _deltaTotal);
        Diff(upstream, _previousUpstream, _deltaUpstream);
    }

    /// <summary>Files what <see cref="Sample"/> found over the interval the tick observed.</summary>
    public void Absorb(in TickSplit split, MetricRollupBacklog rollups) => Absorb(split, _deltaCounters, _deltaTotal, _deltaUpstream, rollups);

    /// <summary>
    /// Files a gain over the interval <paramref name="split"/> describes: each bucket the interval touches gets its share, and the
    /// buckets and the minute the interval has left behind are closed.
    /// </summary>
    public void Absorb(in TickSplit split, ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream, MetricRollupBacklog rollups)
    {
        lock (_gate)
        {
            for (var segment = 0; segment < split.Segments; segment++)
            {
                Advance(split.FirstBucket + segment, rollups);
                _bucket.Add(counters, total, upstream, split, segment);
                _minute.Add(counters, total, upstream, split, segment);
            }

            // An interval that ended on a boundary has finished its bucket, and the minute, though nothing is left to file in the next.
            Advance(split.EndBucket, rollups);
        }
    }

    /// <summary>Writes out the unfinished minute and bucket as they stand (partial, with the seconds they observed): the object is going away, or the process is stopping.</summary>
    public void Flush(MetricRollupBacklog rollups)
    {
        lock (_gate)
        {
            CloseMinute(rollups);
            if (_bucketId >= 0)
            {
                var open = _bucket.ToPoint(_bucketId * BucketSeconds, BucketSeconds);
                if (open.CoveredSeconds > 0 || open.Requests > 0)
                {
                    Push(open);
                }
            }

            _bucket.Clear();
            _bucketId = -1;
            _minuteId = -1;
        }
    }

    /// <summary>The finished buckets oldest first and the one still filling, with the seconds it has observed so far.</summary>
    public MetricSeriesView View()
    {
        lock (_gate)
        {
            var points = new MetricPoint[_count];
            for (var i = 0; i < _count; i++)
            {
                points[i] = _ring[(_head - _count + i + RingPoints) % RingPoints];
            }

            return new MetricSeriesView(points, _bucketId < 0 ? default : _bucket.ToPoint(_bucketId * BucketSeconds, BucketSeconds));
        }
    }

    /// <summary>Everything the ring and the open bucket hold from <paramref name="sinceUnixSeconds"/> on, as one point: its rates are over the seconds those buckets observed.</summary>
    public MetricPoint Summarise(long sinceUnixSeconds, long unixSeconds)
    {
        lock (_gate)
        {
            List<MetricPoint> taken = [];
            for (var i = 0; i < _count; i++)
            {
                var point = _ring[(_head - _count + i + RingPoints) % RingPoints];
                if (point.Start + point.Width > sinceUnixSeconds)
                {
                    taken.Add(point);
                }
            }

            if (_bucketId >= 0)
            {
                taken.Add(_bucket.ToPoint(_bucketId * BucketSeconds, BucketSeconds));
            }

            var from = taken.Count > 0 ? Math.Max(sinceUnixSeconds, taken[0].Start) : sinceUnixSeconds;
            return MetricPoints.Combine(taken.ToArray(), from, (int)Math.Max(unixSeconds - from, 1));
        }
    }

    private void Advance(long bucketId, MetricRollupBacklog rollups)
    {
        if (bucketId != _bucketId)
        {
            if (_bucketId >= 0)
            {
                Push(_bucket.ToPoint(_bucketId * BucketSeconds, BucketSeconds));
                // Buckets the sampler never saw (a stall, a stop) are in the ring as buckets nobody observed, so its axis stays in
                // time and a gap shows as a gap. Only the last hour's worth are kept: older ones would be evicted again at once.
                for (var gap = Math.Max(_bucketId + 1, bucketId - RingPoints); gap < bucketId; gap++)
                {
                    Push(new MetricPoint { Start = gap * BucketSeconds, Width = BucketSeconds });
                }
            }

            _bucket.Clear();
            _bucketId = bucketId;
        }

        var minuteId = bucketId / BucketsPerMinute;
        if (minuteId != _minuteId)
        {
            CloseMinute(rollups);
            _minuteId = minuteId;
        }
    }

    // The totals have a row for every minute the process observed, idle ones included: a minute with no row is a minute the
    // process was down. The others stay sparse; a missing row inside an observed minute means no requests.
    private void CloseMinute(MetricRollupBacklog rollups)
    {
        if (_minuteId < 0)
        {
            return;
        }

        var point = _minute.ToPoint(_minuteId * MinuteSeconds, MinuteSeconds);
        if (point.Requests > 0 || (_kind == MetricRollupSubject.Total && point.CoveredSeconds > 0))
        {
            rollups.Enqueue(new MetricRollup(_kind, _subjectId, point));
        }

        _minute.Clear();
    }

    private void Push(MetricPoint point)
    {
        _ring[_head] = point;
        _head = (_head + 1) % RingPoints;
        _count = Math.Min(_count + 1, RingPoints);
    }

    // A counter that went down belongs to a slot that was reused: what it holds now is all new.
    private static void Diff(ReadOnlySpan<long> current, long[] previous, long[] delta)
    {
        for (var i = 0; i < delta.Length; i++)
        {
            var now = i < current.Length ? current[i] : 0;
            var gained = now - previous[i];
            delta[i] = gained < 0 ? now : gained;
            previous[i] = now;
        }
    }
}

/// <summary>A series as the dashboard reads it: finished ten-second buckets oldest first, and the bucket still filling.</summary>
/// <param name="Points">The ring, up to an hour.</param>
/// <param name="Open">The bucket being filled; its <see cref="MetricPoint.CoveredSeconds"/> is the part of it observed so far.</param>
public readonly record struct MetricSeriesView(MetricPoint[] Points, MetricPoint Open);
