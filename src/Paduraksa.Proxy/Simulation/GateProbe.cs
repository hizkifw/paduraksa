using System.Net;
using System.Security.Authentication;

namespace Paduraksa.Proxy.Simulation;

/// <summary>How the request reaches Paduraksa.</summary>
public enum ProbeArrival : byte
{
    /// <summary>The client connects itself: its address is the peer's.</summary>
    Direct = 0,

    /// <summary>Through a generic reverse proxy, which names the client in the policy's forwarding header.</summary>
    TrustedProxy = 1,

    /// <summary>Through Cloudflare's edge, which names the client in <c>CF-Connecting-IP</c>.</summary>
    CloudflareProxy = 2,

    /// <summary>Through a local <c>cloudflared</c>, which names the client in <c>CF-Connecting-IP</c>.</summary>
    CloudflareTunnel = 3,
}

/// <summary>The HTTP version the connection speaks (what ALPN settled on).</summary>
public enum ProbeHttpVersion : byte
{
    Http11 = 0,
    Http2 = 1,

    /// <summary>HTTP/3 over QUIC: always HTTPS and TLS 1.3.</summary>
    Http3 = 2,
}

/// <summary>One request header, as sent.</summary>
public readonly record struct ProbeHeader(string Name, string Value);

/// <summary>
/// A request to put through the gate without a network, as plain facts: everything the gate reads. Fields
/// that say nothing get the defaults a real request would have; <see cref="GateSimulator"/> builds the
/// <c>HttpContext</c> from it.
/// </summary>
public sealed record GateProbe
{
    /// <summary>The request arrives over TLS (<c>https</c>).</summary>
    public bool Https { get; init; }

    /// <summary>The URL's authority: the host name (with the port, if it isn't the scheme's default). On HTTP/1.1 it is the
    /// <c>Host</c> header (unless <see cref="HostHeaders"/> says otherwise); on HTTP/2 and HTTP/3 it is <c>:authority</c>,
    /// which Kestrel turns into the host, so a <c>Host</c> header is ignored there.</summary>
    public required string Host { get; init; }

    /// <summary>
    /// HTTP/1.1 only: the <c>Host</c> headers actually sent. Null: one, <see cref="Host"/>. Anything but exactly one valid
    /// value is what Kestrel answers 400 before the gate runs (no header, several, or one that isn't a host), and the
    /// simulation says so. Ignored on HTTP/2 and HTTP/3.
    /// </summary>
    public IReadOnlyList<string>? HostHeaders { get; init; }

    /// <summary>The request path as the client sent it (percent-encoded), starting with <c>/</c>. The gate sees what Kestrel
    /// makes of it: escapes decoded (<c>%2F</c> and <c>%5C</c> kept, and any that isn't UTF-8), dot segments removed.</summary>
    public string Path { get; init; } = "/";

    /// <summary>The query string without its <c>?</c>; null or empty for none.</summary>
    public string? Query { get; init; }

    public string Method { get; init; } = "GET";

    /// <summary>Request headers other than <c>Host</c> (see <see cref="HostHeaders"/>).</summary>
    public IReadOnlyList<ProbeHeader> Headers { get; init; } = [];

    /// <summary>
    /// The client: with <see cref="ProbeArrival.Direct"/> the connection's address; behind a proxy, the address the proxy
    /// reports (in the header it would send, unless <see cref="Headers"/> already has it).
    /// </summary>
    public required IPAddress ClientAddress { get; init; }

    public ProbeArrival Arrival { get; init; }

    /// <summary>
    /// The connection-level peer. Direct: the client (default <see cref="ClientAddress"/>). Behind a proxy: the proxy
    /// (default: a peer the snapshot trusts for <see cref="Arrival"/>, else loopback).
    /// </summary>
    public IPAddress? PeerAddress { get; init; }

    /// <summary>A PROXY protocol header arrived first: <see cref="PeerAddress"/> is the address it carried and
    /// <see cref="TransportAddress"/> the socket's.</summary>
    public bool ProxyProtocol { get; init; }

    /// <summary>With <see cref="ProxyProtocol"/>: the socket's remote address (the load balancer). Default loopback.</summary>
    public IPAddress? TransportAddress { get; init; }

    public ProbeHttpVersion HttpVersion { get; init; } = ProbeHttpVersion.Http11;

    /// <summary>The name in the ClientHello (HTTPS only). Null: the <c>Host</c> name when it is a DNS name, else none.</summary>
    public string? ServerName { get; init; }

    /// <summary>The highest TLS version the client offers (HTTPS over TCP): <see cref="SslProtocols.Tls12"/> or
    /// <see cref="SslProtocols.Tls13"/>. QUIC is always 1.3.</summary>
    public SslProtocols TlsVersion { get; init; } = SslProtocols.Tls13;
}
