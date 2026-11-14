using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Notifications;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data.Issuance;

namespace Paduraksa.Application.Issuance;

public sealed partial class AcmeIssuanceService
{
    /// <summary>The most steps one run takes (order, authorizations, finalize, download): a CA that keeps changing its mind does not loop it.</summary>
    private const int MaxSteps = 8;

    /// <summary>
    /// Brings the order to a certificate by following the CA's own state: it reads the stored order (or makes a new one),
    /// then does what the order's status asks, until it is valid and the chain is attached and registered.
    /// </summary>
    private async Task<CertificateInfo> DriveAsync(Attempt attempt, bool startFresh, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        if (startFresh)
        {
            await UpdateAsync(
                row,
                r =>
                {
                    Forget(r);
                    r.Status = AcmeOrderStatus.Pending;
                },
                cancellationToken).ConfigureAwait(false);
        }

        // The vault already attached this order's chain (a restart or a failed registration caught it): only the registration is left.
        if (row.AttachedInfo is { } attached)
        {
            if (ReadAttached(attached) is { } info)
            {
                LogStep(row.CertificateId, "registering the attached certificate");
                return await RegisterIssuedAsync(attempt, info).ConfigureAwait(false);
            }

            // Unreadable saved facts (a hand edit, another build): nothing can be registered from them, so start over with a new order.
            LogStep(row.CertificateId, "the saved certificate facts are unreadable; starting a new order");
            await UpdateAsync(row, Forget, cancellationToken).ConfigureAwait(false);
        }

        var order = await FindUsableOrderAsync(attempt, cancellationToken).ConfigureAwait(false);
        order ??= await CreateOrderAsync(attempt, cancellationToken).ConfigureAwait(false);

        for (var step = 0; step < MaxSteps; step++)
        {
            switch (order.Status)
            {
                case AcmeStatus.Pending:
                    LogStep(row.CertificateId, "proving the names");
                    order = await FulfilAsync(attempt, order, cancellationToken).ConfigureAwait(false);
                    break;
                case AcmeStatus.Ready:
                    LogStep(row.CertificateId, "finalizing");
                    order = await FinalizeAsync(attempt, order, cancellationToken).ConfigureAwait(false);
                    break;
                case AcmeStatus.Processing:
                    LogStep(row.CertificateId, "waiting for the certificate");
                    Report(attempt, AcmeProgressStage.Finalizing);
                    order = await attempt.Client.WaitForOrderIssuedAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
                    break;
                case AcmeStatus.Valid:
                    return await CompleteAsync(attempt, order, cancellationToken).ConfigureAwait(false);
                case AcmeStatus.Invalid:
                    throw new IssuanceFailureException(DescribeInvalidOrder(order), dropsOrder: true);
                default:
                    throw new AcmeProtocolException($"The order is {order.Status}, which this client cannot continue.");
            }
        }

        throw new AcmeProtocolException("The order did not reach a certificate within the expected number of steps.");
    }

    /// <summary>The order the row names, read from the CA, if the CA still accepts it; otherwise forgets it and returns null.</summary>
    private async Task<AcmeOrder?> FindUsableOrderAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        if (row.OrderUrl is not { } url)
        {
            return null;
        }

