// Linked into the test projects that need a throwaway server certificate (<Compile Include Link>), so it compiles into each
// assembly as an internal type of its own.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Shared;

internal static class SelfSigned
{
    /// <summary>
    /// A self-signed ECDSA P-256 certificate valid from a day ago to a day ahead, covering <paramref name="dnsNames"/>, with the
    /// server-authentication usage when <paramref name="serverAuth"/>. The caller owns it.
    /// </summary>
    public static X509Certificate2 Create(string subject, string[]? dnsNames = null, bool serverAuth = false)
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback listener; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
#pragma warning restore RS0030
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256);
        if (dnsNames is not null)
        {
            SubjectAlternativeNameBuilder names = new();
            foreach (var name in dnsNames)
            {
                names.AddDnsName(name);
            }

            request.CertificateExtensions.Add(names.Build());
        }

        if (serverAuth)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
