using Paduraksa.Core.Model;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// The compiler's input: a plain, in-memory description of the proxy config, built from the canonical config
/// document, so nothing here knows about storage. Objects are identified by their typed IDs, which must be unique and
/// non-empty; the route, upstream and destination IDs' canonical strings (<c>rte_...</c>, <c>ups_...</c>,
/// <c>dst_...</c>) double as YARP route, cluster and destination IDs.
/// </summary>
public sealed record ProxyConfigModel
{
    public static ProxyConfigModel Empty { get; } = new();

    /// <summary>The committed revision this config comes from, carried into the snapshot.</summary>
    public ConfigRevisionId Revision { get; init; }

    public IReadOnlyList<HostSpec> Hosts { get; init; } = [];

    public IReadOnlyList<UpstreamSpec> Upstreams { get; init; } = [];

    public DefaultsSpec Defaults { get; init; } = new();
}

/// <summary>Global settings the compiler needs.</summary>
public sealed record DefaultsSpec
{
    public UnknownHostBehaviour UnknownHost { get; init; } = UnknownHostBehaviour.NotFound;

    /// <summary>The certificate (resolved by <see cref="SnapshotCompilerOptions.Certificates"/>) served when the SNI
    /// matches no host, or is absent (an IP-literal URL). It is also the certificate of hosts that don't name their own.
    /// Null: such handshakes are rejected, and hosts without a certificate aren't served over HTTPS.</summary>
    public CertificateId? Certificate { get; init; }

    /// <summary>TLS settings of the default certificate's entry (unknown or absent SNI).</summary>
    public HostTlsSpec Tls { get; init; } = HostTlsSpec.Default;

    /// <summary>Who may tell us the client IP.</summary>
    public TrustedProxySpec TrustedProxies { get; init; } = TrustedProxySpec.Off;

    /// <summary>Whether a request for an unknown host gets an access-log line: the global default, since an unknown
    /// host has no settings of its own.</summary>
    public bool AccessLog { get; init; } = true;

    /// <summary>Where an unknown host's request ID is sent: the global default.</summary>
    public RequestIdPropagation RequestIdPropagation { get; init; } = RequestIdPropagation.UpstreamAndResponse;

    /// <summary>The global defaults' <c>Server</c> header, which an unknown host follows too.</summary>
    public HostServerHeaderSpec ServerHeader { get; init; } = HostServerHeaderSpec.Default;
}

/// <summary>Trusted proxies in front of Paduraksa, compiled into a <see cref="TrustedProxyPolicy"/>.</summary>
public sealed record TrustedProxySpec
{
    /// <summary>No trusted proxies.</summary>
    public static TrustedProxySpec Off { get; } = new();

    public TrustedProxyMode Mode { get; init; } = TrustedProxyMode.Off;

    /// <summary>CIDRs or addresses of the trusted peers. <see cref="TrustedProxyMode.CloudflareProxy"/>: must be
    /// empty; Cloudflare's published edge ranges are used.</summary>
    public IReadOnlyList<string> Proxies { get; init; } = [];

    /// <summary>Generic mode only: the header carrying the client (default <c>X-Forwarded-For</c>, read right to
    /// left; or a single-address header such as <c>X-Real-IP</c>).</summary>
    public string? Header { get; init; }

    /// <summary>Cloudflare modes: also accept <c>True-Client-IP</c> (Enterprise) when <c>CF-Connecting-IP</c> is absent.</summary>
    public bool UseTrueClientIp { get; init; }

    /// <summary>Cloudflare modes: fall back to the right-most untrusted <c>X-Forwarded-For</c> entry.</summary>
    public bool ForwardedForFallback { get; init; }

    /// <summary>Generic mode only: take the request scheme (for the HTTPS redirect and the upstream
    /// <c>X-Forwarded-Proto</c>) from the trusted proxies' <c>X-Forwarded-Proto</c>. Off by default: set it only when
    /// every trusted proxy sets that header itself.</summary>
    public bool TrustForwardedProto { get; init; }
}

/// <summary>
/// The configurable part of a host's <see cref="TlsPolicy"/>. The handshake applies the settings of the host named in
/// the SNI; because clients reuse a connection for every name its certificate covers, the gate re-checks each request
/// against its own host's settings and answers 421 Misdirected Request when the connection is weaker.
/// </summary>
public sealed record HostTlsSpec
{
    /// <summary>TLS 1.2+, <c>h2</c> + <c>http/1.1</c>, HTTP/3 on.</summary>
    public static HostTlsSpec Default { get; } = new();

