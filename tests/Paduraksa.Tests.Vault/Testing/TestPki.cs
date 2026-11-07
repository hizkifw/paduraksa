using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Paduraksa.Tests.Vault.Testing;

/// <summary>A small certificate authority for the tests: stands in for an ACME CA or for whoever issued an uploaded certificate.</summary>
internal sealed class TestCa : IDisposable
{
    private readonly ECDsa _key;

    private TestCa(ECDsa key, X509Certificate2 certificate)
    {
        _key = key;
        Certificate = certificate;
    }

    /// <summary>The CA's certificate (public).</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>A self-signed root.</summary>
    public static TestCa CreateRoot(string name = "Test Root")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var self = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return new TestCa(key, X509CertificateLoader.LoadCertificate(self.RawData));
    }

    /// <summary>An intermediate CA signed by this one.</summary>
    public TestCa CreateIntermediate(string name = "Test Intermediate")
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var subjectKeyIdentifier = new X509SubjectKeyIdentifierExtension(request.PublicKey, false);
        request.CertificateExtensions.Add(subjectKeyIdentifier);
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, true, false));
        var now = DateTimeOffset.UtcNow;
        using var signed = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), now.AddDays(-1), now.AddYears(5), NewSerial());
        return new TestCa(key, X509CertificateLoader.LoadCertificate(signed.RawData));
    }

    /// <summary>
    /// Signs a PKCS#10 request, valid from a day ago for 90 days; the result has no private key. The request's SANs are kept,
    /// or replaced by <paramref name="names"/> when given (to issue less than was asked for).
    /// </summary>
    public X509Certificate2 Issue(byte[] csr, TimeSpan? validity = null, DateTimeOffset? notBefore = null, string[]? names = null, byte[]? serial = null)
    {
        var request = CertificateRequest.LoadSigningRequest(
            csr, HashAlgorithmName.SHA256, names is null ? CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions : CertificateRequestLoadOptions.Default);
        if (names is not null)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in names)
            {
                san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
        }

        var start = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, true, false));
        return request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), start, start + (validity ?? TimeSpan.FromDays(90)), serial ?? NewSerial());
    }

    /// <summary>A leaf with its own new key, for the names given; returns the certificate (with its key) for upload tests.</summary>
    public X509Certificate2 IssueWithKey(params string[] names) => IssueWithKey(NewKey(), names);

    /// <summary>As <see cref="IssueWithKey(string[])"/> over <paramref name="key"/>.</summary>
    public X509Certificate2 IssueWithKey(ECDsa key, params string[] names)
    {
        var request = new CertificateRequest(names.Length > 0 && !IPAddress.TryParse(names[0], out _) ? $"CN={names[0]}" : string.Empty, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            if (IPAddress.TryParse(name, out var ip))
            {
                san.AddIpAddress(ip);
            }
            else
            {
                san.AddDnsName(name);
            }
        }

        if (names.Length > 0)
        {
            request.CertificateExtensions.Add(san.Build());
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Certificate, true, false));
        var now = DateTimeOffset.UtcNow;
        using var signed = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(_key), now.AddDays(-1), now.AddDays(90), NewSerial());
        return signed.CopyWithPrivateKey(key);
    }

    public static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        Certificate.Dispose();
    }

    private static byte[] NewSerial()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        serial[0] |= 0x01;
        return serial;
    }
}
