using Paduraksa.Admin.Auth;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Server.Configuration;

/// <summary>
/// Bootstrap settings bound from the <c>Paduraksa</c> configuration section (appsettings, environment).
/// Everything else (hosts, routes, certificates, ...) lives in the database.
/// Bound and validated once per process; not reloadable (listeners can't move at runtime).
/// </summary>
internal sealed class PaduraksaOptions
{
    public const string SectionName = "Paduraksa";

    /// <summary>
    /// Directory holding the databases, the KEK and the runtime files: the one volume to mount and back up.
    /// A relative path is resolved against the content root (the directory holding <c>appsettings.json</c>,
    /// by default the app's base directory) when the options are bound, never against the process's working
    /// directory; after binding this is always a fully qualified path. Absolute paths are used as given.
    /// A leading <c>~</c> is rejected: nothing expands it, so it would silently become a directory named <c>~</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "./data";

    public PaduraksaListenOptions Listen { get; set; } = new();

    /// <summary>Slow-client and connection limits of the public listeners (<c>Paduraksa:Limits</c>).</summary>
    public PaduraksaLimitsOptions Limits { get; set; } = new();

    /// <summary>Sign-in, lockout, session and password settings (<c>Paduraksa:Admin</c>).</summary>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>Certificate startup work: how long the vault/inventory recovery may delay the core host.</summary>
    public PaduraksaCertificateOptions Certificates { get; set; } = new();

    /// <summary>ACME issuance and renewal: the scheduler, its timing and which CAs are trusted.</summary>
    public PaduraksaAcmeOptions Acme { get; set; } = new();

    /// <summary>Cloudflare: the edge-range refresh job and the origin-pull CA.</summary>
    public PaduraksaCloudflareOptions Cloudflare { get; set; } = new();

    /// <summary>The access log: the hot path's ring size, and what the formatter writes.</summary>
    public PaduraksaAccessLogOptions AccessLog { get; set; } = new();

    /// <summary>Switches for development and tests; off in a release build.</summary>
    public PaduraksaDevelopmentOptions Development { get; set; } = new();

    /// <summary>Log files: where the access and error logs live, how big they may get, and how much of them is kept.</summary>
    public PaduraksaLogOptions Logs { get; set; } = new();

    /// <summary>Metrics: how long the one-minute rollups behind the dashboard's longer ranges are kept, and how often they are written.</summary>
    public PaduraksaMetricsOptions Metrics { get; set; } = new();

    /// <summary>Traffic analytics: how long the five-minute and hourly rollups behind the Analytics page are kept, and how often they are written.</summary>
    public PaduraksaAnalyticsOptions Analytics { get; set; } = new();

    /// <summary>Notifications: the address the dashboard is reached at, for the links in a message.</summary>
    public PaduraksaNotificationOptions Notifications { get; set; } = new();

    /// <summary>MCP: whether the admin port serves <c>/mcp</c>.</summary>
    public PaduraksaMcpOptions Mcp { get; set; } = new();

    /// <summary>
    /// Total budget for the graceful stop of all three hosts together (public drain, then admin, then core). A third of it,
    /// at most 10 s, is kept for core's last flush; when the rest elapses, Kestrel aborts the remaining connections and admin
    /// stops without waiting (docs/design/hosting.md "Shutdown").
    /// Keep the supervisor's grace period above this, or the process gets killed mid-drain: Docker's default
    /// is 10 s, so set <c>stop_grace_period: 35s</c> in compose (or <c>docker stop -t 35</c>); systemd's
    /// <c>TimeoutStopSec</c> defaults to 90 s.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What <see cref="ConfigurationListOptions"/> had to ignore while normalising list settings (a scalar set beside
    /// indexed keys, where the scalar wins). Not bound from configuration; logged once at startup as a warning.
    /// </summary>
    internal List<string> ListBindingWarnings { get; } = [];
}

/// <summary>
/// Switches that exist for development and tests (<c>Paduraksa:Development</c>). They default to off in a release build, so a
/// production process can't turn one on by accident.
/// </summary>
internal sealed class PaduraksaDevelopmentOptions
{
#if DEBUG
    internal const bool IsDebugBuild = true;
#else
    internal const bool IsDebugBuild = false;
#endif

