using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>
/// Lowest TLS version a host accepts over TCP (QUIC is always TLS 1.3). One enum, shared by the config document and the
/// proxy's TLS policy.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<TlsMinimumVersion>))]
public enum TlsMinimumVersion
{
    /// <summary>TLS 1.2 or newer.</summary>
    Tls12 = 0,

    /// <summary>TLS 1.3 only.</summary>
    Tls13 = 1,
}

/// <summary>ALPN protocols a host offers over TCP. QUIC always offers <c>h3</c>.</summary>
[JsonConverter(typeof(ConfigEnumConverter<TcpAlpn>))]
public enum TcpAlpn
{
    /// <summary><c>h2</c>, then <c>http/1.1</c>.</summary>
    Http2AndHttp11 = 0,

    /// <summary><c>http/1.1</c> only (for clients or upstream setups that misbehave on HTTP/2).</summary>
    Http11Only = 1,
}

/// <summary>What a host's responses put in <c>Server</c> (PLAN §14 M6).</summary>
[JsonConverter(typeof(ConfigEnumConverter<ServerHeaderMode>))]
public enum ServerHeaderMode
{
    /// <summary><c>Server: Paduraksa</c>, with no version. Replaces an upstream's own header.</summary>
    Paduraksa,

    /// <summary><c>Server: &lt;Value&gt;</c>. Replaces an upstream's own header.</summary>
    Custom,

    /// <summary>The upstream's own header passes through untouched. Gate answers carry none.</summary>
    Upstream,

    /// <summary>No <c>Server</c> header at all: an upstream's own is removed too. HTTP/2 trailers aren't touched.</summary>
    Remove,
}

/// <summary>
/// Where a request's ID (<c>X-Request-Id</c>, PLAN §7.4) goes: to the upstream, back to the client, both or neither.
/// The ID itself always exists - the access log (and later the error pages) records it whatever this says.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<RequestIdPropagation>))]
public enum RequestIdPropagation : byte
{
    /// <summary>The upstream and the client's response (the built-in default).</summary>
    UpstreamAndResponse = 0,

    /// <summary>The upstream only: the client never learns the ID, and the app's log is the one to correlate with.</summary>
    UpstreamOnly = 1,

    /// <summary>The client's response only: the upstream sees no header and can't confuse it with its own tracing.</summary>
    ResponseOnly = 2,

    /// <summary>Neither: only the proxy's own log mentions it.</summary>
    Off = 3,
}

/// <summary>What <see cref="RequestIdPropagation"/> sends where.</summary>
public static class RequestIdPropagationExtensions
{
    /// <summary>Whether the ID goes to the upstream.</summary>
    public static bool SendsUpstream(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.UpstreamOnly;

    /// <summary>Whether the ID goes back to the client.</summary>
    public static bool SendsResponse(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.ResponseOnly;
}
