using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Acme;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

/// <summary>ACME accounts: registers them with a CA, lists them and deactivates them. <b>The account key is born in
/// the vault</b> (<see cref="IJwsSigner.CreateAccountKeyAsync"/>) and only its public parts and signatures come out;
/// this service never sees a private key, and an external-account-binding MAC key is only ever a <see
/// cref="SecretId"/> (opened as a handle by the ACME client).</summary>
public sealed partial class AcmeAccountService
{
    /// <summary>The longest label.</summary>
    public const int MaxLabelLength = 256;

    /// <summary>The most contact URIs an account may have.</summary>
    public const int MaxContacts = 5;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly IAcmeClientProvider _clients;
    private readonly IJwsSigner _signer;
    private readonly IMacKeySource? _macKeys;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IPermissionEvaluator _permissions;
    private readonly ISecretStore? _secrets;

    internal AcmeAccountService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        IAcmeClientProvider clients,
        IJwsSigner signer,
        IMacKeySource? macKeys,
        TimeProvider time,
        ILogger<AcmeAccountService> logger,
        IPermissionEvaluator permissions,
        ISecretStore? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(permissions);
        _contexts = contexts;
        _config = config;
        _clients = clients;
        _signer = signer;
        _macKeys = macKeys;
        _permissions = permissions;
        _secrets = secrets;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Creates the account key in the vault and registers the account with the CA (<c>newAccount</c>, with external account
    /// binding when the request carries one), then records it. If the CA refuses, the key is deleted again and nothing remains;
    /// the result is <see cref="IssuanceChangeStatus.Failed"/> with what the CA said. If registering worked but recording it
    /// fails, the key is deleted and the exception propagates; the CA keeps an account nobody can use (logged), which
    /// costs nothing and which a new account does not conflict with.
    /// </summary>
    public async Task<AcmeAccountResult> CreateAsync(CreateAcmeAccountRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.account.create", null, cancellationToken).ConfigureAwait(false))
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Denied);
        }

        if (await ValidateAsync(request, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Invalid, Errors: [problem]);
        }

        Uri directory = new(request.DirectoryUrl, UriKind.Absolute);
        var id = AcmeAccountId.New();
        _ = await _signer.CreateAccountKeyAsync(id, request.Algorithm, cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _clients.GetClient(directory);
            var registered = await client.RegisterAccountAsync(
                id,
                new AcmeNewAccountRequest(
                    [.. request.Contacts],
                    request.TermsOfServiceAgreed,
                    request.EabKid is { } kid && request.EabSecretId is { } secret ? new AcmeExternalAccountBinding(kid, secret) : null),
                cancellationToken).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            AcmeAccountRow row = new()
            {
                Id = id,
                DirectoryUrl = directory.AbsoluteUri,
                Contacts = [.. registered.Contacts.Count > 0 ? registered.Contacts : request.Contacts],
                Status = AcmeAccountStatus.Active,
                AccountUrl = registered.Url.AbsoluteUri,
                KeyAlgorithm = request.Algorithm,
                EabKid = request.EabKid,
                EabSecretId = request.EabSecretId,
                TermsOfServiceAgreed = request.TermsOfServiceAgreed,
                Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            var result = await _config.ApplyOperationalChangeAsync(
                async context =>
                {
                    context.AcmeAccounts.Add(row);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    var audit = IssuanceMapping.Entry(origin, "acme.account.create", id, AuditOutcome.Success) with
                    {
                        Details = IssuanceMapping.Json(w =>
                        {
                            w.WriteString("directory", row.DirectoryUrl);
                            w.WriteString("algorithm", row.KeyAlgorithm.ToName());
                            w.WriteBoolean("eab", row.EabKid is not null);
                        }),
                    };
                    return (new AcmeAccountResult(IssuanceChangeStatus.Committed, IssuanceMapping.ToSummary(row)), audit);
                },
                reason => Unavailable(reason),
                cancellationToken).ConfigureAwait(false);
            if (result.Status != IssuanceChangeStatus.Committed)
            {
                await DeleteKeyAsync(id).ConfigureAwait(false);
            }
            else
            {
                LogCreated(id, directory);
            }

            return result;
        }
        catch (Exception ex) when (ex is AcmeException or InvalidOperationException or HttpRequestException)
        {
            await DeleteKeyAsync(id).ConfigureAwait(false);
            var message = IssuanceMapping.Sanitize(ex.Message);
            LogCreateFailed(directory, ex.GetType().Name);
            return new AcmeAccountResult(IssuanceChangeStatus.Failed, Message: message);
        }
        catch (Exception ex)
        {
            // Cancelled, or recording failed: either way nothing may keep an account key that no row names.
            await DeleteKeyAsync(id).ConfigureAwait(false);
            LogRecordFailed(id, directory, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>The longest external-account-binding key accepted, in bytes (CAs issue 32 to 64).</summary>
    public const int MaxEabKeyLength = 1024;

    /// <summary>
    /// The one-step "register an account" of the surfaces, for a CA with external account binding: authorizes, validates everything about the
    /// request that doesn't need the key, stores <paramref name="eabHmacKey"/> in the vault (an <see cref="SecretKind.EabHmacKey"/>, HMAC-SHA-256) and registers the
    /// account with the secret's ID. When the account isn't created (invalid, denied, refused by the CA, unavailable) the secret is deleted again, so nothing is left
    /// behind, and nothing is stored for a request that was invalid or not allowed. With no key (<paramref name="eabHmacKey"/> empty) it is
    /// <see cref="CreateAsync"/>; <see cref="CreateAcmeAccountRequest.EabKid"/> goes with the key, and <see cref="CreateAcmeAccountRequest.EabSecretId"/> is ignored.
    /// </summary>
    public async Task<AcmeAccountResult> CreateWithEabAsync(CreateAcmeAccountRequest request, ReadOnlyMemory<byte> eabHmacKey, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.account.create", null, cancellationToken).ConfigureAwait(false))
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Denied);
        }

        var hasKey = !eabHmacKey.IsEmpty;
        request = request with { EabSecretId = null };
        var problem = ValidateFields(request, hasKey)
                      ?? (eabHmacKey.Length > MaxEabKeyLength ? new ConfigError("/eabHmacKey", IssuanceErrorCodes.TextTooLong, $"the binding key is limited to {MaxEabKeyLength} bytes") : null)
                      ?? (hasKey && _secrets is null ? new ConfigError("/eabHmacKey", IssuanceErrorCodes.EabSecretMissing, "this server has no vault to keep a binding key in") : null);
        if (problem is not null)
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Invalid, Errors: [problem]);
        }

