using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Acme;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// ACME issuance orchestration: asks a CA for a certificate and keeps it renewed, on top of the ACME client, the
/// vault's <see cref="ICertificateIssuance"/> and the certificate inventory (<see cref="CertificateService"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Trust boundary.</b> The key is born in the vault. This service only ever holds the CSR the vault returns, the
/// public chain the CA sends, a <see cref="StagedKeyId"/> token and the vault's public <see cref="CertificateInfo"/>; it
/// signs nothing (the ACME client has the vault sign) and sees no key material.
/// </para>
/// <para>
/// <b>One issuance per certificate at a time.</b> A certificate has one <c>AcmeOrders</c> row, and an issuance runs under a
/// per-certificate guard in this process (a second request returns <see cref="IssuanceOutcome.InProgress"/>); one process owns
/// the data directory.
/// </para>
/// <para>
/// <b>Resumable.</b> The row is saved at each step (the order URL, then the staged key's token and the CSR before the CA
/// is asked to finalize), and every run starts by reading the CA's order, so a run follows the CA's state: a restart in the
/// middle (or a retry after a failure) continues the same order where it stands instead of starting a new one, which
/// would count against the CA's limits. An order the CA no longer accepts is dropped and a new one made. What a restart
/// cannot recover: a crash between the vault attaching the new chain and the certificate row being updated leaves the vault
/// serving the new certificate under the old metadata; the next attempt cannot attach again (the staged key is spent), fails,
/// and the one after starts a fresh order, which repairs it.
/// </para>
/// <para>
/// <b>Failures</b> are recorded on the row (the error, the failure count and the time of the next attempt, from the
/// failure's kind: <see cref="RenewalSchedule.Backoff"/>) and audited (<c>acme.order.failed</c>). A certificate that was
/// never issued, or has expired, becomes <see cref="CertificateStatus.Failed"/> with the message; one that still serves
/// keeps serving. Only a cancellation (shutdown) leaves the row as it is, to resume.
/// </para>
/// </remarks>
public sealed partial class AcmeIssuanceService
{
    /// <summary>The most names an order may have (the limit of Let's Encrypt, the strictest common CA).</summary>
    public const int MaxNames = 100;

    /// <summary>The longest ACME profile name.</summary>
    public const int MaxProfileLength = 64;

    /// <summary>The longest preferred-chain issuer name.</summary>
    public const int MaxChainLength = 256;

    private readonly ConcurrentDictionary<CertificateId, byte> _running = [];
    private readonly ConcurrentDictionary<CertificateId, AcmeOrderProgress> _progress = [];
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly ConfigService _config;
    private readonly CertificateService _certificates;
    private readonly IAcmeClientProvider _clients;
    private readonly ICertificateIssuance _vault;
    private readonly ICertificateCatalog _catalog;
    private readonly IAcmeChallengeStore _challenges;
    private readonly IDnsProviderFactory? _dns;
    private readonly IDnsPropagationCheck? _propagation;
    private readonly AcmeIssuanceOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly ILogger _logger;
    private readonly IPermissionEvaluator _permissions;

