using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>How the client IP reaches Paduraksa: no trusted proxies, configured proxies, Cloudflare's edge or a local
/// <c>cloudflared</c>.</summary>
[JsonConverter(typeof(ConfigEnumConverter<TrustedProxyMode>))]
public enum TrustedProxyMode
{
    /// <summary>No trusted proxies: the connection-level address is the client.</summary>
    Off = 0,

    /// <summary>
    /// Configured proxy CIDRs; client from a forwarding header (default <c>X-Forwarded-For</c>), right-most untrusted
    /// entry.
    /// </summary>
    Generic = 1,

    /// <summary>Cloudflare connects to us directly: the peer must be in Cloudflare's edge ranges; client from
    /// <c>CF-Connecting-IP</c>.</summary>
    CloudflareProxy = 2,

    /// <summary>A local <c>cloudflared</c> connects to us: the peer must be in the configured cloudflared CIDRs;
    /// client from <c>CF-Connecting-IP</c>.</summary>
    CloudflareTunnel = 3,
}
