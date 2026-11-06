using System.Diagnostics.CodeAnalysis;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Turns a certificate <i>reference</i> from the config (a certificate ID or name) into a ready-to-serve
/// <see cref="ServerCertificate"/> at snapshot compile time (cold path). The proxy never loads or exports keys: whoever
/// implements this does (the Vault's <c>IServerCertificateSource</c> from M3; a placeholder in the server until then).
/// </summary>
/// <remarks>
/// Return the <b>same</b> <see cref="ServerCertificate"/> instance for an unchanged certificate across compiles. The
/// certificate table deduplicates by context instance, and an equal instance keeps sharing one options object per
/// transport; a new instance (a renewed or replaced certificate) is picked up by the next published snapshot.
/// </remarks>
public interface IServerCertificateResolver
{
    /// <summary>Finds the certificate for <paramref name="reference"/>; false if there is none.</summary>
    bool TryResolve(string reference, [NotNullWhen(true)] out ServerCertificate? certificate);
}