    /// <summary>
    /// Serve an in-memory self-signed placeholder certificate (<c>PlaceholderCertificates</c>): it is the public default
    /// certificate of a config that names none, the stand-in tests use for certificates they provide themselves, and a
    /// built-in inventory entry. <b>On by default in a Debug build, off in a release build</b>, where a host without a vault
    /// certificate gets none and an unknown name is rejected. Set it to <c>true</c> explicitly to run a release build
    /// with it (the process warns at startup). The admin listener's own certificate is separate and not affected.
    /// </summary>
    public bool PlaceholderCertificate { get; set; } = IsDebugBuild;

    /// <summary>
    /// Accept <c>http://</c> ACME URLs <b>of loopback hosts</b> (<c>AcmeClientOptions.AllowInsecureHttp</c>; any other host's <c>http://</c> URL is refused
    /// even so). ACME runs over HTTPS (RFC 8555 §6.1), so this exists for a CA test server on this machine and is off unless set; the
    /// process warns at startup when it is on.
    /// </summary>
    public bool AcmeInsecureHttp { get; set; }
}

/// <summary>
/// The access log (<c>Paduraksa:AccessLog</c>): the ring the hot path writes and the drain reads, and what the
/// formatted line may contain.
/// </summary>
internal sealed class PaduraksaAccessLogOptions
{
    /// <summary>
    /// Slots of the access-log ring (<c>Paduraksa:AccessLog:RingSize</c>): a power of two between 1 024 and 1 048 576,
    /// default 16 384. A slot is padded to three 64-byte cache lines (192 bytes), so the default is about 3 MiB. When the
    /// ring is full a line is dropped and counted, never waited for: size it for the worst burst the writer must not lose.
    /// </summary>
    public int RingSize { get; set; } = Proxy.Logging.AccessLogRing.DefaultCapacity;

    /// <summary>
    /// Entries the dashboard's live tail keeps (<c>Paduraksa:AccessLog:TailSize</c>): from 64 to 65 536, default 4 096. They are
    /// copies, capped and redacted like the file's lines. An entry is typically a few hundred bytes to a KiB (2 to 4 MiB for a full
    /// default buffer) and at most about 8 KiB with every field at its cap, so a full buffer is at worst about 32 MiB at the default
    /// and about 512 MiB at 65 536. A request a host keeps out of the file is not in it either.
    /// </summary>
    public int TailSize { get; set; } = Proxy.Logging.AccessLogTail.DefaultCapacity;

    /// <summary>
    /// Query parameters whose values never reach the log (<c>Paduraksa:AccessLog:SensitiveQueryKeys</c>, comma-separated or
    /// indexed): each is written as <c>name=REDACTED</c>. Empty or unset, the built-in list of well-known secret-carrying
    /// names applies; setting it replaces that list, so a deployment that needs the defaults too lists them.
    /// </summary>
    public IList<string> SensitiveQueryKeys { get; } = new List<string>();

    /// <summary>
    /// Write the query string in the access log (<c>Paduraksa:AccessLog:LogQueryString</c>, default true). Off, only the
    /// path is logged, whatever the sensitive list says.
    /// </summary>
    public bool LogQueryString { get; set; } = true;

    /// <summary>The formatter's options, from these settings.</summary>
    public Proxy.Logging.AccessLogFormatOptions ToFormatOptions() => new()
    {
        LogQueryString = LogQueryString,
        SensitiveQueryKeys = SensitiveQueryKeys.Count > 0 ? [.. SensitiveQueryKeys] : [.. Proxy.Logging.AccessLogFormatOptions.DefaultSensitiveQueryKeys],
    };
}

