using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// A throwaway certificate authority standing in for Cloudflare's origin-pull CA: it issues the client certificates the
/// edge would present, valid, expired or from another authority altogether (a second instance).
/// </summary>
/// <remarks>Test-only key handling, like <see cref="TestPki"/>: the one <c>CopyWithPrivateKey</c> is suppressed in place.</remarks>
internal sealed class TestOriginPullCa : IDisposable
{
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    private readonly List<IDisposable> _owned = [];

    private TestOriginPullCa(X509Certificate2 certificate, ECDsa key)
    {
        Certificate = certificate;
        _owned.Add(certificate);
        _owned.Add(key);
    }

    /// <summary>The authority's own certificate: what the proxy trusts.</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>The roots to hand the compiler, as <c>SnapshotCompilerOptions.OriginPullCa</c> takes them.</summary>
    public X509Certificate2Collection Roots => [Certificate];

    public static TestOriginPullCa Create(string name = "Test Origin Pull CA")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(30));
        return new TestOriginPullCa(certificate, key);
    }

    /// <summary>A client certificate this authority signed, valid from <paramref name="notBefore"/> to <paramref name="notAfter"/>
    /// (a day ago to tomorrow by default).</summary>
    public X509Certificate2 IssueClient(string commonName, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], false));
        using var signed = request.Create(
            Certificate,
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
#pragma warning disable RS0030 // Test CA: attaches an in-memory key generated just above; never exported or persisted.
        var client = signed.CopyWithPrivateKey(key);
#pragma warning restore RS0030
        _owned.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }
}
