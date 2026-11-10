using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Application.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Server.Placeholders;

/// <summary>
/// TEMPORARY: the certificate source and the built-in inventory until the vault's exist. It knows the in-memory
/// self-signed <see cref="PlaceholderServerCertificate"/> under <see cref="Id"/>, the default certificate of a config that
/// names none (so the dev binary answers HTTPS out of the box), and the certificates tests add with <see cref="Set"/>.
/// </summary>
/// <remarks>
/// <b>The two halves.</b> As an <see cref="IServerCertificateSource"/> it hands out serving contexts, the same instance
/// every time, like the vault will. As <see cref="IBuiltInCertificates"/> it contributes inventory entries (public facts
/// only), so the compiler treats the placeholder like any other certificate: Specific bindings, Auto matching (it covers
/// <c>localhost</c> and the loopback addresses), expiry.
///
/// <b>A stable ID.</b> <see cref="Id"/> is the same in every process (the certificate itself is new in each), so a stored
/// document that names it, the benchmark runner's hosts for one, keeps compiling after a restart. When the vault provides
/// real certificates the placeholder goes, and a config that still names this ID degrades like any binding to a missing
/// certificate instead of failing to start. TODO: compose with the vault's <c>IServerCertificateSource</c> (the vault first,
/// this for <see cref="Id"/> only), and drop the placeholder from the release build.
/// </remarks>
internal sealed class PlaceholderCertificates : IServerCertificateSource, IBuiltInCertificates, IServerCertificateResolver
{
    /// <summary>The ID of the placeholder certificate: well known and stable across processes.</summary>
    public static CertificateId Id { get; } = CertificateId.FromValue(new Guid("0190a000-0000-7000-8000-70616c616365"));

    private readonly ConcurrentDictionary<CertificateId, Entry> _certificates = new();

    public PlaceholderCertificates(PlaceholderServerCertificate placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        Placeholder = ServerCertificate.Create(placeholder.Certificate, intermediates: null);
        _certificates[Id] = new Entry(Placeholder, InfoOf(placeholder.Certificate), IsFallbackDefault: true);
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
        _certificates[id] = new Entry(certificate, InfoOf(certificate.Leaf), IsFallbackDefault: id == Id);
    }

    /// <summary>
    /// Makes <paramref name="certificate"/> loadable under <paramref name="id"/> without listing it as a built-in
    /// certificate: the stand-in for a key the vault holds, whose metadata the certificate service records (tests).
    /// </summary>
    public void Provide(CertificateId id, ServerCertificate certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        _certificates[id] = new Entry(certificate, Info: null, IsFallbackDefault: false);
    }

    public IReadOnlyList<BuiltInCertificate> Current =>
        [.. _certificates.Where(c => c.Value.Info is not null).Select(c => new BuiltInCertificate(c.Key, c.Value.Info!, c.Value.IsFallbackDefault))];

    /// <summary>Resolves directly, for tests that compile a proxy model without going through the inventory.</summary>
    public bool TryResolve(CertificateId id, [NotNullWhen(true)] out ServerCertificate? certificate)
    {
        certificate = _certificates.TryGetValue(id, out var entry) ? entry.Serving : null;
        return certificate is not null;
    }

    public ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_certificates.TryGetValue(id, out var entry) ? entry.Serving.Context : null);

    /// <summary>The public facts of <paramref name="certificate"/> (its key never leaves it).</summary>
    internal static CertificateInfo InfoOf(X509Certificate2 certificate)
    {
        List<string> names = [];
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value == "2.5.29.17")
            {
                X509SubjectAlternativeNameExtension san = new(extension.RawData, extension.Critical);
                names.AddRange(san.EnumerateDnsNames());
                names.AddRange(san.EnumerateIPAddresses().Select(ip => ip.ToString()));
            }
        }

        return new CertificateInfo(
            certificate.Subject,
            names,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            certificate.Issuer,
            certificate.SerialNumber,
            AuthorityKeyIdentifier: null,
            Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)),
            certificate.PublicKey.Oid.FriendlyName ?? certificate.PublicKey.Oid.Value ?? string.Empty,
            [certificate.Issuer],
            certificate.ExportCertificatePem());
    }

    private sealed record Entry(ServerCertificate Serving, CertificateInfo? Info, bool IsFallbackDefault);
}
