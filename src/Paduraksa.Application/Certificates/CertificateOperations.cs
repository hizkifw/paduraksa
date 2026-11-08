using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// Decides whether an origin may run a certificate operation (the M4 seam: roles and token scopes). Denials are not audited
/// here yet; the surface that asks reports them.
/// </summary>
public interface ICertificateOperationAuthorizer
{
    /// <summary>True if <paramref name="origin"/> may run <paramref name="operation"/>.</summary>
    ValueTask<bool> IsAllowedAsync(CertificateOperation operation, CommandOrigin origin, CancellationToken cancellationToken);
}

/// <summary>The authorizer until roles and scopes exist: allows everything, like <c>AllowAllConfigCommands</c>.</summary>
internal sealed class AllowAllCertificateOperations : ICertificateOperationAuthorizer
{
    public static AllowAllCertificateOperations Instance { get; } = new();

    // TODO(M4): check the actor's role (Owner/Admin may manage certificates) and a token's scopes (certificates:write).
    public ValueTask<bool> IsAllowedAsync(CertificateOperation operation, CommandOrigin origin, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}

/// <summary>
/// The certificate use cases that touch the vault (PLAN §9): upload, self-signed, the local CA root and its leaves, re-issue and
/// delete. Each calls the vault, then records the result in the inventory through <see cref="CertificateService"/>, which audits
/// it and republishes the config; the three surfaces (dashboard, API, CLI) call this and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering between the vault and the metadata</b> decides what a failure leaves behind, so it is chosen to leave nothing
/// that serves. The inventory only ever loads certificates from its table, and only <see cref="CertificateStatus.Active"/> ones, so
/// a key in the vault with no active row is never served.
/// </para>
/// <list type="bullet">
/// <item><b>A new certificate whose names are known up front</b> (self-signed, local-CA leaf): (1) register it
/// <i>pending</i> (validates names and label, visible, audited, never served); (2) the vault creates key and chain; (3) register
/// the vault's facts (the same row becomes active and the config is republished). If (2) fails, the pending row is deleted. If (3) is
/// refused or fails, the vault's key is deleted again and the pending row with it. A crash in between leaves a visible pending row,
/// which the user deletes; there is no hidden key.</item>
/// <item><b>An upload of a new certificate</b>: its names are known only after the vault read it, so (1) the vault imports,
/// (2) the row is registered active, and (3) if that is refused or fails, the vault's key is deleted again. A crash between (1) and
/// (2) leaves a key with no row, which nothing ever loads.</item>
/// <item><b>Replacing an existing ID</b> (re-issue, a new upload over it): the vault swaps key and chain atomically first and the row
/// is updated after. It cannot be undone, so if updating the row fails the result says so, the failure is logged, and doing the
/// same operation again repairs it: the vault serves the new pair meanwhile, which is the pair the row's names were taken from.</item>
/// <item><b>Delete</b>: the inventory goes first, and refuses (before anything is touched in the vault) while a Specific binding
/// or the default certificate uses the certificate; only after it committed is the key deleted. If that deletion fails, the
/// certificate is gone from the inventory and the key is an unreachable orphan (a warning in the result, logged).</item>
/// </list>
/// <para>
/// <b>The local CA.</b> There is one root (<see cref="CertificateSource.LocalCaRoot"/>); creating it again returns it (only an
/// active row counts: a root row a crash left pending is completed under its own ID). If the vault holds the root under another ID
/// (the inventory was lost) it is recorded under that ID. It cannot be deleted while leaves issued by it exist, since they would lose their issuer, and a leaf's re-issue needs it.
/// </para>
/// <para>No operation logs a key or a password; only IDs, sources and fingerprints.</para>
/// </remarks>
public sealed partial class CertificateOperations
{
    /// <summary>The default validity of a self-signed certificate.</summary>
    public static readonly TimeSpan DefaultSelfSignedValidity = TimeSpan.FromDays(365);

    /// <summary>The default validity of a local-CA leaf.</summary>
    public static readonly TimeSpan DefaultLeafValidity = TimeSpan.FromDays(90);

    /// <summary>The default validity of the local CA root.</summary>
    public static readonly TimeSpan DefaultRootValidity = TimeSpan.FromDays(3650);

    private readonly CertificateService _certificates;
    private readonly ICertificateIssuance _issuance;
    private readonly ICertificateImport _import;
    private readonly ICertificateOperationAuthorizer _authorizer;
    private readonly ILogger _logger;

