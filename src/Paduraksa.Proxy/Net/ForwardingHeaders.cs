namespace Paduraksa.Proxy.Net;

/// <summary>
/// Names of the generic forwarding headers Paduraksa reads (client IP and scheme from a trusted proxy) or rewrites
/// for the upstream. Cloudflare's own headers are in <see cref="CloudflareHeaders"/>.
/// </summary>
internal static class ForwardingHeaders
{
    public const string XForwardedFor = "X-Forwarded-For";
    public const string XForwardedProto = "X-Forwarded-Proto";
    public const string XForwardedHost = "X-Forwarded-Host";
    public const string XForwardedPrefix = "X-Forwarded-Prefix";
    public const string XForwardedPort = "X-Forwarded-Port";
    public const string XRealIp = "X-Real-IP";
    public const string Forwarded = "Forwarded";
}
