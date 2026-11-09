using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// What <see cref="SnapshotCompiler"/> needs besides the config document: deployment facts (listener layout, public
/// ports) and the seams that turn IDs into runtime objects. Built once per process from the server's options;
/// immutable, so tests derive variants with <c>with</c>.
/// </summary>
public sealed record SnapshotCompilerOptions
{
    /// <summary>443, no certificate resolver.</summary>
    public static SnapshotCompilerOptions Default { get; } = new();

    /// <summary>Resolves the certificate IDs of hosts and of the default certificate. Null: none can be resolved, so a
    /// config that names one fails to compile, and one without them compiles to an empty TLS table.</summary>
    public IServerCertificateResolver? Certificates { get; init; }

    /// <summary>The HTTPS port clients use (<c>Paduraksa:Listen:PublicHttpsPort</c>), which differs from the bound
    /// port behind a port mapping.</summary>
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
    /// HTTP/3 left on gets an <c>Alt-Svc</c> advertising <see cref="PublicHttpsPort"/>.
    /// </summary>
    public bool AdvertiseHttp3 { get; init; }

    /// <summary>
    /// Whether an admin host's <c>__admin</c> cluster can be compiled: true when the admin app's internal listener
    /// exists (<c>Paduraksa:Listen:AdminInternal</c>). False: a config with an admin host is refused, with that reason.
    /// </summary>
    public bool AdminUpstream { get; init; }

    /// <summary>Cloudflare's edge ranges for <see cref="Paduraksa.Core.Model.TrustedProxyMode.CloudflareProxy"/>;
    /// null for the baked-in <see cref="CloudflareIpRanges.Default"/>.</summary>
    public CidrSet? CloudflareEdgeRanges { get; init; }
}
