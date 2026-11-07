using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Certificates;
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
        AcmeOrderRow row = attempt.Row;
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
            LogStep(row.CertificateId, "registering the attached certificate");
            return await RegisterIssuedAsync(attempt, JsonSerializer.Deserialize<CertificateInfo>(attached)!).ConfigureAwait(false);
        }

        AcmeOrder? order = await FindUsableOrderAsync(attempt, cancellationToken).ConfigureAwait(false);
        order ??= await CreateOrderAsync(attempt, cancellationToken).ConfigureAwait(false);

        for (int step = 0; step < MaxSteps; step++)
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
        AcmeOrderRow row = attempt.Row;
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
            return order;
        }

        LogStep(row.CertificateId, "the stored order is no longer usable");
        await UpdateAsync(row, Forget, cancellationToken).ConfigureAwait(false);
        return null;
    }

    /// <summary>A new order for the row's names, naming the current certificate as the one it replaces (ARI) when there is one.</summary>
    private async Task<AcmeOrder> CreateOrderAsync(Attempt attempt, CancellationToken cancellationToken)
    {
        AcmeOrderRow row = attempt.Row;
        CertificateDetail? current = await _certificates.GetAsync(row.CertificateId, cancellationToken).ConfigureAwait(false);
        string? replaces = current is { Summary: { Status: CertificateStatus.Active, AuthorityKeyIdentifier: { } aki } summary }
            ? AcmeRenewalInfoId.Create(aki, summary.SerialNumber)
            : null;
        AcmeIdentifier[] identifiers = [.. row.Names.Select(ToIdentifier)];

        AcmeOrder order;
        try
        {
            order = await attempt.Client.CreateOrderAsync(attempt.Ref, new AcmeNewOrderRequest(identifiers, row.Profile, replaces), cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeProblemException ex) when (replaces is not null && ex.Type == AcmeProblemTypes.AlreadyReplaced)
        {
            // Another order already replaces this certificate (a manual one, or ours before a restart): order without the hint.
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
        return order;
    }

    /// <summary>
    /// Proves every pending authorization: publishes what the challenge type needs (an HTTP-01 answer in the store, DNS-01 TXT
    /// records through the provider), tells the CA to validate, waits for the verdicts and, whatever happens, takes the
    /// published answers down again. Returns the order once the CA calls it ready.
    /// </summary>
    private async Task<AcmeOrder> FulfilAsync(Attempt attempt, AcmeOrder order, CancellationToken cancellationToken)
    {
        AcmeOrderRow row = attempt.Row;
        string challengeType = row.ChallengeType.ToName();
        List<(AcmeAuthorization Authorization, AcmeChallenge Challenge, string KeyAuthorization)> pending = [];
        foreach (Uri authorizationUrl in order.Authorizations)
        {
            AcmeAuthorization authorization = await attempt.Client.GetAuthorizationAsync(attempt.Ref, authorizationUrl, cancellationToken).ConfigureAwait(false);
            if (authorization.Status == AcmeStatus.Valid)
            {
                continue;
            }

            if (authorization.Status != AcmeStatus.Pending)
            {
                throw new IssuanceFailureException(DescribeAuthorization(authorization), dropsOrder: true);
            }

            AcmeChallenge challenge = authorization.FindChallenge(challengeType)
                ?? throw new IssuanceFailureException($"The CA offers no {challengeType} challenge for {authorization.Identifier.Value}.", dropsOrder: true);
            string keyAuthorization = await attempt.Client.GetKeyAuthorizationAsync(attempt.Account.Id, challenge, cancellationToken).ConfigureAwait(false);
            pending.Add((authorization, challenge, keyAuthorization));
        }

        IDnsProvider? dns = row.ChallengeType == AcmeChallengeType.Dns01 && pending.Count > 0 ? await OpenDnsProviderAsync(row, cancellationToken).ConfigureAwait(false) : null;
        List<string> tokens = [];
        List<(string Name, string Value)> records = [];
        try
        {
            foreach ((AcmeAuthorization authorization, AcmeChallenge challenge, string keyAuthorization) in pending)
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
                    records.Add((AcmeChallenges.Dns01RecordName(authorization.Identifier), AcmeChallenges.Dns01TxtValue(keyAuthorization)));
                }
            }

            if (dns is not null && records.Count > 0)
            {
                // One call for the order's records: a provider that limits writes (deSEC) can send them together.
                await dns.CreateTxtBatchAsync([.. records.Select(r => new DnsChallengeRecord(r.Name, r.Value))], cancellationToken).ConfigureAwait(false);
            }

            if (records.Count > 0)
            {
                await WaitForDnsAsync(row.CertificateId, records, cancellationToken).ConfigureAwait(false);
            }

            foreach ((AcmeAuthorization _, AcmeChallenge challenge, string _) in pending)
            {
                // A challenge the CA is already validating (a resumed order) is only waited for.
                if (challenge.Status == AcmeStatus.Pending)
                {
                    _ = await attempt.Client.RespondToChallengeAsync(attempt.Ref, challenge, cancellationToken).ConfigureAwait(false);
                }
            }

            foreach ((AcmeAuthorization authorization, AcmeChallenge _, string _) in pending)
            {
                AcmeAuthorization done = await attempt.Client.WaitForAuthorizationAsync(attempt.Ref, authorization.Url, cancellationToken).ConfigureAwait(false);
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

        AcmeOrder ready = await attempt.Client.WaitForOrderReadyAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
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
        if (_propagation is not null)
        {
            try
            {
                DnsPropagationResult result = await _propagation.WaitAsync([.. records.Select(r => new DnsChallengeRecord(r.Name, r.Value))], cancellationToken).ConfigureAwait(false);
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

        if (_options.DnsPropagationDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.DnsPropagationDelay, _time, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Removes the published challenge answers. Runs on failure and on cancellation too, and does not stop at one failing delete: a
    /// stale TXT record is only clutter, but a stale token would keep being answered. The DNS deletes are one batch with their own
    /// three minutes, independent of the caller's token (which may be the very cancellation that brought us here); a hung provider
    /// cannot hold a shutdown longer. A transient failure (a throttle, say) is tried again up to three times, waiting as long as the
    /// provider asked, up to a minute.
    /// </summary>
    private async Task CleanUpAsync(CertificateId certificate, IDnsProvider? dns, List<string> tokens, List<(string Name, string Value)> records)
    {
        foreach (string token in tokens)
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
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await dns.DeleteTxtBatchAsync(batch, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (DnsProviderException ex) when (ex.IsTransient && attempt < DnsCleanupAttempts && !timeout.IsCancellationRequested)
            {
                // Throttled or briefly unreachable: wait as asked (bounded) and delete again, which is idempotent.
                TimeSpan wait = ex.RetryAfter is { } asked && asked > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(asked.Ticks, DnsCleanupMaxWait.Ticks)) : TimeSpan.FromSeconds(5);
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
        AcmeOrderRow row = attempt.Row;
        if (row.StagedKeyId is null || row.Csr is null)
        {
            CsrResult csr = await _vault.CreateKeyAndCsrAsync(row.CertificateId, row.KeySpec, row.Names, cancellationToken).ConfigureAwait(false);
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
            AcmeOrder current = await attempt.Client.GetOrderAsync(attempt.Ref, order.Url, cancellationToken).ConfigureAwait(false);
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
        AcmeOrderRow row = attempt.Row;
        if (row.StagedKeyId is not { } staged)
        {
            throw new IssuanceFailureException(
                "The CA issued the certificate but no key is staged for it here; the next attempt starts a new order.", AcmeFailureKind.Transient, dropsOrder: true);
        }

        Uri certificateUrl = order.Certificate ?? throw new AcmeProtocolException("The order is valid but has no certificate URL.");
        AcmeCertificateDownload download = await attempt.Client.DownloadCertificateAsync(attempt.Ref, certificateUrl, row.PreferredChain, cancellationToken).ConfigureAwait(false);
        CertificateInfo info;
        try
        {
            info = await _vault.AttachChainAsync(row.CertificateId, staged, Encoding.ASCII.GetBytes(download.PemChain), cancellationToken).ConfigureAwait(false);
        }
        catch (StagedKeyMismatchException)
        {
            // The staged key is spent: either another key was staged since (a real mismatch), or this very chain was attached before a
            // crash lost the record of it. The vault knows which: if it already serves this leaf, only the registration is missing.
            CertificateInfo? held = await FindAttachedAsync(row.CertificateId, download.PemChain, cancellationToken).ConfigureAwait(false);
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
        if (_catalog is null || await _catalog.DescribeAsync(id, cancellationToken).ConfigureAwait(false) is not { } held)
        {
            return null;
        }

        try
        {
            string orderLeaf = Convert.ToHexStringLower(SHA256.HashData(AcmePem.LeafDer(pemChain)));
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
        AcmeOrderRow row = attempt.Row;
        CancellationToken cancellationToken = CancellationToken.None;
        CertificateChangeResult registered = await _certificates.RegisterAsync(
            new CertificateRegistration(row.CertificateId, CertificateSource.Acme, info), attempt.Origin, cancellationToken).ConfigureAwait(false);
        for (int retry = 0; retry < 4 && registered.Status == CertificateChangeStatus.Unavailable; retry++)
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
        bool renewalInfoFailed = false;
        try
        {
            renewalInfo = await attempt.Client.GetRenewalInfoAsync(info, cancellationToken).ConfigureAwait(false);
        }
        catch (AcmeException ex)
        {
            renewalInfoFailed = true;
            LogRenewalInfoFailed(row.CertificateId, ex.GetType().Name);
        }

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset renewAt = RenewalSchedule.NextRenewalAt(info.NotBefore, info.NotAfter, renewalInfo, _random(), _options);
        DateTimeOffset? ariCheckAt = renewalInfo is not null ? RenewalSchedule.NextAriCheck(now, renewalInfo.RetryAfter, _options)
            : renewalInfoFailed ? now + _options.AriMinRecheck
            : null;
        await _config.ApplyOperationalChangeAsync(
            async context =>
            {
                AcmeOrderRow stored = await context.AcmeOrders.FirstAsync(o => o.Id == row.Id, CancellationToken.None).ConfigureAwait(false);
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
                AuditEntry audit = IssuanceMapping.Entry(attempt.Origin, "acme.order.issued", stored.Id, AuditOutcome.Success) with
                {
                    Details = IssuanceMapping.Json(w =>
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
        return info;
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
        AcmeProblem? problem = authorization.Challenges.Select(c => c.Error).FirstOrDefault(e => e is not null);
        string detail = problem is null
            ? string.Empty
            : $": {IssuanceMapping.Sanitize(problem.Type, 200)}{(problem.Detail is null ? string.Empty : $" ({IssuanceMapping.Sanitize(problem.Detail)})")}";
        return $"The CA's validation of {authorization.Identifier.Value} ended {authorization.Status.ToString().ToLowerInvariant()}{detail}";
    }
}
