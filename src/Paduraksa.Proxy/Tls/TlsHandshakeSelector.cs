using System.Net;
using System.Net.Security;

using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Connection-stage gate for QUIC (PLAN §7.2 A). QUIC has no <c>ListenOptions.Use</c>, so the IP deny / per-IP cap /
/// ClientInfo work runs inside the QUIC handshake callback, after the 0 B certificate lookup but before the options
/// are returned. Return false to reject.
/// </summary>
/// <remarks>
/// <para>Runs once per QUIC handshake on the hot path: it must not block or allocate.</para>
/// <para>
/// <b>Releasing what the gate acquires.</b> The gate runs last in the selection, so when it returns true the callback
/// always returns options. Anything it acquires (e.g. a per-IP connection slot) must be released when the connection
/// closes, via <see cref="BaseConnectionContext.ConnectionClosed"/>. Caveat, verified in Docker: if the TLS handshake fails
/// <i>after</i> the callback returned (e.g. a required client certificate is missing or rejected), Kestrel never fires
/// <c>ConnectionClosed</c> for that connection. A slot taken in the gate would then leak. Pair the
/// acquisition with a bound that doesn't depend on that signal (e.g. a short lease that the established connection
/// confirms) until this is solved (KestrelQuicTlsTests.ConnectionClosedReleasesWhatTheGateAcquired pins the behaviour).
/// </para>
/// </remarks>
/// <param name="remoteEndPoint">
/// The client's endpoint, or null if Kestrel didn't provide an IP endpoint. On a dual-mode <c>[::]</c> listener QUIC
/// reports IPv4 clients as plain IPv4 while TCP reports them IPv4-mapped (spike S1), so normalise before matching.
/// </param>
/// <param name="connection">The QUIC connection (e.g. to attach a ClientInfo feature), or null when not called from Kestrel.</param>
public delegate bool QuicConnectionGate(IPEndPoint? remoteEndPoint, ConnectionContext? connection);

/// <summary>
/// Per-SNI certificate selection for Kestrel's TCP and QUIC listeners (PLAN §7.2 B). Each handshake does one volatile
/// read of the current <see cref="CertificateTable"/>, one 0 B lookup, and returns a prebuilt, shared options object.
/// Unknown names are rejected by throwing <see cref="TlsHandshakeRejectedException"/> and counted, because Kestrel
/// logs QUIC rejections nowhere (and TCP ones only at Debug).
/// </summary>
/// <remarks>
/// One instance per process (its <see cref="Counters"/> are process-lifetime state, PLAN §6.3). The accepted path allocates nothing;
/// a rejection allocates its exception.
/// </remarks>
public sealed class TlsHandshakeSelector
{
    // Static delegates + OnConnectionState = this: no closure, nothing allocated per handshake.
    private static readonly Func<TlsHandshakeCallbackContext, ValueTask<SslServerAuthenticationOptions>> TcpCallback = OnTcpHandshake;
    private static readonly Func<TlsHandshakeCallbackContext, ValueTask<SslServerAuthenticationOptions>> QuicCallback = OnQuicHandshake;

    private readonly ICertificateTableSource _tables;
    private readonly QuicConnectionGate? _quicGate;

    public TlsHandshakeSelector(ICertificateTableSource tables, QuicConnectionGate? quicConnectionGate = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = tables;
        _quicGate = quicConnectionGate;
    }

