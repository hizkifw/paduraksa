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
using Paduraksa.Data;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The configured DNS providers for DNS-01 (PLAN §5.1, §9.3). A provider row is a kind, a label and the
/// <see cref="SecretId"/> of its credential in the vault; <b>there is no credential here</b>, and the hosts the token may be sent
/// to are metadata of that secret, set when the secret was stored. Changes are audited (<c>acme.dns_provider.create</c>,
/// <c>acme.dns_provider.delete</c>), create no revision and publish nothing.
/// </summary>
public sealed partial class DnsProviderService
{
    /// <summary>The longest label.</summary>
    public const int MaxLabelLength = 256;

    /// <summary>The one API host a <paramref name="kind"/> provider's credential is ever sent to (the vault enforces it), for the surfaces to say so.</summary>
    public static string ApiHostOf(DnsProviderKind kind) => DnsProviderHosts.ApiHostOf(kind);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly ISecretStore _secrets;
    private readonly IDnsProviderFactory? _providers;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IPermissionEvaluator? _permissions;

    internal DnsProviderService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        ISecretStore secrets,
        IDnsProviderFactory? providers,
        TimeProvider time,
        ILogger<DnsProviderService> logger,
        IPermissionEvaluator? permissions = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _contexts = contexts;
        _config = config;
        _secrets = secrets;
        _providers = providers;
        _permissions = permissions;
        _time = time;
        _logger = logger;
    }

    /// <summary>Adds a provider that uses the vault secret named in the request, which must exist and be a DNS API token.</summary>
    public async Task<DnsProviderResult> CreateAsync(CreateDnsProviderRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.dns_provider.create", null, cancellationToken).ConfigureAwait(false))
        {
            return new DnsProviderResult(IssuanceChangeStatus.Denied);
        }

        string label = (request.Label ?? string.Empty).Trim();
        ConfigError? problem = !Enum.IsDefined(request.Kind) ? new ConfigError("/kind", IssuanceErrorCodes.KindUndefined, $"unknown DNS provider kind {request.Kind}")
            : label.Length == 0 ? new ConfigError("/label", IssuanceErrorCodes.LabelRequired, "a DNS provider needs a label")
            : label.Length > MaxLabelLength ? new ConfigError("/label", IssuanceErrorCodes.TextTooLong, $"the label is limited to {MaxLabelLength} characters")
            : null;
        if (problem is null)
        {
            SecretMetadata? secret = await _secrets.DescribeAsync(request.CredentialSecretId, cancellationToken).ConfigureAwait(false);
            if (secret is not { Kind: SecretKind.DnsApiToken })
            {
                problem = new ConfigError("/credentialSecretId", IssuanceErrorCodes.CredentialMissing, "the secret does not exist or is not a DNS API token");
            }
        }

        if (problem is not null)
        {
            return new DnsProviderResult(IssuanceChangeStatus.Invalid, Errors: [problem]);
        }

        DateTimeOffset now = _time.GetUtcNow();
        DnsProviderRow row = new()
        {
            Id = DnsProviderId.New(),
            Kind = request.Kind,
            Label = label,
            CredentialSecretId = request.CredentialSecretId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        DnsProviderResult result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                context.DnsProviders.Add(row);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                AuditEntry audit = IssuanceMapping.Entry(origin, "acme.dns_provider.create", row.Id, AuditOutcome.Success) with
                {
                    Details = IssuanceMapping.Json(w => w.WriteString("kind", row.Kind.ToName())),
                };
                return (new DnsProviderResult(IssuanceChangeStatus.Committed, IssuanceMapping.ToSummary(row)), audit);
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        if (result.Status == IssuanceChangeStatus.Committed)
        {
            LogCreated(row.Id, row.Kind);
        }

        return result;
    }

    /// <summary>Deletes a provider's row, unless a certificate is still ordered through it. The vault secret is the caller's to delete afterwards.</summary>
    public async Task<DnsProviderResult> DeleteAsync(DnsProviderId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.dns_provider.delete", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new DnsProviderResult(IssuanceChangeStatus.Denied);
        }

        DnsProviderResult result = await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                if (!await context.DnsProviders.AnyAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false))
                {
                    return (new DnsProviderResult(IssuanceChangeStatus.NotFound), null);
                }

                if (await context.AcmeOrders.AnyAsync(o => o.DnsProviderId == id, cancellationToken).ConfigureAwait(false))
                {
                    return (
                        new DnsProviderResult(
                            IssuanceChangeStatus.Invalid,
                            Errors: [new ConfigError(string.Empty, IssuanceErrorCodes.DnsProviderInUse, "certificates still use this provider; delete them first")]),
                        IssuanceMapping.Entry(origin, "acme.dns_provider.delete", id, AuditOutcome.Invalid));
                }

                await context.DnsProviders.Where(p => p.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
                return (new DnsProviderResult(IssuanceChangeStatus.Committed), IssuanceMapping.Entry(origin, "acme.dns_provider.delete", id, AuditOutcome.Success));
            },
            reason => Unavailable(reason),
            cancellationToken).ConfigureAwait(false);
        if (result.Status == IssuanceChangeStatus.Committed)
        {
            LogDeleted(id);
        }

        return result;
    }

    /// <summary>All providers, newest first.</summary>
    public async Task<IReadOnlyList<DnsProviderSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            List<DnsProviderRow> rows = await context.DnsProviders.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Select(IssuanceMapping.ToSummary)];
        }
    }

    /// <summary>One provider; null when there is none.</summary>
    public async Task<DnsProviderSummary?> GetAsync(DnsProviderId id, CancellationToken cancellationToken = default) =>
        await FindRowAsync(id, cancellationToken).ConfigureAwait(false) is { } row ? IssuanceMapping.ToSummary(row) : null;

    /// <summary>The provider's row, which (unlike the summary) names its credential in the vault: for the flows that use it, never for a response.</summary>
    private async Task<DnsProviderRow?> FindRowAsync(DnsProviderId id, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.DnsProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// "Test credentials": asks the provider to check its credential with a read-only call, for an origin holding <c>providers:write</c> (the call
    /// uses the credential, so it is not a read). A bad credential is a result, not an exception. The test is audited as
    /// <c>acme.dns_provider.test</c> with its verdict, never the credential or what the provider said.
    /// </summary>
    public async Task<DnsProviderTestResult> TestAsync(DnsProviderId id, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.ProvidersWrite, "acme.dns_provider.test", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new DnsProviderTestResult(Found: false, Valid: false) { Denied = true };
        }

        DnsProviderTestResult result = await TestCoreAsync(id, cancellationToken).ConfigureAwait(false);
        if (result.Found)
        {
            AuditEntry entry = IssuanceMapping.Entry(origin, "acme.dns_provider.test", id, result.Valid ? AuditOutcome.Success : AuditOutcome.Failed) with
            {
                Details = IssuanceMapping.Json(w => w.WriteBoolean("valid", result.Valid)),
            };
            await _config.TryAppendAuditAsync(entry).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<DnsProviderTestResult> TestCoreAsync(DnsProviderId id, CancellationToken cancellationToken)
    {
        DnsProviderRow? provider = await FindRowAsync(id, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return new DnsProviderTestResult(Found: false, Valid: false);
        }

        if (_providers is null)
        {
            return new DnsProviderTestResult(Found: true, Valid: false, "No DNS provider implementations are installed.");
        }

        try
        {
            DnsCredentialCheck check = await _providers.Create(provider.Kind, provider.CredentialSecretId)
                .ValidateCredentialsAsync(cancellationToken).ConfigureAwait(false);
            return new DnsProviderTestResult(Found: true, check.Valid, IssuanceMapping.Sanitize(check.Message) is { Length: > 0 } message ? message : null);
        }
        catch (Exception ex) when (ex is DnsProviderException or NotSupportedException)
        {
            return new DnsProviderTestResult(Found: true, Valid: false, IssuanceMapping.Sanitize(ex.Message));
        }
    }

    private static DnsProviderResult Unavailable(string reason) =>
        new(IssuanceChangeStatus.Unavailable, Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so DNS provider changes are refused: {reason}.")]);

    [LoggerMessage(Level = LogLevel.Information, Message = "DNS provider {Provider} ({Kind}) added.")]
    private partial void LogCreated(DnsProviderId provider, DnsProviderKind kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "DNS provider {Provider} deleted.")]
    private partial void LogDeleted(DnsProviderId provider);
}
