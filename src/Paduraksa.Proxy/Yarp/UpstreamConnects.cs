using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Times the upstream connections the forwarder opens (DNS, TCP and, for an HTTPS upstream, TLS) and tells a request whether it
/// paid for one (docs/design/observability.md "Request timing"). The handler's connect callback starts the clock, its
/// plaintext-stream filter (which runs once TLS is up, before the first byte of HTTP) stops it, and both are keyed by the request
/// that made the pool open the connection.
/// </summary>
/// <remarks>
/// <para><b>Cost.</b> A request on a pooled connection pays one lock-free <see cref="ConditionalWeakTable{TKey,TValue}"/> lookup
/// at its end, 0 B. A new connection pays a small object and the table's lock to add it, on a path that is waiting on the network
/// anyway. The keys are weak, so a message nobody reads again goes with the next collection.</para>
/// <para><b>Attribution.</b> The pool opens a connection on behalf of a waiting request, but hands that request whichever
/// connection is free first. When a pooled connection frees up before the new one is ready, the request that triggered the connect
/// is reported with it though it didn't wait for all of it, and the request that does get the new connection reports none. The
/// figure is capped at the request's upstream time. Under steady keep-alive traffic both are rare.</para>
/// <para><b>HTTP/3 upstreams</b> are not timed: the pool runs the plaintext filter only for HTTP/1.1 and HTTP/2 connections, so an
/// HTTP/3 connect would never stop the clock. No cluster asks for HTTP/3 to its upstream today; one that did would report its
/// connects as unknown-and-answered (0) or, for a failed forward, the whole upstream time.</para>
/// </remarks>
internal static class UpstreamConnects
{
    private static readonly ConditionalWeakTable<HttpRequestMessage, Connect> Started = new();

    /// <summary>The connect callback a cluster's handler uses: <paramref name="inner"/> (null: the handler's own TCP connect), timed.</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Timed(
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? inner)
    {
        var connect = inner ?? DefaultConnectAsync;
        return (context, cancellationToken) =>
        {
            Started.AddOrUpdate(context.InitialRequestMessage, new Connect(Stopwatch.GetTimestamp()));
            return connect(context, cancellationToken);
        };
    }

    /// <summary>The plaintext-stream filter: the connection is ready for HTTP, so its connect is over.</summary>
    public static ValueTask<Stream> CompletedAsync(SocketsHttpPlaintextStreamFilterContext context, CancellationToken cancellationToken)
    {
        if (Started.TryGetValue(context.InitialRequestMessage, out var connect))
        {
            connect.Complete();
        }

        return ValueTask.FromResult(context.PlaintextStream);
    }

    /// <summary>
    /// The connect <paramref name="message"/> paid for, in <see cref="TimeSpan"/> ticks, at most <paramref name="upstreamTicks"/>: 0
    /// when it went out on a pooled connection; when its connect never finished, 0 if the forward was <paramref name="answered"/>
    /// anyway (the pool handed it a connection that freed up while its own was still opening) and all of
    /// <paramref name="upstreamTicks"/> if not (the forward failed while connecting).
    /// </summary>
    public static long TicksFor(HttpRequestMessage message, long upstreamTicks, bool answered)
    {
        if (!Started.TryGetValue(message, out var connect))
        {
            return 0;
        }

        var ticks = connect.Ticks;
        if (ticks < 0)
        {
            return answered ? 0 : upstreamTicks;
        }

        return ticks > upstreamTicks ? upstreamTicks : ticks;
    }

    // What SocketsHttpHandler does without a callback: a dual-mode socket, Nagle off, every address of the name tried in turn.
    private static async ValueTask<Stream> DefaultConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class Connect(long started)
    {
        private long _ticks = -1;

        public long Ticks => Volatile.Read(ref _ticks);

        public void Complete() => Volatile.Write(ref _ticks, Stopwatch.GetElapsedTime(started).Ticks);
    }
}
