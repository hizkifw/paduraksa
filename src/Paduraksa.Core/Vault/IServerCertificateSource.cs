using System.Net.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The one sanctioned egress for serving: Kestrel needs the private key in-process, so the vault hands out
/// a prebuilt <see cref="SslStreamCertificateContext"/> for this purpose. Together with <see cref="IClientCertificateSource"/>
/// it is the only Core contract that may expose a certificate object.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the guard is.</b> The context's <c>TargetCertificate</c> is the leaf <i>with</i> its key, so the type itself does
/// not stop a recipient from exporting it. What does is the key-material ban list (<c>BannedSymbols.KeyMaterial.txt</c>:
/// <c>GetRSAPrivateKey</c>, <c>Export</c>, <c>ExportPkcs12</c>, <c>CopyWithPrivateKey</c> and the rest) enforced by the analyzer in every
/// project except Vault, and the rule that only the composition of Application and Server ever holds the context.
/// </para>
/// <para>
/// <b>Why async.</b> Loading a certificate reads the vault database and unwraps a key, which is I/O. So this runs on the
/// cold path only: Application loads every certificate a binding can choose before a compile and hands the compiler a
/// synchronous resolver over the map, so no load happens while a request is in flight. A certificate that cannot be
/// loaded (<see langword="null"/>) is left out of the inventory the compiler sees (and logged as a warning), so a binding
/// to it degrades to a compile warning and the default certificate rather than a failed handshake or compile.
/// </para>
/// <para>
/// <b>Instance identity and lifetime.</b> For an unchanged certificate the vault returns the <i>same</i> context instance on
/// every call, and a new instance only after the certificate or its chain changed. The proxy's certificate table
/// deduplicates by instance, so this keeps one options object per certificate across compiles and makes a renewal take
/// effect with the next published snapshot. <b>The vault never disposes an instance it has handed out</b>, and callers
/// must not either: a replaced instance is dropped and left to the garbage collector, which is safe for handshakes still
/// using it.
/// </para>
/// </remarks>
public interface IServerCertificateSource
{
    /// <summary>
    /// Returns the serving context (leaf with its private key, plus intermediates, built offline so no chain building
    /// or AIA fetch happens during a handshake) for <paramref name="id"/>.
    /// </summary>
    /// <returns>
    /// The context, or <see langword="null"/> when the certificate is unknown, has no key, or has no chain attached yet
    /// (a pending ACME order).
    /// </returns>
    ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken);
}
