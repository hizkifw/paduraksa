namespace Paduraksa.Tests.Acme.Fakes;

/// <summary>
/// A clock that only moves when something waits on it: a timer set for <c>d</c> advances the clock by <c>d</c> and fires
/// at once. Polling tests run instantly, and the requested delays are recorded in <see cref="Delays"/>. Timers longer than
/// <see cref="AutoFireLimit"/> (the request deadlines) never fire.
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    private long _ticks;

    public List<TimeSpan> Delays { get; } = [];

    public TimeSpan AutoFireLimit { get; } = TimeSpan.FromHours(1);

    public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Now + TimeSpan.FromTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime <= AutoFireLimit)
        {
            Delays.Add(dueTime);
            Interlocked.Add(ref _ticks, dueTime.Ticks);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
