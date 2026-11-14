using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Tells a request whether it paid for an upstream connection (DNS, TCP and, for an HTTPS upstream, TLS) and how long
/// (docs/design/observability.md "Request timing"). The handler's plaintext-stream filter, which runs once TLS is up and before
/// the first byte of HTTP, stamps the connect's end, keyed by the request that made the pool open the connection; the start is the
/// forward's start.
/// </summary>
/// <remarks>
/// <para><b>Cost.</b> A request on a pooled connection pays one lock-free <see cref="ConditionalWeakTable{TKey,TValue}"/> lookup
/// at its end, 0 B. A new connection pays a small object and the table's lock to add it, on a path that is waiting on the network
/// anyway. The keys are weak, so a message nobody reads again goes with the next collection.</para>
/// <para><b>No connect callback.</b> Timing the start would take a <see cref="SocketsHttpHandler.ConnectCallback"/>, and any
/// callback replaces the handler's own connect with an extra async frame and a rethrow of the failure: measured at 6-8 µs of CPU
/// per failed connect on a 123 µs one, and 10-12 µs with the table add. A closed upstream's 502 page is a path worth keeping
/// cheap, so the start is taken from the forward's start instead. That overstates a new connection's connect by YARP's own header
/// copy and the remaining request transforms, a few µs.</para>
/// <para><b>A connect that failed</b> never reaches the filter, so it leaves no entry; the forwarder's error says it was the
/// connect that failed, and the connect is then the whole upstream leg.</para>
/// <para><b>Attribution.</b> The pool opens a connection on behalf of a waiting request, but hands that request whichever
/// connection is free first. When a pooled connection frees up before the new one is ready, the request that triggered the connect
/// is reported with it though it didn't wait for all of it, and the request that does get the new connection reports none. The
/// figure is capped at the request's upstream time. Under steady keep-alive traffic both are rare.</para>
/// <para><b>HTTP/3 upstreams</b> are not timed: the pool runs the plaintext filter only for HTTP/1.1 and HTTP/2 connections, so an
/// HTTP/3 connect would never leave an entry. No cluster asks for HTTP/3 to its upstream today; one that did would report its
/// connects as 0, or, for a failed forward, the whole upstream time.</para>
/// </remarks>
internal static class UpstreamConnects
{
    private static readonly ConditionalWeakTable<HttpRequestMessage, Connect> Ended = new();

    /// <summary>The plaintext-stream filter: the connection is ready for HTTP, so its connect is over.</summary>
    public static ValueTask<Stream> CompletedAsync(SocketsHttpPlaintextStreamFilterContext context, CancellationToken cancellationToken)
    {
        Ended.AddOrUpdate(context.InitialRequestMessage, new Connect(Stopwatch.GetTimestamp()));
        return ValueTask.FromResult(context.PlaintextStream);
    }

    /// <summary>
    /// The connect <paramref name="message"/> paid for, in <see cref="TimeSpan"/> ticks, at most <paramref name="upstreamTicks"/>: 0
    /// when it went out on a pooled connection, or was answered though its own connect never finished (the pool handed it a
    /// connection that freed up first); all of <paramref name="upstreamTicks"/> when the forward failed while connecting.
    /// <paramref name="forwardStarted"/> is the forward's <see cref="Stopwatch"/> timestamp; the connect is taken to start there, and
    /// that is <paramref name="startedTimestamp"/> whenever the figure is above 0, else 0. <paramref name="failure"/> is the
    /// forwarder's error for a forward that was not answered, else null.
    /// </summary>
    public static long TicksFor(HttpRequestMessage message, long forwardStarted, long upstreamTicks, Exception? failure, out long startedTimestamp)
    {
        long ticks;
        if (Ended.TryGetValue(message, out var connect))
        {
            ticks = Math.Clamp(Stopwatch.GetElapsedTime(forwardStarted, connect.Ended).Ticks, 0, upstreamTicks);
        }
        else
        {
            ticks = failure is not null && IsConnectFailure(failure) ? upstreamTicks : 0;
        }

        startedTimestamp = ticks > 0 ? forwardStarted : 0;
        return ticks;
    }

    // What the handler throws while it is still connecting: its connect timeout is a cancellation with a TimeoutException inside
    // (not an HttpRequestException), and DNS, TCP and TLS each have an error of their own. A failure on a connection that was up
    // is another error, or a plain HttpRequestException.
    private static bool IsConnectFailure(Exception failure) => failure switch
    {
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError } => true,
        OperationCanceledException { InnerException: TimeoutException } => true,
        _ => false,
    };

    private sealed class Connect(long ended)
    {
        public long Ended => ended;
    }
}
