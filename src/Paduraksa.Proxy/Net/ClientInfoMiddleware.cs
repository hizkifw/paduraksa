using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Stage-A TCP connection middleware (PLAN §7.2 A): reads the PROXY protocol header when the listener enables it,
/// then publishes one immutable <see cref="ClientInfo"/> as an <see cref="IClientInfoFeature"/> connection feature.
/// </summary>
/// <remarks>
/// <para>Install it before TLS (<see cref="ClientInfoListenOptionsExtensions.UseClientInfo"/> before
/// <c>UseHttps</c>), so the header is consumed from the raw stream and the real client IP is known to everything
/// after it: IP deny, per-IP connection caps, SNI logic. Later connection middleware reads the feature.</para>
/// <para>With PROXY protocol on, the header is mandatory: an untrusted socket peer is aborted at once without
/// reading, and a trusted one is aborted on an invalid header, a close, or the timeout. Rejections are counted in
/// <see cref="ProxyProtocolOptions.Counters"/>, never logged per connection.</para>
/// <para>QUIC has no connection middleware: use <see cref="ClientInfo.FromEndPoint"/> in the QUIC TLS callback.</para>
/// </remarks>
public sealed class ClientInfoMiddleware
{
    private static readonly ConnectionAbortedException UntrustedSource = new("PROXY protocol: peer is not a trusted source.");
    private static readonly ConnectionAbortedException InvalidHeader = new("PROXY protocol: invalid header.");
    private static readonly ConnectionAbortedException HeaderTimedOut = new("PROXY protocol: header timed out.");
    private static readonly ConnectionAbortedException ClosedBeforeHeader = new("PROXY protocol: closed before the header.");

    private readonly ConnectionDelegate _next;
    private readonly ProxyProtocolOptions? _proxyProtocol;

    /// <param name="next">The rest of the connection pipeline.</param>
    /// <param name="proxyProtocol">PROXY protocol settings, or null to take the client from the socket.</param>
    public ClientInfoMiddleware(ConnectionDelegate next, ProxyProtocolOptions? proxyProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
        _proxyProtocol = proxyProtocol;
    }

    public Task OnConnectionAsync(ConnectionContext connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _ = IpKey.TryFromEndPoint(connection.RemoteEndPoint, out var peer, out var peerPort);

        var options = _proxyProtocol;
        if (options is null)
        {
            connection.Features.Set<IClientInfoFeature>(
                new ClientInfo(peer, peerPort, ConnectionAddressSource.Socket, peer, peerPort));
            return _next(connection);
        }

        if (!options.TrustedSources.Contains(peer))
        {
            options.Counters.CountUntrustedSource();
            connection.Abort(UntrustedSource);
            return Task.CompletedTask;
        }

        return ReadHeaderThenContinueAsync(connection, options, peer, peerPort);
    }

    private async Task ReadHeaderThenContinueAsync(ConnectionContext connection, ProxyProtocolOptions options, IpKey peer, ushort peerPort)
    {
        var result = await ProxyProtocolReader.ReadAsync(connection.Transport.Input, options.HeaderTimeout, options.MaxV2Length)
            .ConfigureAwait(false);
        var header = result.Header;
        options.Counters.Count(result.Status, header.HasAddresses);

        if (result.Status != ProxyProtocolReadStatus.Success)
        {
            connection.Abort(result.Status switch
            {
                ProxyProtocolReadStatus.Invalid => InvalidHeader,
                ProxyProtocolReadStatus.TimedOut => HeaderTimedOut,
                _ => ClosedBeforeHeader,
            });
            return;
        }

        var info = header.HasAddresses
            ? new ClientInfo(header.SourceAddress, header.SourcePort, ConnectionAddressSource.ProxyProtocol, peer, peerPort)
            : new ClientInfo(peer, peerPort, ConnectionAddressSource.ProxyProtocolUnspecified, peer, peerPort);
        connection.Features.Set<IClientInfoFeature>(info);
        await _next(connection).ConfigureAwait(false);
    }
}

public static class ClientInfoListenOptionsExtensions
{
    /// <summary>
    /// Adds <see cref="ClientInfoMiddleware"/> to a TCP listener. Call it before <c>UseHttps</c> and before any
    /// connection middleware that needs the client IP.
    /// </summary>
    /// <param name="listenOptions">The TCP listener.</param>
    /// <param name="proxyProtocol">Non-null to require a PROXY protocol header (v1 or v2) on every connection.</param>
    public static ListenOptions UseClientInfo(this ListenOptions listenOptions, ProxyProtocolOptions? proxyProtocol = null)
    {
        ArgumentNullException.ThrowIfNull(listenOptions);
        listenOptions.Use(next => new ClientInfoMiddleware(next, proxyProtocol).OnConnectionAsync);
        return listenOptions;
    }
}
