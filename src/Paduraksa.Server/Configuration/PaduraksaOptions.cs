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

    /// <summary>
    /// Total budget for the graceful stop of all three hosts together (public drain, then admin, then core).
    /// When it elapses, Kestrel aborts the remaining connections and the rest stops without waiting.
    /// Keep the supervisor's grace period above this, or the process gets killed mid-drain: Docker's default
    /// is 10 s, so set <c>stop_grace_period: 35s</c> in compose (or <c>docker stop -t 35</c>); systemd's
    /// <c>TimeoutStopSec</c> defaults to 90 s.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

internal sealed class PaduraksaListenOptions
{
    /// <summary>Public plain-HTTP port (H1). 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Http { get; set; } = 80;

    /// <summary>Public HTTPS port (H1/H2 over TCP). 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Https { get; set; } = 443;

    /// <summary>Admin HTTPS port. 0 (OS-assigned) only with <c>PaduraksaHostOptions.AllowEphemeralPorts</c> (tests).</summary>
    public int Admin { get; set; } = 9443;

    /// <summary>Bind address of the admin listener (§3: configurable).</summary>
    public string AdminBind { get; set; } = "0.0.0.0";
}