    /// <summary>Rejected handshakes by reason (process lifetime; read by metrics).</summary>
    public TlsHandshakeCounters Counters { get; } = new();

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
        var selection = Select(_tables.Current, serverName, quic: false);
        if (selection.Options is not { } options)
        {
            Counters.CountUnknownServerName(quic: false);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.UnknownServerName, serverName ?? string.Empty);
        }

        return options;
    }

    /// <summary>
    /// The selection both transports make, without counters, exceptions or the connection gate: the entry for
    /// <paramref name="serverName"/> and its options for the transport, or why the handshake is rejected. The handshake
    /// callbacks and the request simulator (PLAN §7.7) both use it.
    /// </summary>
    public static TlsSelection Select(CertificateTable table, ReadOnlySpan<char> serverName, bool quic)
    {
        ArgumentNullException.ThrowIfNull(table);
        var entry = table.Find(serverName, out var match);
        if (entry is null)
        {
            return new TlsSelection(null, CertificateMatchKind.None, TlsRejectionReason.UnknownServerName, null);
        }

        if (!quic)
        {
            return new TlsSelection(entry, match, null, entry.TcpOptions);
        }

        return entry.QuicOptions is { } options
            ? new TlsSelection(entry, match, null, options)
            : new TlsSelection(entry, match, TlsRejectionReason.Http3Disabled, null);
    }

    /// <summary>
    /// Selects the QUIC options for <paramref name="serverName"/>, then runs the connection gate, or throws. A host that
    /// opted out of H3 is rejected rather than served by the wildcard or default certificate. Unknown names and
    /// H3-disabled hosts are rejected before the gate runs, so an accepting gate always leads to returned options.
    /// </summary>
    /// <exception cref="TlsHandshakeRejectedException">Denied by the gate, no entry, or H3 disabled for the host.</exception>
    public SslServerAuthenticationOptions SelectQuic(string? serverName, IPEndPoint? remoteEndPoint, ConnectionContext? connection)
    {
        // Selection first (0 B, no side effects), the gate last: once the gate has accepted (and possibly acquired a
        // per-IP slot), nothing in here rejects any more.
        var selection = Select(_tables.Current, serverName, quic: true);
        if (selection.Rejection == TlsRejectionReason.UnknownServerName)
        {
            Counters.CountUnknownServerName(quic: true);
            throw new TlsHandshakeRejectedException(TlsRejectionReason.UnknownServerName, serverName ?? string.Empty);
        }

        if (selection.Options is not { } options)
        {
            Counters.CountHttp3Disabled();
            throw new TlsHandshakeRejectedException(TlsRejectionReason.Http3Disabled, serverName ?? string.Empty);
        }

        if (_quicGate is not null && !_quicGate(remoteEndPoint, connection))
        {
            Counters.CountConnectionDenied();
            throw new TlsHandshakeRejectedException(TlsRejectionReason.ConnectionDenied, serverName ?? string.Empty);
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

/// <summary>The outcome of <see cref="TlsHandshakeSelector.Select"/>.</summary>
/// <param name="Entry">The table entry that answered, or null when none did.</param>
/// <param name="Match">Which part of the table it was.</param>
/// <param name="Rejection">Why the handshake is rejected; null when it goes on with <paramref name="Options"/>.</param>
/// <param name="Options">The prebuilt options for the transport; null exactly when <paramref name="Rejection"/> is set.</param>
public readonly record struct TlsSelection(
    CertificateTableEntry? Entry,
    CertificateMatchKind Match,
    TlsRejectionReason? Rejection,
    SslServerAuthenticationOptions? Options);

/// <summary>Lock-free counters of the handshakes a <see cref="TlsHandshakeSelector"/> rejected.</summary>
public sealed class TlsHandshakeCounters
{
    // TODO(M9 metrics): these share a cache line; pad or stripe them if rejection floods ever show contention.
    private long _unknownServerNameTcp;
    private long _unknownServerNameQuic;
    private long _http3Disabled;
    private long _connectionDenied;

    /// <summary>TCP handshakes rejected for an unknown (or absent, with no default) server name.</summary>
    public long UnknownServerNameTcp => Volatile.Read(ref _unknownServerNameTcp);

    /// <summary>QUIC handshakes rejected for an unknown (or absent, with no default) server name.</summary>
    public long UnknownServerNameQuic => Volatile.Read(ref _unknownServerNameQuic);

    /// <summary>QUIC handshakes rejected because the matching host opted out of HTTP/3.</summary>
    public long Http3Disabled => Volatile.Read(ref _http3Disabled);

    /// <summary>QUIC handshakes rejected by the <see cref="QuicConnectionGate"/>.</summary>
    public long ConnectionDenied => Volatile.Read(ref _connectionDenied);

    internal void CountUnknownServerName(bool quic) =>
        Interlocked.Increment(ref quic ? ref _unknownServerNameQuic : ref _unknownServerNameTcp);

    internal void CountHttp3Disabled() => Interlocked.Increment(ref _http3Disabled);

    internal void CountConnectionDenied() => Interlocked.Increment(ref _connectionDenied);
}