        AcmeOrder? order;
        try
        {
            order = await attempt.Client.GetOrderAsync(attempt.Ref, new Uri(url), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeProblemException ex) when (ex.Kind == AcmeFailureKind.Permanent && ex.Type != AcmeProblemTypes.AccountDoesNotExist)
        {
            // The CA refuses to show it (gone, not ours, malformed): it cannot be continued.
            order = null;
        }

        if (order is { Status: AcmeStatus.Pending or AcmeStatus.Ready or AcmeStatus.Processing or AcmeStatus.Valid })
        {
            LogStep(row.CertificateId, "resuming the order at the CA");
            Report(attempt, AcmeProgressStage.OrderCreated, "Resumed the order the CA already has.");
            return order;
        }

        LogStep(row.CertificateId, "the stored order is no longer usable");
        await UpdateAsync(row, Forget, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Whether the optional <c>replaces</c> hint may be dropped and the order repeated immediately: the CA says another
    /// order already replaced it, or answers a bare <c>serverInternal</c> 500 (Pebble's ARI order lookup lags). A 503, a
    /// 408, a bad nonce, a rate limit or any answer with <c>Retry-After</c> is left to the normal backoff.
    /// </summary>
    private static bool MayDropReplaces(AcmeProblemException ex) =>
        ex.Type == AcmeProblemTypes.AlreadyReplaced
        || (ex.Type == AcmeProblemTypes.ServerInternal && ex.Status == 500 && ex.RetryAfter is null);

    /// <summary>A new order for the row's names, naming the current certificate as the one it replaces (ARI) when there is one.</summary>
    private async Task<AcmeOrder> CreateOrderAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        var current = await _certificates.GetAsync(row.CertificateId, cancellationToken).ConfigureAwait(false);
        var replaces = current is { Summary: { Status: CertificateStatus.Active, AuthorityKeyIdentifier: { } aki } summary }
            ? AcmeRenewalInfoId.Create(aki, summary.SerialNumber)
            : null;
        AcmeIdentifier[] identifiers = [.. row.Names.Select(ToIdentifier)];

        AcmeOrder order;
        try
        {
            order = await attempt.Client.CreateOrderAsync(attempt.Ref, new AcmeNewOrderRequest(identifiers, row.Profile, replaces), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeProblemException ex) when (replaces is not null && MayDropReplaces(ex))
        {
            // The `replaces` hint is optional in a narrow case: another order already replaces this certificate (a manual
            // one, or ours before a restart), or the CA answered a bare serverInternal 500 while looking the old order up
            // (Pebble's index can lag). Never retry on 503/408/badNonce/rateLimited or when the CA sent Retry-After: those
            // are a CA outage or a limit, where an immediate second order ignores the CA's timing, doubles its load and
            // loses the ARI renewals' exemption.
            LogReplacesDropped(row.CertificateId, ex.Type);
            order = await attempt.Client.CreateOrderAsync(attempt.Ref, new AcmeNewOrderRequest(identifiers, row.Profile), cancellationToken).ConfigureAwait(false);
        }

        await UpdateAsync(
            row,
            r =>
            {
                r.OrderUrl = order.Url.AbsoluteUri;
                r.StagedKeyId = null;
                r.Csr = null;
                r.Status = AcmeOrderStatus.Ordered;
            },
            cancellationToken).ConfigureAwait(false);
        LogStep(row.CertificateId, "order created");
        Report(attempt, AcmeProgressStage.OrderCreated, Plural(row.Names.Count, "name", "names") + " ordered.");
        return order;
    }

    /// <summary>
    /// Proves every pending authorization: publishes what the challenge type needs (an HTTP-01 answer in the store, DNS-01 TXT
    /// records through the provider), tells the CA to validate, waits for the verdicts and, whatever happens, takes the
    /// published answers down again. Returns the order once the CA calls it ready.
    /// </summary>
    private async Task<AcmeOrder> FulfilAsync(Attempt attempt, AcmeOrder order, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        Report(attempt, AcmeProgressStage.Authorizing, Plural(order.Authorizations.Count, "authorization", "authorizations") + " to check.");
        var challengeType = row.ChallengeType.ToName();
        List<(AcmeAuthorization Authorization, AcmeChallenge Challenge, string KeyAuthorization)> pending = [];
        foreach (var authorizationUrl in order.Authorizations)
        {
            var authorization = await attempt.Client.GetAuthorizationAsync(attempt.Ref, authorizationUrl, cancellationToken).ConfigureAwait(false);
            if (authorization.Status == AcmeStatus.Valid)
            {
                continue;
            }

            if (authorization.Status != AcmeStatus.Pending)
            {
                throw new IssuanceFailureException(DescribeAuthorization(authorization), dropsOrder: true);
            }

            var challenge = authorization.FindChallenge(challengeType)
                            ?? throw new IssuanceFailureException($"The CA offers no {challengeType} challenge for {authorization.Identifier.Value}.", dropsOrder: true);
            var keyAuthorization = await attempt.Client.GetKeyAuthorizationAsync(attempt.Account.Id, challenge, cancellationToken).ConfigureAwait(false);
            pending.Add((authorization, challenge, keyAuthorization));
        }

        var dns = row.ChallengeType == AcmeChallengeType.Dns01 && pending.Count > 0 ? await OpenDnsProviderAsync(row, cancellationToken).ConfigureAwait(false) : null;
        List<string> tokens = [];
        List<(string Name, string Value)> records = [];
        List<string> delegated = [];
        try
        {
            foreach (var (authorization, challenge, keyAuthorization) in pending)
            {
                if (dns is null)
                {
                    try
                    {
                        _challenges.Publish(challenge.Token!, keyAuthorization);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // The store is full of other issuances' answers: they finish or time out, so this is worth trying again.
                        throw new IssuanceFailureException(ex.Message, AcmeFailureKind.Transient);
                    }

                    tokens.Add(challenge.Token!);
                }
                else
                {
                    var name = AcmeChallenges.Dns01RecordName(authorization.Identifier);
                    var target = _challengeNames is null || !Options.DnsPropagationCheck ? name : await _challengeNames.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(target, name, StringComparison.Ordinal))
                    {
                        delegated.Add(name);
                    }

                    records.Add((target, AcmeChallenges.Dns01TxtValue(keyAuthorization)));
                }
            }

            if (dns is not null && records.Count > 0)
            {
                // One call for the order's records: a provider that limits writes (deSEC) can send them together.
                await dns.CreateTxtBatchAsync([.. records.Select(r => new DnsChallengeRecord(r.Name, r.Value))], cancellationToken).ConfigureAwait(false);
            }

            if (pending.Count > 0)
            {
                Report(
                    attempt,
                    AcmeProgressStage.ChallengePublished,
                    records.Count > 0
                        ? $"{Plural(records.Count, "TXT record", "TXT records")} at {string.Join(", ", records.Select(r => r.Name).Distinct(StringComparer.Ordinal))}"
                          + (delegated.Count > 0 ? $" (followed the CNAME of {string.Join(", ", delegated.Distinct(StringComparer.Ordinal))})." : ".")
                        : $"{Plural(tokens.Count, "HTTP-01 answer", "HTTP-01 answers")} served under /.well-known/acme-challenge/.");
            }

            if (records.Count > 0)
            {
                Report(attempt, AcmeProgressStage.PropagationCheck, "Waiting for the zone's authoritative name servers to return the records.");
                await WaitForDnsAsync(row.CertificateId, records, cancellationToken).ConfigureAwait(false);
            }

            if (pending.Count > 0)
            {
                Report(attempt, AcmeProgressStage.Validating);
            }

            foreach (var (_, challenge, _) in pending)
            {
                // A challenge the CA is already validating (a resumed order) is only waited for.
                if (challenge.Status == AcmeStatus.Pending)
                {
                    _ = await attempt.Client.RespondToChallengeAsync(attempt.Ref, challenge, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var (authorization, _, _) in pending)
            {
                var done = await attempt.Client.WaitForAuthorizationAsync(attempt.Ref, authorization.Url, cancellationToken).ConfigureAwait(false);
                if (done.Status != AcmeStatus.Valid)
                {
                    throw new IssuanceFailureException(DescribeAuthorization(done), dropsOrder: true);
                }
            }
        }
        finally
        {
            await CleanUpAsync(row.CertificateId, dns, tokens, records).ConfigureAwait(false);
        }

        var ready = await attempt.Client.WaitForOrderReadyAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
        Report(attempt, AcmeProgressStage.Validated);
        return ready;
    }

    /// <summary>
    /// Waits until the CA can be expected to see the published TXT records: with a propagation check, until every authoritative name
    /// server returns them. A check that times out lets the CA try anyway (it may see them, and the CA's own verdict is the
    /// real answer); one that cannot be made (no name servers found, nobody answering, or a failure of its own) falls back to
    /// the fixed <see cref="AcmeIssuanceOptions.DnsPropagationDelay"/>, as does having no check at all.
    /// </summary>
    private async Task WaitForDnsAsync(CertificateId certificate, List<(string Name, string Value)> records, CancellationToken cancellationToken)
    {
        if (_propagation is not null && Options.DnsPropagationCheck)
        {
            try
            {
                var result = await _propagation.WaitAsync([.. records.Select(r => new DnsChallengeRecord(r.Name, r.Value))], cancellationToken).ConfigureAwait(false);
                if (result.Outcome == DnsPropagationOutcome.TimedOut && !result.AnyServerHadRecords)
                {
                    // Not one authoritative server returns the record, so the CA will not see it either, and a failed validation counts against its limits.
                    throw new IssuanceFailureException($"The DNS challenge records did not appear on the authoritative name servers in time. {IssuanceMapping.Sanitize(result.Detail)}", AcmeFailureKind.Transient);
                }

                if (result.Outcome != DnsPropagationOutcome.Unverifiable)
                {
                    return;
                }
            }
#pragma warning disable CA1031 // A broken check must not stop an issuance that a fixed delay can still serve.
            catch (Exception ex) when (ex is not OperationCanceledException and not IssuanceFailureException)
#pragma warning restore CA1031
            {
                LogPropagationCheckFailed(certificate, ex.GetType().Name);
            }
        }

        if (Options.DnsPropagationDelay > TimeSpan.Zero)
        {
            await Task.Delay(Options.DnsPropagationDelay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan DnsCleanupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DnsCleanupMaxWait = TimeSpan.FromSeconds(65);
    private const int DnsCleanupAttempts = 3;

    private async Task<IDnsProvider> OpenDnsProviderAsync(AcmeOrderRow row, CancellationToken cancellationToken)
    {
        DnsProviderRow? provider = null;
        if (row.DnsProviderId is { } id)
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                provider = await context.DnsProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken).ConfigureAwait(false);
            }
        }

        if (provider is null || _dns is null)
        {
            throw new IssuanceFailureException(provider is null ? "The DNS provider of this certificate no longer exists." : "No DNS provider implementations are installed.");
        }

        try
        {
            return _dns.Create(provider.Kind, provider.CredentialSecretId);
        }
        catch (NotSupportedException ex)
        {
            throw new IssuanceFailureException(IssuanceMapping.Sanitize(ex.Message));
        }
    }

    /// <summary>Removes the published challenge answers. Runs on failure and on cancellation too, and does not stop
    /// at one failing delete: a stale TXT record is only clutter, but a stale token would keep being answered. The
    /// DNS deletes are one batch with their own three minutes, independent of the caller's token (which may be the
    /// very cancellation that brought us here); a hung provider cannot hold a shutdown longer.</summary>
    private async Task CleanUpAsync(CertificateId certificate, IDnsProvider? dns, List<string> tokens, List<(string Name, string Value)> records)
    {
        foreach (var token in tokens)
        {
            _ = _challenges.Remove(token);
        }

        if (dns is null || records.Count == 0)
        {
            return;
        }

        // The system clock, so that a hung provider is cut off in real time whatever the test or host clock does.
        using CancellationTokenSource timeout = new(DnsCleanupTimeout);
        DnsChallengeRecord[] batch = [.. records.Select(r => new DnsChallengeRecord(r.Name, r.Value))];
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await dns.DeleteTxtBatchAsync(batch, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (DnsProviderException ex) when (ex.IsTransient && attempt < DnsCleanupAttempts && !timeout.IsCancellationRequested)
            {
                // Throttled or briefly unreachable: wait as asked (bounded) and delete again, which is idempotent.
                var wait = ex.RetryAfter is { } asked && asked > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(asked.Ticks, DnsCleanupMaxWait.Ticks)) : TimeSpan.FromSeconds(5);
                try
                {
                    await Task.Delay(wait, _time, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogDnsCleanupFailed(certificate, ex.GetType().Name);
                    return;
                }
            }
#pragma warning disable CA1031 // A failed cleanup must not hide the result of the issuance.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogDnsCleanupFailed(certificate, ex.GetType().Name);
                return;
            }
        }
    }

    /// <summary>
    /// Has the vault stage a key and make the CSR (kept on the row <i>before</i> the CA is asked to finalize, so a restart
    /// can still pair the chain with the key), submits it and waits for the certificate. A CSR saved by an earlier run of
    /// the same order is submitted again.
    /// </summary>
    private async Task<AcmeOrder> FinalizeAsync(Attempt attempt, AcmeOrder order, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        Report(attempt, AcmeProgressStage.Finalizing);
        if (row.StagedKeyId is null || row.Csr is null)
        {
            var csr = await _vault.CreateKeyAndCsrAsync(row.CertificateId, row.KeySpec, row.Names, cancellationToken).ConfigureAwait(false);
            await UpdateAsync(
                row,
                r =>
                {
                    r.StagedKeyId = csr.StagedKey;
                    r.Csr = csr.Der.ToArray();
                    r.Status = AcmeOrderStatus.Finalizing;
                },
                cancellationToken).ConfigureAwait(false);
        }
        else if (row.Status != AcmeOrderStatus.Finalizing)
        {
            await UpdateAsync(row, r => r.Status = AcmeOrderStatus.Finalizing, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            _ = await attempt.Client.FinalizeOrderAsync(attempt.Ref, order, row.Csr!, cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeProblemException ex) when (ex.Type == AcmeProblemTypes.OrderNotReady)
        {
            // A finalize that raced with another one (ours before a restart): the order may be processing already. Look, then continue.
            var current = await attempt.Client.GetOrderAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
            if (current.Status is not (AcmeStatus.Processing or AcmeStatus.Valid))
            {
                throw;
            }
        }

        return await attempt.Client.WaitForOrderIssuedAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The order is valid: downloads the chain (the preferred one if the CA offers it), has the vault attach it to the staged
    /// key, registers the certificate (which republishes the live config), plans the renewal and records the success.
    /// </summary>
    private async Task<CertificateInfo> CompleteAsync(Attempt attempt, AcmeOrder order, CancellationToken cancellationToken)
    {
        var row = attempt.Row;
        if (row.StagedKeyId is not { } staged)
        {
            throw new IssuanceFailureException(
                "The CA issued the certificate but no key is staged for it here; the next attempt starts a new order.", AcmeFailureKind.Transient, dropsOrder: true);
        }

        var certificateUrl = order.Certificate ?? throw new AcmeProtocolException("The order is valid but has no certificate URL.");
        var download = await attempt.Client.DownloadCertificateAsync(attempt.Ref, certificateUrl, row.PreferredChain, cancellationToken).ConfigureAwait(false);
        CertificateInfo info;
        try
        {
            info = await _vault.AttachChainAsync(row.CertificateId, staged, Encoding.ASCII.GetBytes(download.PemChain), cancellationToken).ConfigureAwait(false);
        }
        catch (StagedKeyMismatchException)
        {
            // The staged key is spent: either another key was staged since (a real mismatch), or this very chain was attached before a
            // crash lost the record of it. The vault knows which: if it already serves this leaf, only the registration is missing.
            var held = await FindAttachedAsync(row.CertificateId, download.PemChain, cancellationToken).ConfigureAwait(false);
            if (held is null)
            {
                throw;
            }

            info = held;
            LogAttachRepaired(row.CertificateId);
        }

        // From here the vault serves the new certificate and the staged key is spent: nothing may be left to the caller's token.
        // The public facts are saved first, so whatever happens next (a crash, a refused registration) only has registration left to do.
        await UpdateAsync(row, r => r.AttachedInfo = JsonSerializer.Serialize(info), CancellationToken.None).ConfigureAwait(false);
        return await RegisterIssuedAsync(attempt, info).ConfigureAwait(false);
    }

    /// <summary>The vault's description of the certificate it holds under <paramref name="id"/> when its leaf is the one in <paramref name="pemChain"/> (this order's: the SHA-256 of the leaf's DER is the described fingerprint), else null.</summary>
    private async Task<CertificateInfo?> FindAttachedAsync(CertificateId id, string pemChain, CancellationToken cancellationToken)
    {
        if (await _catalog.DescribeAsync(id, cancellationToken).ConfigureAwait(false) is not { } held)
        {
            return null;
        }

        try
        {
            var orderLeaf = Convert.ToHexStringLower(SHA256.HashData(AcmePem.LeafDer(pemChain)));
            return string.Equals(held.Info.Sha256Fingerprint, orderLeaf, StringComparison.Ordinal) ? held.Info : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers the certificate the vault attached (which republishes the live config), plans the renewal and records the
    /// success. Runs to the end whatever the caller does: a refused registration because the store is busy is tried again
    /// a few times, and the attached facts stay on the row until it is done.
    /// </summary>
    private async Task<CertificateInfo> RegisterIssuedAsync(Attempt attempt, CertificateInfo info)
    {
        var row = attempt.Row;
        var cancellationToken = CancellationToken.None;
        var registered = await _certificates.RegisterAsync(
            new CertificateRegistration(row.CertificateId, CertificateSource.Acme, info), attempt.Origin, cancellationToken).ConfigureAwait(false);
        for (var retry = 0; retry < 4 && registered.Status == CertificateChangeStatus.Unavailable; retry++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), _time, cancellationToken).ConfigureAwait(false);
            registered = await _certificates.RegisterAsync(
                new CertificateRegistration(row.CertificateId, CertificateSource.Acme, info), attempt.Origin, cancellationToken).ConfigureAwait(false);
        }

        if (registered.Status is not (CertificateChangeStatus.Committed or CertificateChangeStatus.Unchanged))
        {
            throw new IssuanceFailureException(
                $"The certificate was issued and attached, but recording it failed: {(registered.Errors.Count > 0 ? registered.Errors[0].Message : registered.Status.ToString())}",
                registered.Status == CertificateChangeStatus.Unavailable ? AcmeFailureKind.Transient : AcmeFailureKind.Permanent);
        }

        AcmeRenewalInfo? renewalInfo = null;
        var renewalInfoFailed = false;
        try
        {
            renewalInfo = await attempt.Client.GetRenewalInfoAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeException ex)
        {
            renewalInfoFailed = true;
            LogRenewalInfoFailed(row.CertificateId, ex.GetType().Name);
        }

        var now = _time.GetUtcNow();
        var wasFailing = row.Attempts > 0;
        var renewAt = RenewalSchedule.NextRenewalAt(info.NotBefore, info.NotAfter, renewalInfo, _random(), Options);
        DateTimeOffset? ariCheckAt = renewalInfo is not null ? RenewalSchedule.NextAriCheck(now, renewalInfo.RetryAfter, Options)
            : renewalInfoFailed ? now + Options.AriMinRecheck
            : null;
        await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                var stored = await context.AcmeOrders.FirstAsync(o => o.Id == row.Id, CancellationToken.None).ConfigureAwait(false);
                stored.Status = AcmeOrderStatus.Valid;
                stored.Error = null;
                stored.Attempts = 0;
                stored.RetryAt = null;
                stored.LastAttemptAt = now;
                stored.OrderUrl = null;
                stored.StagedKeyId = null;
                stored.Csr = null;
                stored.AttachedInfo = null;
                stored.NextRenewalAt = renewAt;
                stored.AriWindowStart = renewalInfo?.WindowStart;
                stored.AriWindowEnd = renewalInfo?.WindowEnd;
                stored.AriExplanationUrl = renewalInfo?.ExplanationUrl?.AbsoluteUri;
                stored.AriCheckAt = ariCheckAt;
                stored.UpdatedAt = now;
                await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                var audit = IssuanceMapping.Entry(attempt.Origin, "acme.order.issued", stored.Id, AuditOutcome.Success) with
                {
                    Details = AuditDetails.Json(w =>
                    {
                        w.WriteString("certificate", row.CertificateId.ToString());
                        w.WriteString("fingerprint", info.Sha256Fingerprint);
                        w.WriteString("notAfter", info.NotAfter.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        w.WriteString("renewAt", renewAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                        w.WriteBoolean("ari", renewalInfo is not null);
                    }),
                };
                return (0, audit);
            },
            reason => throw new InvalidOperationException($"The config store became unavailable: {reason}"),
            CancellationToken.None).ConfigureAwait(false);

        row.Status = AcmeOrderStatus.Valid;
        row.Error = null;
        row.Attempts = 0;
        row.RetryAt = null;
        row.NextRenewalAt = renewAt;
        LogRenewalInfo(row.CertificateId, renewAt);
        if (wasFailing)
        {
            _notifications?.Publish(NotificationText.AcmeRecovered(
                row.Names.FirstOrDefault() ?? row.CertificateId.ToString(), row.CertificateId, info.NotAfter, now));
        }

        return info;
    }

    private static CertificateInfo? ReadAttached(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CertificateInfo>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Forgets the stored order and everything that belongs to it: the next run makes a new order.</summary>
    private static void Forget(AcmeOrderRow row)
    {
        row.OrderUrl = null;
        row.StagedKeyId = null;
        row.Csr = null;
        row.AttachedInfo = null;
    }

    private static string DescribeInvalidOrder(AcmeOrder order) =>
        order.Error is { } problem
            ? $"The CA marked the order invalid: {IssuanceMapping.Sanitize(problem.Type, 200)}{(problem.Detail is null ? string.Empty : $" ({IssuanceMapping.Sanitize(problem.Detail)})")}"
            : "The CA marked the order invalid.";

    /// <summary>What went wrong with an authorization: its status and the CA's reason from its challenges, sanitised.</summary>
    private static string DescribeAuthorization(AcmeAuthorization authorization)
    {
        var problem = authorization.Challenges.Select(c => c.Error).FirstOrDefault(e => e is not null);
        var detail = problem is null
            ? string.Empty
            : $": {IssuanceMapping.Sanitize(problem.Type, 200)}{(problem.Detail is null ? string.Empty : $" ({IssuanceMapping.Sanitize(problem.Detail)})")}";
        return $"The CA's validation of {authorization.Identifier.Value} ended {authorization.Status.ToString().ToLowerInvariant()}{detail}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Certificate {Certificate}: the CA could not use the ARI replaces hint ({ProblemType}); ordering again without it.")]
    private partial void LogReplacesDropped(CertificateId certificate, string problemType);

    private static string Plural(int count, string one, string many) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}
