using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// What <see cref="SnapshotCompiler"/> needs besides the config document: deployment facts (listener layout, public
/// ports) and the seams that turn references into runtime objects. Built once per process from the server's options;
/// immutable, so tests derive variants with <c>with</c>.
/// </summary>
public sealed record SnapshotCompilerOptions
{
    /// <summary>443, no certificate resolver.</summary>
    public static SnapshotCompilerOptions Default { get; } = new();

    /// <summary>
    /// Resolves the certificate references of hosts and of the default certificate. Null means no certificates can be
    /// resolved: a config that references one fails to compile, one without them compiles to an empty TLS table.
    /// </summary>
    public IServerCertificateResolver? Certificates { get; init; }

    /// <summary>
    /// The HTTPS port clients use (<c>Paduraksa:Listen:PublicHttpsPort</c>), which differs from the bound port behind a
    /// port mapping (PLAN §7.5). Used for the HTTP→HTTPS redirect.
    /// </summary>
    public int PublicHttpsPort
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 65535);
            field = value;
        }
    } = 443;

    /// <summary>
    /// Whether the HTTP/3 listener exists (enabled and libmsquic present). When true, every host served over HTTPS with
    /// HTTP/3 left on gets an <c>Alt-Svc</c> advertising <see cref="PublicHttpsPort"/> (PLAN §7.5).
    /// </summary>
    public bool AdvertiseHttp3 { get; init; }

    /// <summary>
    /// Cloudflare's edge ranges for <see cref="TrustedProxyMode.CloudflareProxy"/>; null for the baked-in
    /// <see cref="CloudflareIpRanges.Default"/>. The range refresh job (M6) passes its latest list here; tests use it to
    /// put loopback "at the edge".
    /// </summary>
    public CidrSet? CloudflareEdgeRanges { get; init; }
}
