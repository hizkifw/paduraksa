namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The page's clock, except that the 250 ms timer behind the dashboard's form fields (<c>DebounceInterval="250"</c>) never fires. MudBlazor
/// applies a typed value when that timer fires, or when the field is left; a test that types and leaves the field meant the second, but under
/// load the timer can fire between the two: the value is then queued behind the leave, a re-render that comes first sets the unfocused
/// field's text back to the old value, and the typed text is lost without an error (the form stays invalid and its submit does nothing).
/// With the timer held, leaving the field is the only way a typed value is applied, which is what the tests do.
/// </summary>
internal sealed class HeldDebounceTimeProvider(TimeProvider inner) : TimeProvider
{
    private static readonly TimeSpan FormFieldDebounce = TimeSpan.FromMilliseconds(250);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        dueTime == FormFieldDebounce && period == Timeout.InfiniteTimeSpan ? new Held() : inner.CreateTimer(callback, state, dueTime, period);

    private sealed class Held : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