    /// <summary>Lowest TLS version over TCP (QUIC is always TLS 1.3). Requests on an older connection get 421.</summary>
    public TlsMinimumVersion MinimumVersion { get; init; } = TlsMinimumVersion.Tls12;

    /// <summary>ALPN offered over TCP. With HTTP/1.1 only, requests on an HTTP/2 connection get 421.</summary>
    public TcpAlpn TcpAlpn { get; init; } = TcpAlpn.Http2AndHttp11;

    /// <summary>
    /// Serve the host over HTTP/3 (when the H3 listener exists). Off: QUIC handshakes whose SNI names the host are
    /// rejected, HTTP/3 requests for it arriving on a QUIC connection negotiated for another name get 421, and it gets
    /// no <c>Alt-Svc</c>.
    /// </summary>
    public bool Http3 { get; init; } = true;
}

/// <summary>
/// A host's HSTS, already resolved across the layers by the caller: the compiler turns it into the header
/// value once per host (<see cref="Tls.HstsHeader"/>). Plain HTTP never carries it, and neither does a request for an IP
/// literal or a loopback name.
/// </summary>
public sealed record HostHstsSpec
{
    /// <summary>Off: no header of ours, so an upstream's own passes through untouched.</summary>
    public static HostHstsSpec Off { get; } = new();

    /// <summary>Send <c>Strict-Transport-Security</c> on the host's HTTPS responses.</summary>
    public bool Enabled { get; init; }

    /// <summary><c>max-age</c>, in seconds: how long browsers keep the host HTTPS-only. 0 tells them to forget it.</summary>
    public int MaxAgeSeconds { get; init; } = HstsHeader.DefaultMaxAgeSeconds;

    /// <summary>Add <c>includeSubDomains</c>.</summary>
    public bool IncludeSubDomains { get; init; }

    /// <summary>Add <c>preload</c>: only valid with <see cref="IncludeSubDomains"/> and a year or more of
    /// <see cref="MaxAgeSeconds"/>.</summary>
    public bool Preload { get; init; }
}

/// <summary>What a host's responses put in <c>Server</c>, resolved across the layers.</summary>
public sealed record HostServerHeaderSpec
{
    /// <summary><c>Paduraksa</c>, with no version.</summary>
    public static HostServerHeaderSpec Default { get; } = new();

    public ServerHeaderMode Mode { get; init; } = ServerHeaderMode.Paduraksa;

    /// <summary>The value <see cref="ServerHeaderMode.Custom"/> sends; ignored by every other mode.</summary>
    public string? Value { get; init; }
}

/// <summary>A host. Disabled hosts are validated but left out of the snapshot.</summary>
public sealed record HostSpec
{
    public required HostId Id { get; init; }

    /// <summary>Exact names, <c>*.parent</c> wildcards, or bracketed IPv6 literals; normalised by the compiler.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    public HostKind Kind { get; init; } = HostKind.Proxy;

    /// <summary>
    /// False: the host is fully validated (names, routes, policies, certificate) but left out of the snapshot. Its host
    /// and route IDs are identities, so they must be unique across all hosts; its names only have to be unique within
    /// the host, because a disabled host serves nothing (a replacement can be staged next to the live host).
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Plain-HTTP requests are redirected to HTTPS before anything else. Needs <see cref="Https"/>.</summary>
    public bool ForceHttps { get; init; }

    /// <summary>
    /// False: the host isn't served over HTTPS (certificate binding <i>None</i>). Its names stay out of the certificate
    /// table, so a handshake naming it is served by whatever else covers the name (another host's wildcard, the default
    /// certificate) or rejected; either way the gate answers an HTTPS request for the host 421, on any connection. It
    /// can't name a <see cref="Certificate"/> or force HTTPS.
    /// </summary>
    public bool Https { get; init; } = true;

    /// <summary>The certificate served for this host's names (exact and wildcard; IP literals never arrive as SNI).
    /// Null: the default certificate with this host's TLS settings, or no HTTPS when there is no default.</summary>
    public CertificateId? Certificate { get; init; }

    /// <summary>Certificates for individual names of the host, overriding <see cref="Certificate"/> (or the default
    /// one) for exactly those names: the key is one of <see cref="Names"/> (compared after normalisation, so any
    /// spelling of it does), the value the certificate its handshakes get. IP literals never reach the certificate
    /// table, so they can't be listed.</summary>
    public IReadOnlyDictionary<string, CertificateId>? NameCertificates { get; init; }

    public HostTlsSpec Tls { get; init; } = HostTlsSpec.Default;

    public HostHstsSpec Hsts { get; init; } = HostHstsSpec.Off;

    /// <summary>Write a line per request of this host to the access log; the settings layers resolve it, built-in
    /// default on.</summary>
    public bool AccessLog { get; init; } = true;

