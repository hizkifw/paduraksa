using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;

namespace Paduraksa.Contracts.Simulation;

/// <summary>How the simulated request reaches Paduraksa (PLAN §7.6, §7.7).</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationArrival>))]
public enum SimulationArrival
{
    /// <summary>The client connects itself: its address is the connection's peer.</summary>
    Direct,

    /// <summary>Through a generic reverse proxy, which names the client in its forwarding header
    /// (<c>X-Forwarded-For</c> unless the trusted-proxy settings name another).</summary>
    TrustedProxy,

    /// <summary>Through Cloudflare's edge, which names the client in <c>CF-Connecting-IP</c>.</summary>
    CloudflareProxy,

    /// <summary>Through a local <c>cloudflared</c>, which names the client in <c>CF-Connecting-IP</c>.</summary>
    CloudflareTunnel,
}

/// <summary>The HTTP version of the simulated connection (what ALPN settled on).</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationHttpVersion>))]
public enum SimulationHttpVersion
{
    Http11,
    Http2,

    /// <summary>HTTP/3 over QUIC: HTTPS only, always TLS 1.3.</summary>
    Http3,
}

/// <summary>The TLS version the simulated client negotiates over TCP (QUIC is always 1.3).</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationTlsVersion>))]
public enum SimulationTlsVersion
{
    Tls12,
    Tls13,
}

/// <summary>A request header, as sent.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Value">The value.</param>
public sealed record SimulationHeader(string Name, string Value);

/// <summary>The TLS context of an HTTPS request. Every part defaults to what a browser would do for the URL.</summary>
/// <param name="ServerName">The name in the ClientHello. Default: the URL's host when it is a DNS name; none for an IP
/// address. Set it to test a connection negotiated for one name that then asks for another (the 421 case).</param>
/// <param name="HttpVersion">Default: HTTP/2.</param>
/// <param name="TlsVersion">Default: TLS 1.3.</param>
public sealed record SimulationTls(
    string? ServerName = null,
    SimulationHttpVersion? HttpVersion = null,
    SimulationTlsVersion? TlsVersion = null);

/// <summary>
/// A request to ask "what would this get, and why" about (PLAN §7.7). A URL and a client IP are enough; everything else
/// defaults to a plain GET from a client that connects directly.
/// </summary>
/// <param name="Url">The absolute <c>http</c> or <c>https</c> URL: scheme, host, port, path and query. The <c>Host</c>
/// header is its authority, unless <paramref name="Headers"/> sets one.</param>
/// <param name="ClientIp">The client's address (IPv4 or IPv6). Behind a proxy (<paramref name="Arrival"/>), the address the
/// proxy reports.</param>
/// <param name="Method">Default <c>GET</c>. Case matters, as on the wire: <c>get</c> is not <c>GET</c>.</param>
/// <param name="Headers">Request headers (user agent, cookies, <c>X-Forwarded-Proto</c>, <c>CF-Visitor</c>, ...). A
/// header the arrival would add (the client-IP header) is added only when absent here. <c>Host</c> headers given here are the
/// ones sent over HTTP/1.1 (two of them, or one that isn't a host, is a 400 from the server); over HTTP/2 and HTTP/3 the
/// URL's authority is the host and a <c>Host</c> header is ignored.</param>
/// <param name="Arrival">How the request reaches Paduraksa. Default: directly.</param>
/// <param name="PeerIp">The connection-level peer, for a request that comes through a proxy (<paramref name="Arrival"/> other
/// than direct) or with <paramref name="ProxyProtocol"/>. Default: an address the config trusts as that kind of proxy
/// (loopback when it trusts none). A direct request's peer is the client, so setting it there is refused.</param>
/// <param name="ProxyProtocol">A PROXY protocol header arrived first: <paramref name="PeerIp"/> is the address it carried.
/// The listener must expect one: otherwise the connection is rejected, as the real one would be.</param>
/// <param name="TransportIp">With <paramref name="ProxyProtocol"/> only: the socket's remote address (the load balancer; it must
/// be one of the listener's trusted sources). Default: loopback.</param>
/// <param name="Tls">The TLS context; <c>https</c> URLs only (a plain-HTTP listener speaks HTTP/1.1 and has no handshake).
/// Default: SNI from the URL, HTTP/2, TLS 1.3.</param>
public sealed record SimulationRequest(
    string Url,
    string ClientIp,
    string? Method = null,
    IReadOnlyList<SimulationHeader>? Headers = null,
    SimulationArrival Arrival = SimulationArrival.Direct,
    string? PeerIp = null,
    bool ProxyProtocol = false,
    string? TransportIp = null,
    SimulationTls? Tls = null)
{
    public IReadOnlyList<SimulationHeader> Headers { get; init; } = Headers ?? [];
}
