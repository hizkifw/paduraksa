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
}
