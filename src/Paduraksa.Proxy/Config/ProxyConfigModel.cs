using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
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

    /// <summary>The named rate-limit policies a host or route's settings can refer to.</summary>
    public IReadOnlyList<RateLimitPolicySpec> RateLimitPolicies { get; init; } = [];

    public DefaultsSpec Defaults { get; init; } = new();

    /// <summary>The named access lists the policies in host and route chains refer to.</summary>
    public IReadOnlyList<AccessListSpec> AccessLists { get; init; } = [];

    /// <summary>The rule sets (document sets and the built-in presets any host names) the chains refer to.</summary>
    public IReadOnlyList<RuleSetSpec> RuleSets { get; init; } = [];

    /// <summary>The auth providers (forward-auth endpoints) hosts and routes name.</summary>
    public IReadOnlyList<AuthProviderSpec> AuthProviders { get; init; } = [];
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

    /// <summary>The global defaults' error pages, which a request for an unknown host is answered with.</summary>
    public HostErrorPagesSpec ErrorPages { get; init; } = HostErrorPagesSpec.Default;

    /// <summary>The connection stage: the per-IP cap and the connection deny list.</summary>
    public ConnectionsSpec Connections { get; init; } = ConnectionsSpec.Default;
}

/// <summary>The connection-stage settings, compiled into a <see cref="ConnectionPolicy"/> (PLAN §7.2 A).</summary>
public sealed record ConnectionsSpec
{
    /// <summary>The cap when the config sets none: far above what a household or a small office behind one address opens,
    /// far below what one host needs to exhaust Kestrel's connection limit.</summary>
    public const int DefaultMaxPerIp = 256;

    /// <summary>The largest cap: past it the limit is "off" in all but name, and the table's count has no room.</summary>
    public const int MaxMaxPerIp = 100_000;

    public static ConnectionsSpec Default { get; } = new();

    /// <summary>Open connections one client address (an IPv6 /64 is one) may hold; 0 turns the cap off.</summary>
    public int MaxPerIp { get; init; } = DefaultMaxPerIp;

    /// <summary>The access list whose deny addresses are refused before TLS; empty for none.</summary>
    public AccessListId DenyList { get; init; }
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

/// <summary>The proof-of-work settings resolved across the layers; a route may only override <see cref="Enabled"/>.</summary>
public sealed record HostProofOfWorkSpec
{
    /// <summary>Off, with the built-in values in place.</summary>
    public static HostProofOfWorkSpec Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>Leading zero bits of SHA-256, 8 to 28 (the compiler checks it).</summary>
    public int Difficulty { get; init; } = ProofOfWorkLimits.DefaultDifficulty;

    /// <summary>How long a token lasts, in hours, 1 to 720 (the compiler checks it).</summary>
    public int TokenLifetimeHours { get; init; } = ProofOfWorkLimits.DefaultTokenLifetimeHours;
}

/// <summary>The <c>robots.txt</c> settings resolved across the layers; a host has one file, so a route has no say.</summary>
public sealed record HostRobotsSpec
{
    /// <summary>Forward <c>/robots.txt</c> to the upstream, which answers with its own file.</summary>
    public static HostRobotsSpec Passthrough { get; } = new();

    public RobotsMode Mode { get; init; } = RobotsMode.Passthrough;

    /// <summary>The text <see cref="RobotsMode.Custom"/> serves, as stored; the compiler normalises its line endings.</summary>
    public string? CustomText { get; init; }
}

/// <summary>One error page as the layers resolved it. Which fields are set follows <see cref="Mode"/> (the validator checks it; the compiler's backstop does too).</summary>
public sealed record ErrorPageSpec
{
    public ErrorPageMode Mode { get; init; }

    public string? Heading { get; init; }

    public string? Text { get; init; }

    public string? Html { get; init; }

