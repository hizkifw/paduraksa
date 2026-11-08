namespace Paduraksa.Admin.Certificates;

/// <summary>
/// The local CA root the process is serving right now (PLAN §3, §12.1). The core host's <c>AdminCertificateService</c>
/// publishes it on every run and the download endpoints read it, so an anonymous request costs no vault read and no PEM
/// parsing; it is cleared when the vault no longer holds a root. Null until a root exists.
/// </summary>
public sealed class CaRootSource
{
    private volatile CaRoot? _current;

    /// <summary>The root as last seen by the process, or null when there is none (yet).</summary>
    public CaRoot? Current => _current;

    /// <summary>Publishes the root (null clears it); a later request sees it at once.</summary>
    public void Publish(CaRoot? root) => _current = root;
}
