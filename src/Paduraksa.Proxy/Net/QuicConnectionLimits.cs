using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// The QUIC side of the connection stage (docs/design/hot-path.md "A. Connection"). QUIC has no connection middleware before the handshake, so the
/// work is split where the transport allows it.
/// </summary>
/// <remarks>
/// <para><b>In the handshake callback</b> (<see cref="CreateGate"/>): the deny list and the cap <i>read</i>, never taken. Kestrel
/// signals no close for a handshake that fails after the callback returned (<see cref="QuicConnectionGate"/>), so a place
/// taken here could leak and lock an address out for good, which an attacker spoofing a victim's address would use.</para>
/// <para><b>When the connection is established</b> (<see cref="QuicConnectionLimitMiddleware"/>, a multiplexed connection
/// middleware that runs only for accepted connections): the place is taken and released with the connection, as TCP does.
/// Handshakes still in flight aren't counted, so a burst of simultaneous handshakes from one address may pass the cap by
/// as many as the burst; the established connections can't.</para>
/// </remarks>
public static class QuicConnectionLimits
{
    /// <summary>
    /// A gate that runs <paramref name="inner"/> first (the client info and the lockdown), then the deny list and the cap
    /// read. Created once per process.
    /// </summary>
    public static QuicConnectionGate CreateGate(SnapshotStore store, ConnectionRuntime runtime, QuicConnectionGate inner)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(inner);
        return (remoteEndPoint, connection) =>
        {
            if (!inner(remoteEndPoint, connection))
            {
                return false;
            }

            var snapshot = store.Current;
            if (snapshot.Connections.IsInert)
            {
                return true;
            }

            _ = IpKey.TryFromEndPoint(remoteEndPoint, out var address, out _);
            return runtime.Check(snapshot, address, ConnectionTransport.Quic) is ConnectionDecision.Admitted;
        };
    }

    /// <summary>Adds the established-connection counting to the HTTP/3 listener.</summary>
    public static ListenOptions UseQuicConnectionLimits(this ListenOptions listenOptions, SnapshotStore store, ConnectionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        ((IMultiplexedConnectionBuilder)listenOptions).Use(next => new QuicConnectionLimitMiddleware(next, store, runtime).OnConnectionAsync);
        return listenOptions;
    }
}

/// <summary>Counts an established QUIC connection against its address's cap and releases it when the connection ends.</summary>
public sealed class QuicConnectionLimitMiddleware
{
    private readonly MultiplexedConnectionDelegate _next;
    private readonly SnapshotStore _store;
    private readonly ConnectionRuntime _runtime;

    public QuicConnectionLimitMiddleware(MultiplexedConnectionDelegate next, SnapshotStore store, ConnectionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        _next = next;
        _store = store;
        _runtime = runtime;
    }

    public Task OnConnectionAsync(MultiplexedConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var snapshot = _store.Current;
        if (snapshot.Connections.IsInert)
        {
            return _next(connection);
        }

        var address = connection.Features.Get<IClientInfoFeature>()?.ClientInfo.Address ?? AddressOfEndPoint(connection);
        switch (_runtime.Admit(snapshot, address, ConnectionTransport.Quic, out var hash))
        {
            case ConnectionDecision.Denied:
            case ConnectionDecision.OverCap:
                connection.Abort(new ConnectionAbortedException("Connection limits: the address may not hold another connection."));
                return Task.CompletedTask;
            case ConnectionDecision.Counted:
                return ServeCounted(connection, hash);
            default:
                return _next(connection);
        }
    }

    private static IpKey AddressOfEndPoint(MultiplexedConnectionContext connection)
    {
        _ = IpKey.TryFromEndPoint(connection.RemoteEndPoint, out var address, out _);
        return address;
    }

    // A pipeline that already finished is given back at once, with no continuation; one still running is awaited.
    private Task ServeCounted(MultiplexedConnectionContext connection, ulong hash)
    {
        Task served;
        try
        {
            served = _next(connection);
        }
        catch
        {
            _runtime.Release(hash);
            throw;
        }

        if (served.IsCompleted)
        {
            _runtime.Release(hash);
            return served;
        }

        return ReleaseWhenDoneAsync(served, hash);
    }

    private async Task ReleaseWhenDoneAsync(Task served, ulong hash)
    {
        try
        {
            await served.ConfigureAwait(false);
        }
        finally
        {
            _runtime.Release(hash);
        }
    }
}