/// <summary>
/// Certificate startup settings (<c>Paduraksa:Certificates</c>). The inventory itself lives in the database; only the bounds of
/// startup work that touches the vault before the web apps serve are here.
/// </summary>
internal sealed class PaduraksaCertificateOptions
{
    /// <summary>
    /// How long the core host waits for the startup certificate recovery (registering what the vault holds after a lost database)
    /// before it continues in the background (1 second to 5 minutes; default 10 seconds). The recovery never stops the process.
    /// </summary>
    public TimeSpan RecoveryStartupTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// ACME settings (<c>Paduraksa:Acme</c>). Accounts, DNS providers and orders live in the database; what is here is what the process
/// needs before it can read it: whether the renewal scheduler runs, how it paces itself and which CA certificates its HTTPS
/// connections to a CA accept.
/// </summary>
internal sealed class PaduraksaAcmeOptions
{
    /// <summary>
    /// Run the renewal scheduler (resume unfinished orders, retry failures, renew, refresh ARI). On by default; turn it off to
    /// pause everything ACME does on its own (an operator can still issue and renew on request). Not reloadable.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// A PEM file (at most 1 MiB) of <b>self-signed root CA certificates</b> to trust, besides the system's, for the HTTPS connection to a CA's
    /// directory: the root of a private CA (step-ca) or of a test server (Pebble). An intermediate, a leaf or anything that is not a
    /// CA root in the file stops the process at startup. It widens the chain check only: the name must still match and the
    /// certificate must allow server authentication. A relative path resolves against the content root. Empty means the system's trust alone.
    /// </summary>
    public string? TrustedRootsFile { get; set; }

    /// <summary>How long after the process starts the scheduler's first look waits, so the listeners are up before it resumes or renews anything (see <c>AcmeIssuanceOptions.SchedulerStartDelay</c>). 0 to 1 hour.</summary>
    public TimeSpan SchedulerStartDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the scheduler looks for work (see <c>AcmeIssuanceOptions.SchedulerPollInterval</c>). 1 second to 1 day.</summary>
    public TimeSpan SchedulerPollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait after publishing DNS-01 TXT records before the CA is told to validate (see <c>AcmeIssuanceOptions.DnsPropagationDelay</c>). 0 to 1 hour.</summary>
    public TimeSpan DnsPropagationDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The DNS-01 propagation check (<c>Paduraksa:Acme:DnsPropagation</c>): before the CA is told to validate, the zone's own name
    /// servers are asked until each returns the TXT records. <c>Enabled</c> false keeps the fixed <see cref="DnsPropagationDelay"/>
    /// (a host that cannot send DNS to the zone's servers); <c>Resolvers</c> names the recursive resolvers that find them
    /// (<c>1.1.1.1</c>, <c>10.0.0.2:53</c>), the system's when empty. The check's timeout is 1 second to 1 hour, the intervals 0.1
    /// second to 10 minutes, a query 0.1 to 30 seconds, <c>UnreachableRounds</c> 1 to 20 and at most 8 resolvers. See <c>DnsPropagationSettings</c>.
    /// </summary>
    public DnsPropagationSettings DnsPropagation { get; set; } = new();
}

/// <summary>
/// Cloudflare settings (<c>Paduraksa:Cloudflare</c>): which hosts trust Cloudflare is config, but where the process gets
/// Cloudflare's published data is a deployment fact.
/// </summary>
internal sealed class PaduraksaCloudflareOptions
{
    /// <summary>The job that keeps Cloudflare's edge ranges current (<c>Paduraksa:Cloudflare:RangeRefresh</c>).</summary>
    public PaduraksaRangeRefreshOptions RangeRefresh { get; set; } = new();

    /// <summary>
    /// A PEM file (at most 1 MiB) of the certificate authority that signs Cloudflare's Authenticated Origin Pulls client
    /// certificate, replacing the one shipped with this build (whose copy expires 2029-11-01). Public certificates only. A
    /// relative path resolves against the content root. Empty means the shipped one.
    /// </summary>
    public string? OriginPullCaFile { get; set; }
}

/// <summary>
/// <c>Paduraksa:Cloudflare:RangeRefresh</c>: fetch Cloudflare's published edge ranges daily, so the trusted list isn't the one
/// this build shipped with. The URLs are Cloudflare's own; they can be pointed elsewhere for a test or a mirror. The checks a
/// list must pass, and what happens when a refresh fails, are fixed (see <c>CloudflareRangeRefresher</c>).
/// </summary>
internal sealed class PaduraksaRangeRefreshOptions
{
    private static readonly CloudflareRangeRefreshOptions Defaults = new();

