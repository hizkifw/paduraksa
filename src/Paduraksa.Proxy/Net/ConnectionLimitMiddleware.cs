using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Stage-A connection middleware: the connection deny list and the per-IP connection cap (PLAN §7.2 A), before TLS. A
/// refused connection is aborted unread, so a scanner on the deny list never gets as far as a handshake. Install it after
/// <see cref="ClientInfoMiddleware"/> (the address is the PROXY protocol source when there is one) and
/// <see cref="OriginLockdownMiddleware"/>, and before <c>UseHttps</c>.
/// </summary>
/// <remarks>
/// <para>A connection that isn't counted (the stage is off, the peer is a trusted proxy, the table had no room) goes
/// straight on and allocates nothing here. A counted one is released when the rest of the pipeline ends, which costs the
/// continuation that waits for it: that is what <see cref="ConnectionRuntime.Release"/> needs and the only per-connection
/// allocation of this class. A refused one allocates its abort exception, like the PROXY protocol and lockdown drops.</para>
/// <para>This sees the connecting address: behind a trusted proxy that is the proxy, which is exempt from the cap, and
/// the visitors behind it are judged by their real addresses later, at the gate (access lists, rate limits).</para>
/// </remarks>
public sealed class ConnectionLimitMiddleware
{
    private readonly ConnectionDelegate _next;
    private readonly SnapshotStore _store;
    private readonly ConnectionRuntime _runtime;

    public ConnectionLimitMiddleware(ConnectionDelegate next, SnapshotStore store, ConnectionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        _next = next;
        _store = store;
        _runtime = runtime;
    }

    public Task OnConnectionAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var snapshot = _store.Current;
        if (snapshot.Connections.IsInert)
        {
            return _next(connection);
        }

        switch (_runtime.Admit(snapshot, AddressOf(connection), ConnectionTransport.Tcp, out var hash))
        {
            case ConnectionDecision.Denied:
                connection.Abort(new ConnectionAbortedException("Connection limits: the address is in the connection deny list."));
                return Task.CompletedTask;
            case ConnectionDecision.OverCap:
                connection.Abort(new ConnectionAbortedException("Connection limits: the address already holds its limit of connections."));
                return Task.CompletedTask;
            case ConnectionDecision.Counted:
                return ServeCountedAsync(connection, hash);
            default:
                return _next(connection);
        }
    }

    internal static IpKey AddressOf(ConnectionContext connection)
    {
        if (connection.Features.Get<IClientInfoFeature>()?.ClientInfo is { } info)
        {
            return info.Address;
        }

        _ = IpKey.TryFromEndPoint(connection.RemoteEndPoint, out var address, out _);
        return address;
    }

    private async Task ServeCountedAsync(ConnectionContext connection, ulong hash)
    {
        try
        {
            await _next(connection).ConfigureAwait(false);
        }
        finally
        {
            _runtime.Release(hash);
        }
    }
}

public static class ConnectionLimitListenOptionsExtensions
{
    /// <summary>Adds <see cref="ConnectionLimitMiddleware"/> to a TCP listener, after <c>UseClientInfo</c> and before <c>UseHttps</c>.</summary>
    public static ListenOptions UseConnectionLimits(this ListenOptions listenOptions, SnapshotStore store, ConnectionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runtime);
        listenOptions.Use(next => new ConnectionLimitMiddleware(next, store, runtime).OnConnectionAsync);
        return listenOptions;
    }
}
