using System.Text.Json.Serialization;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// One layer of host settings (PLAN §5.3, §6.4). Every field is nullable and null (absent in JSON) means
/// <i>inherit</i>: the compiler resolves built-in defaults → global defaults → profile → host → route. Nested settings
/// (<see cref="Tls"/>) merge field by field; list-valued settings (<see cref="IpAllowList"/>) replace as a whole.
/// </summary>
/// <remarks>A new setting is a new nullable field at the end: stored documents simply don't set it.</remarks>
public sealed record HostSettings
{
    /// <summary>Redirect plain-HTTP requests to HTTPS before anything else. Built-in default: off.</summary>
    public bool? ForceHttps { get; init; }

    /// <summary>Merged field by field with the layers below.</summary>
    public TlsSettings? Tls { get; init; }

    /// <summary>Replaces the inherited allow list as a whole. Built-in default: none.</summary>
    public IpAllowListSettings? IpAllowList { get; init; }
}

/// <summary>The route layer: the route-level subset of <see cref="HostSettings"/>, on top of the host's resolved
/// settings. Null fields inherit from the host.</summary>
public sealed record RouteSettings
{
    /// <summary>Replaces the host's allow list for this route as a whole.</summary>
    public IpAllowListSettings? IpAllowList { get; init; }
}

/// <summary>TLS settings (PLAN §7.2 B); each field inherits on its own. A layer that sets none is the same as no layer.</summary>
public sealed record TlsSettings
{
    /// <summary>Lowest TLS version over TCP (QUIC is always 1.3). Built-in default: TLS 1.2.</summary>
    public TlsMinimumVersion? MinimumVersion { get; init; }

    /// <summary>ALPN offered over TCP. Built-in default: HTTP/2 and HTTP/1.1.</summary>
    public TcpAlpn? TcpAlpn { get; init; }

    /// <summary>Serve over HTTP/3 when the listener exists. Built-in default: on.</summary>
    public bool? Http3 { get; init; }
}

/// <summary>
/// Only clients inside <paramref name="Addresses"/> get through, judged on the resolved client IP; others get 403, or
/// are dropped with <paramref name="DropDenied"/>. An empty list means no restriction: that's how a layer lifts an
/// inherited one.
/// </summary>
/// <param name="Addresses">Addresses or CIDRs (<c>192.0.2.1</c>, <c>10.0.0.0/8</c>, <c>2001:db8::/32</c>); a set
/// (canonical order: sorted, no duplicates).</param>
/// <param name="DropDenied">Abort denied requests instead of answering 403. Needs addresses.</param>
public sealed record IpAllowListSettings(IReadOnlyList<string> Addresses, bool DropDenied = false);

/// <summary>Lowest TLS version over TCP.</summary>
[JsonConverter(typeof(ConfigEnumConverter<TlsMinimumVersion>))]
public enum TlsMinimumVersion
{
    Tls12,
    Tls13,
}

/// <summary>ALPN protocols offered over TCP (QUIC always offers <c>h3</c>).</summary>
[JsonConverter(typeof(ConfigEnumConverter<TcpAlpn>))]
public enum TcpAlpn
{
    /// <summary><c>h2</c>, then <c>http/1.1</c>.</summary>
    Http2AndHttp11,

    /// <summary><c>http/1.1</c> only.</summary>
    Http11Only,
}