    /// <summary>Run the job. On by default; off keeps the list this build shipped with (or the last one stored). Not reloadable.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The JSON endpoint: an <c>https</c> URL (an <c>http</c> one only for a loopback host).</summary>
    public Uri ApiUrl { get; set; } = Defaults.ApiUrl;

    /// <summary>The plain-text IPv4 list the job falls back to.</summary>
    public Uri TextV4Url { get; set; } = Defaults.TextV4Url;

    /// <summary>The plain-text IPv6 list the job falls back to.</summary>
    public Uri TextV6Url { get; set; } = Defaults.TextV6Url;

    /// <summary>How long after startup the first fetch waits (0 to 1 hour).</summary>
    public TimeSpan StartDelay { get; set; } = Defaults.StartDelay;

    /// <summary>The time between fetches (1 minute to 7 days), with up to an hour of jitter added.</summary>
    public TimeSpan Interval { get; set; } = Defaults.Interval;

    internal CloudflareRangeRefreshOptions ToOptions() => new()
    {
        ApiUrl = ApiUrl,
        TextV4Url = TextV4Url,
        TextV6Url = TextV6Url,
        StartDelay = StartDelay,
        Interval = Interval,
    };
}

/// <summary>
/// <c>Paduraksa:Admin</c>: how admin users sign in and how long that lasts. The defaults are the values Paduraksa ships with; the
/// validator holds each to sane bounds (a lockout of zero, or a session of a year, is a mistake, not a policy).
/// </summary>
internal sealed class AdminOptions
{
    /// <summary>Consecutive wrong passwords or codes before an account is locked (2 to 100).</summary>
    public int MaxFailedAttempts { get; set; } = new IdentityOptions().MaxFailedAttempts;

    /// <summary>How long a locked account refuses sign-in (1 minute to 24 hours).</summary>
    public TimeSpan LockoutDuration { get; set; } = new IdentityOptions().LockoutDuration;

    /// <summary>The absolute lifetime of a session (5 minutes to 30 days).</summary>
    public TimeSpan SessionLifetime { get; set; } = new IdentityOptions().SessionLifetime;

    /// <summary>A session unused for this long ends (1 minute up to <see cref="SessionLifetime"/>).</summary>
    public TimeSpan SessionIdleTimeout { get; set; } = new IdentityOptions().SessionIdleTimeout;

    /// <summary>How long a person has to give the second factor after the password (30 seconds to 30 minutes).</summary>
    public TimeSpan PartialSessionLifetime { get; set; } = new IdentityOptions().PartialSessionLifetime;

    /// <summary>The shortest password accepted, in characters (8 to 128). There are no composition rules.</summary>
    public int MinPasswordLength { get; set; } = new IdentityOptions().MinPasswordLength;

    /// <summary>PBKDF2-HMAC-SHA-512 iterations for new password hashes (100 000 to 10 000 000); older hashes are upgraded at sign-in.</summary>
    public int PasswordIterations { get; set; } = new IdentityOptions().PasswordIterations;

    /// <summary>The least interval between writes of a token's last-used or a session's last-seen time (1 second to 1 hour).</summary>
    public TimeSpan TouchInterval { get; set; } = new IdentityOptions().TouchInterval;

    /// <summary>The issuer name authenticator apps show (1 to 64 characters, no colon).</summary>
    public string TotpIssuer { get; set; } = new IdentityOptions().TotpIssuer;

    /// <summary>Sign-in rate limits (<c>Paduraksa:Admin:RateLimit</c>): per client IP and per username, on top of the account lockout.</summary>
    public AdminRateLimitOptions RateLimit { get; set; } = new();

