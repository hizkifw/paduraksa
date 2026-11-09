using System.Net;

using Microsoft.AspNetCore.Connections;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Stage A for QUIC: QUIC has no <c>ListenOptions.Use</c>, so the connection-level work runs as the
/// <see cref="QuicConnectionGate"/> of the <see cref="TlsHandshakeSelector"/>, after the certificate lookup. It
/// publishes <see cref="ClientInfo.FromEndPoint"/> as the connection's <see cref="IClientInfoFeature"/>, the same
/// feature <see cref="ClientInfoMiddleware"/> sets on TCP, so H3 requests resolve their client like H1/H2 ones.
/// </summary>
/// <remarks>
/// <para>QUIC reports dual-mode IPv4 peers as plain IPv4 and TCP as IPv4-mapped; <see cref="IpKey"/> stores both the
/// same way, so policies, rate keys and logs agree across transports.</para>
/// <para>No PROXY protocol on QUIC (it is a TCP framing). One <see cref="ClientInfo"/> per QUIC connection, like TCP.</para>
/// <para><b>Nothing is acquired here yet</b>, so the documented gap (no <c>ConnectionClosed</c> after a handshake that
/// fails past the gate, see <see cref="QuicConnectionGate"/>) cannot leak anything. A per-IP connection cap must pair
/// its slot with a bound that does not depend on that signal.</para>
/// </remarks>
public static class QuicClientInfoGate
{
    /// <summary>The gate delegate, created once (pass it to <see cref="TlsHandshakeSelector"/>).</summary>
    public static QuicConnectionGate Gate { get; } = Admit;

    /// <summary>Admits the connection and attaches its <see cref="ClientInfo"/>; false would reject the handshake.</summary>
    public static bool Admit(IPEndPoint? remoteEndPoint, ConnectionContext? connection)
    {
        ClientInfo info = ClientInfo.FromEndPoint(remoteEndPoint);

        // SEAM: the global connection-level IP deny and the per-IP connection cap go here, on info.Address,
        // reading the live snapshot's deny set. Neither exists yet, so every connection is admitted.
        connection?.Features.Set<IClientInfoFeature>(info);
        return true;
    }
}
