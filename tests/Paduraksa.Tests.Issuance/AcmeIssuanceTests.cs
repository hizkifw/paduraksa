using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Acme;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Acme.Fakes;
using Paduraksa.Tests.Data.Commands;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The issuance orchestrator (PLAN §9.3) against the fake CA, on a real database: the happy paths for both challenge types,
/// failures and what they leave behind, resuming where a crash stopped, renewals and the one-at-a-time rule.
/// </summary>
public sealed partial class AcmeIssuanceTests
{
    [GeneratedRegex("BEGIN CERTIFICATE")]
    private static partial Regex CertificateBlock();

    private static async Task<CertificateId> OnlyCertificateAsync(IssuanceHarness h) => (await h.Issuance.ListOrdersAsync(Ct)).Single().CertificateId;

    private static HttpResponseMessage Unavailable() =>
        FakeAcmeServer.Problem(HttpStatusCode.ServiceUnavailable, "urn:ietf:params:acme:error:serverInternal", "the CA is down");

    [Fact]
    public async Task Http01IssuanceRegistersTheCertificateAndPlansTheRenewal()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var generation = h.Config.Store.Current.Generation;
        var publishedDuringValidation = -1;
        h.Hook.Before = (path, _) =>
        {
            if (path.StartsWith("chall/", StringComparison.Ordinal))
            {
                publishedDuringValidation = h.Challenges.Count;
            }

            return Task.CompletedTask;
        };

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "App.Example.com.", "www.example.com", "192.0.2.7"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        var certificate = result.Certificate!;
        Assert.Equal((CertificateSource.Acme, CertificateStatus.Active), (certificate.Source, certificate.Status));
        Assert.Equal(["192.0.2.7", "app.example.com", "www.example.com"], certificate.Names);
        // The answers were published when the CA was told to validate, and are gone afterwards.
        Assert.Equal(3, publishedDuringValidation);
        Assert.Equal(0, h.Challenges.Count);
        // The vault made the CSR for the normalised names and the chain went to it (the CA's default chain).
        var csr = Assert.Single(h.Vault.Csrs);
        Assert.Equal((certificate.Id, KeySpec.EcdsaP256), (csr.Id, csr.Spec));
        Assert.Equal(["app.example.com", "www.example.com", "192.0.2.7"], csr.Names);
        Assert.Equal(3, CertificateBlock().Count(Assert.Single(h.Vault.Attached).Chain));
        Assert.Equal(h.Server.CsrOf(0), [0x30, 0x03, 0x02, 0x01, 1]);
        Assert.Null(h.Server.ReplacesOf(0));
        // The row now holds the schedule: no ARI body, so a third of the lifetime before expiry, less half the jitter.
        var order = await h.OrderAsync(certificate.Id);
        Assert.Equal((AcmeOrderStatus.Valid, 0, null), (order.Status, order.Attempts, order.Error));
        var remaining = (certificate.NotAfter - certificate.NotBefore) * (1d / 3d);
        Assert.Equal(certificate.NotAfter - remaining - (remaining * (0.1 * 0.5)), order.NextRenewalAt);
        Assert.Null(order.RetryAt);
        // The existing republish ran, and the success is audited.
        Assert.True(h.Config.Store.Current.Generation > generation);
        var audit = Assert.Single(await h.AuditAsync("acme.order.issued"));
        Assert.Equal((AuditOutcome.Success, order.OrderId.ToString()), (audit.Outcome, audit.Target));
        await h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task APreferredChainIsHonoured()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com") with { PreferredChain = "ISRG Root X2" }, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        // The default chain has three certificates; the alternate one, ending at X2, has two.
        Assert.Equal(2, CertificateBlock().Count(Assert.Single(h.Vault.Attached).Chain));
    }

    [Fact]
    public async Task Dns01IssuanceUsesTheProviderForWildcardAndBaseNameThenCleansUp()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var provider = await h.CreateDnsProviderAsync();
        List<(string Name, string Value)> seen = [];
        h.Hook.Before = (path, _) =>
        {
            if (path.StartsWith("chall/", StringComparison.Ordinal) && seen.Count == 0)
            {
                seen.AddRange(h.Dns.Provider.Records);
            }

            return Task.CompletedTask;
        };

        var result = await h.Issuance.RequestCertificateAsync(Dns01(account.Id, provider.Id, "example.com", "*.example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        // Both records sit at one name with different values when the CA validates; none is left afterwards.
        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.Equal("_acme-challenge.example.com", r.Name));
        Assert.NotEqual(seen[0].Value, seen[1].Value);
        Assert.Empty(h.Dns.Provider.Records);
        Assert.Equal(["create", "create", "delete", "delete"], h.Dns.Provider.Calls.Select(c => c.Split(' ')[0]));
        Assert.Equal((DnsProviderKind.Cloudflare, provider.CredentialSecretId), Assert.Single(h.Dns.Created));
        // DNS needs time to propagate before the CA is asked.
        Assert.Contains(h.Options.DnsPropagationDelay, h.Time.Delays);
        Assert.Equal(["*.example.com", "example.com"], result.Certificate!.Names);
    }

    [Fact]
    public async Task ADnsFailureIsTransientKeepsTheOrderAndTakesDownWhatWasPublished()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var provider = await h.CreateDnsProviderAsync();
        h.Dns.Provider.CreateFailure = new Paduraksa.Acme.Dns.DnsProviderException("zone is busy", isTransient: true);

        var failed = await h.Issuance.RequestCertificateAsync(Dns01(account.Id, provider.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("zone is busy", failed.Message, StringComparison.Ordinal);
        var order = await h.OrderAsync(failed.CertificateId!.Value);
        Assert.Equal((AcmeOrderStatus.Failed, 1), (order.Status, order.Attempts));
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(5), order.RetryAt);
        Assert.Empty(h.Dns.Provider.Records);

        // Fixed: the same order at the CA is continued, not a new one made.
        h.Dns.Provider.CreateFailure = null;
        var retried = await h.Issuance.RenewAsync(failed.CertificateId!.Value, Admin, Ct);
        Assert.Equal(IssuanceOutcome.Issued, retried.Outcome);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Equal((AcmeOrderStatus.Valid, 0), ((await h.OrderAsync(retried.CertificateId!.Value)).Status, (await h.OrderAsync(retried.CertificateId!.Value)).Attempts));
    }

    [Fact]
    public async Task AFailedValidationMarksTheCertificateFailedAndTheNextAttemptStartsANewOrder()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { FailValidation = true });
        var account = await h.CreateAccountAsync();

        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("ended invalid", failed.Message, StringComparison.Ordinal);
        var certificate = failed.Certificate!;
        Assert.Equal((CertificateStatus.Failed, failed.Message), (certificate.Status, certificate.StatusMessage));
        Assert.Equal(0, h.Challenges.Count);
        var order = await h.OrderAsync(certificate.Id);
        Assert.Equal((AcmeOrderStatus.Failed, 1), (order.Status, order.Attempts));
        // A validation failure is permanent until something changes: an hour, not minutes.
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(1), order.RetryAt);
        var audit = Assert.Single(await h.AuditAsync("acme.order.failed"));
        Assert.Equal(AuditOutcome.Failed, audit.Outcome);
        Assert.DoesNotContain("token", audit.Details, StringComparison.OrdinalIgnoreCase);

        h.Server.FailValidation = false;
        var retried = await h.Issuance.RenewAsync(certificate.Id, Admin, Ct);

        // The invalid order was dropped; the retry made a new one and the certificate recovered.
        Assert.Equal(IssuanceOutcome.Issued, retried.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
        Assert.Equal((CertificateStatus.Active, null), (retried.Certificate!.Status, retried.Certificate.StatusMessage));
        Assert.Equal(0, (await h.OrderAsync(certificate.Id)).Attempts);
        await h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task APermanentCaRefusalIsRecordedWithTheCaTextAndALongBackoff()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, "urn:ietf:params:acme:error:rejectedIdentifier", "policy forbids bad.example.com"));

        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "bad.example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("rejectedIdentifier", failed.Message, StringComparison.Ordinal);
        Assert.Contains("policy forbids", failed.Message, StringComparison.Ordinal);
        Assert.Equal(CertificateStatus.Failed, failed.Certificate!.Status);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(1), (await h.OrderAsync(failed.CertificateId!.Value)).RetryAt);
    }

    [Fact]
    public async Task ACrashRightAfterFinalizeResumesTheSameOrderWithTheStagedKey()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        using var crash = new CancellationTokenSource();
        h.Hook.After = (path, _) =>
        {
            if (path.StartsWith("finalize/", StringComparison.Ordinal))
            {
                crash.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, crash.Token));

        // What a restart finds: an unfinished order that is not failed, with the staged key saved, a pending certificate.
        var id = await OnlyCertificateAsync(h);
        var stalled = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Finalizing, 0, null), (stalled.Status, stalled.Attempts, stalled.Error));
        Assert.Equal(CertificateStatus.Pending, (await h.CertificateAsync(id)).Status);
        Assert.NotNull(h.Vault.StagedFor(id));
        Assert.Empty(h.Vault.Attached);

        h.Hook.After = null;
        var resumed = await h.NewIssuance().RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Single(h.Vault.Csrs);
        Assert.Single(h.Vault.Attached);
        Assert.Equal((AcmeOrderStatus.Valid, CertificateStatus.Active), ((await h.OrderAsync(id)).Status, (await h.CertificateAsync(id)).Status));
    }

    [Fact]
    public async Task ACrashWhileTheCaValidatesResumesTheOrderAndPublishesAgain()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        using var crash = new CancellationTokenSource();
        h.Hook.After = (path, _) =>
        {
            if (path.StartsWith("chall/", StringComparison.Ordinal))
            {
                crash.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, crash.Token));

        var id = await OnlyCertificateAsync(h);
        Assert.Equal(AcmeOrderStatus.Ordered, (await h.OrderAsync(id)).Status);
        // A shutdown still takes the published answers down; the restart publishes them again.
        Assert.Equal(0, h.Challenges.Count);
        var publishedAgain = -1;
        h.Hook.After = null;
        h.Hook.Before = (path, _) =>
        {
            if (path.StartsWith("authz/", StringComparison.Ordinal))
            {
                publishedAgain = Math.Max(publishedAgain, h.Challenges.Count);
            }

            return Task.CompletedTask;
        };

        var resumed = await h.NewIssuance().RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Equal(0, h.Challenges.Count);
        Assert.Equal(1, publishedAgain);
    }

    [Fact]
    public async Task ASpentStagedKeyDropsTheOrderSoTheNextAttemptStartsOver()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Vault.AttachFailure = new StagedKeyMismatchException("staged again elsewhere");

        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("starts a new order", failed.Message, StringComparison.Ordinal);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(5), (await h.OrderAsync(failed.CertificateId!.Value)).RetryAt);

        var retried = await h.Issuance.RenewAsync(failed.CertificateId!.Value, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, retried.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
        Assert.Equal(2, h.Vault.Csrs.Count);
    }

    [Fact]
    public async Task RenewalKeepsTheIdOrdersWithTheReplacesHintAndSwapsTheMetadata()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = first.CertificateId!.Value;
        h.Time.Now += TimeSpan.FromDays(60);

        var renewed = await h.Issuance.RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, renewed.Outcome);
        Assert.Equal(id, renewed.CertificateId);
        // The fake CA serves the same leaf every time (its fingerprint is that leaf's), so the new facts are told by the serial.
        Assert.NotEqual(first.Certificate!.SerialNumber, renewed.Certificate!.SerialNumber);
        Assert.True(renewed.Certificate.NotAfter > first.Certificate.NotAfter);
        Assert.Equal(2, h.Server.OrderCount);
        // The CA is told which certificate this order replaces (ARI), by the first certificate's AKI and serial.
        Assert.Equal(AcmeRenewalInfoId.Create("abcd1234", first.Certificate.SerialNumber), h.Server.ReplacesOf(1));
        Assert.Single(await h.Certificates.ListAsync(null, Ct));
    }

    [Fact]
    public async Task ARateLimitedRenewalKeepsTheCertificateServingAndWaitsForRetryAfter()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = first.CertificateId!.Value;
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.TooManyRequests, "urn:ietf:params:acme:error:rateLimited", "too many orders", retryAfter: "7200"));

        var failed = await h.Issuance.RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        // Nothing usable was lost: the old certificate still serves.
        Assert.Equal(CertificateStatus.Active, failed.Certificate!.Status);
        Assert.Equal(first.Certificate!.Sha256Fingerprint, failed.Certificate.Sha256Fingerprint);
        var order = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Failed, 1), (order.Status, order.Attempts));
        // The CA asked for two hours, which is longer than the first rate-limit backoff (one hour).
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(2), order.RetryAt);
    }

    [Fact]
    public async Task ARenewalFailureOnAnExpiredCertificateMarksItFailed()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        h.Time.Now += TimeSpan.FromDays(91);
        h.Server.Inject("new-order", _ => Unavailable());

        var failed = await h.Issuance.RenewAsync(first.CertificateId!.Value, Admin, Ct);

        Assert.Equal((IssuanceOutcome.Failed, CertificateStatus.Failed), (failed.Outcome, failed.Certificate!.Status));
    }

    [Fact]
    public async Task OnlyOneIssuancePerCertificateRunsAtATime()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Hook.Before = async (path, _) =>
        {
            if (path == "new-order")
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var first = h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        await entered.Task;
        var id = await OnlyCertificateAsync(h);

        var second = await h.Issuance.RenewAsync(id, Admin, Ct);
        release.SetResult();

        Assert.Equal(IssuanceOutcome.InProgress, second.Outcome);
        Assert.Equal(IssuanceOutcome.Issued, (await first).Outcome);
        Assert.Equal(1, h.Server.OrderCount);
    }

    [Fact]
    public async Task AnUnknownCertificateHasNothingToRenew()
    {
        await using var h = await IssuanceHarness.CreateAsync();

        Assert.Equal(IssuanceOutcome.NotFound, (await h.Issuance.RenewAsync(CertificateId.New(), Admin, Ct)).Outcome);
        Assert.Null(await h.Issuance.GetOrderAsync(CertificateId.New(), Ct));
    }

    [Fact]
    public async Task RefusedRequestsRegisterNothing()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var provider = await h.CreateDnsProviderAsync();
        var deactivated = await h.CreateAccountAsync();
        Assert.Equal(IssuanceChangeStatus.Committed, (await h.Accounts.DeactivateAsync(deactivated.Id, Admin, Ct)).Status);

        (CertificateIssueRequest Request, string Code)[] cases =
        [
            (Http01(account.Id), IssuanceErrorCodes.NamesRequired),
            (Http01(account.Id, "not a name"), IssuanceErrorCodes.NameInvalid),
            (Http01(account.Id, "*.example.com"), IssuanceErrorCodes.WildcardNeedsDns),
            (Dns01(account.Id, provider.Id, "192.0.2.1"), IssuanceErrorCodes.IpNeedsHttp),
            (Http01(account.Id, "example.com") with { ChallengeType = AcmeChallengeType.Dns01 }, IssuanceErrorCodes.DnsProviderRequired),
            (Http01(account.Id, "example.com") with { DnsProviderId = provider.Id }, IssuanceErrorCodes.DnsProviderNotAllowed),
            (Dns01(account.Id, DnsProviderId.New(), "example.com"), IssuanceErrorCodes.DnsProviderUnknown),
            (Http01(AcmeAccountId.New(), "example.com"), IssuanceErrorCodes.AccountUnknown),
            (Http01(deactivated.Id, "example.com"), IssuanceErrorCodes.AccountInactive),
            (Http01(account.Id, "example.com") with { KeySpec = (KeySpec)99 }, IssuanceErrorCodes.KeySpecUndefined),
            (Http01(account.Id, [.. Enumerable.Range(0, AcmeIssuanceService.MaxNames + 1).Select(i => $"n{i}.example.com")]), IssuanceErrorCodes.TooManyNames),
            (Http01(account.Id, "example.com") with { Profile = new string('p', AcmeIssuanceService.MaxProfileLength + 1) }, IssuanceErrorCodes.TextTooLong),
            (Http01(account.Id, "example.com") with { PreferredChain = new string('c', AcmeIssuanceService.MaxChainLength + 1) }, IssuanceErrorCodes.TextTooLong),
        ];
        foreach (var (request, code) in cases)
        {
            var result = await h.Issuance.RequestCertificateAsync(request, Admin, Ct);

            Assert.Equal(IssuanceOutcome.Invalid, result.Outcome);
            Assert.Equal(code, Assert.Single(result.Errors).Code);
        }

        Assert.Empty(await h.Certificates.ListAsync(null, Ct));
        Assert.Empty(await h.Issuance.ListOrdersAsync(Ct));
        Assert.Equal(0, h.Server.OrderCount);
    }

    [Fact]
    public async Task AuthorizationsTheCaAlreadyHasValidNeedNoChallenge()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { ReuseValidAuthorizations = true });
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var challengesAnswered = h.Server.Requests.Count(r => r.Path.StartsWith("chall/", StringComparison.Ordinal));
        var publishedAgain = 0;
        h.Hook.Before = (path, _) =>
        {
            publishedAgain = Math.Max(publishedAgain, h.Challenges.Count);
            return Task.CompletedTask;
        };

        var renewed = await h.Issuance.RenewAsync(first.CertificateId!.Value, Admin, Ct);

        // The renewal's order came with the authorization already valid: nothing was published, nothing answered.
        Assert.Equal(IssuanceOutcome.Issued, renewed.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
        Assert.Equal(challengesAnswered, h.Server.Requests.Count(r => r.Path.StartsWith("chall/", StringComparison.Ordinal)));
        Assert.Equal(0, publishedAgain);
    }

    [Fact]
    public async Task ADeactivatedAuthorizationDropsTheOrderAndTheNextAttemptStartsOver()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var deactivated = false;
        h.Hook.Before = (path, _) =>
        {
            if (!deactivated && path.StartsWith("authz/", StringComparison.Ordinal))
            {
                deactivated = true;
                h.Server.DeactivateAuthorizationsOf(0);
            }

            return Task.CompletedTask;
        };

        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("ended deactivated", failed.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Challenges.Count);

        var retried = await h.Issuance.RenewAsync(failed.CertificateId!.Value, Admin, Ct);
        Assert.Equal(IssuanceOutcome.Issued, retried.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task AStoredOrderTheCaCallsInvalidIsReplacedOnResume()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        await CrashWhileTheCaValidatesAsync(h, account.Id);
        h.Server.InvalidateOrder(0);

        var resumed = await h.NewIssuance().RenewAsync(await OnlyCertificateAsync(h), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task AStoredOrderTheCaRefusesToShowIsReplacedOnResumeWhateverTheProblem()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        await CrashWhileTheCaValidatesAsync(h, account.Id);
        h.Server.Inject(h.Server.OrderPath(0), _ => FakeAcmeServer.Problem(HttpStatusCode.Unauthorized, AcmeProblemTypes.Prefix + "unauthorized", "not your order"));

        var resumed = await h.NewIssuance().RenewAsync(await OnlyCertificateAsync(h), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task AReplacesHintTheCaRefusesIsDroppedAndTheOrderRepeated()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.Conflict, AcmeProblemTypes.AlreadyReplaced, "already replaced"));

        var renewed = await h.Issuance.RenewAsync(first.CertificateId!.Value, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, renewed.Outcome);
        Assert.Equal(3, h.Server.To("new-order").Count());
        Assert.Equal(2, h.Server.OrderCount);
        Assert.Null(h.Server.ReplacesOf(1));
    }

    [Fact]
    public async Task AFinalizeThatRacedWithAnotherContinuesWithTheOrderInsteadOfDroppingIt()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { RaceFinalizeOnce = true });
        var account = await h.CreateAccountAsync();

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Single(h.Vault.Csrs);
    }

    [Fact]
    public async Task APermanentlyRefusedAccountIsMarkedDeactivated()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.BadRequest, AcmeProblemTypes.AccountDoesNotExist, "no such account"));

        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Equal(AcmeAccountStatus.Deactivated, (await h.Accounts.GetAsync(account.Id, Ct))!.Status);
    }

    [Fact]
    public async Task ACancellationAfterTheVaultAttachedStillRegistersTheCertificate()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        using var crash = new CancellationTokenSource();
        h.Vault.AfterAttach = crash.Cancel;

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, crash.Token);

        // The vault already serves the new certificate: the metadata and the schedule follow whatever the caller does.
        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(result.CertificateId!.Value)).Status);
        var order = await h.OrderAsync(result.CertificateId.Value);
        Assert.Equal(AcmeOrderStatus.Valid, order.Status);
        Assert.NotNull(order.NextRenewalAt);
        Assert.Null(await h.Config.Db.ScalarAsync("SELECT AttachedInfo FROM AcmeOrders"));
    }

    [Fact]
    public async Task AnOrderCaughtAfterTheAttachIsRegisteredFromTheSavedFactsWithoutAttachingAgain()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var first = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = first.CertificateId!.Value;
        // The state a hard crash between the attach and the registration leaves: the facts saved, the certificate row still the old one.
        var facts = System.Text.Json.JsonSerializer.Serialize(h.Vault.Issued[0] with { Sha256Fingerprint = new string('f', 64), SerialNumber = "FFFF" });
        await h.Config.Db.ExecuteAsync($"UPDATE AcmeOrders SET Status = 'finalizing', AttachedInfo = '{facts.Replace("'", "''", StringComparison.Ordinal)}'");

        var resumed = await h.NewIssuance().RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal("FFFF", resumed.Certificate!.SerialNumber);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Single(h.Vault.Attached);
        Assert.Single(h.Vault.Csrs);
        Assert.Equal(AcmeOrderStatus.Valid, (await h.OrderAsync(id)).Status);
        Assert.Null(await h.Config.Db.ScalarAsync("SELECT AttachedInfo FROM AcmeOrders"));
    }

    [Fact]
    public async Task AChainTheVaultAttachedBeforeACrashLostTheRecordIsRegisteredFromTheVaultsDescription()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        // The hard crash the saved facts can't cover: the vault stored the new pair, and the process died before it could say so.
        h.Vault.AfterAttach = () => throw new IOException("the process died");
        var crashed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        Assert.Equal(IssuanceOutcome.Failed, crashed.Outcome);
        var id = crashed.CertificateId!.Value;
        Assert.Equal(CertificateStatus.Failed, (await h.CertificateAsync(id)).Status);
        h.Vault.AfterAttach = null;

        var resumed = await h.NewIssuance().RenewAsync(id, Admin, Ct);

        // The staged key is spent, but the vault describes the chain this very order produced: nothing is attached or ordered again.
        Assert.Equal(IssuanceOutcome.Issued, resumed.Outcome);
        Assert.Equal(h.Vault.Issued[0].Sha256Fingerprint, resumed.Certificate!.Sha256Fingerprint);
        Assert.Equal(h.Vault.Issued[0].Sha256Fingerprint, (await h.CertificateAsync(id)).Sha256Fingerprint);
        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(id)).Status);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Single(h.Vault.Attached);
        Assert.Single(h.Vault.Csrs);
        var order = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Valid, 0), (order.Status, order.Attempts));
        Assert.NotNull(order.NextRenewalAt);
        Assert.Null(await h.Config.Db.ScalarAsync("SELECT AttachedInfo FROM AcmeOrders"));
    }

    [Fact]
    public async Task ASpentStagedKeyIsNotRepairedWhenTheVaultHoldsAnotherLeaf()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        h.Vault.AfterAttach = () => throw new IOException("the process died");
        var crashed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = crashed.CertificateId!.Value;
        h.Vault.AfterAttach = null;
        // Someone replaced the certificate in the meantime: the vault holds another leaf, not this order's.
        using var otherKey = System.Security.Cryptography.ECDsa.Create();
        using var other = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=other.example.com", otherKey, System.Security.Cryptography.HashAlgorithmName.SHA256)
            .CreateSelfSigned(h.Time.GetUtcNow().AddDays(-1), h.Time.GetUtcNow().AddDays(30));
        var otherPem = other.ExportCertificatePem();
        var held = h.Vault.Issued[0] with
        {
            PublicChainPem = otherPem,
            Sha256Fingerprint = Convert.ToHexStringLower(other.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)),
        };
        Assert.NotEqual(h.Vault.Issued[0].Sha256Fingerprint, held.Sha256Fingerprint);
        h.Vault.Hold(id, held);

        var resumed = await h.NewIssuance().RenewAsync(id, Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, resumed.Outcome);
        Assert.NotEqual(CertificateStatus.Active, (await h.CertificateAsync(id)).Status);
        Assert.Single(h.Vault.Attached);
    }

    [Fact]
    public async Task ARenewalInfoFailureIsRetriedSoonWithoutMovingTheSchedule()
    {
        var server = new FakeAcmeServer { RenewalInfoRetryAfterSeconds = 21600 };
        await using var h = await IssuanceHarness.CreateAsync(server);
        var ari = AcmeRenewalInfoId.Create("abcd1234", "1000");
        var start = h.Time.GetUtcNow() + TimeSpan.FromDays(10);
        server.RenewalInfoBodies[ari] =
            "{\"suggestedWindow\":{\"start\":\"" + start.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"end\":\"" + start.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\"}}";
        var account = await h.CreateAccountAsync();
        var issued = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = issued.CertificateId!.Value;
        var before = await h.OrderAsync(id);
        server.Inject("renewal-info/" + ari, _ => FakeAcmeServer.Problem(HttpStatusCode.InternalServerError, AcmeProblemTypes.ServerInternal, "ARI is down"));

        var refreshed = (await h.Issuance.RefreshRenewalInfoAsync(id, Ct))!;

        Assert.Equal(before.NextRenewalAt, refreshed.NextRenewalAt);
        Assert.Equal(before.AriWindowStart, refreshed.AriWindowStart);
        Assert.Equal(h.Time.GetUtcNow() + h.Options.AriMinRecheck, refreshed.AriCheckAt);
    }

    [Fact]
    public async Task NeitherTheAuditNorTheLogsCarryTokensOrKeyAuthorizations()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { FailValidation = true });
        var account = await h.CreateAccountAsync();
        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        h.Server.FailValidation = false;
        _ = await h.Issuance.RenewAsync(failed.CertificateId!.Value, Admin, Ct);

        string[] secrets = [.. h.Server.TokensOf(0), .. h.Server.TokensOf(1)];
        var audited = (await h.Config.AuditAsync()).Select(a => a.Details ?? string.Empty).ToList();
        Assert.NotEmpty(h.Loggers.Entries);
        Assert.NotEmpty(audited);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(h.Loggers.Entries, e => e.Message.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(audited, details => details.Contains(secret, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(h.Loggers.Entries, e => e.Message.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFullChallengeStoreIsATransientFailureNotAPermanentOne()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        AcmeChallengeStore full = new(capacity: 1);
        full.Publish("someone-elses-token", "answer");

        var failed = await h.NewIssuance(full).RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        Assert.Equal(IssuanceOutcome.Failed, failed.Outcome);
        Assert.Contains("challenge store", failed.Message, StringComparison.Ordinal);
        // Transient: five minutes, not the hour a permanent failure waits. The other issuance's answer is untouched.
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(5), (await h.OrderAsync(failed.CertificateId!.Value)).RetryAt);
        Assert.Equal(1, full.Count);
    }

    [Fact]
    public async Task WhileTheConfigStoreIsDegradedNothingIsAskedOfTheCa()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var issued = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = issued.CertificateId!.Value;

        // A restart that finds the latest revision damaged starts on the last-known-good file and refuses changes.
        var path = Path.Combine(Path.GetDirectoryName(h.Config.Db.DatabasePath)!, LastKnownGoodFile.FileName);
        await using (var writer = new LastKnownGoodFile(path))
        {
            using var store = new SnapshotStore();
            await using var publisher = new SnapshotPublisher(store);
            using var first = new ConfigService(h.Config.Db.Factory, new ConfigServiceHarness.FakeCompiler(), publisher, lastKnownGood: writer);
            await first.StartAsync(Ct);
            await first.StartupPublishFollowed;
        }

        await h.Config.Db.ExecuteAsync("UPDATE ConfigRevisions SET DocumentHash = zeroblob(32)");
        await using var lastGood = new LastKnownGoodFile(path);
        using var degradedStore = new SnapshotStore();
        await using var degradedPublisher = new SnapshotPublisher(degradedStore);
        using var degraded = new ConfigService(h.Config.Db.Factory, new ConfigServiceHarness.FakeCompiler(), degradedPublisher, lastKnownGood: lastGood);
        Assert.False((await degraded.StartAsync(Ct)).AcceptsCommands);
        using var clients = new AcmeClientProvider(h.Signer, null, null, () => h.Hook, h.Time);
        var issuance = new AcmeIssuanceService(
            h.Config.Db.Factory,
            degraded,
            new CertificateService(h.Config.Db.Factory, degraded, h.Time, NullLogger<CertificateService>.Instance),
            clients,
            h.Vault,
            h.Challenges,
            h.Dns,
            h.Options,
            h.Time,
            NullLogger<AcmeIssuanceService>.Instance);
        h.Time.Now += TimeSpan.FromDays(80);
        var requests = h.Server.Requests.Count;

        var renewed = await issuance.RenewAsync(id, Admin, Ct);
        var due = await issuance.RunDueAsync(id, Ct);

        // Paused, not failed: no CA request, no attempt counted, no backoff recorded, nothing to resume wrongly.
        Assert.Equal(IssuanceOutcome.Unavailable, renewed.Outcome);
        Assert.Equal(ConfigService.StoreUnavailableCode, Assert.Single(renewed.Errors).Code);
        Assert.False(due);
        Assert.Equal(requests, h.Server.Requests.Count);
        var order = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Valid, 0, null), (order.Status, order.Attempts, order.RetryAt));
    }

    private static async Task CrashWhileTheCaValidatesAsync(IssuanceHarness h, AcmeAccountId account)
    {
        using var crash = new CancellationTokenSource();
        h.Hook.After = (path, _) =>
        {
            if (path.StartsWith("chall/", StringComparison.Ordinal))
            {
                crash.Cancel();
            }

            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Issuance.RequestCertificateAsync(Http01(account, "example.com"), Admin, crash.Token));
        h.Hook.After = null;
    }
}