    /// <summary>
    /// The largest request body the admin API accepts, in bytes (1 KiB to 16 MiB). 64 KiB suits every JSON request; an endpoint that takes
    /// larger bodies (a certificate or config upload) raises its own limit.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = AdminSecurityOptions.DefaultMaxRequestBodyBytes;

    /// <summary>
    /// Extra names the admin listener's certificate covers (<c>Paduraksa:Admin:CertificateNames</c>):
    /// DNS names and IP addresses the owner reaches the admin UI by. The process always adds <c>localhost</c>,
    /// <c>127.0.0.1</c>, <c>::1</c>, the machine's host name and a specific <c>Paduraksa:Listen:AdminBind</c> address, so
    /// this is only for additional names (an admin host name, say). A change re-issues the certificate under the same ID.
    /// Wildcards are refused (the certificate is served for exact names) and so is an IPv6 zone/scope id.
    /// </summary>
    public IList<string> CertificateNames { get; } = new List<string>();

    /// <summary>
    /// How long the core host waits for the admin certificate at startup (<c>Paduraksa:Admin:CertificateStartupTimeout</c>):
    /// every web app, the public proxy included, binds only after it, so this bounds how long a slow vault can
    /// delay the listeners. On timeout the certificate is prepared in the background and the admin port serves what it has
    /// (the vault's existing certificate, else the placeholder) until it is ready. 1 second to 5 minutes.
    /// </summary>
    public TimeSpan CertificateStartupTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often an open dashboard page re-checks its session in the background (<c>Paduraksa:Admin:SessionRecheckInterval</c>, 1 second
    /// to 5 minutes): a revoked or expired session, a disabled user or a password change elsewhere ends the page within this interval.
    /// </summary>
    public TimeSpan SessionRecheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    public IdentityOptions ToIdentityOptions() => new()
    {
        MaxFailedAttempts = MaxFailedAttempts,
        LockoutDuration = LockoutDuration,
        SessionLifetime = SessionLifetime,
        SessionIdleTimeout = SessionIdleTimeout,
        PartialSessionLifetime = PartialSessionLifetime,
        MinPasswordLength = MinPasswordLength,
        PasswordIterations = PasswordIterations,
        TouchInterval = TouchInterval,
        TotpIssuer = TotpIssuer,
    };

    public AdminSecurityOptions ToSecurityOptions(int loopbackHttpPort = 0, IReadOnlyList<string>? adminAllowedSources = null) => new()
    {
        LoopbackHttpPort = loopbackHttpPort,
        RateLimitPermitsPerIp = RateLimit.PermitsPerIp,
        RateLimitPermitsPerUser = RateLimit.PermitsPerUser,
        RateLimitWindow = RateLimit.Window,
        MaxRequestBodyBytes = MaxRequestBodyBytes,
        AdminAllowedSources = adminAllowedSources ?? [],
    };
}

/// <summary>
/// <c>Paduraksa:Admin:RateLimit</c>: how often credentials may be tried (login, second factor, setup), in fixed windows. A refusal is a 429
/// with <c>Retry-After</c>. It counts every attempt, successful or not, and is independent of the lockout (which counts failures per account).
/// </summary>
internal sealed class AdminRateLimitOptions
{
    /// <summary>Attempts, successful or not, one client may make per <see cref="Window"/> (1 to 10 000). An IPv6 client is one /64.</summary>
    public int PermitsPerIp { get; set; } = new AdminSecurityOptions().RateLimitPermitsPerIp;

    /// <summary>Failed attempts against one username per <see cref="Window"/> (1 to 1000); successful ones do not count.</summary>
    public int PermitsPerUser { get; set; } = new AdminSecurityOptions().RateLimitPermitsPerUser;

    /// <summary>The window (1 second to 10 minutes): memory held per client and username lives no longer than this.</summary>
    public TimeSpan Window { get; set; } = new AdminSecurityOptions().RateLimitWindow;
}

internal sealed class PaduraksaListenOptions
{
    /// <summary>Public plain-HTTP port (H1). 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Http { get; set; } = 80;

