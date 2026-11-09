using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// TEMPORARY: an in-memory self-signed certificate so the public HTTPS and admin listeners have
/// something to serve before the vault exists. Replaced by <c>IServerCertificateSource</c> (public, per-SNI)
/// and the internal-CA admin certificate from <c>Paduraksa.Vault</c>.
/// The key is generated in memory and never persisted or exported (except the Windows-only round trip below).
/// </summary>
internal sealed class PlaceholderServerCertificate : IDisposable
{
    private static readonly Oid ServerAuthOid = new("1.3.6.1.5.5.7.3.1", "Server Authentication");

    private PlaceholderServerCertificate(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    public static PlaceholderServerCertificate Create(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=Paduraksa placeholder", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuthOid], critical: false));

        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = timeProvider.GetUtcNow();
        X509Certificate2 certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(365));

        if (OperatingSystem.IsWindows())
        {
            // SChannel can't serve TLS from an ephemeral CNG key, so on Windows the certificate has to be
            // round-tripped through PKCS#12. Narrowly scoped exception to the key-material ban; the vault
            // takes over and this helper is deleted.
#pragma warning disable RS0030 // TODO: remove with the vault-backed IServerCertificateSource.
            using (certificate)
            {
                return new PlaceholderServerCertificate(
                    X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null));
            }
#pragma warning restore RS0030
        }

        return new PlaceholderServerCertificate(certificate);
    }

    public void Dispose() => Certificate.Dispose();
}
