using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.ProxyProtocol;

namespace Paduraksa.Proxy.Net;

/// <summary>Connections the Cloudflare lockdown dropped before TLS (process lifetime; read by metrics).</summary>
public sealed class OriginLockdownCounters
{
    private long _tcp;
    private long _quic;

    /// <summary>TCP connections dropped by <see cref="OriginLockdownMiddleware"/>.</summary>
    public long Tcp => Volatile.Read(ref _tcp);

    /// <summary>QUIC handshakes refused by the gate <see cref="OriginLockdown.CreateQuicGate"/> makes.</summary>
    public long Quic => Volatile.Read(ref _quic);

    internal void CountTcp() => Interlocked.Increment(ref _tcp);

    internal void CountQuic() => Interlocked.Increment(ref _quic);
}

/// <summary>The connection-stage form of origin lockdown, for both transports.</summary>
public static class OriginLockdown
{
    /// <summary>
    /// Whether the live snapshot refuses a connection from <paramref name="peer"/> before TLS: every host in it is locked
    /// down (<see cref="RuntimeSnapshot.LockdownAtConnection"/>, decided once at compile time) and the peer isn't one of its
    /// trusted Cloudflare peers. 0 B.
    /// </summary>
    public static bool RefusesBeforeTls(RuntimeSnapshot snapshot, IpKey peer) =>
        snapshot.LockdownAtConnection && !snapshot.TrustedProxies.IsTrustedPeer(peer);

    /// <summary>
    /// The QUIC counterpart of <see cref="OriginLockdownMiddleware"/>: QUIC has no connection middleware, so the same check
    /// runs in the handshake callback, after the connection's <see cref="ClientInfo"/> is set (the same one
    /// <see cref="QuicClientInfoGate.Admit"/> publishes). Created once per process.
    /// </summary>
    public static QuicConnectionGate CreateQuicGate(SnapshotStore store, OriginLockdownCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (remoteEndPoint, connection) =>
        {
            if (!QuicClientInfoGate.Admit(remoteEndPoint, connection))
            {
                return false;
            }

            _ = IpKey.TryFromEndPoint(remoteEndPoint, out var peer, out _);
            if (!RefusesBeforeTls(store.Current, peer))
            {
                return true;
            }

            counters?.CountQuic();
            return false;
        };
    }
}

/// <summary>
/// Stage-A TCP connection middleware: when every host is locked down to Cloudflare, a peer that isn't a trusted Cloudflare
/// peer is dropped before the TLS handshake, so the proxy answers a scanner with nothing at all. Install it after
/// <see cref="ClientInfoMiddleware"/> (the peer is the PROXY protocol source when there is one) and before TLS. The
/// plain-HTTP listener doesn't get it: an ACME validation of a DNS-only record comes from the CA's address, and the gate
/// answers its challenge before the host's lockdown applies.
/// </summary>
public sealed class OriginLockdownMiddleware
{
    private readonly ConnectionDelegate _next;
    private readonly SnapshotStore _store;
    private readonly OriginLockdownCounters? _counters;

    public OriginLockdownMiddleware(ConnectionDelegate next, SnapshotStore store, OriginLockdownCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        _next = next;
        _store = store;
        _counters = counters;
    }

    public Task OnConnectionAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var snapshot = _store.Current;
        if (!snapshot.LockdownAtConnection)
        {
            return _next(connection);
        }

        IpKey peer;
        if (connection.Features.Get<IClientInfoFeature>()?.ClientInfo is { } info)
        {
            peer = info.Address;
        }
        else
        {
            _ = IpKey.TryFromEndPoint(connection.RemoteEndPoint, out peer, out _);
        }

        if (!OriginLockdown.RefusesBeforeTls(snapshot, peer))
        {
            return _next(connection);
        }

        _counters?.CountTcp();

        // A scanner's connection is the one this runs for: allocated per abort like the PROXY protocol drop, since a shared
        // exception would carry one connection's trace into every other's log.
        connection.Abort(new ConnectionAbortedException("Origin lockdown: the peer is not a Cloudflare address."));
        return Task.CompletedTask;
    }
}

public static class OriginLockdownListenOptionsExtensions
{
    /// <summary>Adds <see cref="OriginLockdownMiddleware"/> to the HTTPS TCP listener, after <c>UseClientInfo</c> and before
    /// <c>UseHttps</c>.</summary>
    public static ListenOptions UseOriginLockdown(this ListenOptions listenOptions, SnapshotStore store, OriginLockdownCounters? counters = null)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ArgumentNullException.ThrowIfNull(store);
        listenOptions.Use(next => new OriginLockdownMiddleware(next, store, counters).OnConnectionAsync);
        return listenOptions;
    }
}
