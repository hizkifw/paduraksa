using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Unit.Compilation;

/// <summary>
/// Compiles documents with an inventory in which every certificate ID the document names (a Specific binding, the
/// default) is one usable self-signed certificate that covers no name (so Auto never matches it), except the IDs in
/// <see cref="Missing"/>, which the inventory doesn't know.
/// </summary>
internal sealed class AnyCertificate : IDisposable
{
    private readonly X509Certificate2 _leaf;
    private readonly ServerCertificate _certificate;
    private readonly ConfigCompiler _compiler = new(SnapshotCompilerOptions.Default);

    public AnyCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=paduraksa test", key, HashAlgorithmName.SHA256);
        _leaf = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _certificate = ServerCertificate.Create(_leaf, intermediates: null);
    }

    public HashSet<CertificateId> Missing { get; } = [];

    public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision) => _compiler.Compile(document, revision, InventoryFor(document));

    public CertificateInventory InventoryFor(ConfigDocument document)
    {
        HashSet<CertificateId> named = [];
        if (document.Settings?.DefaultCertificateId is { IsEmpty: false } defaultId)
        {
            named.Add(defaultId);
        }

        foreach (var host in document.Hosts ?? [])
        {
            if (host?.Certificate is { CertificateId: { IsEmpty: false } id })
            {
                named.Add(id);
            }
        }

        var now = DateTimeOffset.UtcNow;
        return new CertificateInventory(
        [
            .. named.Where(id => !Missing.Contains(id))
                .Select(id => new InventoryCertificate(id, [], now.AddDays(-1), now.AddDays(1), _certificate, null)),
        ], null, null);
    }

    public void Dispose() => _leaf.Dispose();
}
