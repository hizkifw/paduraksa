using System.Diagnostics;

namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Milliseconds since the process started, from <see cref="Stopwatch"/>: monotonic, so a wall-clock jump can neither
/// refill a bucket nor freeze one. Every engine call takes the time as a parameter (tests drive it); this is only how a
/// request and the sweeper read it.
/// </summary>
public static class RateLimitClock
{
    private static readonly long Start = Stopwatch.GetTimestamp();

    public static long NowMs => Stopwatch.GetElapsedTime(Start).Ticks / TimeSpan.TicksPerMillisecond;
}
