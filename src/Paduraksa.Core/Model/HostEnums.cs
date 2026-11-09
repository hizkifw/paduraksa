using System.Text.Json.Serialization;

using Paduraksa.Core.Json;

namespace Paduraksa.Core.Model;

/// <summary>
/// What a host does with requests (PLAN §5.1). One enum, shared by the config document (its wire form) and the proxy
/// (what a compiled host does), so the two can't drift apart.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<HostKind>))]
public enum HostKind
{
    /// <summary>Proxies to upstreams through its routes.</summary>
    Proxy,

    /// <summary>Redirects every request to a precomputed target.</summary>
    Redirect,

    /// <summary>Answers every request with a fixed status and body.</summary>
    Respond,

    /// <summary>
    /// Serves the admin app through the internal listener (PLAN §3.1): every path is forwarded to <c>__admin</c>, HTTPS
    /// is forced, and it has no routes or fixed answer of its own. At most one exists.
    /// </summary>
    Admin,
}

/// <summary>How a route's path matches the request path (segment-aware, case-insensitive).</summary>
[JsonConverter(typeof(ConfigEnumConverter<RouteMatchKind>))]
public enum RouteMatchKind
{
    /// <summary>
    /// Segment-aware prefix: <c>/api</c> matches <c>/api</c>, <c>/api/</c> and <c>/api/x</c>, not <c>/apix</c>. A prefix
    /// ending in <c>/</c> matches by plain prefix, so <c>/</c> matches everything.
    /// </summary>
    Prefix,

    /// <summary>The whole path, nothing more.</summary>
    Exact,
}

/// <summary>
/// What a request for an unknown host gets (PLAN §5.1, §7.2). Named as the contract always has: the name is the
/// <c>components.schemas</c> key of the API's OpenAPI document, so renaming it would rename a published schema.
/// </summary>
[JsonConverter(typeof(ConfigEnumConverter<UnknownHostBehaviour>))]
public enum UnknownHostBehaviour
{
    /// <summary>404 with a short body.</summary>
    NotFound,

    /// <summary>421 Misdirected Request.</summary>
    MisdirectedRequest,

    /// <summary>No response: the connection (H1) or stream (H2/H3) is aborted.</summary>
    Close,
}
