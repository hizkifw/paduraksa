using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// TEMPORARY (M3): the <see cref="IServerCertificateResolver"/> the snapshot compiler uses until the vault's
/// <c>IServerCertificateSource</c> exists. It knows one certificate, <see cref="Id"/>: the in-memory self-signed
/// <see cref="PlaceholderServerCertificate"/>, which the startup config uses as its default certificate. Tests add
/// their own certificates under other IDs with <see cref="Set"/>.
/// </summary>
internal sealed class PlaceholderCertificates : IServerCertificateResolver
{
    /// <summary>
    /// The ID of the placeholder certificate: new per process, like the certificate itself (it never reaches the
    /// database).
    /// </summary>
    public static CertificateId Id { get; } = CertificateId.New();

    private readonly ConcurrentDictionary<CertificateId, ServerCertificate> _certificates = new();

    public PlaceholderCertificates(PlaceholderServerCertificate placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        Placeholder = ServerCertificate.Create(placeholder.Certificate, intermediates: null);
        _certificates[Id] = Placeholder;
    }

    public ServerCertificate Placeholder { get; }

    /// <summary>Adds or replaces a certificate; the next compiled snapshot picks it up (a hot swap once published).
    /// The caller keeps ownership of the certificate.</summary>
    public void Set(CertificateId id, ServerCertificate certificate)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("The certificate ID can't be empty.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(certificate);
        _certificates[id] = certificate;
    }

    public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate) =>
        _certificates.TryGetValue(id, out certificate);
}
