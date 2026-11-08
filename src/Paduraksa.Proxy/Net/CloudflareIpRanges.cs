using System.Collections.Immutable;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// Cloudflare's published edge ranges, baked in as the default trust list for <see cref="TrustedProxyMode.CloudflareProxy"/>
/// (PLAN §7.6). A background refresh (M7) replaces them at runtime by building a new <see cref="CidrSet"/> and
/// passing it to <see cref="TrustedProxyPolicy.CloudflareProxy"/>.
/// </summary>
public static class CloudflareIpRanges
{
    /// <summary>When the lists below were fetched (UTC), for the staleness warning.</summary>
    public static readonly DateOnly FetchedOn = new(2026, 11, 6);

    /// <summary>https://www.cloudflare.com/ips-v4, fetched 2026-11-06.</summary>
    public static ImmutableArray<string> IPv4 { get; } =
    [
        "173.245.48.0/20",
        "103.21.244.0/22",
        "103.22.200.0/22",
        "103.31.4.0/22",
        "141.101.64.0/18",
        "108.162.192.0/18",
        "190.93.240.0/20",
        "188.114.96.0/20",
        "197.234.240.0/22",
        "198.41.128.0/17",
        "162.158.0.0/15",
        "104.16.0.0/13",
        "104.24.0.0/14",
        "172.64.0.0/13",
        "131.0.72.0/22",
    ];

    /// <summary>https://www.cloudflare.com/ips-v6, fetched 2026-11-06.</summary>
    public static ImmutableArray<string> IPv6 { get; } =
    [
        "2400:cb00::/32",
        "2606:4700::/32",
        "2803:f800::/32",
        "2405:b500::/32",
        "2405:8100::/32",
        "2a06:98c0::/29",
        "2c0f:f248::/32",
    ];

    /// <summary>The baked-in ranges (both families) as one set.</summary>
    public static CidrSet Default { get; } = Build();

    private static CidrSet Build()
    {
        var builder = new CidrSet.Builder();
        foreach (var block in IPv4)
        {
            builder.Add(block);
        }

        foreach (var block in IPv6)
        {
            builder.Add(block);
        }

        return builder.Build();
    }
}

/// <summary>Cloudflare request header names (PLAN §7.6).</summary>
public static class CloudflareHeaders
{
    public const string ConnectingIp = "CF-Connecting-IP";
    public const string TrueClientIp = "True-Client-IP";
    public const string IpCountry = "CF-IPCountry";
    public const string Ray = "CF-Ray";
    public const string Visitor = "CF-Visitor";

    /// <summary>The prefix of every Cloudflare request header (<c>CF-Connecting-IP</c>, <c>Cf-Access-Jwt-Assertion</c>,
    /// <c>cf-ipcity</c>, ...), compared ignoring case.</summary>
    public const string Prefix = "cf-";

    /// <summary>The well-known headers that are only meaningful from a trusted Cloudflare peer: the client-IP ones and
    /// those Paduraksa reads. The strip rule is wider, see <see cref="IsStrippedWhenUntrusted"/>.</summary>
    public static ImmutableArray<string> StripWhenUntrusted { get; } = [ConnectingIp, TrueClientIp, IpCountry, Ray, Visitor];

    /// <summary>
    /// Whether a header is only trustworthy from a trusted Cloudflare peer: <b>every</b> <c>cf-*</c> header (Cloudflare
    /// adds more over time, and some carry identity, e.g. Cloudflare Access's <c>Cf-Access-Authenticated-User-Email</c>
    /// and <c>Cf-Access-Jwt-Assertion</c>), plus <c>True-Client-IP</c>. Unless
    /// <see cref="ClientIpResolution.CloudflareHeadersTrusted"/> is true (a Cloudflare mode <em>and</em> a trusted peer;
    /// <see cref="ClientIpResolution.PeerTrusted"/> is not enough, since a trusted generic proxy passes client headers
    /// through), such headers are ignored and <b>removed before forwarding upstream</b>, so a client can't spoof them.
    /// </summary>
    public static bool IsStrippedWhenUntrusted(ReadOnlySpan<char> name) =>
        name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || name.Equals(TrueClientIp, StringComparison.OrdinalIgnoreCase);
}
