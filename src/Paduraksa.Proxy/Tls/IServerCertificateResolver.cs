using System.Diagnostics.CodeAnalysis;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Turns a certificate ID from the config into a ready-to-serve <see cref="ServerCertificate"/> at snapshot compile
/// time (cold path). The proxy never loads or exports keys: whoever implements this does (the Vault's
/// <c>IServerCertificateSource</c>).
/// </summary>
/// <remarks>
/// Return the <b>same</b> <see cref="ServerCertificate"/> instance for an unchanged certificate across compiles. The
/// certificate table deduplicates by context instance, and an equal instance keeps sharing one options object per
/// transport; a new instance (a renewed or replaced certificate) is picked up by the next published snapshot.
/// </remarks>
public interface IServerCertificateResolver
{
    /// <summary>Finds the certificate with ID <paramref name="id"/>; false if there is none.</summary>
    bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate);
}
