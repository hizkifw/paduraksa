using System.Diagnostics;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// What a connection knows about its own timing, for the requests it carries (docs/design/observability.md "Request timing"):
/// how long its TLS handshake took and how many requests it has started. A connection feature, set once per connection by the
/// public listeners (<see cref="ConnectionTimingListenOptionsExtensions"/>) and read by the access log at the top of each request.
/// </summary>
/// <remarks>
/// <para><b>Shared by every stream</b> of an HTTP/2 or HTTP/3 connection, so the request count is an atomic and the handshake
/// is written once, before the first request can start (the connection pipeline goes on only after the handshake).</para>
/// <para><b>TCP:</b> the handshake runs from the middleware just before Kestrel's <c>UseHttps</c> (after PROXY protocol, the
/// lockdown and the connection limits) to the middleware just after it, so it covers the wait for the ClientHello as well as the
/// exchange. <b>QUIC:</b> from the handshake callback (the ClientHello has arrived) to the established connection's middleware.</para>
/// </remarks>
public sealed class ConnectionTimings
{
    /// <summary>The handshake isn't known: a plain connection, or a TLS one whose handshake hasn't finished.</summary>
    public const long Unknown = -1;

    private readonly long _handshakeStarted;
    private long _handshakeTicks = Unknown;
    private int _requests;

    private ConnectionTimings(bool tls, long handshakeStarted)
    {
        IsTls = tls;
        _handshakeStarted = handshakeStarted;
    }

    /// <summary>Whether the connection is TLS (TCP with TLS, or QUIC).</summary>
    public bool IsTls { get; }

    /// <summary>The handshake in <see cref="TimeSpan"/> ticks, or <see cref="Unknown"/>.</summary>
    public long HandshakeTicks => Volatile.Read(ref _handshakeTicks);

    /// <summary>Requests started on the connection so far.</summary>
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>A plain-HTTP connection: nothing to time but its requests.</summary>
    public static ConnectionTimings Plain() => new(tls: false, 0);

    /// <summary>A TLS connection whose handshake starts now.</summary>
    public static ConnectionTimings StartingHandshake() => new(tls: true, Stopwatch.GetTimestamp());

    /// <summary>The handshake is over; a second call changes nothing.</summary>
    public void HandshakeCompleted()
    {
        if (IsTls && Volatile.Read(ref _handshakeTicks) == Unknown)
        {
            Volatile.Write(ref _handshakeTicks, Stopwatch.GetElapsedTime(_handshakeStarted).Ticks);
        }
    }

    /// <summary>Counts a request starting on the connection; its index, from 1.</summary>
    internal int NextRequest() => Interlocked.Increment(ref _requests);
}

/// <summary>How the public listeners put <see cref="ConnectionTimings"/> on their connections.</summary>
public static class ConnectionTimingListenOptionsExtensions
{
    /// <summary>A plain-HTTP listener: counts each connection's requests. Allocates one small object per connection.</summary>
    public static ListenOptions UseConnectionTimings(this ListenOptions listenOptions)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        listenOptions.Use(static next => connection =>
        {
            connection.Features.Set(ConnectionTimings.Plain());
            return next(connection);
        });
        return listenOptions;
    }

    /// <summary>
    /// Kestrel's <c>UseHttps</c> with the handshake timed around it. Call it where <c>UseHttps</c> would go, after the
    /// connection middleware that runs before TLS, so their waits (a PROXY protocol header) aren't counted as the handshake.
    /// </summary>
    public static ListenOptions UseTimedHttps(this ListenOptions listenOptions, TlsHandshakeCallbackOptions callbackOptions)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ArgumentNullException.ThrowIfNull(callbackOptions);
        listenOptions.Use(static next => connection =>
        {
            connection.Features.Set(ConnectionTimings.StartingHandshake());
            return next(connection);
        });
        listenOptions.UseHttps(callbackOptions);
        listenOptions.Use(static next => connection =>
        {
            connection.Features.Get<ConnectionTimings>()?.HandshakeCompleted();
            return next(connection);
        });
        return listenOptions;
    }

    /// <summary>
    /// The QUIC listener: ends the handshake the TLS callback started (<see cref="StartQuicHandshake"/>) when the connection is
    /// established, which is when Kestrel runs the multiplexed middleware.
    /// </summary>
    public static ListenOptions UseQuicHandshakeTiming(this ListenOptions listenOptions)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ((IMultiplexedConnectionBuilder)listenOptions).Use(static next => connection =>
        {
            connection.Features.Get<ConnectionTimings>()?.HandshakeCompleted();
            return next(connection);
        });
        return listenOptions;
    }

    /// <summary>Starts a QUIC connection's handshake timing, from the TLS callback that has just read its ClientHello.</summary>
    public static void StartQuicHandshake(ConnectionContext? connection) => connection?.Features.Set(ConnectionTimings.StartingHandshake());
}
