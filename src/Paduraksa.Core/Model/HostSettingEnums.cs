using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>Lowest TLS version a host accepts over TCP (QUIC is always TLS 1.3).</summary>
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

/// <summary>What a host's responses put in <c>Server</c>.</summary>
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

/// <summary>Where a request's ID (<c>X-Request-Id</c>) goes: to the upstream, back to the client, both or neither.</summary>
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

/// <summary>How long a rate-limit policy's rate is measured over.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RateLimitPeriod>))]
public enum RateLimitPeriod
{
    /// <summary>Requests per second.</summary>
    Second,

    /// <summary>Requests per minute (the built-in period).</summary>
    Minute,

    /// <summary>Requests per hour.</summary>
    Hour,
}

/// <summary>What a rate-limit bucket is keyed by.</summary>
[JsonConverter(typeof(ConfigEnumConverter<RateLimitKey>))]
public enum RateLimitKey
{
    /// <summary>One bucket per resolved client IP (IPv6 by /64).</summary>
    ClientIp,

    /// <summary>One bucket per client IP and canonical matching path.</summary>
    ClientIpAndPath,

    /// <summary>One bucket per request header value, with the client IP as the fallback.</summary>
    Header,
}

/// <summary>How an access list combines its address rule and its passwords.</summary>
[JsonConverter(typeof(ConfigEnumConverter<AccessListSatisfy>))]
public enum AccessListSatisfy
{
    /// <summary>The address must pass and, when the list has users, the credentials too (the built-in choice).</summary>
    All,

    /// <summary>Either passes: a trusted range can skip the password prompt.</summary>
    Any,
}

/// <summary>The bounds of an access list's users and realm, shared by the validator, the compiler and the proxy.</summary>
public static class AccessListLimits
{
    public const int MaxUsernameLength = 64;

    public const int MaxRealmLength = 64;

    /// <summary>The users of one list: each is a hash a request may have to check, and a document this large is a mistake.</summary>
    public const int MaxUsers = 1000;

    /// <summary>Longest password the dashboard, the API and the gate deal in (UTF-8 bytes).</summary>
    public const int MaxPasswordBytes = 256;
}

/// <summary>The bounds of the proof-of-work settings, shared by the validator, the compiler and the proxy.</summary>
public static class ProofOfWorkLimits
{
    /// <summary>Lowest difficulty: 8 leading zero bits, a few milliseconds.</summary>
    public const int MinDifficulty = 8;

    /// <summary>Highest difficulty: 28 leading zero bits, minutes of work on one core.</summary>
    public const int MaxDifficulty = 28;

    /// <summary>The built-in difficulty: 16 leading zero bits, well under a second on an ordinary laptop.</summary>
    public const int DefaultDifficulty = 16;

    /// <summary>Shortest token lifetime: an hour.</summary>
    public const int MinTokenLifetimeHours = 1;

    /// <summary>Longest token lifetime: 30 days.</summary>
    public const int MaxTokenLifetimeHours = 720;

    /// <summary>The built-in token lifetime: seven days.</summary>
    public const int DefaultTokenLifetimeHours = 168;
}