        if (!hasKey)
        {
            return await CreateAsync(request, origin, cancellationToken).ConfigureAwait(false);
        }

        var secret = await _secrets!.PutAsync(
            SecretKind.EabHmacKey,
            eabHmacKey,
            new SecretOptions(string.IsNullOrWhiteSpace(request.Label) ? "EAB key" : $"EAB key: {request.Label.Trim()}", MacAlgorithm.HmacSha256, null),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await CreateAsync(request with { EabSecretId = secret }, origin, cancellationToken).ConfigureAwait(false);
            if (result.Status != IssuanceChangeStatus.Committed)
            {
                await SecretCleanup.TryDeleteAsync(_secrets!, secret, _logger, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await SecretCleanup.TryDeleteAsync(_secrets!, secret, _logger, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Deactivates the account at the CA (RFC 8555 §7.3.6: final) and records it, then deletes its key from the vault.
    /// Refused while certificates are ordered with it; deactivating a deactivated account changes nothing.
    /// </summary>
    public async Task<AcmeAccountResult> DeactivateAsync(AcmeAccountId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.account.deactivate", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Denied);
        }

        AcmeAccountRow? row;
        bool inUse;
        var read = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            row = await read.AcmeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
            inUse = row is not null && await read.AcmeOrders.AnyAsync(o => o.AccountId == id, cancellationToken).ConfigureAwait(false);
        }

        if (row is null)
        {
            return new AcmeAccountResult(IssuanceChangeStatus.NotFound);
        }

        if (row.Status == AcmeAccountStatus.Deactivated)
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Committed, IssuanceMapping.ToSummary(row));
        }

        if (inUse)
        {
            return new AcmeAccountResult(
                IssuanceChangeStatus.Invalid,
                Errors: [new ConfigError(string.Empty, IssuanceErrorCodes.AccountInUse, "certificates are still ordered with this account; delete them first")]);
        }

        try
        {
            await _clients.GetClient(new Uri(row.DirectoryUrl))
                .DeactivateAccountAsync(new AcmeAccountRef(id, new Uri(row.AccountUrl)), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeProblemException ex) when (ex.Type is AcmeProblemTypes.AccountDoesNotExist or AcmeProblemTypes.Prefix + "unauthorized")
        {
            // A retried deactivation after one that worked at the CA but was not recorded here: the account is already gone there.
        }
        catch (Exception ex) when (ex is AcmeException or HttpRequestException)
        {
            return new AcmeAccountResult(IssuanceChangeStatus.Failed, Message: IssuanceMapping.Sanitize(ex.Message));
        }

        var result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                var tracked = await context.AcmeAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
                if (tracked is null)
                {
                    return (new AcmeAccountResult(IssuanceChangeStatus.NotFound), null);
                }

                tracked.Status = AcmeAccountStatus.Deactivated;
                tracked.UpdatedAt = _time.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (
                    new AcmeAccountResult(IssuanceChangeStatus.Committed, IssuanceMapping.ToSummary(tracked)),
                    IssuanceMapping.Entry(origin, "acme.account.deactivate", id, AuditOutcome.Success));
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        if (result.Status == IssuanceChangeStatus.Committed)
        {
            await DeleteKeyAsync(id).ConfigureAwait(false);
            LogDeactivated(id);
        }

        return result;
    }

    /// <summary>All accounts, newest first.</summary>
    public async Task<IReadOnlyList<AcmeAccountSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.AcmeAccounts.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id).Select(IssuanceMapping.ToSummary)];
        }
    }

    /// <summary>One account; null when there is none.</summary>
    public async Task<AcmeAccountSummary?> GetAsync(AcmeAccountId id, CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var row = await context.AcmeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : IssuanceMapping.ToSummary(row);
        }
    }

    private async Task<ConfigError?> ValidateAsync(CreateAcmeAccountRequest request, CancellationToken cancellationToken)
    {
        if (ValidateFields(request, request.EabSecretId is not null) is { } problem)
        {
            return problem;
        }

        return request.EabSecretId is { } secret
            && (_macKeys is null || await _macKeys.OpenAsync(secret, SecretKind.EabHmacKey, cancellationToken).ConfigureAwait(false) is null)
            ? new ConfigError("/eabSecretId", IssuanceErrorCodes.EabSecretMissing, "the secret does not exist or is not an EAB HMAC key")
            : null;
    }

    /// <summary>Everything about the request that needs no secret: <paramref name="hasEabSecret"/> says whether the binding's key is there.</summary>
    private static ConfigError? ValidateFields(CreateAcmeAccountRequest request, bool hasEabSecret)
    {
        if (request.Contacts is null)
        {
            return new ConfigError("/contacts", ConfigErrorCodes.ValueNull, "the contacts are missing (use an empty list for none)");
        }

        if (!Uri.TryCreate(request.DirectoryUrl, UriKind.Absolute, out var directory) || directory.Scheme != Uri.UriSchemeHttps || directory.Host.Length == 0)
        {
            return new ConfigError("/directoryUrl", IssuanceErrorCodes.DirectoryInvalid, "the directory URL must be an absolute https URL");
        }

        if (!Enum.IsDefined(request.Algorithm))
        {
            return new ConfigError("/algorithm", IssuanceErrorCodes.AlgorithmUndefined, $"unknown key algorithm {request.Algorithm}");
        }

        if (request.Contacts.Count > MaxContacts || request.Contacts.Any(c => c is null || !c.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || c.Length is <= 7 or > 256 || c.Any(char.IsControl)))
        {
            return new ConfigError("/contacts", IssuanceErrorCodes.ContactInvalid, $"at most {MaxContacts} contacts, each a mailto: URI of at most 256 characters");
        }

        if (request.Label is { Length: > MaxLabelLength })
        {
            return new ConfigError("/label", IssuanceErrorCodes.TextTooLong, $"the label is limited to {MaxLabelLength} characters");
        }

        if ((request.EabKid is null) == hasEabSecret || request.EabKid is { Length: 0 })
        {
            return new ConfigError("/eabKid", IssuanceErrorCodes.EabIncomplete, "external account binding needs both the key identifier and the secret");
        }

        return null;
    }

    private async Task DeleteKeyAsync(AcmeAccountId id)
    {
        try
        {
            await _signer.DeleteAccountKeyAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort cleanup; the original failure is what the caller needs to see.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogKeyCleanupFailed(id, ex.GetType().Name);
        }
    }

    private static AcmeAccountResult Unavailable(string reason) =>
        new(IssuanceChangeStatus.Unavailable, Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so ACME account changes are refused: {reason}.")]);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACME account {Account} registered at {Directory}.")]
    private partial void LogCreated(AcmeAccountId account, Uri directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering an ACME account at {Directory} failed ({ErrorType}); its key was deleted.")]
    private partial void LogCreateFailed(Uri directory, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ACME account {Account} at {Directory} was not recorded ({ErrorType}); its key was deleted and the CA may still hold the account.")]
    private partial void LogRecordFailed(AcmeAccountId account, Uri directory, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACME account {Account} deactivated.")]
    private partial void LogDeactivated(AcmeAccountId account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the vault key of ACME account {Account} failed ({ErrorType}); it stays in the vault.")]
    private partial void LogKeyCleanupFailed(AcmeAccountId account, string errorType);
}
