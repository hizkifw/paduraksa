using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Proxy.Testing;

/// <summary>Throwaway server certificates for loopback listeners that no client validates.</summary>
internal static class TestCertificates
{
    private const string Subject = "O=Paduraksa test";

    /// <summary>Whether a server presented one of these: what a test client checks instead of a chain.</summary>
    public static bool IsTestCertificate(X509Certificate? certificate) => certificate?.Subject.EndsWith(Subject, StringComparison.Ordinal) == true;

    public static X509Certificate2 SelfSigned(string name = "localhost")
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback listener; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}, {Subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning restore RS0030
    }
}
