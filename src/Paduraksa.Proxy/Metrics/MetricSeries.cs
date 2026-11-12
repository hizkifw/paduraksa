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

    public void Add(ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream)
    {
        for (var i = 0; i < _counters.Length && i < counters.Length; i++)
        {
            _counters[i] += counters[i];
        }

        for (var i = 0; i < LatencyBuckets.Stride; i++)
        {
            _total[i] += total[i];
            _upstream[i] += upstream[i];
        }
    }

    public void Clear()
    {
        Array.Clear(_counters);
        Array.Clear(_total);
        Array.Clear(_upstream);
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

    public MetricPoint ToPoint(long start, int seconds)
    {
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
                Width = seconds,
                CoveredSeconds = seconds,
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
            Width = seconds,
            CoveredSeconds = seconds,
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

    /// <summary>The cumulative values to diff against: the sampler writes what it read here, and this turns it into what happened since the last tick.</summary>
    public void Sample(long unixSeconds, ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream, MetricRollupBacklog rollups)
    {
        Diff(counters, _previousCounters, _deltaCounters);
        Diff(total, _previousTotal, _deltaTotal);
        Diff(upstream, _previousUpstream, _deltaUpstream);
        Absorb(unixSeconds, _deltaCounters, _deltaTotal, _deltaUpstream, rollups);
    }

    /// <summary>Adds what happened during one second: closes the ten-second bucket and the minute that the second has left behind.</summary>
    public void Absorb(long unixSeconds, ReadOnlySpan<long> counters, ReadOnlySpan<long> total, ReadOnlySpan<long> upstream, MetricRollupBacklog rollups)
    {
        var bucketId = unixSeconds / BucketSeconds;
        var minuteId = unixSeconds / 60;
        lock (_gate)
        {
            if (bucketId != _bucketId)
            {
                if (_bucketId >= 0)
                {
                    Push(_bucket.ToPoint(_bucketId * BucketSeconds, BucketSeconds));
                    // A sampler that stalled leaves empty buckets behind, so the ring's axis stays in time.
                    for (var gap = _bucketId + 1; gap < bucketId && gap - _bucketId <= RingPoints; gap++)
                    {
                        Push(new MetricPoint { Start = gap * BucketSeconds, Width = BucketSeconds, CoveredSeconds = BucketSeconds });
                    }
                }

                _bucket.Clear();
                _bucketId = bucketId;
            }

            if (minuteId != _minuteId)
            {
                CloseMinute(rollups);
                _minuteId = minuteId;
            }

            _bucket.Add(counters, total, upstream);
            _minute.Add(counters, total, upstream);
        }
    }

    /// <summary>The unfinished minute is written out when the object goes away, so its last figures aren't lost with its slot.</summary>
    public void Flush(MetricRollupBacklog rollups)
    {
        lock (_gate)
        {
            CloseMinute(rollups);
            _minuteId = -1;
        }
    }

    /// <summary>The finished buckets oldest first, the unfinished one (as many seconds wide as it has seen) and the last second.</summary>
    public MetricSeriesView View(long unixSeconds)
    {
        lock (_gate)
        {
            var points = new MetricPoint[_count];
            for (var i = 0; i < _count; i++)
            {
                points[i] = _ring[(_head - _count + i + RingPoints) % RingPoints];
            }

            var open = _bucketId < 0
                ? default
                : _bucket.ToPoint(_bucketId * BucketSeconds, (int)Math.Clamp(unixSeconds - (_bucketId * BucketSeconds) + 1, 1, BucketSeconds));
            return new MetricSeriesView(points, open);
        }
    }

    /// <summary>Everything the ring and the open bucket hold from <paramref name="sinceUnixSeconds"/> on, as one point.</summary>
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

            // A series younger than the window is divided by the time it has existed, so a rate isn't diluted by an hour it never saw.
            var from = taken.Count > 0 ? Math.Max(sinceUnixSeconds, taken[0].Start) : sinceUnixSeconds;
            return MetricPoints.Combine(taken.ToArray(), from, (int)Math.Max(unixSeconds - from, 1));
        }
    }

    private void CloseMinute(MetricRollupBacklog rollups)
    {
        if (_minuteId < 0)
        {
            return;
        }

        var point = _minute.ToPoint(_minuteId * 60, 60);
        if (point.Requests > 0)
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
/// <param name="Open">The bucket being filled; its <see cref="MetricPoint.CoveredSeconds"/> is how long it has been open.</param>
public readonly record struct MetricSeriesView(MetricPoint[] Points, MetricPoint Open);
