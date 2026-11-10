namespace Paduraksa.Tests.Data;

/// <summary>
/// Settable clock for generators and stores. Timers it creates fire when <see cref="Advance"/> moves the clock past them
/// (once, or every period), on the calling thread.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = now;

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

    public override DateTimeOffset GetUtcNow() => Now;

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
        Now += by;
        while (true)
        {
            ManualTimer[] due;
            lock (_timers)
            {
                due = [.. _timers.Where(t => t.IsDue(Now)).OrderBy(t => t.DueAt)];
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
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt => _dueAt;

        public bool IsArmed => _dueAt is not null;

        public bool IsDue(DateTimeOffset now) => _dueAt is { } at && at <= now;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now + dueTime;
            _period = period;
            return true;
        }

        public void Fire()
        {
            _dueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : owner.Now + _period;
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
