using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The gate's seam for the client address (PLAN §7.2 C, §7.6): called once per request with the request's snapshot.
/// Must be allocation-free: it runs on every request.
/// </summary>
public interface IClientAddressSource
{
    /// <summary>The request's client address, where it came from, and whether Cloudflare headers may be trusted.</summary>
    ClientIpResolution Resolve(HttpContext context, RuntimeSnapshot snapshot);
}

/// <summary>
/// The default source: the connection's <see cref="ClientInfo"/> (PROXY protocol or socket peer, set by stage A on TCP
/// and by the QUIC handshake gate), resolved through the snapshot's <see cref="RuntimeSnapshot.TrustedProxies"/> with
/// <see cref="ClientIpResolver"/>. A connection without the feature (a listener without <c>UseClientInfo</c>, or a
/// non-Kestrel server) falls back to <c>HttpContext.Connection.RemoteIpAddress</c>. 0 B either way.
/// </summary>
/// <remarks>
/// The policy is global for M1. A per-host override (PLAN §5.3) needs the host first, so it would resolve after the
/// host lookup; that arrives with the per-host settings (M2/M6).
/// </remarks>
public sealed class TrustedProxyClientAddressSource : IClientAddressSource
{
    private long _headerMissing;
    private long _headerInvalid;

    /// <summary>Requests from a trusted proxy that carried no usable client-IP header (the peer was used). PLAN §7.6
    /// counts these; process lifetime, read by metrics (M7).</summary>
    public long HeaderMissing => Interlocked.Read(ref _headerMissing);

    /// <summary>Requests from a trusted proxy whose client-IP header didn't parse (the peer was used).</summary>
    public long HeaderInvalid => Interlocked.Read(ref _headerInvalid);

    public ClientIpResolution Resolve(HttpContext context, RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);

        IpKey peer;
        ClientIpSource peerSource;
        if (context.Features.Get<IClientInfoFeature>()?.ClientInfo is { } connection)
        {
            peer = connection.Address;
            peerSource = connection.IsFromProxyProtocol ? ClientIpSource.ProxyProtocol : ClientIpSource.Socket;
        }
        else
        {
            peer = context.Connection.RemoteIpAddress is { } remote ? IpKey.FromIPAddress(remote) : IpKey.Unspecified;
            peerSource = ClientIpSource.Socket;
        }

        ClientIpResolution resolution = ClientIpResolver.Resolve(peer, peerSource, context.Request.Headers, snapshot.TrustedProxies);
        if (resolution.IsFallback)
        {
            Interlocked.Increment(ref resolution.Outcome == ClientIpOutcome.HeaderMissing ? ref _headerMissing : ref _headerInvalid);
        }

        return resolution;
    }
}
