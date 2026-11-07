using System.Globalization;
using System.Net;

using Microsoft.Extensions.Logging;

using Paduraksa.Acme;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Acme.Fakes;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The renewal scheduler (PLAN §9, §9.3) on a virtual clock: nothing happens before an order's time, the ARI window and the
/// lifetime rule decide when, failures wait out their backoff, a rate limit waits for the CA's Retry-After, and an
/// unfinished order is resumed.
/// </summary>
public sealed class AcmeRenewalSchedulerTests
{
    private static void AdvanceTo(IssuanceHarness h, DateTimeOffset moment) => h.Time.Now += moment - h.Time.GetUtcNow();

    private static HttpResponseMessage ServerError() =>
        FakeAcmeServer.Problem(HttpStatusCode.ServiceUnavailable, "urn:ietf:params:acme:error:serverInternal", "the CA is down");

    private static DateTimeOffset WholeSeconds(DateTimeOffset moment) => new(moment.Ticks - (moment.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);

    private static string Window(DateTimeOffset start, DateTimeOffset end) =>
        $$"""{"suggestedWindow":{"start":"{{start.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)}}","end":"{{end.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)}}"},"explanationURL":"https://ca.test/why"}""";

    /// <summary>The ARI ID of the first certificate the fake vault attaches (serial 1000).</summary>
    private static string FirstAriId => AcmeRenewalInfoId.Create("abcd1234", "1000");

    private static async Task<(IssuanceHarness Harness, CertificateId Id)> IssuedAsync(FakeAcmeServer? server = null, Action<IssuanceHarness>? beforeIssuing = null)
    {
        var h = await IssuanceHarness.CreateAsync(server);
        beforeIssuing?.Invoke(h);
        var account = await h.CreateAccountAsync();
        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        return (h, result.CertificateId!.Value);
    }

    [Fact]
    public async Task RenewsAtTheRenewalTimeAndNotBefore()
    {
        var (h, id) = await IssuedAsync();
        await using var _ = h;
        var due = (await h.OrderAsync(id)).NextRenewalAt!.Value;
        var firstNotAfter = (await h.CertificateAsync(id)).NotAfter;
        // About 30 days before expiry for a 90-day certificate.
        Assert.InRange(firstNotAfter - due, TimeSpan.FromDays(30), TimeSpan.FromDays(33.1));

        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));
        AdvanceTo(h, due - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));
        Assert.Equal(1, h.Server.OrderCount);

        AdvanceTo(h, due);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        Assert.Equal(2, h.Server.OrderCount);
        var order = await h.OrderAsync(id);
        Assert.True(order.NextRenewalAt > due);
        Assert.True((await h.CertificateAsync(id)).NotAfter > firstNotAfter);
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task TheAriWindowDecidesWhenAndThePickIsStable()
    {
        var server = new FakeAcmeServer { RenewalInfoRetryAfterSeconds = 21600 };
        var (h, id) = await IssuedAsync(server, h =>
        {
            var start = WholeSeconds(h.Time.GetUtcNow()) + TimeSpan.FromDays(10);
            h.Random = 0.25;
            server.RenewalInfoBodies[FirstAriId] = Window(start, start + TimeSpan.FromDays(2));
        });
        await using var _ = h;
        var order = await h.OrderAsync(id);
        var windowStart = order.AriWindowStart!.Value;

        // A quarter into the window, not a third of the lifetime; the CA's explanation and Retry-After are kept.
        Assert.Equal(windowStart + TimeSpan.FromHours(12), order.NextRenewalAt);
        Assert.Equal(windowStart + TimeSpan.FromDays(2), order.AriWindowEnd);
        Assert.Equal("https://ca.test/why", order.AriExplanationUrl);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(6), order.AriCheckAt);

        // Six hours on, ARI is asked again. The same window changes nothing, whatever the dice would now say.
        h.Random = 0.9;
        AdvanceTo(h, order.AriCheckAt!.Value);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));
        var rechecked = await h.OrderAsync(id);
        Assert.Equal(order.NextRenewalAt, rechecked.NextRenewalAt);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromHours(6), rechecked.AriCheckAt);
        Assert.Equal(1, h.Server.OrderCount);

        // The CA moves the window (a revocation event, say): the pick is made again inside the new one.
        var moved = windowStart + TimeSpan.FromDays(30);
        server.RenewalInfoBodies[FirstAriId] = Window(moved, moved + TimeSpan.FromDays(1));
        AdvanceTo(h, rechecked.AriCheckAt!.Value);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));
        var shifted = await h.OrderAsync(id);
        Assert.Equal(moved, shifted.AriWindowStart);
        Assert.Equal(moved + TimeSpan.FromHours(21.6), shifted.NextRenewalAt);
    }

    [Fact]
    public async Task AnOpenAriWindowMakesTheRenewalDueRightAway()
    {
        var server = new FakeAcmeServer();
        var (h, id) = await IssuedAsync(server, h =>
        {
            // A window the CA opened in the past (it wants the certificate replaced now).
            var start = WholeSeconds(h.Time.GetUtcNow()) - TimeSpan.FromHours(2);
            server.RenewalInfoBodies[FirstAriId] = Window(start, start + TimeSpan.FromHours(1));
        });
        await using var _ = h;
        Assert.True((await h.OrderAsync(id)).NextRenewalAt <= h.Time.GetUtcNow());

        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task AFailedRenewalWaitsOutAnExponentialBackoffAndTheCertificateKeepsServing()
    {
        var (h, id) = await IssuedAsync();
        await using var _ = h;
        AdvanceTo(h, (await h.OrderAsync(id)).NextRenewalAt!.Value);
        h.Server.Inject("new-order", _ => ServerError());

        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        var first = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Failed, 1), (first.Status, first.Attempts));
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(5), first.RetryAt);
        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(id)).Status);
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));

        AdvanceTo(h, first.RetryAt!.Value);
        h.Server.Inject("new-order", _ => ServerError());
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));
        var second = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Failed, 2), (second.Status, second.Attempts));
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromMinutes(10), second.RetryAt);

        AdvanceTo(h, second.RetryAt!.Value - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));
        AdvanceTo(h, second.RetryAt.Value);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        var done = await h.OrderAsync(id);
        Assert.Equal((AcmeOrderStatus.Valid, 0, null), (done.Status, done.Attempts, done.RetryAt));
        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task ARateLimitWaitsForTheCasRetryAfter()
    {
        var (h, id) = await IssuedAsync();
        await using var _ = h;
        AdvanceTo(h, (await h.OrderAsync(id)).NextRenewalAt!.Value);
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.TooManyRequests, "urn:ietf:params:acme:error:rateLimited", "too many", retryAfter: "259200"));

        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        var limited = await h.OrderAsync(id);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromDays(3), limited.RetryAt);
        AdvanceTo(h, limited.RetryAt!.Value - TimeSpan.FromHours(1));
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));
        AdvanceTo(h, limited.RetryAt.Value);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));
        Assert.Equal(AcmeOrderStatus.Valid, (await h.OrderAsync(id)).Status);
    }

    [Fact]
    public async Task AFirstIssuanceThatFailedIsRetriedWhenItsBackoffEnds()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { FailValidation = true });
        var account = await h.CreateAccountAsync();
        var failed = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        Assert.Equal(CertificateStatus.Failed, failed.Certificate!.Status);
        Assert.Equal(0, await h.Scheduler.RunOnceAsync(Ct));

        h.Server.FailValidation = false;
        AdvanceTo(h, (await h.OrderAsync(failed.CertificateId!.Value)).RetryAt!.Value);
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));

        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(failed.CertificateId.Value)).Status);
    }

    [Fact]
    public async Task AnUnfinishedOrderIsResumedAfterARestart()
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
        h.Hook.After = null;

        var restarted = h.NewScheduler(h.NewIssuance());
        Assert.Equal(1, await restarted.RunOnceAsync(Ct));

        var id = (await h.Issuance.ListOrdersAsync(Ct)).Single().CertificateId;
        Assert.Equal(CertificateStatus.Active, (await h.CertificateAsync(id)).Status);
        Assert.Equal(1, h.Server.OrderCount);
        Assert.Equal(0, await restarted.RunOnceAsync(Ct));
    }

    [Fact]
    public async Task OneBrokenCertificateDoesNotStopTheOthers()
    {
        var (h, _) = await IssuedAsync();
        await using var harness = h;
        var account = (await h.Accounts.ListAsync(Ct)).Single();
        var second = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "other.example.com"), Admin, Ct);
        Assert.Equal(IssuanceOutcome.Issued, second.Outcome);
        // Both come due; the CA fails the first renewal it sees and serves the other.
        AdvanceTo(h, (await h.Issuance.ListOrdersAsync(Ct)).Max(o => o.NextRenewalAt)!.Value);
        h.Server.Inject("new-order", _ => ServerError());

        Assert.Equal(2, await h.Scheduler.RunOnceAsync(Ct));

        var orders = await h.Issuance.ListOrdersAsync(Ct);
        Assert.Equal([AcmeOrderStatus.Valid, AcmeOrderStatus.Failed], orders.Select(o => o.Status).Order());
    }

    [Fact]
    public async Task ACertificateRenewedWhileTheSchedulerWorkedIsNotRenewedAgain()
    {
        var (h, _) = await IssuedAsync();
        await using var harness = h;
        var account = (await h.Accounts.ListAsync(Ct)).Single();
        var second = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "other.example.com"), Admin, Ct);
        var orders = await h.Issuance.ListOrdersAsync(Ct);
        // Both are due. The scheduler lists them, and while it renews the newer one, someone renews the other by hand.
        AdvanceTo(h, orders.Max(o => o.NextRenewalAt)!.Value);
        var older = orders.Single(o => o.CertificateId != second.CertificateId!.Value).CertificateId;
        var manual = false;
        h.Hook.Before = async (path, _) =>
        {
            if (!manual && path == "new-order")
            {
                manual = true;
                Assert.Equal(IssuanceOutcome.Issued, (await h.Issuance.RenewAsync(older, Admin, Ct)).Outcome);
            }
        };

        var acted = await h.Scheduler.RunOnceAsync(Ct);

        // Two issuances plus one scheduled renewal plus the manual one; not a fifth order for the certificate renewed by hand.
        Assert.Equal(1, acted);
        Assert.Equal(4, h.Server.OrderCount);
    }

    [Fact]
    public async Task AFailedOrderIsNotRetriedBeforeItsRetryAtEvenIfTheListingSawItDue()
    {
        var (h, id) = await IssuedAsync();
        await using var harness = h;
        AdvanceTo(h, (await h.OrderAsync(id)).NextRenewalAt!.Value);
        h.Server.Inject("new-order", _ => ServerError());
        Assert.Equal(1, await h.Scheduler.RunOnceAsync(Ct));
        var failed = await h.OrderAsync(id);

        // The row says wait, whatever an older listing said: the service decides from its own read.
        Assert.False(await h.Issuance.RunDueAsync(id, Ct));
        AdvanceTo(h, failed.RetryAt!.Value);
        Assert.True(await h.Issuance.RunDueAsync(id, Ct));
        Assert.Equal(2, h.Server.OrderCount);
    }

    [Fact]
    public async Task AbsurdPacingFromTheOptionsDoesNotEndTheLoop()
    {
        var (h, _) = await IssuedAsync();
        await using var _ = h;
        // 60 days is more than Task.Delay accepts at once: the loop pauses for at most a day instead of faulting.
        var scheduler = new AcmeRenewalScheduler(
            h.Issuance,
            h.Options with { SchedulerStartDelay = TimeSpan.FromDays(60), SchedulerPollInterval = TimeSpan.FromDays(60) },
            TimeProvider.System,
            h.Loggers.CreateLogger<AcmeRenewalScheduler>());

        await scheduler.StartAsync(Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);

        Assert.False(scheduler.ExecuteTask!.IsCompleted);
        await scheduler.StopAsync(Ct);
        Assert.True(scheduler.ExecuteTask.IsCompletedSuccessfully);
        Assert.DoesNotContain(h.Loggers.Entries, e => e.Message.Contains("look failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADrainingSchedulerStartsNothingNewAndAnIdleOneStopsAtOnce()
    {
        var (h, id) = await IssuedAsync();
        await using var _ = h;
        AdvanceTo(h, (await h.OrderAsync(id)).NextRenewalAt!.Value);
        // A start delay far beyond the test keeps the scheduler idle until the drain: nothing can race it into a look.
        var scheduler = new AcmeRenewalScheduler(
            h.Issuance,
            h.Options with { SchedulerStartDelay = TimeSpan.FromHours(1) },
            TimeProvider.System,
            h.Loggers.CreateLogger<AcmeRenewalScheduler>());
        await scheduler.StartAsync(Ct);

        // Idle (in its start delay): the grace is not waited for.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.DrainAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"drained after {watch.Elapsed}");
        Assert.True(scheduler.ExecuteTask!.IsCompletedSuccessfully);
        // A look made now, after the drain began, leaves the due renewal alone.
        Assert.Equal(0, await scheduler.RunOnceAsync(Ct));
        Assert.Equal(1, h.Server.OrderCount);
    }
}
