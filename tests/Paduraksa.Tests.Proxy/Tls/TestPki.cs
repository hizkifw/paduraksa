using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// Throwaway three-level PKI for tests: root CA → intermediate CA → leaves (ECDSA P-256, no AIA extension), so the
/// only way a client can see the intermediate is if the server sends it (spike S1).
/// </summary>
/// <remarks>
/// Test-only key handling. Leaves are signed by the intermediate, so the private key has to be attached with
/// <see cref="ECDsaCertificateExtensions.CopyWithPrivateKey"/>. The key-material ban list
/// (<c>BannedSymbols.KeyMaterial.txt</c>) covers <c>X509Certificate2.CopyWithPrivateKey</c> but not this extension
/// method, so no suppression is needed; nothing here loads, exports or persists a key.
/// </remarks>
internal sealed class TestPki : IDisposable
{
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    private readonly ECDsa _intermediateKey;
    private readonly List<IDisposable> _owned = [];

    private TestPki(X509Certificate2 root, X509Certificate2 intermediate, ECDsa intermediateKey)
    {
        Root = root;
        Intermediate = intermediate;
        _intermediateKey = intermediateKey;
        _owned.Add(root);
        _owned.Add(intermediate);
        _owned.Add(intermediateKey);
    }

    public X509Certificate2 Root { get; }

    /// <summary>The intermediate CA certificate (public part only).</summary>
    public X509Certificate2 Intermediate { get; }

    public static TestPki Create(string name = "Paduraksa Test")
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = CaRequest($"CN={name} Root CA", rootKey);
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = CaRequest($"CN={name} Intermediate CA", intermediateKey);
        var intermediate = intermediateRequest.Create(
            root, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), RandomNumberGenerator.GetBytes(16));

        return new TestPki(root, intermediate, intermediateKey);
    }

    /// <summary>A leaf for <paramref name="dnsNames"/>, wrapped with the intermediate as chain (offline context).</summary>
    public ServerCertificate Leaf(string commonName, params string[] dnsNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], false));

        var generator = X509SignatureGenerator.CreateForECDsa(_intermediateKey);
        using var signed = request.Create(
            Intermediate.SubjectName,
            generator,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(28),
            RandomNumberGenerator.GetBytes(16));
        var leaf = signed.CopyWithPrivateKey(key);
        _owned.Add(leaf);
        return ServerCertificate.Create(leaf, [Intermediate]);
    }

    /// <summary>A self-signed client certificate (clientAuth EKU), e.g. to stand in for Cloudflare's origin-pull cert.</summary>
    public X509Certificate2 ClientCertificate(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], false));
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _owned.Add(certificate);
        return certificate;
    }

    public void Dispose()
    {
        foreach (var owned in _owned)
        {
            owned.Dispose();
        }
    }

    private static CertificateRequest CaRequest(string subject, ECDsa key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }
}
