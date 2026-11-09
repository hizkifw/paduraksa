using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The serving contexts for configs that are only simulated: a draft, or a revision that isn't live. The
/// simulator asks which certificate entry answers a name and whether a host is served over HTTPS, never for a handshake
/// to complete, so every certificate the config could choose is served by a throwaway self-signed placeholder, made here
/// from a key generated here. <b>No key, no chain, nothing that belongs to a real certificate is loaded.</b> Whether a
/// certificate counts as usable is not decided here: <see cref="CertificateInventoryLoader.ForSimulation"/> asks the vault's
/// catalog, which describes public facts only, and only then asks this source for what to serve.
/// </summary>
/// <remarks>
/// One placeholder per certificate ID, for the life of the process (a certificate table deduplicates by context
/// instance, so equal IDs must share one). They hold no real name and are never sent to a client.
/// </remarks>
internal sealed class SimulationCertificateSource : IServerCertificateSource
{
    private readonly ConcurrentDictionary<CertificateId, SslStreamCertificateContext> _placeholders = new();

    public ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SslStreamCertificateContext?>(_placeholders.GetOrAdd(id, static key => Create(key)));

    private static SslStreamCertificateContext Create(CertificateId id)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN=paduraksa simulation placeholder {id}", key, HashAlgorithmName.SHA256);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Certificate2 leaf = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        return SslStreamCertificateContext.Create(leaf, null, offline: true);
    }
}