    /// <summary>The layer that set the page, for the simulator and the dashboard.</summary>
    public ErrorPageSource Source { get; init; } = ErrorPageSource.BuiltIn;
}

/// <summary>The pages a host's layers set, by <see cref="ErrorPageStatuses"/> key, and whether an upstream's error bodies give way to them.</summary>
public sealed record HostErrorPagesSpec
{
    /// <summary>The built-in pages, and an upstream's error bodies untouched.</summary>
    public static HostErrorPagesSpec Default { get; } = new();

    public UpstreamErrorReplacement ReplaceUpstream { get; init; } = UpstreamErrorReplacement.Off;

    /// <summary>The pages a layer set; a status that isn't here is served from its built-in page.</summary>
    public IReadOnlyDictionary<int, ErrorPageSpec> Pages { get; init; } = new Dictionary<int, ErrorPageSpec>();
}

/// <summary>Maintenance mode, resolved across the layers; the compiler checks the values and an admin host ignores it.</summary>
public sealed record HostMaintenanceSpec
{
    /// <summary>Not in maintenance.</summary>
    public static HostMaintenanceSpec Off { get; } = new();

    public bool Enabled { get; init; }

    /// <summary>The status of the answer.</summary>
    public int Status { get; init; } = ErrorPageStatuses.DefaultMaintenanceStatus;

    /// <summary>Seconds for <c>Retry-After</c>; 0 sends none.</summary>
    public int RetryAfterSeconds { get; init; }

    /// <summary>The access list whose allowed addresses skip the page; <see cref="AccessListId.Empty"/> for none.</summary>
    public AccessListId BypassAccessListId { get; init; }
}

/// <summary>
/// Keeping a host reachable through Cloudflare only, resolved across the layers. Both fields need the global trusted-proxy
/// mode to be a Cloudflare one to have an effect (the compiler ignores them otherwise).
/// </summary>
public sealed record HostCloudflareSpec
{
    /// <summary>No lockdown, no Authenticated Origin Pulls.</summary>
    public static HostCloudflareSpec Off { get; } = new();

    public OriginLockdownMode OriginLockdown { get; init; } = OriginLockdownMode.Off;

    public bool AuthenticatedOriginPulls { get; init; }
}

/// <summary>The rate-limit settings resolved across the layers; a route may override the switch and the policy.</summary>
public sealed record HostRateLimitSpec
{
    /// <summary>No limit.</summary>
    public static HostRateLimitSpec Off { get; } = new();

    /// <summary>False: an inherited policy is named but inert.</summary>
    public bool Enabled { get; init; }

    /// <summary>The policy to apply; <see cref="RateLimitPolicyId.Empty"/> when none is named.</summary>
    public RateLimitPolicyId PolicyId { get; init; }

    /// <summary>The policy that actually applies, or null.</summary>
    public RateLimitPolicyId? EffectivePolicyId => Enabled && !PolicyId.IsEmpty ? PolicyId : null;
}

/// <summary>
/// What a host takes of a request, resolved across the layers; a route may override only <see cref="MaxBodySize"/>. The
/// compiler checks every field against <see cref="RequestLimitBounds"/>.
/// </summary>
public sealed record HostRequestLimitsSpec
{
    /// <summary>The built-in limits.</summary>
    public static HostRequestLimitsSpec Default { get; } = new();

    /// <summary>The largest body in bytes; <see cref="RequestLimitBounds.UnlimitedBodySize"/> (0) for none.</summary>
    public long MaxBodySize { get; init; } = RequestLimitBounds.DefaultMaxBodySize;

    /// <summary>Most header lines.</summary>
    public int MaxHeaderCount { get; init; } = RequestLimitBounds.DefaultMaxHeaderCount;

    /// <summary>Most bytes of header names and values together.</summary>
    public int MaxHeaderBytes { get; init; } = RequestLimitBounds.DefaultMaxHeaderBytes;

    /// <summary>Longest request target, in bytes.</summary>
    public int MaxUrlLength { get; init; } = RequestLimitBounds.DefaultMaxUrlLength;
}

/// <summary>The access-list setting resolved across the layers; a route may override the switch and the list.</summary>
public sealed record HostAccessListSpec
{
    /// <summary>No access list.</summary>
    public static HostAccessListSpec Off { get; } = new();

