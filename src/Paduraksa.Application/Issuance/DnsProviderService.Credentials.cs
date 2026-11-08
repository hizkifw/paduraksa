using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Acme.Dns;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

public sealed partial class DnsProviderService
{
    /// <summary>The longest credential accepted, in bytes (a DNS API token is a few dozen).</summary>
    public const int MaxCredentialLength = 4096;

    /// <summary>
    /// The one-step "add a provider" of the surfaces: stores <paramref name="credential"/> in the vault as a DNS API token that may only be
    /// sent to the provider's own API host, then adds the provider that names it. The credential passes through here once, on the way in; if
    /// the provider isn't added (invalid, denied, unavailable) the secret is deleted again, so nothing is left behind.
    /// </summary>
    public async Task<DnsProviderResult> CreateWithCredentialAsync(
        DnsProviderKind kind, string label, ReadOnlyMemory<byte> credential, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.dns_provider.create", null, cancellationToken).ConfigureAwait(false))
        {
            return new DnsProviderResult(IssuanceChangeStatus.Denied);
        }

        string trimmed = (label ?? string.Empty).Trim();
        ConfigError? problem = !Enum.IsDefined(kind) ? new ConfigError("/kind", IssuanceErrorCodes.KindUndefined, $"unknown DNS provider kind {kind}")
            : trimmed.Length == 0 ? new ConfigError("/label", IssuanceErrorCodes.LabelRequired, "a DNS provider needs a label")
            : trimmed.Length > MaxLabelLength ? new ConfigError("/label", IssuanceErrorCodes.TextTooLong, $"the label is limited to {MaxLabelLength} characters")
            : CredentialProblem(credential);
        if (problem is not null)
        {
            return new DnsProviderResult(IssuanceChangeStatus.Invalid, Errors: [problem]);
        }

        string apiHost = DnsProviderHosts.ApiHostOf(kind);
        SecretId secret = await _secrets.PutAsync(SecretKind.DnsApiToken, credential, new SecretOptions($"DNS provider: {trimmed}", null, [apiHost]), cancellationToken).ConfigureAwait(false);
        DnsProviderResult result;
        try
        {
            result = await CreateAsync(new CreateDnsProviderRequest(kind, trimmed, secret), origin, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SecretCleanup.TryDeleteAsync(_secrets, secret, _logger, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (result.Status != IssuanceChangeStatus.Committed)
        {
            await SecretCleanup.TryDeleteAsync(_secrets, secret, _logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Replaces the provider's credential in the vault (the secret keeps its ID and allowed hosts, so every reference stays valid) and
    /// audits it as <c>acme.dns_provider.rotate</c>. The value itself is never audited or returned.
    /// </summary>
    public async Task<DnsProviderResult> RotateCredentialAsync(DnsProviderId id, ReadOnlyMemory<byte> credential, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.dns_provider.rotate", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new DnsProviderResult(IssuanceChangeStatus.Denied);
        }

        if (CredentialProblem(credential) is { } problem)
        {
            return new DnsProviderResult(IssuanceChangeStatus.Invalid, Errors: [problem]);
        }

        return await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                DnsProviderRow? row = await context.DnsProviders.FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    return (new DnsProviderResult(IssuanceChangeStatus.NotFound), null);
                }

                try
                {
                    _ = await _secrets.ReplaceAsync(row.CredentialSecretId, credential, null, cancellationToken).ConfigureAwait(false);
                }
                catch (KeyNotFoundException)
                {
                    return (
                        new DnsProviderResult(IssuanceChangeStatus.Invalid, Errors: [new ConfigError(string.Empty, IssuanceErrorCodes.CredentialMissing, "the provider's secret no longer exists in the vault")]),
                        IssuanceMapping.Entry(origin, "acme.dns_provider.rotate", id, AuditOutcome.Failed));
                }

                row.UpdatedAt = _time.GetUtcNow();
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return (new DnsProviderResult(IssuanceChangeStatus.Committed, IssuanceMapping.ToSummary(row)), IssuanceMapping.Entry(origin, "acme.dns_provider.rotate", id, AuditOutcome.Success));
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the provider (<see cref="DeleteAsync"/>) and, once that is done, its credential from the vault.</summary>
    public async Task<DnsProviderResult> DeleteWithCredentialAsync(DnsProviderId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        SecretId? credential = (await FindRowAsync(id, cancellationToken).ConfigureAwait(false))?.CredentialSecretId;
        DnsProviderResult result = await DeleteAsync(id, origin, cancellationToken).ConfigureAwait(false);
        if (result.Status == IssuanceChangeStatus.Committed && credential is { } secret)
        {
            await SecretCleanup.TryDeleteAsync(_secrets, secret, _logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static ConfigError? CredentialProblem(ReadOnlyMemory<byte> credential) =>
        credential.IsEmpty ? new ConfigError("/credential", IssuanceErrorCodes.CredentialMissing, "the credential is required")
        : credential.Length > MaxCredentialLength ? new ConfigError("/credential", IssuanceErrorCodes.TextTooLong, $"the credential is limited to {MaxCredentialLength} bytes")
        : null;

}
