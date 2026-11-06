using System.Net;
using System.Net.Security;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Connection-stage gate for QUIC (PLAN §7.2 A). QUIC has no <c>ListenOptions.Use</c>, so the IP deny / per-IP cap /
/// ClientInfo work runs inside the QUIC handshake callback, before any certificate is chosen. Return false to reject.
/// </summary>
/// <param name="remoteEndPoint">
/// The client's endpoint, or null if Kestrel didn't provide an IP endpoint. On a dual-mode <c>[::]</c> listener QUIC
/// reports IPv4 clients as plain IPv4 while TCP reports them IPv4-mapped (spike S1), so normalise before matching.
/// </param>
/// <param name="connection">The QUIC connection (e.g. to attach a ClientInfo feature), or null when not called from Kestrel.</param>
/// <remarks>Runs once per QUIC handshake on the hot path: it must not block or allocate.</remarks>
public delegate bool QuicConnectionGate(IPEndPoint? remoteEndPoint, ConnectionContext? connection);

/// <summary>
/// Per-SNI certificate selection for Kestrel's TCP and QUIC listeners (PLAN §7.2 B). Each handshake does one volatile
/// read of the current <see cref="CertificateTable"/>, one 0 B lookup, and returns a prebuilt, shared options object.
/// Unknown names are rejected by throwing <see cref="TlsHandshakeRejectedException"/> and counted, because Kestrel
/// logs QUIC rejections nowhere (and TCP ones only at Debug).
/// </summary>
/// <remarks>
/// One instance per process (the counters are process-lifetime state, PLAN §6.3). The accepted path allocates nothing;
/// a rejection allocates its exception.
/// </remarks>
public sealed class TlsHandshakeSelector
{
    // Static delegates + OnConnectionState = this: no closure, nothing allocated per handshake.
    private static readonly Func<TlsHandshakeCallbackContext, ValueTask<SslServerAuthenticationOptions>> TcpCallback = OnTcpHandshake;
    private static readonly Func<TlsHandshakeCallbackContext, ValueTask<SslServerAuthenticationOptions>> QuicCallback = OnQuicHandshake;

    private readonly ICertificateTableSource _tables;
    private readonly QuicConnectionGate? _quicGate;

    private long _unknownServerNameTcp;
    private long _unknownServerNameQuic;
    private long _http3Disabled;
    private long _connectionDenied;

    public TlsHandshakeSelector(ICertificateTableSource tables, QuicConnectionGate? quicConnectionGate = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = tables;
        _quicGate = quicConnectionGate;
    }

    /// <summary>TCP handshakes rejected for an unknown (or absent, with no default) server name.</summary>
    public long RejectedUnknownServerNameTcp => Interlocked.Read(ref _unknownServerNameTcp);

    /// <summary>QUIC handshakes rejected for an unknown (or absent, with no default) server name.</summary>
    public long RejectedUnknownServerNameQuic => Interlocked.Read(ref _unknownServerNameQuic);

    /// <summary>QUIC handshakes rejected because the matching host opted out of HTTP/3.</summary>
    public long RejectedHttp3Disabled => Interlocked.Read(ref _http3Disabled);

    /// <summary>QUIC handshakes rejected by the <see cref="QuicConnectionGate"/>.</summary>
    public long RejectedConnectionDenied => Interlocked.Read(ref _connectionDenied);

    /// <summary>Options for the TCP (H1/H2) listener's <c>UseHttps</c>.</summary>
    public TlsHandshakeCallbackOptions CreateTcpCallbackOptions() =>
        new() { OnConnection = TcpCallback, OnConnectionState = this };

    /// <summary>Options for the QUIC (H3) listener's <c>UseHttps</c>; also runs the <see cref="QuicConnectionGate"/>.</summary>
    public TlsHandshakeCallbackOptions CreateQuicCallbackOptions() =>
        new() { OnConnection = QuicCallback, OnConnectionState = this };

    /// <summary>Selects the TCP options for <paramref name="serverName"/> (empty or null = no SNI), or throws.</summary>
    /// <exception cref="TlsHandshakeRejectedException">No entry and no default.</exception>
    public SslServerAuthenticationOptions SelectTcp(string? serverName)
    {
        var entry = _tables.Current.Find(serverName);
        if (entry is null)
        {
            Interlocked.Increment(ref _unknownServerNameTcp);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.UnknownServerName, serverName ?? string.Empty);
        }

        return entry.TcpOptions;
    }

    /// <summary>
    /// Runs the connection gate, then selects the QUIC options for <paramref name="serverName"/>, or throws. A host that
    /// opted out of H3 is rejected rather than served by the wildcard or default certificate.
    /// </summary>
    /// <exception cref="TlsHandshakeRejectedException">Denied by the gate, no entry, or H3 disabled for the host.</exception>
    public SslServerAuthenticationOptions SelectQuic(string? serverName, IPEndPoint? remoteEndPoint, ConnectionContext? connection)
    {
        if (_quicGate is not null && !_quicGate(remoteEndPoint, connection))
        {
            Interlocked.Increment(ref _connectionDenied);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.ConnectionDenied, serverName ?? string.Empty);
        }

        var entry = _tables.Current.Find(serverName);
        if (entry is null)
        {
            Interlocked.Increment(ref _unknownServerNameQuic);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.UnknownServerName, serverName ?? string.Empty);
        }

        if (entry.QuicOptions is not { } options)
        {
            Interlocked.Increment(ref _http3Disabled);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.Http3Disabled, serverName ?? string.Empty);
        }

        return options;
    }

    private static ValueTask<SslServerAuthenticationOptions> OnTcpHandshake(TlsHandshakeCallbackContext context)
    {
        var selector = (TlsHandshakeSelector)context.State!;
        return ValueTask.FromResult(selector.SelectTcp(context.ClientHelloInfo.ServerName));
    }

    private static ValueTask<SslServerAuthenticationOptions> OnQuicHandshake(TlsHandshakeCallbackContext context)
    {
        var selector = (TlsHandshakeSelector)context.State!;
        var connection = context.Connection;
        return ValueTask.FromResult(selector.SelectQuic(
            context.ClientHelloInfo.ServerName,
            connection?.RemoteEndPoint as IPEndPoint,
            connection));
    }
}