    /// <summary>
    /// Public HTTPS port: H1/H2 over TCP and, with <see cref="Http3"/>, H3 over UDP on the same number. 0 (OS-assigned)
    /// only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests), and never with HTTP/3.
    /// </summary>
    public int Https { get; set; } = 443;

    /// <summary>
    /// The HTTPS port clients reach: advertised in <c>Alt-Svc</c> and used by the HTTP→HTTPS redirect.
    /// Differs from <see cref="Https"/> only behind a port mapping (e.g. bound to 8443, published as 443).
    /// </summary>
    public int PublicHttpsPort { get; set; } = 443;

    /// <summary>
    /// Serve HTTP/3 (QUIC on UDP <see cref="Https"/>). On by default; off, with an Information log, where libmsquic isn't installed.
    /// </summary>
    public bool Http3 { get; set; } = true;

    /// <summary>PROXY protocol on the public HTTP listener. Off by default.</summary>
    public ProxyProtocolListenOptions HttpProxyProtocol { get; set; } = new();

    /// <summary>PROXY protocol on the public HTTPS TCP listener, read before TLS. QUIC has none. Off by default.</summary>
    public ProxyProtocolListenOptions HttpsProxyProtocol { get; set; } = new();

    /// <summary>Admin HTTPS port. 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Admin { get; set; } = 9443;

    public string AdminBind { get; set; } = "0.0.0.0";

    /// <summary>
    /// The admin app's internal listener, through which the public proxy reaches it: a Unix socket at
    /// <c>&lt;data dir&gt;/run/admin.sock</c> (0600, in a 0700 directory), or <c>127.0.0.1:&lt;ephemeral&gt;</c> where one can't be
    /// bound. It is <b>on by default</b> and independent of the config, so creating an admin host later needs no restart. Off:
    /// no admin host can compile (a config with one is refused). The listener carries no TLS: it is process-local.
    /// </summary>
    public bool AdminInternal { get; set; } = true;

    /// <summary>
    /// Addresses and CIDR blocks allowed to connect to the admin listener (e.g. <c>192.168.1.0/24</c>); empty means no restriction.
    /// Loopback is <b>not</b> implied: list <c>127.0.0.1/32</c> or <c>::1/128</c> to allow it (a proxy on this machine would otherwise let
    /// every client in as loopback). Enforced per connection, before TLS; the loopback fallback listener is not subject to it.
    /// IPv4-mapped IPv6 blocks are refused: write the IPv4 block.
    /// </summary>
    public IList<string> AdminAllowedSources { get; } = new List<string>();

    /// <summary>
    /// Opt-in plain-HTTP admin listener on this port, bound to loopback only (127.0.0.1 and ::1; there is no setting to bind anything
    /// else). For headless setups reached through an SSH tunnel. Same app, same authentication; the session cookie is still sent
    /// <c>Secure</c>, which Chromium and Firefox accept for <c>http://localhost</c> and <c>http://127.0.0.1</c> (Safari does not).
    /// 0 turns it off (the default).
    /// </summary>
    public int AdminLoopbackHttp { get; set; }
}

/// <summary>
/// PROXY protocol (v1 and v2) on one public TCP listener. Enabling it makes the header <b>mandatory</b> on that listener:
/// connections from outside <see cref="TrustedSources"/> are dropped unread, and trusted ones must send a valid header
/// within <see cref="HeaderTimeout"/>.
/// </summary>
internal sealed class ProxyProtocolListenOptions
{
    public bool Enabled { get; set; }

    /// <summary>Addresses or CIDRs of the load balancers allowed to connect (required when enabled).</summary>
    public IList<string> TrustedSources { get; } = new List<string>();

    /// <summary>How long a trusted source has to send the whole header (default 3 s, at most 1 min).</summary>
    public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Log files (<c>Paduraksa:Logs</c>): the access log and the process's own error log, both on disk under one directory, rotated
/// by size and by day and trimmed by count, age and total size. The options here are the same for both logs; only the error log has a level
/// of its own, because it is the only one the process writes itself.
/// </summary>
internal sealed class PaduraksaLogOptions
{
    /// <summary>
    /// Where the logs live. Empty means <c>&lt;data dir&gt;/logs</c>; a relative path resolves against the data directory (never the working
    /// directory). The directory and its files are owner-only on Unix (0700/0600): the access log holds client IPs.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// The lowest level the error log keeps (<c>Information</c>, <c>Warning</c>, <c>Error</c> or <c>Critical</c>; default <c>Warning</c>).
    /// Independent of the console's <c>Logging:LogLevel</c>: this is the file's own filter.
    /// </summary>
    public LogLevel ErrorLevel { get; set; } = LogLevel.Warning;

