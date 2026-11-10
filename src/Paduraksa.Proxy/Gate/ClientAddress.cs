using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// The gate's seam for the client address: called once per request with the request's snapshot.
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
/// The policy is global: a per-host override needs the host first, so it would resolve after the host lookup. The
/// request scheme is resolved in the same step (<see cref="ForwardedScheme"/>).
/// </remarks>
public sealed partial class TrustedProxyClientAddressSource : IClientAddressSource
{
    private readonly ILogger? _logger;

    public TrustedProxyClientAddressSource()
    {
    }

    /// <param name="logger">Gets one warning the first time Cloudflare "Flexible" SSL is seen.</param>
    public TrustedProxyClientAddressSource(ILogger<TrustedProxyClientAddressSource> logger) => _logger = logger;

    /// <summary>Header fallbacks and Cloudflare plain-HTTP requests (process lifetime; read by metrics).</summary>
    public ClientAddressCounters Counters { get; } = new();

    public ClientIpResolution Resolve(HttpContext context, RuntimeSnapshot snapshot) =>
        ResolveCore(context, snapshot, Counters, _logger);

    /// <summary>
    /// The resolution itself. <paramref name="counters"/> null: count nothing and log nothing, which is how the request
    /// simulator resolves: the same code, no side effects.
    /// </summary>
    internal static ClientIpResolution ResolveCore(HttpContext context, RuntimeSnapshot snapshot, ClientAddressCounters? counters, ILogger? logger)
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

        var headers = context.Request.Headers;
        var resolution = ClientIpResolver.Resolve(peer, peerSource, headers, snapshot.TrustedProxies);
        if (resolution.IsFallback)
        {
            counters?.CountFallback(resolution.Outcome);
        }

        var scheme = ForwardedScheme.Resolve(in resolution, headers, snapshot.TrustedProxies);
        if (scheme is { Source: SchemeSource.CfVisitor, IsHttps: false } && !context.Request.IsHttps
            && counters?.CountCloudflarePlainHttp() == 1 && logger is not null)
        {
            LogCloudflarePlainHttp(logger);
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

    /// <summary>Requests from a trusted proxy that carried no usable client-IP header (the peer was used).</summary>
    public long HeaderMissing => Volatile.Read(ref _headerMissing);

    /// <summary>Requests from a trusted proxy whose client-IP header didn't parse (the peer was used).</summary>
    public long HeaderInvalid => Volatile.Read(ref _headerInvalid);

    /// <summary>
    /// Requests a trusted Cloudflare peer sent over plain HTTP saying the visitor used plain HTTP too
    /// (<c>CF-Visitor: {"scheme":"http"}</c>): Cloudflare "Flexible" SSL or plain-HTTP visitors. Flexible is insecure;
    /// the first one is logged as a warning.
    /// </summary>
    public long CloudflarePlainHttp => Volatile.Read(ref _cloudflarePlainHttp);

    /// <summary>Counts a header fallback (<see cref="ClientIpResolution.IsFallback"/>) by its outcome.</summary>
    internal void CountFallback(ClientIpOutcome outcome) =>
        Interlocked.Increment(ref outcome == ClientIpOutcome.HeaderMissing ? ref _headerMissing : ref _headerInvalid);

    /// <summary>Returns the new count, so the caller can log the first one.</summary>
    internal long CountCloudflarePlainHttp() => Interlocked.Increment(ref _cloudflarePlainHttp);
}
