namespace Paduraksa.Tests.Data;

/// <summary>
/// Settable clock for generators and stores. Timers it creates fire when <see cref="Advance"/> moves the clock past them
/// (once, or every period), on the calling thread.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    private DateTimeOffset _now = now;
    private long _timestamp = now.UtcTicks;

    /// <summary>The wall clock. Setting it forward moves the monotonic clock too (time passed); setting it back moves only the wall clock, as a correction would.</summary>
    public DateTimeOffset Now
    {
        get => _now;
        set
        {
            _timestamp += Math.Max(0, (value - _now).Ticks);
            _now = value;
        }
    }

    /// <summary>How many timers are armed (not disposed, not fired and without a period).</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count(t => t.IsArmed);
            }
        }
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        timer.Change(dueTime, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock forward and fires every timer that came due, earliest first.</summary>
    public void Advance(TimeSpan by)
    {
        Now = _now + by;
        while (true)
        {
            ManualTimer[] due;
            lock (_timers)
            {
                due = [.. _timers.Where(t => t.IsDue(_timestamp)).OrderBy(t => t.DueAt)];
            }

            if (due.Length == 0)
            {
                return;
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        // Timers run on the monotonic clock, as real ones do: a wall clock stepped back doesn't postpone them.
        private long? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public long? DueAt => _dueAt;

        public bool IsArmed => _dueAt is not null;

        public bool IsDue(long timestamp) => _dueAt is { } at && at <= timestamp;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._timestamp + dueTime.Ticks;
            _period = period;
            return true;
        }

        public void Fire()
        {
            _dueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner._timestamp + _period.Ticks;
            callback(state);
        }

        public void Dispose() => _dueAt = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
