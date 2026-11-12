using System.Net;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>What issuance tells the notification dispatcher: the first failure of a streak and the recovery, never every retry.</summary>
public sealed class AcmeNotificationTests
{
    private static CertificateIssueRequest Http01(AcmeAccountId account, params string[] names) => new(names, account, AcmeChallengeType.Http01);

    [Fact]
    public async Task OnlyTheFirstFailureOfAStreakIsPublishedAndTheRecoveryOnceAfterIt()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var issuance = h.NewIssuance();
        var account = await h.CreateAccountAsync();
        var first = await issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);
        var id = first.CertificateId!.Value;
        Assert.Empty(h.Events.Published);

        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.TooManyRequests, "urn:ietf:params:acme:error:rateLimited", "too many orders", retryAfter: "7200"));
        _ = await issuance.RenewAsync(id, Admin, Ct);
        var failed = Assert.Single(h.Events.Published);
        Assert.Equal(NotificationEventKind.AcmeRenewalFailed, failed.Kind);
        Assert.Contains("too many orders", failed.Body, StringComparison.Ordinal);
        Assert.Contains("example.com", failed.Title, StringComparison.Ordinal);
        Assert.Equal("/certificates", failed.LinkPath);

        // The second failure of the same streak is the scheduler's retry: no second message.
        h.Server.Inject("new-order", _ => FakeAcmeServer.Problem(HttpStatusCode.TooManyRequests, "urn:ietf:params:acme:error:rateLimited", "too many orders", retryAfter: "7200"));
        _ = await issuance.RenewAsync(id, Admin, Ct);
        Assert.Single(h.Events.Published);

        var recovered = await issuance.RenewAsync(id, Admin, Ct);
        Assert.Equal(IssuanceOutcome.Issued, recovered.Outcome);
        Assert.Equal([NotificationEventKind.AcmeRenewalFailed, NotificationEventKind.AcmeRenewalRecovered], h.Events.Published.Select(e => e.Kind));

        // A later clean renewal after the recovery is silent.
        _ = await issuance.RenewAsync(id, Admin, Ct);
        Assert.Equal(2, h.Events.Published.Count);
    }
}