    /// <summary>Rotate when the next line would push a file past this (default 64 MiB; 1 KiB to 16 GiB).</summary>
    public long MaxFileSize { get; set; } = 64L * 1024 * 1024;

    /// <summary>How many rotated files each log keeps, newest first (default 30; 1 to 10 000).</summary>
    public int RetainedFiles { get; set; } = 30;

    /// <summary>Delete rotated files older than this (default 30 days; 1 minute to 10 years).</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Delete the oldest rotated files until each log's rotated files total at most this (default 1 GiB; 1 MiB to 1 TiB).</summary>
    public long MaxTotalSize { get; set; } = 1024L * 1024 * 1024;

    /// <summary>The logs directory after <see cref="Hosting.PaduraksaHost"/> resolved it: always absolute once the options are validated.</summary>
    internal string ResolvedDirectory { get; set; } = string.Empty;
}


/// <summary>
/// Metrics (<c>Paduraksa:Metrics</c>): the counters themselves are always on and cost nothing to configure; these set what is kept
/// of them. The sampler reads them every second into memory (an hour of ten-second buckets), and the rollup job writes each finished
/// minute to the config database, where the overview's day, week and month ranges read it.
/// </summary>
internal sealed class PaduraksaMetricsOptions
{
    /// <summary>How long a minute is kept (default 14 days; 1 hour to 5 years). Older rows are deleted hourly.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How often finished minutes are written to the database (default 15 s; 1 s to 10 min). A restart loses the minute in progress and whatever was waiting.</summary>
    public TimeSpan WriteInterval { get; set; } = TimeSpan.FromSeconds(15);

    public Application.Metrics.MetricRollupOptions ToOptions() => new() { Retention = Retention, WriteInterval = WriteInterval };
}


/// <summary>
/// MCP (<c>Paduraksa:Mcp</c>, docs/design/admin-surfaces.md "MCP in code mode (M10)"). The tools run a model's JavaScript in a child process the server starts, bounds from outside and kills on a breach; the operator opts in.
/// </summary>
internal sealed class PaduraksaMcpOptions
{
    /// <summary>Whether <c>/mcp</c> is mapped on the admin port (default false: it answers 404 and the dashboard says how to turn it on).</summary>
    public bool Enabled { get; set; }

    /// <summary>How long a sandbox child waits without a run before it is despawned (default 10 min; 10 s to 24 h). The next call starts a new one.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
}


/// <summary>
/// Traffic analytics (<c>Paduraksa:Analytics</c>): computed in the access-log drain, so a host with its access log off is in neither. These set
/// what is kept of it: the drain closes a five-minute bucket per host, the job writes it to the config database, and an hour that has passed is
/// rolled up into one row for the longer ranges.
/// </summary>
internal sealed class PaduraksaAnalyticsOptions
{
    /// <summary>How long a stretch is kept (default 30 days; 1 hour to 5 years). Older rows are deleted hourly. The five-minute rows are kept two days once rolled up.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How often closed buckets are written to the database (default 15 s; 1 s to 10 min). A crash loses the bucket in progress and whatever was waiting; a graceful stop writes them.</summary>
    public TimeSpan WriteInterval { get; set; } = TimeSpan.FromSeconds(15);

