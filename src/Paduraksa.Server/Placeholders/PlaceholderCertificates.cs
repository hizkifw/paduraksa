using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// TEMPORARY (M3): the <see cref="IServerCertificateResolver"/> the snapshot compiler uses until the vault's
/// <c>IServerCertificateSource</c> exists. It knows one reference, <see cref="Reference"/>: the in-memory self-signed
/// <see cref="PlaceholderServerCertificate"/>, which the startup config uses as its default certificate. Tests add
/// their own certificates under other references with <see cref="Set"/>.
/// </summary>
internal sealed class PlaceholderCertificates : IServerCertificateResolver
{
    /// <summary>The reference of the placeholder certificate.</summary>
    public const string Reference = "placeholder";

    private readonly ConcurrentDictionary<string, ServerCertificate> _certificates = new(StringComparer.Ordinal);

    public PlaceholderCertificates(PlaceholderServerCertificate placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        Placeholder = ServerCertificate.Create(placeholder.Certificate, intermediates: null);
        _certificates[Reference] = Placeholder;
    }

    public ServerCertificate Placeholder { get; }

    /// <summary>Adds or replaces a certificate; the next compiled snapshot picks it up (a hot swap once published).
    /// The caller keeps ownership of the certificate.</summary>
    public void Set(string reference, ServerCertificate certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(certificate);
        _certificates[reference] = certificate;
    }

    public bool TryResolve(string reference, [NotNullWhen(true)] out ServerCertificate? certificate) =>
        _certificates.TryGetValue(reference, out certificate);
}