    /// <summary>False: an inherited list is named but inert.</summary>
    public bool Enabled { get; init; }

    /// <summary>The list to apply; <see cref="AccessListId.Empty"/> when none is named.</summary>
    public AccessListId AccessListId { get; init; }

    /// <summary>The list that actually applies, or null.</summary>
    public AccessListId? Effective => Enabled && !AccessListId.IsEmpty ? AccessListId : null;
}

/// <summary>
/// One named rate-limit policy, resolved across the document: its bucket numbers, partition key and, for a header key,
/// the header name (already a valid token). The compiler pairs it with a host into a bucket scope.
/// </summary>
public sealed record RateLimitPolicySpec
{
    public required RateLimitPolicyId Id { get; init; }

    /// <summary>The human label, for traces and errors.</summary>
    public required string Name { get; init; }

    public required RateLimitParameters Parameters { get; init; }

    public RateLimitKey Key { get; init; }

    /// <summary>With <see cref="RateLimitKey.Header"/>: the header whose value is the partition key; null otherwise.</summary>
    public string? Header { get; init; }
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

    /// <summary>The host's proof-of-work switch and parameters; each route's own switch may override it.</summary>
    public HostProofOfWorkSpec ProofOfWork { get; init; } = HostProofOfWorkSpec.Off;

    /// <summary>The host's resolved rate-limit policy; each route's own layer may override it.</summary>
    public HostRateLimitSpec RateLimit { get; init; } = HostRateLimitSpec.Off;

    /// <summary>The host's resolved access list. The chains below are built from it and from each route's own layer.</summary>
    public HostAccessListSpec AccessList { get; init; } = HostAccessListSpec.Off;

    /// <summary>The body, header and request-target limits; each route's own body limit may override the first.</summary>
    public HostRequestLimitsSpec RequestLimits { get; init; } = HostRequestLimitsSpec.Default;

    /// <summary>The host's resolved rule set, for the overview; its policy sits in the chains like the access list's.</summary>
    public HostRuleSetSpec RuleSet { get; init; } = HostRuleSetSpec.Off;

    /// <summary>The host's resolved auth provider; each route's own layer may override it. Proxy hosts only: the others have no route to
    /// ask it on.</summary>
    public HostAuthProviderSpec AuthProvider { get; init; } = HostAuthProviderSpec.Off;

    /// <summary>The host's own pages for the statuses the gate and the upstream answer, and whether an upstream's error bodies are replaced.</summary>
    public HostErrorPagesSpec ErrorPages { get; init; } = HostErrorPagesSpec.Default;

    /// <summary>Maintenance mode; an admin host ignores it.</summary>
    public HostMaintenanceSpec Maintenance { get; init; } = HostMaintenanceSpec.Off;

    /// <summary>What the host answers at <c>/robots.txt</c>.</summary>
    public HostRobotsSpec Robots { get; init; } = HostRobotsSpec.Passthrough;

    /// <summary>Origin lockdown and Authenticated Origin Pulls; inert unless the trusted-proxy mode is a Cloudflare one.</summary>
    public HostCloudflareSpec Cloudflare { get; init; } = HostCloudflareSpec.Off;

    /// <summary>Proxy hosts only; listed in the order the host evaluates them: exact first, then the longest prefix,
    /// then <see cref="RouteSpec.Order"/>, then as listed.</summary>
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

    /// <summary>A specificity tiebreaker between routes of equal match kind and path length; equal orders keep the
    /// listed order.</summary>
    public int Order { get; init; }

    /// <summary>The <see cref="UpstreamSpec"/> to proxy to.</summary>
    public required UpstreamId Upstream { get; init; }

    /// <summary>This route's effective proof of work: the host's values with the route's own switch, when it sets one.</summary>
    public HostProofOfWorkSpec ProofOfWork { get; init; } = HostProofOfWorkSpec.Off;

