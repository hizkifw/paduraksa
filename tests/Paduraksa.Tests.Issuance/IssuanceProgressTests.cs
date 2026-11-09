using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Acme.Fakes;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using static Paduraksa.Tests.Issuance.IssuanceHarness;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The steps an issuance reports while it runs (the dashboard's progress dialog): in order, with the certificate's ID from the
/// first report, the CA's message on a failure, nothing secret, and kept for other watchers only while the run lasts.
/// </summary>
public sealed class IssuanceProgressTests
{
    [Fact]
    public async Task AnHttp01RunReportsEveryStepInOrder()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        Recorder progress = new();
        AcmeOrderProgress? seenByOthers = null;
        h.Hook.Before = (path, _) =>
        {
            if (path.StartsWith("chall/", StringComparison.Ordinal) && progress.Steps.Count > 0)
            {
                seenByOthers = h.Issuance.GetProgress(progress.Steps[0].CertificateId);
            }

            return Task.CompletedTask;
        };

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com", "www.example.com"), Admin, progress, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        Assert.Equal(
            [
                AcmeProgressStage.Starting, AcmeProgressStage.OrderCreated, AcmeProgressStage.Authorizing, AcmeProgressStage.ChallengePublished,
                AcmeProgressStage.Validating, AcmeProgressStage.Validated, AcmeProgressStage.Finalizing, AcmeProgressStage.Issued,
            ],
            progress.Steps.Select(s => s.Stage));
        Assert.All(progress.Steps, s => Assert.Equal((result.CertificateId, AcmeChallengeType.Http01), (s.CertificateId, s.ChallengeType)));
        Assert.Equal("2 HTTP-01 answers served under /.well-known/acme-challenge/.", progress.Steps.Single(s => s.Stage == AcmeProgressStage.ChallengePublished).Detail);
        // Anyone may look while it runs; once it ended there is nothing to look at.
        Assert.Equal(AcmeProgressStage.Validating, seenByOthers?.Stage);
        Assert.Null(h.Issuance.GetProgress(result.CertificateId!.Value));
    }

    [Fact]
    public async Task ADns01RunReportsTheRecordsAndThePropagationCheck()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var provider = await h.CreateDnsProviderAsync();
        Recorder progress = new();

        var result = await h.Issuance.RequestCertificateAsync(Dns01(account.Id, provider.Id, "example.com", "*.example.com"), Admin, progress, Ct);

        Assert.Equal(IssuanceOutcome.Issued, result.Outcome);
        AcmeProgressStage[] stages = [.. progress.Steps.Select(s => s.Stage)];
        Assert.True(Array.IndexOf(stages, AcmeProgressStage.ChallengePublished) < Array.IndexOf(stages, AcmeProgressStage.PropagationCheck));
        Assert.True(Array.IndexOf(stages, AcmeProgressStage.PropagationCheck) < Array.IndexOf(stages, AcmeProgressStage.Validating));
        Assert.Equal("2 TXT records at _acme-challenge.example.com.", progress.Steps.Single(s => s.Stage == AcmeProgressStage.ChallengePublished).Detail);
        // A TXT value is a digest of the key authorization: it is never in a report.
        Assert.All(progress.Steps, s => Assert.DoesNotContain(h.Dns.Provider.Calls.Select(c => c.Split(' ')[^1]), value => s.Detail?.Contains(value, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task AFailureIsTheLastReportWithTheMessageAndTheRetry()
    {
        await using var h = await IssuanceHarness.CreateAsync(new FakeAcmeServer { FailValidation = true });
        var account = await h.CreateAccountAsync();
        Recorder progress = new();

        var result = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, progress, Ct);

        Assert.Equal(IssuanceOutcome.Failed, result.Outcome);
        AcmeOrderProgress last = progress.Steps[^1];
        Assert.Equal((AcmeProgressStage.Failed, result.Message), (last.Stage, last.Detail));
        Assert.Equal((await h.OrderAsync(result.CertificateId!.Value)).RetryAt, last.RetryAt);
    }

    [Fact]
    public async Task AReporterThatThrowsDoesNotStopTheIssuance()
    {
        await using var h = await IssuanceHarness.CreateAsync();
        var account = await h.CreateAccountAsync();
        var issued = await h.Issuance.RequestCertificateAsync(Http01(account.Id, "example.com"), Admin, Ct);

        var renewed = await h.Issuance.RenewAsync(issued.CertificateId!.Value, Admin, new Throwing(), Ct);

        Assert.Equal(IssuanceOutcome.Issued, renewed.Outcome);
    }

    /// <summary>Collects the reports synchronously (<see cref="Progress{T}"/> would post them to the thread pool).</summary>
    private sealed class Recorder : IProgress<AcmeOrderProgress>
    {
        public List<AcmeOrderProgress> Steps { get; } = [];

        public void Report(AcmeOrderProgress value) => Steps.Add(value);
    }

    private sealed class Throwing : IProgress<AcmeOrderProgress>
    {
        public void Report(AcmeOrderProgress value) => throw new InvalidOperationException("the page is gone");
    }
}
