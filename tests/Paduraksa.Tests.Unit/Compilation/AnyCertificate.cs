using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>Resolves every certificate ID (but <see cref="Missing"/>) to one self-signed certificate.</summary>
internal sealed class AnyCertificate : IServerCertificateResolver, IDisposable
{
    private readonly X509Certificate2 _leaf;
    private readonly ServerCertificate _certificate;

    public AnyCertificate()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=paduraksa test", key, HashAlgorithmName.SHA256);
        _leaf = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _certificate = ServerCertificate.Create(_leaf, intermediates: null);
    }

    public HashSet<CertificateId> Missing { get; } = [];

    public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate)
    {
        certificate = Missing.Contains(id) ? null : _certificate;
        return certificate is not null;
    }

    public void Dispose() => _leaf.Dispose();
}
