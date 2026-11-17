using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Integration;

internal static class CertificateFingerprints
{
    public static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
}
