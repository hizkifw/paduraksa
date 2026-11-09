using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// What <see cref="CertificateRecovery.RecoverAsync"/> did.
/// </summary>
/// <param name="Recovered">The certificates it registered again.</param>
/// <param name="Deleted">The certificates the audit trail says were deleted on purpose, whose keys the vault still held: the key was deleted now, nothing was registered.</param>
/// <param name="Failed">The certificates the vault holds that it could not register or delete (refused, the store unavailable, the vault failed); they stay as they were.</param>
public sealed record CertificateRecoveryResult(IReadOnlyList<CertificateId> Recovered, IReadOnlyList<CertificateId> Deleted, IReadOnlyList<CertificateId> Failed);

/// <summary>
/// Gets back the certificates the config database lost: the database was deleted or restored from an older backup while
/// the vault still holds the keys, so restored Specific bindings would degrade for want of a row. The vault describes what it holds
/// (<see cref="ICertificateCatalog"/>, public data only) and every certificate with no row is registered again through
/// <see cref="CertificateService.RegisterRecoveredAsync"/> without republishing per certificate: the startup step publishes the live
/// config once after the run, so the first snapshot includes what came back.
/// </summary>
/// <remarks>
/// <para>
/// <b>The source is not known</b>, since the row that said it is gone, so it is inferred from public facts and never guessed in the
/// direction of more capability: the vault's local CA root is <see cref="CertificateSource.LocalCaRoot"/>; a certificate whose
/// <b>signature verifies under that root's public key</b> (a matching name alone is not enough) is <see cref="CertificateSource.LocalCa"/>;
/// everything else is <see cref="CertificateSource.Uploaded"/>. A self-signed certificate is therefore recorded as uploaded (it cannot
/// be told from an uploaded self-signed one, and an upload is never replaced by a re-issue), and an ACME certificate is recorded as
/// uploaded too: its order, account and schedule were in the lost database, so it is not renewed and expires, until it is requested again.
/// Each recovered row carries the label <see cref="Label"/>, so the dashboard can say what happened.
/// </para>
/// <para>
/// <b>A deleted certificate is not resurrected.</b> Deleting removes the row (audited as <c>certificate.delete</c>) and then the key; if
/// the key's deletion failed, the vault still holds it, and the database is intact. Before registering, the audit trail is asked: a
/// successful <c>certificate.delete</c> for the ID means the database was not lost, so the deletion is finished (the vault's key is
/// deleted) instead. The same trail covers a creation that was rolled back (its pending row was deleted and audited). A key whose
/// registration was refused, or that no flow ever recorded (an upload interrupted between the vault and the row), has no such
/// trail: it is registered, visible, and can be deleted. When the database itself is lost the trail is lost too, so nothing is
/// ever treated as deleted then.
/// </para>
/// <para>
/// <b>Safe next to other writers.</b> Whether a row exists, and whether a delete is on record, is checked under the config writer's lock,
/// per certificate, right before registering: a certificate an upload registers at that moment is never overwritten, and an
/// existing row (its label included) is never changed. One remaining window: an upload that has reached the vault but not yet
/// registered when this runs is registered as recovered, and its own registration then replaces the metadata (an upload
/// without a label keeps the recovered one). It runs at startup, where that is rare, and never changes what is served.
/// </para>
/// </remarks>
public sealed partial class CertificateRecovery
{
    /// <summary>The label of a recovered certificate.</summary>
    public const string Label = "Recovered from the vault";

    private readonly CertificateService _certificates;
    private readonly ICertificateCatalog _catalog;
    private readonly ICertificateIssuance _issuance;
    private readonly ILogger _logger;

