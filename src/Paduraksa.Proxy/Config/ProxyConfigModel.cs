using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// The compiler's input: a plain, in-memory description of the proxy config (M1). M2 builds the same records from
/// the EF entities / canonical config document, so nothing here knows about storage. Keys are stable identities
/// (typed-ID strings from M2) and double as YARP route, cluster and destination IDs: letters, digits and
/// <c>_ . ~ -</c>, at most 128 characters, unique ignoring case.
/// </summary>
public sealed record ProxyConfigModel
{
    public static ProxyConfigModel Empty { get; } = new();

    /// <summary>Label carried into the snapshot (<c>ConfigRevisionId</c> from M2).</summary>
    public string Revision { get; init; } = "empty";

    public IReadOnlyList<HostSpec> Hosts { get; init; } = [];

    public IReadOnlyList<UpstreamSpec> Upstreams { get; init; } = [];

    public DefaultsSpec Defaults { get; init; } = new();
}

/// <summary>What an unknown host gets (PLAN §5.1 default-host behaviour).</summary>
public enum UnknownHostMode
{
    /// <summary>404 with a short body.</summary>
    NotFound,

    /// <summary>421 Misdirected Request.</summary>
    MisdirectedRequest,

    /// <summary>No response: the connection (H1) or stream (H2/H3) is aborted.</summary>
    Close,
}

/// <summary>Global settings the compiler needs.</summary>
public sealed record DefaultsSpec
{
    public UnknownHostMode UnknownHost { get; init; } = UnknownHostMode.NotFound;

    /// <summary>The HTTPS port clients use (may differ from the bound port behind a port mapping, PLAN §7.5).</summary>
    public int PublicHttpsPort { get; init; } = 443;
}

/// <summary>A host (PLAN §5.1). Disabled hosts are validated but left out of the snapshot.</summary>
public sealed record HostSpec
{
    public required string Key { get; init; }

    /// <summary>Exact names, <c>*.parent</c> wildcards, or bracketed IPv6 literals; normalised by the compiler.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    public HostKind Kind { get; init; } = HostKind.Proxy;

    public bool Enabled { get; init; } = true;

    /// <summary>Plain-HTTP requests are redirected to HTTPS before anything else.</summary>
    public bool ForceHttps { get; init; }

    /// <summary>Proxy hosts only; ordered by <see cref="RouteSpec.Order"/>, then as listed.</summary>
    public IReadOnlyList<RouteSpec> Routes { get; init; } = [];

    /// <summary>Host-level chain; runs before each route's own policies.</summary>
    public IReadOnlyList<PolicySpec> Policies { get; init; } = [];

    /// <summary>Required for <see cref="HostKind.Redirect"/>.</summary>
    public RedirectSpec? Redirect { get; init; }

    /// <summary>Required for <see cref="HostKind.Respond"/>.</summary>
    public RespondSpec? Respond { get; init; }
}

/// <summary>One route of a proxy host. Regex matching comes later.</summary>
public sealed record RouteSpec
{
    public required string Key { get; init; }

    public RouteMatchKind Match { get; init; } = RouteMatchKind.Prefix;

    /// <summary>Starts with <c>/</c>; no query or fragment.</summary>
    public string Path { get; init; } = "/";

    /// <summary>Lower runs first; equal orders keep their listed order.</summary>
    public int Order { get; init; }

    /// <summary>Key of the <see cref="UpstreamSpec"/> to proxy to.</summary>
    public required string Upstream { get; init; }

    public IReadOnlyList<PolicySpec> Policies { get; init; } = [];
}

/// <summary>A Redirect host's target.</summary>
public sealed record RedirectSpec
{
    /// <summary>Absolute <c>http</c>/<c>https</c> URL.</summary>
    public required string Target { get; init; }

    /// <summary>301, 302, 303, 307 or 308.</summary>
    public int StatusCode { get; init; } = 301;

    /// <summary>Append the request's path and query to <see cref="Target"/>.</summary>
    public bool PreservePath { get; init; } = true;
}

/// <summary>A Respond host's fixed answer.</summary>
public sealed record RespondSpec
{
    /// <summary>200–599.</summary>
    public int StatusCode { get; init; } = 200;

    public string Body { get; init; } = string.Empty;

    public string ContentType { get; init; } = "text/plain; charset=utf-8";
}

/// <summary>A YARP cluster.</summary>
public sealed record UpstreamSpec
{
    public required string Key { get; init; }

    public required IReadOnlyList<DestinationSpec> Destinations { get; init; }

    /// <summary>YARP policy name (<c>RoundRobin</c>, <c>PowerOfTwoChoices</c>, ...); <see langword="null"/> = YARP's default.</summary>
    public string? LoadBalancing { get; init; }

    /// <summary>Cookie-based session affinity.</summary>
    public bool SessionAffinity { get; init; }

    /// <summary>Passive health (transport failure rate).</summary>
    public bool PassiveHealth { get; init; }

    /// <summary>How long a passively unhealthy destination stays out; <see langword="null"/> = YARP's default.</summary>
    public TimeSpan? ReactivationPeriod { get; init; }

    /// <summary>Part of the HTTP client config: changing it creates a new client for this upstream.</summary>
    public int? MaxConnectionsPerServer { get; init; }
}

/// <summary>A YARP destination; the key must stay stable for health state to survive (PLAN §6.3).</summary>
public sealed record DestinationSpec(string Key, string Address);

/// <summary>A policy in a host or route chain. Real WAF policies arrive with M6.</summary>
public abstract record PolicySpec;

/// <summary>Always answers with a fixed status and body.</summary>
public sealed record DenyPolicySpec(int StatusCode = 403, string Body = "forbidden\n") : PolicySpec;

/// <summary>Always aborts the request.</summary>
public sealed record DropPolicySpec : PolicySpec;

/// <summary>
/// Only the listed client addresses get through (exact addresses in M1; CIDR sets arrive with the IP tables).
/// Others get 403, or are dropped when <paramref name="DropDenied"/> is set.
/// </summary>
public sealed record IpAllowListPolicySpec(IReadOnlyList<string> Addresses, bool DropDenied = false) : PolicySpec;