    internal CertificateOperations(
        CertificateService certificates,
        ICertificateIssuance issuance,
        ICertificateImport import,
        ICertificateOperationAuthorizer authorizer,
        ILogger<CertificateOperations> logger)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(logger);
        _certificates = certificates;
        _issuance = issuance;
        _import = import;
        _authorizer = authorizer;
        _logger = logger;
    }

    /// <summary>
    /// Takes an uploaded PEM or PFX with its key into the vault and records it (<see cref="CertificateSource.Uploaded"/>). With
    /// <paramref name="replaceId"/> it replaces that uploaded certificate under the same ID. The stream is read by the vault
    /// (at most <see cref="CertificateImportLimits.MaxBytes"/>) and not buffered here.
    /// </summary>
    public async Task<CertificateOperationResult> UploadAsync(
        Stream data, ReadOnlyMemory<char> password, string? label, CertificateId? replaceId, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.Upload, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        // Everything the inventory would refuse about the request itself is checked before the vault changes anything: a replacement
        // can't be undone, so "invalid" must never be reported for an upload the vault already took.
        if (LabelProblem(label) is { } labelError)
        {
            return Invalid(labelError);
        }

        CertificateId id = replaceId ?? CertificateId.New();
        if (replaceId is { } existing && await CheckReplaceableAsync(existing, CertificateSource.Uploaded, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            return refusal;
        }

        CertificateInfo info;
        try
        {
            info = await _import.ImportAsync(id, data, password, cancellationToken).ConfigureAwait(false);
        }
        catch (CertificateImportException ex)
        {
            LogImportRefused(id, ex.Reason);
            return Invalid(new ConfigError(string.Empty, ImportCode(ex.Reason), ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            // The ID is the local CA root's.
            return Invalid(new ConfigError("/id", CertificateErrorCodes.NotReplaceable, ex.Message));
        }

        return await RegisterVaultResultAsync(id, CertificateSource.Uploaded, info, label, isNew: replaceId is null, pendingFirst: false, origin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a self-signed certificate and records it (<see cref="CertificateSource.SelfSigned"/>).</summary>
    public async Task<CertificateOperationResult> IssueSelfSignedAsync(SelfSignedCertificateRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.IssueSelfSigned, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        TimeSpan validity = request.Validity ?? DefaultSelfSignedValidity;
        return await CreateWithNamesAsync(
            CertificateSource.SelfSigned,
            request.Names,
            request.Label,
            origin,
            (id, names, ct) => _issuance.CreateSelfSignedAsync(id, request.KeySpec, names, validity, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the local CA root and records it (<see cref="CertificateSource.LocalCaRoot"/>); if it exists, returns it.</summary>
    public async Task<CertificateOperationResult> EnsureLocalCaAsync(LocalCaRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.CreateLocalCa, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        IReadOnlyList<CertificateSummary> roots = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot), cancellationToken).ConfigureAwait(false);
        CertificateSummary? active = roots.FirstOrDefault(r => r.Status == CertificateStatus.Active);
        if (active is not null)
        {
            return new CertificateOperationResult(CertificateOperationStatus.Unchanged, active);
        }

        // A root row that is not active is what a crash (or a failed activation) left behind: it is completed under its own ID
        // (the vault's ensure is idempotent for it) instead of being passed over as "unchanged" forever.
        TimeSpan validity = request.Validity ?? DefaultRootValidity;
        try
        {
            return await CreateWithNamesAsync(
                CertificateSource.LocalCaRoot,
                [],
                request.Label,
                origin,
                (id, _, ct) => _issuance.EnsureLocalCaAsync(id, request.KeySpec, validity, ct),
                cancellationToken,
                existingPendingId: roots.Count > 0 ? roots[0].Id : null).ConfigureAwait(false);
        }
        catch (LocalCaExistsException exists)
        {
            // The vault holds the root under another ID (the inventory was lost): record it under the ID that names its key.
            return await AdoptExistingRootAsync(exists, request.Label, origin, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CertificateOperationResult> AdoptExistingRootAsync(LocalCaExistsException exists, string? label, CommandOrigin origin, CancellationToken cancellationToken)
    {
        CertificateChangeResult registered = await _certificates.RegisterAsync(
            new CertificateRegistration(exists.ExistingId, CertificateSource.LocalCaRoot, exists.Info, CertificateStatus.Active, label), origin, cancellationToken).ConfigureAwait(false);
        return registered.Status switch
        {
            CertificateChangeStatus.Committed => new CertificateOperationResult(CertificateOperationStatus.Completed, registered.Certificate, Publish: registered.Publish),
            CertificateChangeStatus.Unchanged => new CertificateOperationResult(CertificateOperationStatus.Unchanged, registered.Certificate),
            _ => FromChange(registered),
        };
    }

    /// <summary>Creates a leaf signed by the local CA and records it (<see cref="CertificateSource.LocalCa"/>). Needs the root first.</summary>
    public async Task<CertificateOperationResult> IssueFromLocalCaAsync(LocalCaLeafRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.IssueFromLocalCa, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        if (await FindLocalCaAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.LocalCaMissing, "there is no local CA yet; create it first"));
        }

        TimeSpan validity = request.Validity ?? DefaultLeafValidity;
        return await CreateWithNamesAsync(
            CertificateSource.LocalCa,
            request.Names,
            request.Label,
            origin,
            (id, names, ct) => _issuance.IssueFromLocalCaAsync(id, request.KeySpec, names, validity, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-issues a self-signed or local-CA certificate: a new key and chain under the same ID, for the same names. Other sources
    /// are renewed elsewhere (ACME by its scheduler, uploads by uploading again).
    /// </summary>
    public async Task<CertificateOperationResult> ReissueAsync(CertificateId id, ReissueRequest? request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.Reissue, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        CertificateDetail? current = await _certificates.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new CertificateOperationResult(CertificateOperationStatus.NotFound);
        }

        CertificateSummary summary = current.Summary;
        if (summary.Source is not (CertificateSource.SelfSigned or CertificateSource.LocalCa))
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.NotReissuable, $"a {summary.Source.ToName()} certificate can't be re-issued here"));
        }

        KeySpec spec = request?.KeySpec ?? KeySpecOf(summary.KeyAlgorithm);
        CertificateInfo info;
        try
        {
            if (summary.Source == CertificateSource.SelfSigned)
            {
                info = await _issuance.CreateSelfSignedAsync(id, spec, summary.Names, request?.Validity ?? DefaultSelfSignedValidity, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                info = await _issuance.IssueFromLocalCaAsync(id, spec, summary.Names, request?.Validity ?? DefaultLeafValidity, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.IssuanceRefused, ex.Message));
        }

        return await RegisterVaultResultAsync(id, summary.Source, info, label: null, isNew: false, pendingFirst: false, origin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a certificate and its key. Refused, before the vault is touched, while a Specific binding or the default
    /// certificate uses it (and for the local CA root while leaves issued by it exist).
    /// </summary>
    public async Task<CertificateOperationResult> DeleteAsync(CertificateId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await IsAllowedAsync(CertificateOperation.Delete, origin, cancellationToken).ConfigureAwait(false))
        {
            return Forbidden();
        }

        CertificateDetail? current = await _certificates.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (current?.Summary.Source == CertificateSource.LocalCaRoot
            && (await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCa), cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.LocalCaInUse, "leaf certificates issued by the local CA still exist; delete them first"));
        }

        CertificateChangeResult change = await _certificates.DeleteAsync(id, origin, cancellationToken).ConfigureAwait(false);
        switch (change.Status)
        {
            case CertificateChangeStatus.Committed:
                break;
            case CertificateChangeStatus.NotFound:
                // No row: the key of a certificate whose metadata was lost (PLAN §9.2) may still be in the vault, and goes too.
                return await DeleteOrphanKeyAsync(id, cancellationToken).ConfigureAwait(false);
            case CertificateChangeStatus.Unavailable:
                return new CertificateOperationResult(CertificateOperationStatus.Unavailable);
            case CertificateChangeStatus.Denied:
                return Forbidden();
            case CertificateChangeStatus.Unchanged:
            case CertificateChangeStatus.Invalid:
            default:
                return new CertificateOperationResult(CertificateOperationStatus.Invalid, Errors: change.Errors);
        }

        try
        {
            // The inventory has already committed, so a cancelled request still deletes the key (it would be an unreachable orphan).
            await _issuance.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogKeyNotDeleted(id, ex);
            return new CertificateOperationResult(
                CertificateOperationStatus.Completed,
                Publish: change.Publish,
                Warnings: [new ConfigError(string.Empty, CertificateErrorCodes.KeyNotDeleted, "the certificate is deleted, but its key could not be removed from the vault; delete it again to retry")]);
        }

        return new CertificateOperationResult(CertificateOperationStatus.Completed, Publish: change.Publish);
    }

    /// <summary>The creation flow of a certificate with a new ID and known names: pending first, then the vault, then active (see the remarks).</summary>
    private async Task<CertificateOperationResult> CreateWithNamesAsync(
        CertificateSource source,
        IReadOnlyList<string> names,
        string? label,
        CommandOrigin origin,
        Func<CertificateId, IReadOnlyList<string>, CancellationToken, ValueTask<CertificateInfo>> create,
        CancellationToken cancellationToken,
        CertificateId? existingPendingId = null)
    {
        CertificateId id = existingPendingId ?? CertificateId.New();
        if (existingPendingId is null)
        {
            CertificateChangeResult pending = await _certificates.RegisterPendingAsync(id, source, names, label, origin, cancellationToken).ConfigureAwait(false);
            if (pending.Status != CertificateChangeStatus.Committed)
            {
                return FromChange(pending);
            }
        }

        CertificateInfo info;
        try
        {
            info = await create(id, names, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The key is deleted whatever failed (deleting is idempotent): a vault call that threw after storing it must not leave it.
            await RollBackNewAsync(id, origin, deleteKey: true).ConfigureAwait(false);
            if (ex is LocalCaExistsException)
            {
                throw;
            }

            if (ex is ArgumentException or InvalidOperationException)
            {
                return Invalid(new ConfigError(string.Empty, CertificateErrorCodes.IssuanceRefused, ex.Message));
            }

            throw;
        }

        return await RegisterVaultResultAsync(id, source, info, label, isNew: true, pendingFirst: true, origin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records what the vault made. A refusal or failure takes a new certificate's key and pending row back out.</summary>
    private async Task<CertificateOperationResult> RegisterVaultResultAsync(
        CertificateId id, CertificateSource source, CertificateInfo info, string? label, bool isNew, bool pendingFirst, CommandOrigin origin, CancellationToken cancellationToken)
    {
        CertificateChangeResult registered;
        try
        {
            registered = await _certificates.RegisterAsync(new CertificateRegistration(id, source, info, CertificateStatus.Active, label), origin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (isNew)
            {
                await RollBackNewAsync(id, origin, deleteKey: true, deleteRow: pendingFirst).ConfigureAwait(false);
            }
            else
            {
                LogReplacementNotRecorded(id, ex);
            }

            throw;
        }

        if (registered.Status is CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged)
        {
            return new CertificateOperationResult(CertificateOperationStatus.Completed, registered.Certificate, Publish: registered.Publish);
        }

        if (isNew)
        {
            await RollBackNewAsync(id, origin, deleteKey: true, deleteRow: pendingFirst).ConfigureAwait(false);
        }
        else
        {
            LogReplacementNotRecorded(id, null);
        }

        return FromChange(registered);
    }

    /// <summary>
    /// Undoes a creation that did not complete: the vault's key (if it was made) and the pending row. Best effort and
    /// not cancellable (a cancelled request still has to clean up); a failure is logged and leaves a pending row the user can delete.
    /// </summary>
    private async Task RollBackNewAsync(CertificateId id, CommandOrigin origin, bool deleteKey, bool deleteRow = true)
    {
        if (deleteKey)
        {
            try
            {
                await _issuance.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogKeyNotDeleted(id, ex);
            }
        }

        if (deleteRow)
        {
            try
            {
                // The system takes it back, not the caller: a rollback must not depend on the caller's permission (they may have lost
                // it since the operation began, and a refused rollback would leave a pending row). The channel and address are kept.
                await _certificates.DeleteAsync(id, new CommandOrigin(Actor.System, origin.Channel, origin.ClientIp), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPendingNotDeleted(id, ex);
            }
        }
    }

    private async Task<CertificateOperationResult> DeleteOrphanKeyAsync(CertificateId id, CancellationToken cancellationToken)
    {
        bool deleted = await _issuance.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return new CertificateOperationResult(deleted ? CertificateOperationStatus.Completed : CertificateOperationStatus.NotFound);
    }

    /// <summary>For a replacement: the certificate must exist and be of the expected source. Null if it is.</summary>
    private async Task<CertificateOperationResult?> CheckReplaceableAsync(CertificateId id, CertificateSource expected, CancellationToken cancellationToken)
    {
        CertificateDetail? current = await _certificates.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return current is null
            ? new CertificateOperationResult(CertificateOperationStatus.NotFound)
            : current.Summary.Source != expected
                ? Invalid(new ConfigError("/id", CertificateErrorCodes.NotReplaceable, $"a {current.Summary.Source.ToName()} certificate can't be replaced by an upload"))
                : null;
    }

    private async Task<CertificateSummary?> FindLocalCaAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CertificateSummary> roots = await _certificates.ListAsync(new CertificateQuery(Source: CertificateSource.LocalCaRoot, Status: CertificateStatus.Active), cancellationToken).ConfigureAwait(false);
        return roots.Count > 0 ? roots[0] : null;
    }

    private async ValueTask<bool> IsAllowedAsync(CertificateOperation operation, CommandOrigin origin, CancellationToken cancellationToken)
    {
        bool allowed = await _authorizer.IsAllowedAsync(operation, origin, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            LogForbidden(operation, origin.Actor);
        }

        return allowed;
    }

    private static ConfigError? LabelProblem(string? label) =>
        label is not null && label.Length > CertificateService.MaxLabelLength
            ? new ConfigError("/label", CertificateErrorCodes.TextTooLong, $"the label is limited to {CertificateService.MaxLabelLength} characters")
            : null;

    private static CertificateOperationResult Forbidden() => new(CertificateOperationStatus.Forbidden);

    private static CertificateOperationResult Invalid(ConfigError error) => new(CertificateOperationStatus.Invalid, Errors: [error]);

    private static CertificateOperationResult FromChange(CertificateChangeResult change) => change.Status switch
    {
        CertificateChangeStatus.Unavailable => new CertificateOperationResult(CertificateOperationStatus.Unavailable),
        CertificateChangeStatus.NotFound => new CertificateOperationResult(CertificateOperationStatus.NotFound),
        CertificateChangeStatus.Denied => Forbidden(),
        _ => new CertificateOperationResult(CertificateOperationStatus.Invalid, Errors: change.Errors),
    };

    private static string ImportCode(CertificateImportFailure reason) => reason switch
    {
        CertificateImportFailure.TooLarge => CertificateErrorCodes.ImportTooLarge,
        CertificateImportFailure.Unreadable => CertificateErrorCodes.ImportUnreadable,
        CertificateImportFailure.WrongPassword => CertificateErrorCodes.ImportWrongPassword,
        CertificateImportFailure.UnsupportedKeyFormat => CertificateErrorCodes.ImportUnsupportedKeyFormat,
        CertificateImportFailure.NoMatchingKey => CertificateErrorCodes.ImportNoMatchingKey,
        CertificateImportFailure.NoUsableName => CertificateErrorCodes.ImportNoUsableName,
        CertificateImportFailure.WeakKey => CertificateErrorCodes.ImportWeakKey,
        CertificateImportFailure.CaCertificate => CertificateErrorCodes.ImportCaCertificate,
        _ => CertificateErrorCodes.ImportUnreadable,
    };

    /// <summary>The key kind a certificate has, from its stored display label (<c>ECDSA P-256</c>, <c>RSA 2048</c>); P-256 for any other.</summary>
    private static KeySpec KeySpecOf(string keyAlgorithm) => keyAlgorithm switch
    {
        "ECDSA P-384" => KeySpec.EcdsaP384,
        "RSA 2048" => KeySpec.Rsa2048,
        "RSA 3072" => KeySpec.Rsa3072,
        "RSA 4096" => KeySpec.Rsa4096,
        _ => KeySpec.EcdsaP256,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload for certificate {Certificate} refused by the vault: {Reason}.")]
    private partial void LogImportRefused(CertificateId certificate, CertificateImportFailure reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Operation} refused for {Actor}.")]
    private partial void LogForbidden(CertificateOperation operation, Actor actor);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vault key of certificate {Certificate} could not be deleted; it is unreachable (no inventory row) but still in the vault.")]
    private partial void LogKeyNotDeleted(CertificateId certificate, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The pending row of certificate {Certificate} could not be deleted; delete the certificate to remove it.")]
    private partial void LogPendingNotDeleted(CertificateId certificate, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The vault replaced the key and chain of certificate {Certificate}, but recording them in the inventory failed; repeat the operation to repair it.")]
    private partial void LogReplacementNotRecorded(CertificateId certificate, Exception? exception);
}

/// <summary>Registration of <see cref="CertificateOperations"/>.</summary>
public static class CertificateOperationsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="CertificateOperations"/> as a singleton, with the allow-everything
    /// <see cref="ICertificateOperationAuthorizer"/> unless one is registered. Needs the <see cref="CertificateService"/>
    /// (<c>AddPaduraksaConfigService</c>), <see cref="ICertificateIssuance"/> and <see cref="ICertificateImport"/> (the vault).
    /// </summary>
    public static IServiceCollection AddPaduraksaCertificateOperations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICertificateOperationAuthorizer>(AllowAllCertificateOperations.Instance);
        services.TryAddSingleton(sp => new CertificateOperations(
            sp.GetRequiredService<CertificateService>(),
            sp.GetRequiredService<ICertificateIssuance>(),
            sp.GetRequiredService<ICertificateImport>(),
            sp.GetRequiredService<ICertificateOperationAuthorizer>(),
            (sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<CertificateOperations>()));
        return services;
    }
}