    public CertificateRecovery(CertificateService certificates, ICertificateCatalog catalog, ICertificateIssuance issuance, ILogger<CertificateRecovery> logger)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(logger);
        _certificates = certificates;
        _catalog = catalog;
        _issuance = issuance;
        _logger = logger;
    }

    /// <summary>Registers every certificate the vault holds that the inventory has no row for (and finishes the deletion of those deleted on purpose).</summary>
    public async Task<CertificateRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<VaultCertificate> held = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<VaultCertificate> roots = [.. held.Where(c => c.IsLocalCaRoot)];

        List<CertificateId> recovered = [];
        List<CertificateId> deleted = [];
        List<CertificateId> failed = [];

        // Each root is parsed once for the whole pass, not once per certificate it might have signed.
        List<X509Certificate2> rootCertificates = [];
        try
        {
            foreach (VaultCertificate root in roots)
            {
                if (TryLoadRoot(root.Info) is { } certificate)
                {
                    rootCertificates.Add(certificate);
                }
            }

            foreach (VaultCertificate certificate in held.OrderByDescending(c => c.IsLocalCaRoot))
            {
                // A stop is observed between certificates, never inside one: the registration transaction runs with
                // CancellationToken.None so an abort cannot cancel a commit partway through (and EF log it at Error).
                cancellationToken.ThrowIfCancellationRequested();
                if (certificate.Id == AdminCertificate.Id)
                {
                    // The process's own admin certificate: the core host's AdminCertificateService owns its row
                    // and adopts the vault's leaf itself, so a database loss cannot make two writers race over one fixed ID (nor
                    // leave a delete on record for it, which would make this branch finish the "deletion" of a fresh key).
                    continue;
                }

                CertificateSource source = certificate.IsLocalCaRoot ? CertificateSource.LocalCaRoot
                    : IsSignedBy(certificate.Info, rootCertificates) ? CertificateSource.LocalCa
                    : CertificateSource.Uploaded;
                CertificateRecoveryStep step = await _certificates.RegisterRecoveredAsync(
                    new CertificateRegistration(certificate.Id, source, certificate.Info, CertificateStatus.Active, Label),
                    CommandOrigin.System, republish: false, CancellationToken.None).ConfigureAwait(false);
                switch (step)
                {
                    case CertificateRecoveryStep.Registered:
                        recovered.Add(certificate.Id);
                        LogRecovered(certificate.Id, source);
                        break;
                    case CertificateRecoveryStep.WasDeleted:
                        if (await TryDeleteKeyAsync(certificate.Id, CancellationToken.None).ConfigureAwait(false))
                        {
                            deleted.Add(certificate.Id);
                        }
                        else
                        {
                            failed.Add(certificate.Id);
                        }

                        break;
                    case CertificateRecoveryStep.Failed:
                        failed.Add(certificate.Id);
                        LogNotRecovered(certificate.Id);
                        break;
                    default:
                        // AlreadyRegistered: nothing to do.
                        break;
                }
            }
        }
        finally
        {
            foreach (X509Certificate2 root in rootCertificates)
            {
                root.Dispose();
            }
        }

        CertificateRecoveryResult result = new(recovered, deleted, failed);
        if (recovered.Count + deleted.Count + failed.Count > 0)
        {
            LogSummary(held.Count, recovered.Count, deleted.Count, failed.Count);
        }

        return result;
    }

    private async Task<bool> TryDeleteKeyAsync(CertificateId id, CancellationToken cancellationToken)
    {
        try
        {
            _ = await _issuance.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            LogKeyDeleted(id);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogKeyNotDeleted(id, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Loads a root for the pass; one the vault's description can't yield is skipped, like a leaf that can't be parsed.</summary>
    private static X509Certificate2? TryLoadRoot(CertificateInfo info)
    {
        try
        {
            return X509CertificateLoader.LoadCertificate(AcmePem.LeafDer(info.PublicChainPem));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="leaf"/>'s certificate verifies against any of the pre-parsed roots (time and revocation are not judged: a recovered certificate may have expired).</summary>
    private static bool IsSignedBy(CertificateInfo leaf, List<X509Certificate2> roots)
    {
        if (roots.Count == 0)
        {
            return false;
        }

        try
        {
            using X509Certificate2 leafCertificate = X509CertificateLoader.LoadCertificate(AcmePem.LeafDer(leaf.PublicChainPem));
            using X509Chain chain = new();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 root in roots)
            {
                chain.ChainPolicy.CustomTrustStore.Add(root);
            }

            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
            return chain.Build(leafCertificate);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate} was in the vault but not in the inventory (the database was lost or restored from an older one); registered again as {Source}.")]
    private partial void LogRecovered(CertificateId certificate, CertificateSource source);

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate {Certificate} is in the vault but could not be registered again.")]
    private partial void LogNotRecovered(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate} was deleted on purpose but the vault still held its key (the deletion had failed); the key is deleted now.")]
    private partial void LogKeyDeleted(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate {Certificate} was deleted on purpose but the vault could not delete its key ({ErrorType}); it will be tried again at the next start.")]
    private partial void LogKeyNotDeleted(CertificateId certificate, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "The vault holds {Held} certificates: {Recovered} registered again, {Deleted} keys of deleted certificates removed, {Failed} could not be handled.")]
    private partial void LogSummary(int held, int recovered, int deleted, int failed);
}

/// <summary>Registration of <see cref="CertificateRecovery"/>.</summary>
public static class CertificateRecoveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CertificateRecovery"/>. Needs the <see cref="CertificateService"/> (<c>AddPaduraksaConfigService</c>) and the
    /// vault's <see cref="ICertificateCatalog"/> and <see cref="ICertificateIssuance"/>.
    /// </summary>
    public static IServiceCollection AddPaduraksaCertificateRecovery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new CertificateRecovery(
            sp.GetRequiredService<CertificateService>(),
            sp.GetRequiredService<ICertificateCatalog>(),
            sp.GetRequiredService<ICertificateIssuance>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<CertificateRecovery>()));
        return services;
    }
}
