namespace Paduraksa.Proxy.Net;

public enum TrustedProxyMode : byte
{
    /// <summary>No trusted proxies: the connection-level address is the client.</summary>
    Off = 0,

    /// <summary>Configured proxy CIDRs; client from a forwarding header (default <c>X-Forwarded-For</c>), right-most
    /// untrusted entry.</summary>
    Generic = 1,

    /// <summary>Cloudflare connects to us directly: the peer must be in Cloudflare's edge ranges; client from
    /// <c>CF-Connecting-IP</c>.</summary>
    CloudflareProxy = 2,

    /// <summary>A local <c>cloudflared</c> connects to us: the peer must be in the configured cloudflared CIDRs;
    /// client from <c>CF-Connecting-IP</c>.</summary>
    CloudflareTunnel = 3,
}

/// <summary>
/// Compiled, immutable trusted-proxy settings (PLAN §7.2 C, §7.6), consumed by <see cref="ClientIpResolver"/>.
/// Built on the cold path (snapshot compile); safe to share across threads.
/// </summary>
public sealed class TrustedProxyPolicy
{
    /// <summary>The default forwarding header for <see cref="TrustedProxyMode.Generic"/>.</summary>
    public const string XForwardedFor = ForwardingHeaders.XForwardedFor;

    private TrustedProxyPolicy(
        TrustedProxyMode mode,
        CidrSet trustedPeers,
        string? forwardingHeader,
        bool useTrueClientIp,
        bool trustForwardedProto = false)
    {
        Mode = mode;
        TrustedPeers = trustedPeers;
        ForwardingHeader = forwardingHeader;
        UseTrueClientIp = useTrueClientIp;
        TrustForwardedProto = trustForwardedProto;
    }

    /// <summary>No trusted proxies.</summary>
    public static TrustedProxyPolicy Off { get; } = new(TrustedProxyMode.Off, CidrSet.Empty, null, false);

    public TrustedProxyMode Mode { get; }

    /// <summary>Peers whose headers are honoured: the generic proxies, Cloudflare's edge, or cloudflared.</summary>
    public CidrSet TrustedPeers { get; }

    /// <summary>The forwarding header, read as a right-to-left address list and reported as
    /// <see cref="ClientIpSource.ForwardingHeader"/>. Generic mode: the primary header, never null (default
    /// <c>X-Forwarded-For</c>, or e.g. <c>X-Real-IP</c>). Cloudflare modes: <c>X-Forwarded-For</c> as an optional last
    /// resort after <c>CF-Connecting-IP</c> (and <c>True-Client-IP</c>, if enabled), or null for no fallback.</summary>
    public string? ForwardingHeader { get; }

    /// <summary>Cloudflare modes: also accept <c>True-Client-IP</c> (Enterprise) when <c>CF-Connecting-IP</c> is absent.</summary>
    public bool UseTrueClientIp { get; }

    /// <summary>Generic mode: take the request scheme from a trusted proxy's <c>X-Forwarded-Proto</c>
    /// (<see cref="ForwardedScheme"/>). Off by default: many proxies pass a client's own header through. Cloudflare modes
    /// always use <c>CF-Visitor</c>, then <c>X-Forwarded-Proto</c>, from a trusted peer.</summary>
    public bool TrustForwardedProto { get; }

    /// <summary>Trusted proxies in front of us, read from <paramref name="headerName"/> (a comma-separated list
    /// such as <c>X-Forwarded-For</c>, or a single-address header such as <c>X-Real-IP</c>).</summary>
    /// <param name="proxies">The trusted proxies.</param>
    /// <param name="headerName">The client-IP header.</param>
    /// <param name="trustForwardedProto">Also take the request scheme from their <c>X-Forwarded-Proto</c>.</param>
    public static TrustedProxyPolicy Generic(CidrSet proxies, string headerName = XForwardedFor, bool trustForwardedProto = false)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        return new TrustedProxyPolicy(TrustedProxyMode.Generic, proxies, headerName, false, trustForwardedProto);
    }

    /// <summary>Behind Cloudflare's proxy (orange cloud).</summary>
    /// <param name="edgeRanges">Cloudflare's edge ranges; null for the baked-in <see cref="CloudflareIpRanges.Default"/>.
    /// Pass a refreshed list here.</param>
    /// <param name="useTrueClientIp">Also accept <c>True-Client-IP</c> when <c>CF-Connecting-IP</c> is absent.</param>
    /// <param name="forwardedForFallback">Fall back to the right-most untrusted <c>X-Forwarded-For</c> entry when
    /// the Cloudflare headers are absent.</param>
    public static TrustedProxyPolicy CloudflareProxy(CidrSet? edgeRanges = null, bool useTrueClientIp = false, bool forwardedForFallback = false) =>
        new(TrustedProxyMode.CloudflareProxy, edgeRanges ?? CloudflareIpRanges.Default, forwardedForFallback ? XForwardedFor : null, useTrueClientIp);

    /// <summary>Behind a Cloudflare Tunnel: <paramref name="cloudflared"/> is where cloudflared connects from
    /// (loopback, a Docker network CIDR or a specific IP). Edge ranges don't apply.</summary>
    /// <param name="cloudflared">The cloudflared source addresses.</param>
    /// <param name="useTrueClientIp">Also accept <c>True-Client-IP</c> when <c>CF-Connecting-IP</c> is absent.</param>
    /// <param name="forwardedForFallback">Fall back to the right-most untrusted <c>X-Forwarded-For</c> entry.</param>
    public static TrustedProxyPolicy CloudflareTunnel(CidrSet cloudflared, bool useTrueClientIp = false, bool forwardedForFallback = false)
    {
        ArgumentNullException.ThrowIfNull(cloudflared);
        return new TrustedProxyPolicy(TrustedProxyMode.CloudflareTunnel, cloudflared, forwardedForFallback ? XForwardedFor : null, useTrueClientIp);
    }

    /// <summary>
    /// Whether <paramref name="peer"/> is one of this policy's trusted proxies (Cloudflare edge, cloudflared, or a
    /// generic proxy). Allocation-free. Use it to exempt such peers from per-IP connection caps (PLAN §7.6), where
    /// every visitor would otherwise share a handful of edge addresses.
    /// </summary>
    public bool IsTrustedPeer(IpKey peer) => Mode != TrustedProxyMode.Off && TrustedPeers.Contains(peer);
}
