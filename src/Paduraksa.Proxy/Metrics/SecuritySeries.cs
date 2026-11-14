using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// What the sampler keeps of one security series: the cumulative counters it last saw, the minute being filled, and the minutes this
/// run closed within the last hour. Nothing draws the security counters at ten-second steps (the refusals chart is the request
/// series' breakdown), so a series is kept by the minute: the ring is an hour and a minute of them, and the hour's figures are the
/// seed, those minutes and the open one.
/// </summary>
internal sealed class SecuritySeries
{
    /// <summary>The minutes kept: the hour and the minute its start falls in.</summary>
    public const int RingPoints = 61;

    private const int MinuteSeconds = 60;

    private readonly Lock _gate = new();
    private readonly long[] _previous = new long[SecurityPoint.Count];
    private readonly long[] _delta = new long[SecurityPoint.Count];
    private readonly long[] _minute = new long[SecurityPoint.Count];
    private readonly SecurityPoint?[] _ring = new SecurityPoint?[RingPoints];
    private long _coveredMillis;
    private long _minuteId = -1;
    private int _head;
    private int _count;

    public SecuritySeries(SecurityRollupSubject subject, Guid owner, string subjectId)
    {
        Subject = subject;
        Owner = owner;
        SubjectId = subjectId;
    }

    public SecurityRollupSubject Subject { get; }

    public Guid Owner { get; }

    public string SubjectId { get; }

    /// <summary>What the last <see cref="Sample"/> found the series had gained (valid until the next call).</summary>
    public ReadOnlySpan<long> Delta => _delta;

    /// <summary>Diffs the cumulative counters the sampler read against the ones before. The sources only grow; one that went down (a test's fresh source) counts what it holds now.</summary>
    public void Sample(ReadOnlySpan<long> cumulative)
    {
        for (var i = 0; i < SecurityPoint.Count; i++)
        {
            var gained = cumulative[i] - _previous[i];
            _delta[i] = gained < 0 ? cumulative[i] : gained;
            _previous[i] = cumulative[i];
        }
    }

    /// <summary>Files the last sample's gain over the interval <paramref name="split"/> cut into minutes, closing the minutes the interval has left behind.</summary>
    public void Absorb(in TickSplit split, SecurityRollupBacklog rollups)
    {
        lock (_gate)
        {
            for (var segment = 0; segment < split.Segments; segment++)
            {
                Advance(split.FirstBucket + segment, rollups);
                for (var i = 0; i < SecurityPoint.Count; i++)
                {
                    _minute[i] += split.Share(_delta[i], segment);
                }

                _coveredMillis += split.Millis(segment);
            }

            Advance(split.EndBucket, rollups);
        }
    }

    /// <summary>Writes out the unfinished minute as it stands: the process is stopping.</summary>
    public void Flush(SecurityRollupBacklog rollups)
    {
        lock (_gate)
        {
            Close(rollups);
            _minuteId = -1;
        }
    }

    /// <summary>The minutes this run closed that end after <paramref name="sinceUnixSeconds"/>, and the open one, as one point.</summary>
    public SecurityPoint Summarise(long sinceUnixSeconds, long unixSeconds)
    {
        lock (_gate)
        {
            List<SecurityPoint> taken = [];
            for (var i = 0; i < _count; i++)
            {
                var point = _ring[(_head - _count + i + RingPoints) % RingPoints]!;
                if (point.Start + point.Width > sinceUnixSeconds)
                {
                    taken.Add(point);
                }
            }

            if (_minuteId >= 0)
            {
                taken.Add(Open());
            }

            var from = taken.Count > 0 ? Math.Max(sinceUnixSeconds, taken[0].Start) : sinceUnixSeconds;
            return SecurityPoint.Combine(taken, from, (int)Math.Max(unixSeconds - from, 1));
        }
    }

    private void Advance(long minuteId, SecurityRollupBacklog rollups)
    {
        if (minuteId != _minuteId)
        {
            Close(rollups);
            _minuteId = minuteId;
        }
    }

    private SecurityPoint Open()
    {
        // A minute that saw any part of a second counts that second: 0 is reserved for "nobody was looking".
        var covered = _coveredMillis <= 0 ? 0 : Math.Clamp((int)((_coveredMillis + 500) / 1000), 1, MinuteSeconds);
        return new SecurityPoint(_minuteId * MinuteSeconds, MinuteSeconds, covered, _minute);
    }

    // As with the request series: the totals have a row for every minute the process observed, idle ones included, and a provider or
    // a set only for a minute it counted something in. An idle provider's or set's minute allocates nothing.
    private void Close(SecurityRollupBacklog rollups)
    {
        if (_minuteId < 0)
        {
            return;
        }

        var counted = _minute.AsSpan().ContainsAnyExcept(0L);
        if (counted || (Subject == SecurityRollupSubject.Total && _coveredMillis > 0))
        {
            var point = Open();
            rollups.Enqueue(new SecurityRollup(Subject, SubjectId, point));
            _ring[_head] = point;
            _head = (_head + 1) % RingPoints;
            _count = Math.Min(_count + 1, RingPoints);
        }

        Array.Clear(_minute);
        _coveredMillis = 0;
    }
}
