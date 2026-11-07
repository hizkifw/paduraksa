using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The gate's seam for the client address (PLAN §7.2 C, §7.6): called once per request with the request's snapshot.
/// Must be allocation-free: it runs on every request.
/// </summary>
public interface IClientAddressSource
{
    /// <summary>The request's client address, where it came from, whether Cloudflare headers may be trusted, and the
    /// trusted request scheme (<see cref="ClientIpResolution.Scheme"/>; default = the connection's).</summary>
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
/// host lookup; that arrives with the per-host settings (M2/M6). The request scheme is resolved in the same step
/// (<see cref="ForwardedScheme"/>).
/// </remarks>
public sealed partial class TrustedProxyClientAddressSource : IClientAddressSource
{
    private readonly ILogger? _logger;

    public TrustedProxyClientAddressSource()
    {
    }

    /// <param name="logger">Gets one warning the first time Cloudflare "Flexible" SSL is seen.</param>
    public TrustedProxyClientAddressSource(ILogger<TrustedProxyClientAddressSource> logger) => _logger = logger;

    /// <summary>Header fallbacks and Cloudflare plain-HTTP requests (process lifetime; read by metrics, M7).</summary>
    public ClientAddressCounters Counters { get; } = new();

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

        IHeaderDictionary headers = context.Request.Headers;
        ClientIpResolution resolution = ClientIpResolver.Resolve(peer, peerSource, headers, snapshot.TrustedProxies);
        if (resolution.IsFallback)
        {
            Counters.CountFallback(resolution.Outcome);
        }

        RequestScheme scheme = ForwardedScheme.Resolve(in resolution, headers, snapshot.TrustedProxies);
        if (scheme is { Source: SchemeSource.CfVisitor, IsHttps: false } && !context.Request.IsHttps
            && Counters.CountCloudflarePlainHttp() == 1 && _logger is not null)
        {
            LogCloudflarePlainHttp(_logger);
        }

        return resolution with { Scheme = scheme };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloudflare reached this server over plain HTTP for a plain-HTTP visitor (CF-Visitor scheme http). If Cloudflare's SSL mode is \"Flexible\", visitors' traffic crosses the internet unencrypted between Cloudflare and this server: use \"Full (strict)\" with a certificate here. Logged once; counted in Counters.CloudflarePlainHttp.")]
    private static partial void LogCloudflarePlainHttp(ILogger logger);
}

/// <summary>Lock-free counters of a <see cref="TrustedProxyClientAddressSource"/>.</summary>
public sealed class ClientAddressCounters
{
    private long _headerMissing;
    private long _headerInvalid;
    private long _cloudflarePlainHttp;

    /// <summary>Requests from a trusted proxy that carried no usable client-IP header (the peer was used). PLAN §7.6
    /// counts these.</summary>
    public long HeaderMissing => Volatile.Read(ref _headerMissing);

    /// <summary>Requests from a trusted proxy whose client-IP header didn't parse (the peer was used).</summary>
    public long HeaderInvalid => Volatile.Read(ref _headerInvalid);

    /// <summary>
    /// Requests a trusted Cloudflare peer sent over plain HTTP saying the visitor used plain HTTP too
    /// (<c>CF-Visitor: {"scheme":"http"}</c>): Cloudflare "Flexible" SSL or plain-HTTP visitors. Flexible is insecure
    /// (PLAN §7.6); the first one is logged as a warning.
    /// </summary>
    public long CloudflarePlainHttp => Volatile.Read(ref _cloudflarePlainHttp);

    /// <summary>Counts a header fallback (<see cref="ClientIpResolution.IsFallback"/>) by its outcome.</summary>
    internal void CountFallback(ClientIpOutcome outcome) =>
        Interlocked.Increment(ref outcome == ClientIpOutcome.HeaderMissing ? ref _headerMissing : ref _headerInvalid);

    /// <summary>Returns the new count, so the caller can log the first one.</summary>
    internal long CountCloudflarePlainHttp() => Interlocked.Increment(ref _cloudflarePlainHttp);
}