    public Application.Analytics.AnalyticsOptions ToOptions() => new() { Retention = Retention, WriteInterval = WriteInterval };
}


/// <summary>
/// Notifications (<c>Paduraksa:Notifications</c>). The channels themselves are managed in the dashboard (Settings, Notifications) and live in the
/// database; this is the one thing a message needs that only the operator knows.
/// </summary>
internal sealed class PaduraksaNotificationOptions
{
    /// <summary>
    /// The address the dashboard is reached at (<c>https://admin.example.com</c>, no path or query): messages carry a link to the page that
    /// shows what happened. Empty sends no link, since the process cannot know which name or port its admin is reached by.
    /// </summary>
    public string? AdminUrl { get; set; }

    /// <summary>
    /// How long a destination's new health state must hold before it is reported (default 10 s; 1 s to 10 min): the debounce that keeps one failed
    /// probe or a lapsing passive verdict from sending anything.
    /// </summary>
    public TimeSpan HealthHold { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a channel waits after a message for more of the same kind before sending, so a storm (ten upstreams down at once) is one digest
    /// (default 3 s; 0 to 1 min).
    /// </summary>
    public TimeSpan CoalesceWindow { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A delivery still waiting for an event older than this when the process starts is expired, not sent (default 1 hour; 1 minute to 7 days), so a long outage
    /// does not end in a burst of stale "upstream down" messages. Applied at start only: a retry in a running process finishes inside its own schedule (about 21 minutes).
    /// </summary>
    public TimeSpan MaxDeliveryAge { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a finished event and its deliveries are kept for the notification history (default 14 days; 1 hour to 5 years). Older ones are deleted hourly.</summary>
    public TimeSpan HistoryRetention { get; set; } = TimeSpan.FromDays(14);

    public Application.Notifications.NotificationOptions ToOptions() => new()
    {
        AdminUrl = AdminUrl is { Length: > 0 } url ? new Uri(url.TrimEnd('/') + "/") : null,
        HealthHold = HealthHold,
        CoalesceWindow = CoalesceWindow,
        MaxDeliveryAge = MaxDeliveryAge,
        HistoryRetention = HistoryRetention,
    };
}

/// <summary>
/// The public listeners' slow-client and connection limits (<c>Paduraksa:Limits</c>), set on Kestrel explicitly at startup (not
/// reloadable). The defaults are <see cref="ServerLimits.Default"/>; each setting is described on
/// <see cref="ServerLimits"/> and in docs/install.md. The admin listener keeps Kestrel's own limits.
/// </summary>
internal sealed class PaduraksaLimitsOptions
{
    public TimeSpan RequestHeadersTimeout { get; set; } = ServerLimits.Default.RequestHeadersTimeout;

    public TimeSpan KeepAliveTimeout { get; set; } = ServerLimits.Default.KeepAliveTimeout;

    public TimeSpan TlsHandshakeTimeout { get; set; } = ServerLimits.Default.TlsHandshakeTimeout;

    public long MinRequestBodyRate { get; set; } = ServerLimits.Default.MinRequestBodyRate;

    public TimeSpan MinRequestBodyGrace { get; set; } = ServerLimits.Default.MinRequestBodyGrace;

    public long MinResponseRate { get; set; } = ServerLimits.Default.MinResponseRate;

    public TimeSpan MinResponseGrace { get; set; } = ServerLimits.Default.MinResponseGrace;

    public long MaxConnections { get; set; } = ServerLimits.Default.MaxConnections;

    public long MaxUpgradedConnections { get; set; } = ServerLimits.Default.MaxUpgradedConnections;

    public int Http2MaxStreamsPerConnection { get; set; } = ServerLimits.Default.Http2MaxStreamsPerConnection;

    public ServerLimits ToLimits() => new()
    {
        RequestHeadersTimeout = RequestHeadersTimeout,
        KeepAliveTimeout = KeepAliveTimeout,
        TlsHandshakeTimeout = TlsHandshakeTimeout,
        MinRequestBodyRate = MinRequestBodyRate,
        MinRequestBodyGrace = MinRequestBodyGrace,
        MinResponseRate = MinResponseRate,
        MinResponseGrace = MinResponseGrace,
        MaxConnections = MaxConnections,
        MaxUpgradedConnections = MaxUpgradedConnections,
        Http2MaxStreamsPerConnection = Http2MaxStreamsPerConnection,
    };
}
