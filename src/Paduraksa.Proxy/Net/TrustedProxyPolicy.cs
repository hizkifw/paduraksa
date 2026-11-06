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
    public const string XForwardedFor = "X-Forwarded-For";

    private TrustedProxyPolicy(
        TrustedProxyMode mode,
        CidrSet trustedPeers,
        string? forwardedHeader,
        bool useTrueClientIp)
    {
        Mode = mode;
        TrustedPeers = trustedPeers;
        ForwardedHeader = forwardedHeader;
        UseTrueClientIp = useTrueClientIp;
    }

    /// <summary>No trusted proxies.</summary>
    public static TrustedProxyPolicy Off { get; } = new(TrustedProxyMode.Off, CidrSet.Empty, null, false);

    public TrustedProxyMode Mode { get; }

    /// <summary>Peers whose headers are honoured: the generic proxies, Cloudflare's edge, or cloudflared.</summary>
    public CidrSet TrustedPeers { get; }

    /// <summary>Generic mode: the list header to read. Cloudflare modes: an optional <c>X-Forwarded-For</c>-style
    /// fallback when <c>CF-Connecting-IP</c> (and <c>True-Client-IP</c>, if enabled) are absent; null = no fallback.</summary>
    public string? ForwardedHeader { get; }

    /// <summary>Cloudflare modes: also accept <c>True-Client-IP</c> (Enterprise) when <c>CF-Connecting-IP</c> is absent.</summary>
    public bool UseTrueClientIp { get; }

    /// <summary>Trusted proxies in front of us, read from <paramref name="headerName"/> (a comma-separated list
    /// such as <c>X-Forwarded-For</c>, or a single-address header such as <c>X-Real-IP</c>).</summary>
    public static TrustedProxyPolicy Generic(CidrSet proxies, string headerName = XForwardedFor)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        return new TrustedProxyPolicy(TrustedProxyMode.Generic, proxies, headerName, false);
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
