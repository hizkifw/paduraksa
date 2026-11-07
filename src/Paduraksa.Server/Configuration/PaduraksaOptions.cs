namespace Paduraksa.Server.Configuration;

/// <summary>
/// Bootstrap settings bound from the <c>Paduraksa</c> configuration section (appsettings, environment).
/// Everything else (hosts, routes, certificates, ...) lives in the database from M2 on.
/// Bound and validated once per process; not reloadable (listeners can't move at runtime).
/// </summary>
internal sealed class PaduraksaOptions
{
    public const string SectionName = "Paduraksa";

    /// <summary>
    /// Directory holding the databases, the KEK and runtime files (§16: the one volume).
    /// A relative path is resolved against the content root (the directory holding <c>appsettings.json</c>,
    /// by default the app's base directory) when the options are bound, never against the process's working
    /// directory; after binding this is always a fully qualified path. Absolute paths are used as given.
    /// A leading <c>~</c> is rejected: nothing expands it, so it would silently become a directory named <c>~</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "./data";

    public PaduraksaListenOptions Listen { get; set; } = new();

    /// <summary>Switches for development and tests; off in a release build.</summary>
    public PaduraksaDevelopmentOptions Development { get; set; } = new();

    /// <summary>
    /// Total budget for the graceful stop of all three hosts together (public drain, then admin, then core).
    /// When it elapses, Kestrel aborts the remaining connections and the rest stops without waiting.
    /// Keep the supervisor's grace period above this, or the process gets killed mid-drain: Docker's default
    /// is 10 s, so set <c>stop_grace_period: 35s</c> in compose (or <c>docker stop -t 35</c>); systemd's
    /// <c>TimeoutStopSec</c> defaults to 90 s.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
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
    /// The HTTPS port clients reach (PLAN §7.5, §16): advertised in <c>Alt-Svc</c> and used by the HTTP→HTTPS redirect.
    /// Differs from <see cref="Https"/> only behind a port mapping (e.g. bound to 8443, published as 443).
    /// </summary>
    public int PublicHttpsPort { get; set; } = 443;

    /// <summary>
    /// Serve HTTP/3 (QUIC on UDP <see cref="Https"/>). On by default; off, with an Information log, where libmsquic isn't installed.
    /// </summary>
    public bool Http3 { get; set; } = true;

    /// <summary>PROXY protocol on the public HTTP listener (PLAN §7.2 A). Off by default.</summary>
    public ProxyProtocolListenOptions HttpProxyProtocol { get; set; } = new();

    /// <summary>PROXY protocol on the public HTTPS TCP listener, read before TLS. QUIC has none. Off by default.</summary>
    public ProxyProtocolListenOptions HttpsProxyProtocol { get; set; } = new();

    /// <summary>Admin HTTPS port. 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Admin { get; set; } = 9443;

    /// <summary>Bind address of the admin listener (§3: configurable).</summary>
    public string AdminBind { get; set; } = "0.0.0.0";
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