    /// <summary>True when the route's own layer decided <see cref="ProofOfWork"/>'s switch.</summary>
    public bool ProofOfWorkRouteOverride { get; init; }

    /// <summary>This route's effective rate-limit policy: the host's, a different one, or none.</summary>
    public RateLimitPolicyId? RateLimit { get; init; }

    /// <summary>True when the route's own layer set the rate limit (the trace says so).</summary>
    public bool RateLimitRouteOverride { get; init; }

    /// <summary>This route's effective access list: the host's, a different one, or none. Where the list's policies sit in the
    /// chains is <see cref="Policies"/>' business; this is what a route is guarded by, for the overview.</summary>
    public AccessListId? AccessList { get; init; }

    /// <summary>This route's effective body limit in bytes (0: none): its own, or the host's.</summary>
    public long MaxBodySize { get; init; } = RequestLimitBounds.DefaultMaxBodySize;

    /// <summary>True when the route's own layer set <see cref="MaxBodySize"/> (the trace says so).</summary>
    public bool MaxBodySizeRouteOverride { get; init; }

    /// <summary>This route's effective rule set: the host's, another, or none, for the overview.</summary>
    public RuleSetId? RuleSet { get; init; }

    /// <summary>This route's effective auth provider: the host's, another, or none.</summary>
    public AuthProviderId? AuthProvider { get; init; }

    /// <summary>True when the route's own layer set the provider (the trace says so).</summary>
    public bool AuthProviderRouteOverride { get; init; }

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

    /// <summary>Which <c>Host</c> the upstream receives (and so the SNI of an HTTPS destination).</summary>
    public UpstreamHostHeaderMode HostHeader { get; init; }

    /// <summary>With <see cref="UpstreamHostHeaderMode.Custom"/>: the exact name sent.</summary>
    public string? HostHeaderValue { get; init; }
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

/// <summary>One access list in a chain: the host chain takes <see cref="AccessListStage.AddressOnly"/>, a route's chain the
/// whole list. <paramref name="DeferPasswordsOnPlainHttp"/>: a host chain that carries the whole list (a host with no
/// routes) on a host that redirects to HTTPS skips the password on a plain-HTTP request, which the redirect answers next.</summary>
public sealed record AccessListPolicySpec(AccessListId List, AccessListStage Stage = AccessListStage.Full, bool DeferPasswordsOnPlainHttp = false) : PolicySpec;

/// <summary>The host chain of a host whose routes name different lists: the address rules of all of them as one check.</summary>
public sealed record AccessListUnionPolicySpec(IReadOnlyList<AccessListId> Lists) : PolicySpec;

/// <summary>A named access list. The validator has judged it; the compiler checks the addresses again.</summary>
public sealed record AccessListSpec
{
    public required AccessListId Id { get; init; }

    /// <summary>The human label, for traces and errors.</summary>
    public required string Name { get; init; }

    public IReadOnlyList<string> Allow { get; init; } = [];

    public IReadOnlyList<string> Deny { get; init; } = [];

    public bool DropDenied { get; init; }

    /// <summary>Country codes (<c>DE</c>, <c>T1</c> for Tor, <c>XX</c> for unknown); the compiler reports one it doesn't know.</summary>
    public IReadOnlyList<string> AllowCountries { get; init; } = [];

    public IReadOnlyList<string> DenyCountries { get; init; } = [];

    public AccessListSatisfy Satisfy { get; init; }

    public bool PassCredentialsUpstream { get; init; }

    /// <summary>The <c>realm</c> of the 401 challenge: printable ASCII.</summary>
    public required string Realm { get; init; }

    public IReadOnlyList<AccessListUserSpec> Users { get; init; } = [];
}

/// <summary>A user: the name and the vault's handle on their password, never a password or a hash. A handle with no password in the
/// vault is a user who can't sign in.</summary>
public sealed record AccessListUserSpec(string Username, IPasswordCredential Credential);

/// <summary>The rule-set setting resolved across the layers; a route may override the switch and the set.</summary>
public sealed record HostRuleSetSpec
{
    /// <summary>No rule set.</summary>
    public static HostRuleSetSpec Off { get; } = new();