    /// <summary>Where this host's request ID is sent: the settings layers resolve it, built-in default upstream and
    /// response.</summary>
    public RequestIdPropagation RequestIdPropagation { get; init; } = RequestIdPropagation.UpstreamAndResponse;

    public HostServerHeaderSpec ServerHeader { get; init; } = HostServerHeaderSpec.Default;

    /// <summary>Proxy hosts only; ordered by <see cref="RouteSpec.Order"/>, then as listed.</summary>
    public IReadOnlyList<RouteSpec> Routes { get; init; } = [];

    /// <summary>Host-level chain; runs before each route's own policies.</summary>
    public IReadOnlyList<PolicySpec> Policies { get; init; } = [];

    /// <summary>Required for <see cref="HostKind.Redirect"/>.</summary>
    public RedirectSpec? Redirect { get; init; }

    /// <summary>Required for <see cref="HostKind.Respond"/>.</summary>
    public RespondSpec? Respond { get; init; }
}

/// <summary>One route of a proxy host.</summary>
public sealed record RouteSpec
{
    public required RouteId Id { get; init; }

    public RouteMatchKind Match { get; init; } = RouteMatchKind.Prefix;

    /// <summary>Starts with <c>/</c>; no query or fragment.</summary>
    public string Path { get; init; } = "/";

    /// <summary>Lower runs first; equal orders keep their listed order.</summary>
    public int Order { get; init; }

    /// <summary>The <see cref="UpstreamSpec"/> to proxy to.</summary>
    public required UpstreamId Upstream { get; init; }

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
    /// <summary>200-599.</summary>
    public int StatusCode { get; init; } = 200;

    public string Body { get; init; } = string.Empty;

    public string ContentType { get; init; } = "text/plain; charset=utf-8";
}

public sealed record UpstreamSpec
{
    public required UpstreamId Id { get; init; }

    /// <summary>Empty only while no enabled host routes to the upstream (say, every destination disabled).</summary>
    public required IReadOnlyList<DestinationSpec> Destinations { get; init; }

    /// <summary>YARP policy name (<c>RoundRobin</c>, <c>PowerOfTwoChoices</c>, ...); <see langword="null"/> = YARP's default.</summary>
    public string? LoadBalancing { get; init; }

    public bool SessionAffinity { get; init; }

    /// <summary>Passive health (transport failure rate).</summary>
    public bool PassiveHealth { get; init; }

    /// <summary>How long a passively unhealthy destination stays out; <see langword="null"/> = YARP's default.</summary>
    public TimeSpan? ReactivationPeriod { get; init; }

    /// <summary>Active health probes; <see langword="null"/> = none.</summary>
    public ActiveHealthSpec? ActiveHealth { get; init; }

    /// <summary>Part of the HTTP client config: changing it creates a new client for this upstream.</summary>
    public int? MaxConnectionsPerServer { get; init; }

    public UpstreamTlsTrust TlsTrust { get; init; }

    /// <summary>With <see cref="UpstreamTlsTrust.Pinned"/>: normalised fingerprints (<see
    /// cref="Yarp.UpstreamCertificatePins.TryNormalize"/>).</summary>
    public IReadOnlyList<string> PinnedSha256 { get; init; } = [];
}

/// <summary>
/// YARP active health checks: a GET of <see cref="Path"/> (appended to each destination's address) every
/// <see cref="Interval"/>; a destination is unhealthy after consecutive failures (YARP's default: 2) and healthy again
/// after a successful probe.
/// </summary>
public sealed record ActiveHealthSpec
{
    /// <summary>Starts with <c>/</c>.</summary>
    public string Path { get; init; } = "/";

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>A YARP destination; the ID must stay stable for health state to survive.</summary>
public sealed record DestinationSpec(DestinationId Id, string Address);

/// <summary>A policy in a host or route chain.</summary>
public abstract record PolicySpec;

/// <summary>Always answers with a fixed status and body.</summary>
public sealed record DenyPolicySpec(int StatusCode = 403, string Body = "forbidden\n") : PolicySpec;

/// <summary>Always aborts the request.</summary>
public sealed record DropPolicySpec : PolicySpec;

/// <summary>
/// Only clients inside the listed addresses or CIDRs (<c>192.0.2.1</c>, <c>10.0.0.0/8</c>, <c>2001:db8::/32</c>) get
/// through, judged on the resolved client IP. Others get 403, or are dropped when <paramref name="DropDenied"/> is set.
/// </summary>
public sealed record IpAllowListPolicySpec(IReadOnlyList<string> Addresses, bool DropDenied = false) : PolicySpec;
