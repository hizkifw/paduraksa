using System.Diagnostics;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Where a request's stages sit on its time (docs/design/observability.md "Request timing"): the start offsets the entry keeps, and the
/// upstream wait derived from them, which the access log, the live tail and the API all read from here so they agree.
/// </summary>
public static class RequestStages
{
    /// <summary>
    /// <paramref name="timestamp"/> in microseconds after <paramref name="started"/> (both <see cref="Stopwatch"/> timestamps), or
    /// <see cref="AccessLogEntry.UnknownOffset"/> when it wasn't taken (0) or lies outside what an <see cref="int"/> holds (about 35
    /// minutes into the request).
    /// </summary>
    public static int Offset(long started, long timestamp)
    {
        if (timestamp == 0 || started == 0)
        {
            return AccessLogEntry.UnknownOffset;
        }

        var micros = Stopwatch.GetElapsedTime(started, timestamp).Ticks / TicksPerMicrosecond;
        return micros is >= 0 and <= int.MaxValue ? (int)micros : AccessLogEntry.UnknownOffset;
    }

    /// <summary>
    /// The upstream wait, in <see cref="TimeSpan"/> ticks, as Chrome DevTools' "Waiting for server response": from when our side was
    /// sent to when the upstream answered (its response headers; for a forward that failed, the failure).
    /// <para>"Sent" is the later of the request's headers going out (the connect's end on a new connection, the forward's start on a
    /// pooled one) and the body's last byte read from the client. The body is read as YARP copies it upstream, read by read, so the
    /// last read stands in for the last byte forwarded (a forwarded-side timestamp would mean wrapping YARP's request content).</para>
    /// <para>0 when the upstream answered before that (an early 413 or a redirect while the body was still arriving): the body then
    /// overlaps the answer, or was cut off by it (<paramref name="bodyUnfinished"/>: no read found its end, so its last byte read is not
    /// when it was all sent). <see cref="AccessLogEntry.Unknown"/> when nothing was forwarded.</para>
    /// </summary>
    public static long WaitTicks(int forwardStartMicros, long upstreamTicks, int connectStartMicros, long connectTicks, int bodyStartMicros, long bodyTicks, bool bodyUnfinished)
    {
        if (forwardStartMicros < 0 || upstreamTicks < 0)
        {
            return AccessLogEntry.Unknown;
        }

        if (bodyUnfinished && bodyStartMicros >= 0)
        {
            return 0;
        }

        var forwardStart = forwardStartMicros * TicksPerMicrosecond;
        var answered = Add(forwardStart, upstreamTicks);
        var sent = forwardStart;
        if (connectStartMicros >= 0 && connectTicks > 0)
        {
            sent = Math.Max(sent, Add(connectStartMicros * TicksPerMicrosecond, connectTicks));
        }

        if (bodyStartMicros >= 0 && bodyTicks >= 0)
        {
            sent = Math.Max(sent, Add(bodyStartMicros * TicksPerMicrosecond, bodyTicks));
        }

        return Math.Max(0, answered - sent);
    }

    /// <summary>Where the upstream wait starts, in <see cref="TimeSpan"/> ticks from the request's start: the answer minus the wait.</summary>
    public static long WaitStartTicks(int forwardStartMicros, long upstreamTicks, long waitTicks) =>
        forwardStartMicros < 0 || upstreamTicks < 0 || waitTicks < 0 ? AccessLogEntry.Unknown : Add(forwardStartMicros * TicksPerMicrosecond, upstreamTicks) - waitTicks;

    // An offset (at most about 2e10 ticks) plus a non-negative duration, held at long.MaxValue rather than wrapping.
    private static long Add(long offset, long ticks) => ticks > long.MaxValue - offset ? long.MaxValue : offset + ticks;

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
}
