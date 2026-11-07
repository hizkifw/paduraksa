using System.Net.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The second sanctioned egress (PLAN §8.2): upstream mutual TLS. The proxy's <c>SocketsHttpHandler</c> needs the client
/// certificate with its private key in-process, so the vault hands it out for that one purpose, as the prebuilt context
/// that goes into <c>SslClientAuthenticationOptions.ClientCertificateContext</c>. Returning the same
/// <see cref="SslStreamCertificateContext"/> as <see cref="IServerCertificateSource"/> (instead of a bare certificate)
/// means the chain is built once, offline, and the client certificate is sent with its intermediates.
/// </summary>
/// <remarks>
/// Same flow, guard, instance identity and lifetime rules as <see cref="IServerCertificateSource"/>: Application loads
/// the certificates an upstream names (its <c>CertificateId</c>, PLAN §5.1) on the cold path before a compile and passes the
/// compiler a map, so no loading happens while a request is in flight. The same instance comes back for an unchanged
/// certificate. <b>The vault never disposes a handed-out instance</b> and callers must not; a replaced one is dropped and
/// collected, and connections still using it are unaffected.
/// </remarks>
public interface IClientCertificateSource
{
    /// <summary>Returns the client context (leaf with its private key, plus intermediates) for <paramref name="id"/>, or <see langword="null"/> if the certificate is unknown, has no key, or has no chain yet.</summary>
    ValueTask<SslStreamCertificateContext?> LoadForUpstreamAsync(CertificateId id, CancellationToken cancellationToken);
}
