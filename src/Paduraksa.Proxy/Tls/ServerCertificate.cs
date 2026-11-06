using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// A certificate ready for serving: a prebuilt <see cref="SslStreamCertificateContext"/> (leaf + intermediates).
/// The Vault's <c>IServerCertificateSource</c> (PLAN §8.2, M3) supplies the context; the proxy never loads or exports
/// keys, it only holds what it's given. The proxy doesn't own the certificate either: whoever created it disposes it.
/// </summary>
/// <remarks>
/// Options are shared per context instance: two <see cref="ServerCertificate"/>s wrapping the same context count as
/// one certificate when the <see cref="CertificateTable"/> deduplicates.
/// </remarks>
public sealed class ServerCertificate
{
    public ServerCertificate(SslStreamCertificateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    public SslStreamCertificateContext Context { get; }

    public X509Certificate2 Leaf => Context.TargetCertificate;

    /// <summary>
    /// Builds the context from an already-loaded leaf (with its private key) and its intermediates, offline, so no
    /// AIA fetch ever happens: neither now nor during a handshake (PLAN §9, spike S1). This is the only way the
    /// intermediates are reliably sent on both TCP and QUIC.
    /// </summary>
    public static ServerCertificate Create(X509Certificate2 leaf, X509Certificate2Collection? intermediates)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        return new ServerCertificate(SslStreamCertificateContext.Create(leaf, intermediates, offline: true));
    }
}