    internal AcmeIssuanceService(
        IDbContextFactory<ConfigDbContext> contexts,
        ConfigService config,
        CertificateService certificates,
        IAcmeClientProvider clients,
        ICertificateIssuance vault,
        IAcmeChallengeStore challenges,
        IDnsProviderFactory? dns,
        AcmeIssuanceOptions options,
        TimeProvider time,
        ILogger<AcmeIssuanceService> logger,
        IPermissionEvaluator permissions,
        ICertificateCatalog catalog,
        IDnsPropagationCheck? propagation,
        Func<double> random)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(random);
        _contexts = contexts;
        _config = config;
        _certificates = certificates;
        _clients = clients;
        _vault = vault;
        _catalog = catalog;
        _challenges = challenges;
        _dns = dns;
        _propagation = propagation;
        _options = options;
        _time = time;
        _random = random;
        _permissions = permissions;
        _logger = logger;
    }

    /// <summary>Registers a new pending certificate with a fresh ID and runs its first issuance to the end: new
    /// order, challenges, key and CSR in the vault, finalize, download (the preferred chain if the CA offers it),
    /// attach, register.</summary>
    public Task<IssuanceResult> RequestCertificateAsync(CertificateIssueRequest request, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        RequestCertificateAsync(request, origin, null, cancellationToken);

    /// <summary>
    /// <see cref="RequestCertificateAsync(CertificateIssueRequest, CommandOrigin, CancellationToken)"/>, telling <paramref name="progress"/>
    /// each step as the run reaches it (the first report carries the new certificate's ID). The reports are made on the run's own thread;
    /// a reporter that throws is ignored.
    /// </summary>
    public async Task<IssuanceResult> RequestCertificateAsync(
        CertificateIssueRequest request, CommandOrigin origin, IProgress<AcmeOrderProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.CertificatesWrite, "acme.order.request", null, cancellationToken).ConfigureAwait(false))
        {
            return new IssuanceResult(IssuanceOutcome.Forbidden);
        }

        var (names, problem) = await ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return new IssuanceResult(IssuanceOutcome.Invalid, Errors: [problem]);
        }

        var id = CertificateId.New();
        var registered = await _certificates.RegisterPendingAsync(id, CertificateSource.Acme, names, request.Label, origin, cancellationToken).ConfigureAwait(false);
        if (registered.Status != CertificateChangeStatus.Committed)
        {
            return new IssuanceResult(
                registered.Status == CertificateChangeStatus.Unavailable ? IssuanceOutcome.Unavailable : IssuanceOutcome.Invalid,
                Errors: registered.Errors);
        }

        var now = _time.GetUtcNow();
        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                context.AcmeOrders.Add(new AcmeOrderRow
                {
                    Id = AcmeOrderId.New(),
                    CertificateId = id,
                    AccountId = request.AccountId,
                    Status = AcmeOrderStatus.Pending,
                    ChallengeType = request.ChallengeType,
                    DnsProviderId = request.DnsProviderId,
                    KeySpec = request.KeySpec,
                    Names = names,
                    Profile = string.IsNullOrWhiteSpace(request.Profile) ? null : request.Profile.Trim(),
                    PreferredChain = string.IsNullOrWhiteSpace(request.PreferredChain) ? null : request.PreferredChain.Trim(),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // No order row, so nothing would ever finish this pending certificate: take it back out.
            _ = await _certificates.DeleteAsync(id, origin, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await RunAsync(id, origin, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes the certificate's next step by its state: a certificate whose last issuance finished gets a new
    /// order (a renewal: same ID, names, account and settings, with the CA told which certificate it replaces); one
    /// whose issuance is unfinished or failed continues the order it has, or makes a new one if the CA no longer
    /// accepts it.</summary>
    public Task<IssuanceResult> RenewAsync(CertificateId id, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        RenewAsync(id, origin, null, cancellationToken);

    /// <summary>
    /// <see cref="RenewAsync(CertificateId, CommandOrigin, CancellationToken)"/>, telling <paramref name="progress"/> each step as the run
    /// reaches it.
    /// </summary>
    public async Task<IssuanceResult> RenewAsync(CertificateId id, CommandOrigin origin, IProgress<AcmeOrderProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!await PermissionGate.MayAsync(_permissions, _config, origin, Permission.CertificatesWrite, "acme.order.renew", id.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return new IssuanceResult(IssuanceOutcome.Forbidden, id);
        }

        return await RunAsync(id, origin, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The step a run of this certificate's issuance has reached, while one runs in this process (the scheduler's included); null when
    /// none runs. What a page shows for an order it did not start itself; the stored order (<see cref="GetOrderAsync"/>) says the rest.
    /// </summary>
    public AcmeOrderProgress? GetProgress(CertificateId id) => _progress.GetValueOrDefault(id);

    /// <summary>What the scheduler calls for a certificate: does what is <b>due now</b>, if anything. Under the
    /// certificate's guard it reads the order again and applies the due rules to that fresh row, so a manual renewal
    /// or a retry that ran since the scheduler listed the orders is not repeated, and a failed order is not retried
    /// before its <c>RetryAt</c>: an unfinished order is resumed; a failed one whose <c>RetryAt</c> has passed is
    /// retried; an issued one whose <c>NextRenewalAt</c> has passed is renewed, else its ARI window is refreshed once
    /// <c>AriCheckAt</c> has passed.</summary>
    public async Task<bool> RunDueAsync(CertificateId id, CancellationToken cancellationToken = default)
    {
        if (_config.Status is { AcceptsCommands: false } || !_running.TryAdd(id, 0))
        {
            return false;
        }

        try
        {
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return false;
            }

            var now = _time.GetUtcNow();
            switch (row.Status)
            {
                case AcmeOrderStatus.Pending or AcmeOrderStatus.Ordered or AcmeOrderStatus.Finalizing:
                case AcmeOrderStatus.Failed when row.RetryAt is not { } retryAt || retryAt <= now:
                case AcmeOrderStatus.Valid when row.NextRenewalAt is { } renewAt && renewAt <= now:
                    LogDue(id, row.Status);
                    var result = await RunCoreAsync(row, CommandOrigin.System, null, cancellationToken).ConfigureAwait(false);
                    return result.Outcome is not (IssuanceOutcome.InProgress or IssuanceOutcome.NotFound or IssuanceOutcome.Unavailable);
                case AcmeOrderStatus.Valid when row.AriCheckAt is { } checkAt && checkAt <= now:
                    _ = await RefreshCoreAsync(row, cancellationToken).ConfigureAwait(false);
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _progress.TryRemove(id, out _);
            _running.TryRemove(id, out _);
        }
    }

    /// <summary>Asks the CA again for the renewal window (ARI, RFC 9773) of an issued certificate and updates the
    /// schedule: the window and the next check are stored, and the renewal time is picked again only if it fell
    /// outside the new window. Returns the state afterwards; null when the certificate has no order.</summary>
    public async Task<AcmeOrderSummary?> RefreshRenewalInfoAsync(CertificateId id, CancellationToken cancellationToken = default)
    {
        // Under the certificate's guard, so a late write cannot overwrite the schedule an issuance just made; busy: it shows what is stored.
        var held = _running.TryAdd(id, 0);
        try
        {
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            return row is null ? null : held ? await RefreshCoreAsync(row, cancellationToken).ConfigureAwait(false) : IssuanceMapping.ToSummary(row);
        }
        finally
        {
            if (held)
            {
                _running.TryRemove(id, out _);
            }
        }
    }

    private async Task<AcmeOrderSummary> RefreshCoreAsync(AcmeOrderRow row, CancellationToken cancellationToken)
    {
        var id = row.CertificateId;
        var certificate = row.Status == AcmeOrderStatus.Valid ? await _certificates.GetAsync(id, cancellationToken).ConfigureAwait(false) : null;
        var account = certificate is null ? null : await LoadAccountAsync(row.AccountId, cancellationToken).ConfigureAwait(false);
        if (certificate is null || account is null || certificate.Summary.AuthorityKeyIdentifier is not { } aki)
        {
            return IssuanceMapping.ToSummary(row);
        }

        var now = _time.GetUtcNow();
        AcmeRenewalInfo? info;
        try
        {
            info = await _clients.GetClient(new Uri(account.DirectoryUrl))
                .GetRenewalInfoAsync(AcmeRenewalInfoId.Create(aki, certificate.Summary.SerialNumber), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeException ex)
        {
            LogRenewalInfoFailed(id, ex.GetType().Name);
            await UpdateAsync(row, r => r.AriCheckAt = now + _options.AriMinRecheck, cancellationToken).ConfigureAwait(false);
            return IssuanceMapping.ToSummary(row);
        }

        var notBefore = certificate.Summary.NotBefore;
        var notAfter = certificate.Summary.NotAfter;
        var random = _random();
        await UpdateAsync(
            row,
            r =>
            {
                r.AriWindowStart = info?.WindowStart;
                r.AriWindowEnd = info?.WindowEnd;
                r.AriExplanationUrl = info?.ExplanationUrl?.AbsoluteUri;
                r.AriCheckAt = info is null ? null : RenewalSchedule.NextAriCheck(now, info.RetryAfter, _options);
                if (info is null)
                {
                    r.NextRenewalAt = RenewalSchedule.NextRenewalAt(notBefore, notAfter, null, random, _options);
                }
                else if (!RenewalSchedule.KeepsPick(r.NextRenewalAt, info))
                {
                    r.NextRenewalAt = RenewalSchedule.NextRenewalAt(notBefore, notAfter, info, random, _options);
                }
            },
            cancellationToken).ConfigureAwait(false);
        LogRenewalInfo(id, row.NextRenewalAt ?? default);
        return IssuanceMapping.ToSummary(row);
    }

    /// <summary>The ACME state of every certificate that has an order, newest first.</summary>
    public async Task<IReadOnlyList<AcmeOrderSummary>> ListOrdersAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await context.AcmeOrders.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.OrderByDescending(o => o.CreatedAt).ThenBy(o => o.Id).Select(IssuanceMapping.ToSummary)];
        }
    }

    /// <summary>The ACME state of one certificate; null when it has no order.</summary>
    public async Task<AcmeOrderSummary?> GetOrderAsync(CertificateId id, CancellationToken cancellationToken = default)
    {
        var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return row is null ? null : IssuanceMapping.ToSummary(row);
    }

    private async Task<(List<string> Names, ConfigError? Problem)> ValidateAsync(CertificateIssueRequest request, CancellationToken cancellationToken)
    {
        List<string> names = [];
        if (request.Names is null || request.Names.Count == 0)
        {
            return (names, new ConfigError("/names", IssuanceErrorCodes.NamesRequired, "a certificate needs at least one name"));
        }

        if (request.Names.Count > MaxNames)
        {
            return (names, new ConfigError("/names", IssuanceErrorCodes.TooManyNames, $"an order is limited to {MaxNames} names"));
        }

        names = CertificateNames.Normalize(request.Names, out var rejected);
        if (rejected.Count > 0 || names.Count == 0)
        {
            return (names, new ConfigError("/names", IssuanceErrorCodes.NameInvalid, $"'{(rejected.Count > 0 ? rejected[0] : request.Names[0])}' is not a DNS name, a wildcard or an IP address"));
        }

        foreach (var name in names)
        {
            try
            {
                _ = ToIdentifier(name);
            }
            catch (ArgumentException)
            {
                return (names, new ConfigError("/names", IssuanceErrorCodes.NameInvalid, $"'{name}' cannot be put in an ACME order (a DNS name must be letters, digits and hyphens)"));
            }
        }

        if (!Enum.IsDefined(request.ChallengeType))
        {
            return (names, new ConfigError("/challengeType", IssuanceErrorCodes.ChallengeUndefined, $"unknown challenge type {request.ChallengeType}"));
        }

        if (!Enum.IsDefined(request.KeySpec))
        {
            return (names, new ConfigError("/keySpec", IssuanceErrorCodes.KeySpecUndefined, $"unknown key spec {request.KeySpec}"));
        }

        if (request.ChallengeType == AcmeChallengeType.Http01 && names.FirstOrDefault(n => n.StartsWith("*.", StringComparison.Ordinal)) is { } wildcard)
        {
            return (names, new ConfigError("/names", IssuanceErrorCodes.WildcardNeedsDns, $"'{wildcard}' is a wildcard, which only DNS-01 can prove"));
        }

        if (request.ChallengeType == AcmeChallengeType.Dns01 && names.FirstOrDefault(n => IPAddress.TryParse(n, out _)) is { } ip)
        {
            return (names, new ConfigError("/names", IssuanceErrorCodes.IpNeedsHttp, $"'{ip}' is an IP address, which only HTTP-01 can prove"));
        }

        if (request.Profile is { Length: > MaxProfileLength } || request.PreferredChain is { Length: > MaxChainLength })
        {
            return (names, new ConfigError("/profile", IssuanceErrorCodes.TextTooLong, $"the profile is limited to {MaxProfileLength} and the preferred chain to {MaxChainLength} characters"));
        }

        if (request.Label is { Length: > CertificateService.MaxLabelLength })
        {
            return (names, new ConfigError("/label", IssuanceErrorCodes.TextTooLong, $"the label is limited to {CertificateService.MaxLabelLength} characters"));
        }

        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var account = await context.AcmeAccounts.AsNoTracking()
                .Where(a => a.Id == request.AccountId).Select(a => (AcmeAccountStatus?)a.Status).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (account is null)
            {
                return (names, new ConfigError("/accountId", IssuanceErrorCodes.AccountUnknown, "there is no such ACME account"));
            }

            if (account != AcmeAccountStatus.Active)
            {
                return (names, new ConfigError("/accountId", IssuanceErrorCodes.AccountInactive, "the ACME account is deactivated"));
            }

            if (request.ChallengeType == AcmeChallengeType.Dns01)
            {
                if (request.DnsProviderId is not { } provider)
                {
                    return (names, new ConfigError("/dnsProviderId", IssuanceErrorCodes.DnsProviderRequired, "DNS-01 needs a DNS provider"));
                }

                if (!await context.DnsProviders.AnyAsync(p => p.Id == provider, cancellationToken).ConfigureAwait(false))
                {
                    return (names, new ConfigError("/dnsProviderId", IssuanceErrorCodes.DnsProviderUnknown, "there is no such DNS provider"));
                }
            }
            else if (request.DnsProviderId is not null)
            {
                return (names, new ConfigError("/dnsProviderId", IssuanceErrorCodes.DnsProviderNotAllowed, "a DNS provider only goes with DNS-01"));
            }
        }

        return (names, null);
    }

    private static AcmeIdentifier ToIdentifier(string name) =>
        IPAddress.TryParse(name, out var address) ? AcmeIdentifier.Ip(address) : AcmeIdentifier.Dns(name);

    private async Task<AcmeOrderRow?> LoadAsync(CertificateId id, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.AcmeOrders.AsNoTracking().FirstOrDefaultAsync(o => o.CertificateId == id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AcmeAccountRow?> LoadAccountAsync(AcmeAccountId id, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.AcmeAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the in-memory <paramref name="row"/> and to the stored one, and saves. Plain
    /// progress is saved without the writer lock or an audit event: only the result of an attempt (issued, failed) is audited.
    /// </summary>
    private async Task UpdateAsync(AcmeOrderRow row, Action<AcmeOrderRow> change, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        change(row);
        row.UpdatedAt = now;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var stored = await context.AcmeOrders.FirstAsync(o => o.Id == row.Id, cancellationToken).ConfigureAwait(false);
            change(stored);
            stored.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IssuanceResult> RunAsync(CertificateId id, CommandOrigin origin, IProgress<AcmeOrderProgress>? progress, CancellationToken cancellationToken)
    {
        if (!_running.TryAdd(id, 0))
        {
            return new IssuanceResult(IssuanceOutcome.InProgress, id);
        }

        try
        {
            // Read under the guard: what is decided from the row (a renewal or a continuation) must not be stale.
            var row = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
            return row is null ? new IssuanceResult(IssuanceOutcome.NotFound, id) : await RunCoreAsync(row, origin, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _progress.TryRemove(id, out _);
            _running.TryRemove(id, out _);
        }
    }

    /// <summary>One run for a row read under the certificate's guard: a finished issuance starts a new order, anything else continues.</summary>
    private async Task<IssuanceResult> RunCoreAsync(AcmeOrderRow row, CommandOrigin origin, IProgress<AcmeOrderProgress>? progress, CancellationToken cancellationToken)
    {
        var id = row.CertificateId;
        if (_config.Status is { AcceptsCommands: false } unusable)
        {
            // Nothing could be recorded, so don't start: the CA would be asked again every look without a backoff.
            return new IssuanceResult(
                IssuanceOutcome.Unavailable,
                id,
                Errors: [new ConfigError(string.Empty, ConfigService.StoreUnavailableCode, $"The config store is unavailable, so issuance is paused: {unusable.Problem}.")]);
        }

        var startFresh = row.Status == AcmeOrderStatus.Valid;
        try
        {
            var account = await LoadAccountAsync(row.AccountId, cancellationToken).ConfigureAwait(false)
                          ?? throw new IssuanceFailureException("The ACME account of this certificate no longer exists.");
            if (account.Status != AcmeAccountStatus.Active)
            {
                throw new IssuanceFailureException("The ACME account of this certificate is deactivated; order it again with another account.");
            }

            Attempt attempt = new(
                row,
                account,
                _clients.GetClient(new Uri(account.DirectoryUrl)),
                new AcmeAccountRef(account.Id, new Uri(account.AccountUrl)),
                origin,
                progress);
            Report(attempt, AcmeProgressStage.Starting);
            LogAttempt(id, row.Status, startFresh);
            await UpdateAsync(row, r => r.LastAttemptAt = _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var info = await DriveAsync(attempt, startFresh, cancellationToken).ConfigureAwait(false);
            var issued = await _certificates.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            LogIssued(id, info.Sha256Fingerprint, info.NotAfter);
            Report(attempt, AcmeProgressStage.Issued);
            return new IssuanceResult(IssuanceOutcome.Issued, id, issued?.Summary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the row says where the order stands, and the next start continues from there.
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(row, ex, origin, progress).ConfigureAwait(false);
        }
    }

    /// <summary>Records a failed attempt: the row, an audit event, and the certificate's own status where it matters.</summary>
    private async Task<IssuanceResult> FailAsync(AcmeOrderRow row, Exception ex, CommandOrigin origin, IProgress<AcmeOrderProgress>? progress)
    {
        var (kind, retryAfter, message) = Classify(ex);
        message = IssuanceMapping.Sanitize(message, 1024);
        // The CA no longer knows the account: it cannot order again, so say so on the account row too.
        var accountGone = ex is AcmeProblemException { Type: AcmeProblemTypes.AccountDoesNotExist };
        var now = _time.GetUtcNow();
        var attempts = row.Attempts + 1;
        var retryAt = now + RenewalSchedule.Backoff(kind, attempts, retryAfter, _random(), _options);
        // Work that cannot be continued (the staged key is gone, the CSR was refused) must not be resumed: start over next time.
        var dropOrder = row.AttachedInfo is null
                        && (ex is StagedKeyMismatchException or IssuanceFailureException { DropsOrder: true }
                            || (ex is AcmeProblemException && row.Status == AcmeOrderStatus.Finalizing && kind == AcmeFailureKind.Permanent));

        // The failure is recorded even if the caller is cancelling: that is what the scheduler needs to see after a restart.
        var token = CancellationToken.None;
        await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                var stored = await context.AcmeOrders.FirstOrDefaultAsync(o => o.Id == row.Id, token).ConfigureAwait(false);
                if (stored is null)
                {
                    return (0, null);
                }

                stored.Status = AcmeOrderStatus.Failed;
                stored.Error = message;
                stored.Attempts = attempts;
                stored.LastAttemptAt = now;
                stored.RetryAt = retryAt;
                stored.UpdatedAt = now;
                if (dropOrder)
                {
                    stored.OrderUrl = null;
                    stored.StagedKeyId = null;
                    stored.Csr = null;
                }

                if (accountGone)
                {
                    var account = await context.AcmeAccounts.FirstOrDefaultAsync(a => a.Id == row.AccountId, token).ConfigureAwait(false);
                    if (account is { Status: AcmeAccountStatus.Active })
                    {
                        account.Status = AcmeAccountStatus.Deactivated;
                        account.UpdatedAt = now;
                    }
                }

                await context.SaveChangesAsync(token).ConfigureAwait(false);
                var audit = IssuanceMapping.Entry(origin, "acme.order.failed", stored.Id, AuditOutcome.Failed) with
                {
                    Details = AuditDetails.Json(w =>
                    {
                        w.WriteString("certificate", row.CertificateId.ToString());
                        w.WriteString("kind", kind.ToString());
                        w.WriteNumber("attempts", attempts);
                        w.WriteString("retryAt", retryAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        w.WriteString("message", message);
                    }),
                };
                return (0, audit);
            },
            _ => 0,
            token).ConfigureAwait(false);
        row.Status = AcmeOrderStatus.Failed;
        row.Error = message;
        row.Attempts = attempts;
        row.RetryAt = retryAt;

        LogFailed(row.CertificateId, kind, attempts, retryAt, message);
        Report(row, progress, new AcmeOrderProgress(row.CertificateId, AcmeProgressStage.Failed, row.ChallengeType, now, message, retryAt));

        var certificate = await _certificates.GetAsync(row.CertificateId, token).ConfigureAwait(false);
        if (certificate is { Summary: { } summary }
            && (summary.Status == CertificateStatus.Pending || (summary.Status == CertificateStatus.Active && now > summary.NotAfter)))
        {
            // Nothing usable came of it: say so on the certificate. A certificate that still serves keeps serving.
            _ = await _certificates.SetStatusAsync(row.CertificateId, CertificateStatus.Failed, message, CommandOrigin.System, token).ConfigureAwait(false);
            certificate = await _certificates.GetAsync(row.CertificateId, token).ConfigureAwait(false);
        }

        return new IssuanceResult(IssuanceOutcome.Failed, row.CertificateId, certificate?.Summary, message);
    }

    /// <summary>What kind of failure this is, how long the CA asked to wait, and what to tell the operator.</summary>
    private static (AcmeFailureKind Kind, TimeSpan? RetryAfter, string Message) Classify(Exception ex) => ex switch
    {
        AcmeException acme => (acme.Kind, acme.RetryAfter, IssuanceMapping.Sanitize(acme.Message)),
        IssuanceFailureException failure => (failure.Kind, null, failure.Message),
        DnsProviderException dns => (dns.IsRateLimited ? AcmeFailureKind.RateLimited : dns.IsTransient ? AcmeFailureKind.Transient : AcmeFailureKind.Permanent, dns.RetryAfter, $"DNS provider: {IssuanceMapping.Sanitize(dns.Message)}"),
        StagedKeyMismatchException => (AcmeFailureKind.Transient, null, "The vault no longer holds the key staged for this order; the next attempt starts a new order."),
        HttpRequestException or TimeoutException or IOException => (AcmeFailureKind.Transient, null, $"{ex.GetType().Name}: {IssuanceMapping.Sanitize(ex.Message)}"),
        _ => (AcmeFailureKind.Permanent, null, $"{ex.GetType().Name}: {IssuanceMapping.Sanitize(ex.Message)}"),
    };

    /// <summary>The state of one run: the order row (updated in memory as it is saved), its account, the client and who hears of its steps.</summary>
    private sealed record Attempt(AcmeOrderRow Row, AcmeAccountRow Account, AcmeClient Client, AcmeAccountRef Ref, CommandOrigin Origin, IProgress<AcmeOrderProgress>? Progress);

    /// <summary>Records that the run reached <paramref name="stage"/>, for <see cref="GetProgress"/> and the caller's reporter.</summary>
    private void Report(Attempt attempt, AcmeProgressStage stage, string? detail = null) =>
        Report(attempt.Row, attempt.Progress, new AcmeOrderProgress(attempt.Row.CertificateId, stage, attempt.Row.ChallengeType, _time.GetUtcNow(), detail));

    private void Report(AcmeOrderRow row, IProgress<AcmeOrderProgress>? progress, AcmeOrderProgress step)
    {
        _progress[row.CertificateId] = step;
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(step);
        }
#pragma warning disable CA1031 // A watcher's failure (a closed page) must never fail the issuance it watches.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogProgressReporterFailed(row.CertificateId, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: a progress reporter failed ({ErrorType}); the issuance goes on.")]
    private partial void LogProgressReporterFailed(CertificateId certificate, string errorType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate}: due ({Status}).")]
    private partial void LogDue(CertificateId certificate, AcmeOrderStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate}: ACME attempt starts (order was {Status}, fresh: {Fresh}).")]
    private partial void LogAttempt(CertificateId certificate, AcmeOrderStatus status, bool fresh);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate} issued (fingerprint {Fingerprint}, valid until {NotAfter:O}).")]
    private partial void LogIssued(CertificateId certificate, string fingerprint, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: ACME attempt {Attempts} failed ({Kind}); next try at {RetryAt:O}. {Message}")]
    private partial void LogFailed(CertificateId certificate, AcmeFailureKind kind, int attempts, DateTimeOffset retryAt, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Certificate {Certificate}: renewal planned for {RenewAt:O}.")]
    private partial void LogRenewalInfo(CertificateId certificate, DateTimeOffset renewAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: the vault already holds this order's chain (a crash lost the record of attaching it); registering it.")]
    private partial void LogAttachRepaired(CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: asking the CA for renewal info failed ({ErrorType}); asking again later.")]
    private partial void LogRenewalInfoFailed(CertificateId certificate, string errorType);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Certificate {Certificate}: {Step}.")]
    private partial void LogStep(CertificateId certificate, string step);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: the DNS propagation check failed ({ErrorType}); waiting the fixed delay instead.")]
    private partial void LogPropagationCheckFailed(CertificateId certificate, string errorType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: removing the DNS challenge record failed ({ErrorType}); it may stay in the zone.")]
    private partial void LogDnsCleanupFailed(CertificateId certificate, string errorType);
}

/// <summary>An issuance cannot go on for a reason Paduraksa itself found (not a CA problem document): the CA offers
/// no challenge of the chosen type, an authorization or order went invalid, an account is gone.</summary>
internal sealed class IssuanceFailureException(string message, AcmeFailureKind kind = AcmeFailureKind.Permanent, bool dropsOrder = false) : Exception(message)
{
    public AcmeFailureKind Kind { get; } = kind;

    /// <summary>The order cannot be continued (it is invalid, or the state saved for it is unusable): the next attempt starts a new one.</summary>
    public bool DropsOrder { get; } = dropsOrder;
}