    /// <summary>False: an inherited set is named but inert.</summary>
    public bool Enabled { get; init; }

    /// <summary>The set to apply; <see cref="RuleSetId.Empty"/> when none is named.</summary>
    public RuleSetId RuleSetId { get; init; }

    /// <summary>The set that actually applies, or null.</summary>
    public RuleSetId? Effective => Enabled && !RuleSetId.IsEmpty ? RuleSetId : null;
}

/// <summary>One rule set in a chain; the compiler binds it to the compiled set the ID names.</summary>
public sealed record RuleSetPolicySpec(RuleSetId RuleSet, bool RouteOverride = false) : PolicySpec;

/// <summary>A named rule set, ordered; the validator has judged it, and the compiler checks every pattern again.</summary>
public sealed record RuleSetSpec
{
    public required RuleSetId Id { get; init; }

    /// <summary>The human label, for traces and errors.</summary>
    public required string Name { get; init; }

    /// <summary>In the order they are tried.</summary>
    public IReadOnlyList<RuleSpec> Rules { get; init; } = [];
}

/// <summary>One rule: every condition it states has to match.</summary>
public sealed record RuleSpec
{
    public required RuleAction Action { get; init; }

    /// <summary>Null: any path.</summary>
    public RulePathKind? PathKind { get; init; }

    /// <summary>With <see cref="PathKind"/>: the pattern, as the document holds it.</summary>
    public string? Pattern { get; init; }

    /// <summary>Upper-case methods; empty: any.</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>Substrings of the user agent, compared ignoring ASCII case; empty: none.</summary>
    public IReadOnlyList<string> UserAgentContains { get; init; } = [];

    /// <summary>The user-agent condition also holds for a request with no user agent or an empty one.</summary>
    public bool UserAgentEmpty { get; init; }
}

/// <summary>The auth-provider setting resolved across the layers; a route may override the switch and the provider.</summary>
public sealed record HostAuthProviderSpec
{
    /// <summary>No provider.</summary>
    public static HostAuthProviderSpec Off { get; } = new();

    /// <summary>False: an inherited provider is named but inert.</summary>
    public bool Enabled { get; init; }

    /// <summary>The provider to ask; <see cref="AuthProviderId.Empty"/> when none is named.</summary>
    public AuthProviderId AuthProviderId { get; init; }

    /// <summary>The provider that actually applies, or null.</summary>
    public AuthProviderId? Effective => Enabled && !AuthProviderId.IsEmpty ? AuthProviderId : null;
}

/// <summary>A forward-auth endpoint with every default filled in. The validator has judged it; the compiler checks the URL again.</summary>
public sealed record AuthProviderSpec
{
    public required AuthProviderId Id { get; init; }

    /// <summary>The human label, for traces and errors. Not part of what a decision depends on.</summary>
    public required string Name { get; init; }

    /// <summary>The endpoint: an absolute <c>http</c> or <c>https</c> URL.</summary>
    public required string Url { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(ForwardAuthLimits.DefaultTimeoutSeconds);

    /// <summary>How long an allow is remembered; zero: not at all.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(ForwardAuthLimits.DefaultCacheSeconds);

    public ForwardAuthCacheScope CacheScope { get; init; }

    /// <summary>Client headers sent to the endpoint besides the standard set.</summary>
    public IReadOnlyList<string> RequestHeaders { get; init; } = [];

    /// <summary>The headers copied upstream when the endpoint allows, and removed from the client's request first; a trailing <c>*</c> is a prefix.</summary>
    public IReadOnlyList<string> ResponseHeaders { get; init; } = [];

    /// <summary>Where an unauthenticated browser is sent when the endpoint answers 401; null: the 401 is relayed.</summary>
    public string? SignInUrl { get; init; }

    public string? SignInReturnParameter { get; init; }
}
