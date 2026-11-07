using BenchmarkDotNet.Attributes;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Benchmarks;

/// <summary>
/// <see cref="ClientIpResolver.Resolve(ClientInfo, IHeaderDictionary, TrustedProxyPolicy)"/> per trusted-proxy mode.
/// Headers live in a <see cref="HeaderDictionary"/> (a case-insensitive dictionary; Kestrel's request headers are
/// faster for known names and comparable for unknown ones such as <c>CF-Connecting-IP</c>).
/// </summary>
[MemoryDiagnoser]
public class ClientIpResolverBenchmarks
{
    private static readonly IpKey CloudflareEdge = IpKey.Parse("173.245.48.10");

    private readonly HeaderDictionary _none = [];
    private readonly HeaderDictionary _forwardedFor = new() { ["X-Forwarded-For"] = "203.0.113.7, 10.0.0.2, 10.0.0.1" };
    private readonly HeaderDictionary _cfV4 = new() { [CloudflareHeaders.ConnectingIp] = "203.0.113.7" };
    private readonly HeaderDictionary _cfV6 = new() { [CloudflareHeaders.ConnectingIp] = "2001:db8:85a3::8a2e:370:7334" };

    private readonly ClientInfo _direct = Connection("198.51.100.23");
    private readonly ClientInfo _internalProxy = Connection("10.0.0.1");
    private readonly ClientInfo _edge = new(CloudflareEdge, 44321, ConnectionAddressSource.Socket, CloudflareEdge, 44321);
    private readonly ClientInfo _tunnel = Connection("127.0.0.1");

    private readonly TrustedProxyPolicy _generic = TrustedProxyPolicy.Generic(CidrSet.Parse(["10.0.0.0/8"]));
    private readonly TrustedProxyPolicy _cloudflare = TrustedProxyPolicy.CloudflareProxy();
    private readonly TrustedProxyPolicy _tunnelPolicy = TrustedProxyPolicy.CloudflareTunnel(CidrSet.Parse(["127.0.0.1", "::1"]));

    /// <summary>Policy off: the socket peer, headers ignored.</summary>
    [Benchmark(Baseline = true)]
    public ClientIpResolution Off() => ClientIpResolver.Resolve(_direct, _cfV4, TrustedProxyPolicy.Off);

    /// <summary>Generic mode, peer not in the trusted set: a <see cref="CidrSet"/> miss, headers ignored.</summary>
    [Benchmark]
    public ClientIpResolution GenericUntrustedPeer() => ClientIpResolver.Resolve(_direct, _forwardedFor, _generic);

    /// <summary>Generic mode: <c>X-Forwarded-For</c> with three entries, walked right to left past two trusted hops.</summary>
    [Benchmark]
    public ClientIpResolution GenericForwardedFor() => ClientIpResolver.Resolve(_internalProxy, _forwardedFor, _generic);

    /// <summary>Cloudflare proxy mode: an edge peer (baked-in ranges) with an IPv4 <c>CF-Connecting-IP</c>.</summary>
    [Benchmark]
    public ClientIpResolution CloudflareProxyV4() => ClientIpResolver.Resolve(_edge, _cfV4, _cloudflare);

    /// <summary>Cloudflare proxy mode with an IPv6 <c>CF-Connecting-IP</c>.</summary>
    [Benchmark]
    public ClientIpResolution CloudflareProxyV6() => ClientIpResolver.Resolve(_edge, _cfV6, _cloudflare);

    /// <summary>Cloudflare proxy mode, edge peer, header missing: falls back to the peer.</summary>
    [Benchmark]
    public ClientIpResolution CloudflareProxyMissingHeader() => ClientIpResolver.Resolve(_edge, _none, _cloudflare);

    /// <summary>Cloudflare Tunnel mode: cloudflared on loopback with an IPv4 <c>CF-Connecting-IP</c>.</summary>
    [Benchmark]
    public ClientIpResolution CloudflareTunnelV4() => ClientIpResolver.Resolve(_tunnel, _cfV4, _tunnelPolicy);

    private static ClientInfo Connection(string address)
    {
        IpKey key = IpKey.Parse(address);
        return new ClientInfo(key, 50000, ConnectionAddressSource.Socket, key, 50000);
    }
}
