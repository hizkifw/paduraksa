namespace Paduraksa.Admin.Auth;

/// <summary>
/// The admin app's security knobs (PLAN §10.1). The server binds them from <c>Paduraksa:Admin</c> and <c>Paduraksa:Listen</c> and
/// validates the bounds; the defaults are what ships. A plain object, not <c>IOptions</c>: the server serves one fixed instance for
/// its own options type only.
/// </summary>
public sealed class AdminSecurityOptions
{
    /// <summary>The default for <see cref="MaxRequestBodyBytes"/>: 64 KiB, far more than any JSON request of the identity API.</summary>
    public const long DefaultMaxRequestBodyBytes = 64 * 1024;

    /// <summary>Sign-in attempts (successful or not) one client may make per <see cref="RateLimitWindow"/> (login, second factor, setup). An IPv6 client is one /64.</summary>
    public int RateLimitPermitsPerIp { get; init; } = 20;

    /// <summary>Failed attempts per <see cref="RateLimitWindow"/> against one username (or, for the second factor, one user); successes do not count. On top of the account lockout.</summary>
    public int RateLimitPermitsPerUser { get; init; } = 10;

    /// <summary>The length of the rate-limit window.</summary>
    public TimeSpan RateLimitWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>The most clients (and, separately, usernames) tracked at once; past it new ones share one bucket.</summary>
    public int RateLimitMaxTracked { get; init; } = 10_000;

    /// <summary>The most password derivations (login, setup, re-authentication) running at once: the CPU count by default.</summary>
    public int MaxConcurrentPasswordChecks { get; init; } = Environment.ProcessorCount;

    /// <summary>How long an attempt waits for a derivation slot before it is refused with a 503.</summary>
    public TimeSpan PasswordCheckWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The port of the loopback plain-HTTP listener, 0 when there is none. Requests that arrive on it are accepted only with a <c>Host</c>
    /// of <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c> and this port, so a web page cannot reach it by DNS rebinding.
    /// </summary>
    public int LoopbackHttpPort { get; init; }

    /// <summary>The largest request body accepted unless an endpoint raises it with <see cref="AdminRequestLimits.WithAdminBodyLimit{TBuilder}"/>.</summary>
    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;

    /// <summary>
    /// <c>Paduraksa:Listen:AdminAllowedSources</c>: the addresses and CIDR blocks allowed to reach the admin. The main port applies
    /// it per connection before TLS; the internal listener applies it per request to the forwarded client address (PLAN §3.1).
    /// Empty means no restriction.
    /// </summary>
    public IReadOnlyList<string> AdminAllowedSources { get; init; } = [];
}

/// <summary>
/// Places where other parts of the admin app adjust the pipeline without touching it. Registered as a singleton by
/// <see cref="AdminAuthExtensions.AddPaduraksaAdminAuth"/>; the lists are read on every request, so add to them while the app is
/// being built.
/// </summary>
public sealed class AdminPipelineHooks
{
    /// <summary>
    /// Path prefixes (segment-wise, case-insensitive) that stay reachable, unauthenticated, while first-run setup is incomplete, besides
    /// the health endpoints and <c>/api/v1/setup</c>. The OpenAPI documents (<c>/openapi</c>, <c>/api/v1/openapi.json</c>), the API docs and the local
    /// CA root's download endpoints (the wizard's first step) are here by default.
    /// </summary>
    public IList<string> SetupAllowedPathPrefixes { get; } = ["/openapi", "/api/v1/openapi.json", "/api/docs", Api.CaEndpoint.PathPrefix];

    /// <summary>
    /// Path prefixes whose responses keep the CSP the endpoint sets itself (the API docs, the dashboard). Every other response without a
    /// <c>Content-Security-Policy</c> header gets the strict API one. An endpoint that sets its own header needs no entry here; this
    /// list is for responses that cannot (static files).
    /// </summary>
    public IList<string> CspExemptPathPrefixes { get; } = [];
}
