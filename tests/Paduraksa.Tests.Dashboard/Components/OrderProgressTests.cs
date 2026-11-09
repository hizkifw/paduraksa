using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Certificates;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The ACME progress list: one rendering per state of the run, the DNS-only propagation step, skipped steps on a resumed
/// order, and a failure with the CA's message and the retry.
/// </summary>
public sealed class OrderProgressTests
{
    private static readonly CertificateId Id = CertificateId.New();
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<AcmeProgressStage, string[]> Http01States => new()
    {
        { AcmeProgressStage.Starting, ["active", "waiting", "waiting", "waiting", "waiting", "waiting"] },
        { AcmeProgressStage.OrderCreated, ["done", "active", "waiting", "waiting", "waiting", "waiting"] },
        { AcmeProgressStage.Authorizing, ["done", "active", "waiting", "waiting", "waiting", "waiting"] },
        { AcmeProgressStage.ChallengePublished, ["done", "done", "done", "active", "waiting", "waiting"] },
        { AcmeProgressStage.Validating, ["done", "done", "done", "active", "waiting", "waiting"] },
        { AcmeProgressStage.Validated, ["done", "done", "done", "done", "active", "waiting"] },
        { AcmeProgressStage.Finalizing, ["done", "done", "done", "done", "active", "waiting"] },
        { AcmeProgressStage.Issued, ["done", "done", "done", "done", "done", "done"] },
    };

    [Theory]
    [MemberData(nameof(Http01States))]
    public async Task EachStateOfAnHttp01Run_RendersItsSteps(AcmeProgressStage reached, string[] states)
    {
        await using DashboardTestContext context = new();

        IRenderedComponent<OrderProgressView> view = Render(context, AcmeChallengeType.Http01, Reports(reached));

        Assert.Equal(states, view.FindAll(".pdk-step-item").Select(step => step.GetAttribute("data-state")));
        Assert.DoesNotContain("propagation", view.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(reached == AcmeProgressStage.Issued, view.FindAll(".pdk-progress-done").Count == 1);
    }

    [Fact]
    public async Task ARunThatThrowsBeforeItReports_IsShownInThePanel_AndEnds()
    {
        await using DashboardTestContext context = new();
        context.Services.AddLogging();
        context.Services.AddSingleton(TimeProvider.System);
        IssuanceResult? finished = null;

        IRenderedComponent<AcmeRunPanel> panel = context.Render<AcmeRunPanel>(parameters => parameters
            .Add(p => p.Start, _ => Task.FromException<IssuanceResult>(new InvalidOperationException("database is locked")))
            .Add(p => p.ChallengeType, AcmeChallengeType.Http01)
            .Add(p => p.Finished, result => finished = result));

        await panel.WaitForStateAsync(() => finished is not null);
        Assert.False(panel.Instance.Running);
        Assert.Equal(IssuanceOutcome.Unavailable, finished!.Outcome);
        Assert.Contains("The run didn't get going. Try again", panel.Find(".pdk-form-alert").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADns01Run_ListsThePropagationCheck_WithTheRecords()
    {
        await using DashboardTestContext context = new();
        List<AcmeOrderProgress> reports = [.. Reports(AcmeProgressStage.ChallengePublished)];
        reports[^1] = reports[^1] with { Detail = "2 TXT records at _acme-challenge.example.com." };
        reports.Add(new AcmeOrderProgress(Id, AcmeProgressStage.PropagationCheck, AcmeChallengeType.Dns01, Start.AddSeconds(5)));

        IRenderedComponent<OrderProgressView> view = Render(context, AcmeChallengeType.Dns01, reports);

        Assert.Equal(["done", "done", "done", "active", "waiting", "waiting", "waiting"], view.FindAll(".pdk-step-item").Select(s => s.GetAttribute("data-state")));
        Assert.Contains("Checking DNS propagation", view.Markup, StringComparison.Ordinal);
        Assert.Contains("_acme-challenge.example.com", view.Markup, StringComparison.Ordinal);
        Assert.Contains("Waiting for DNS to propagate", view.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResumedOrderWithValidAuthorizations_MarksTheSkippedSteps()
    {
        await using DashboardTestContext context = new();
        AcmeOrderProgress[] reports =
        [
            new(Id, AcmeProgressStage.Starting, AcmeChallengeType.Http01, Start),
            new(Id, AcmeProgressStage.OrderCreated, AcmeChallengeType.Http01, Start, "Resumed the order the CA already has."),
            new(Id, AcmeProgressStage.Validated, AcmeChallengeType.Http01, Start),
            new(Id, AcmeProgressStage.Finalizing, AcmeChallengeType.Http01, Start),
        ];

        IRenderedComponent<OrderProgressView> view = Render(context, AcmeChallengeType.Http01, reports);

        Assert.Equal(["done", "skipped", "skipped", "done", "active", "waiting"], view.FindAll(".pdk-step-item").Select(s => s.GetAttribute("data-state")));
    }

    [Fact]
    public async Task AFailure_MarksTheStepItWasOn_AndSaysWhatTheCaSaidAndWhenItRetries()
    {
        await using DashboardTestContext context = new();
        List<AcmeOrderProgress> reports = [.. Reports(AcmeProgressStage.Validating)];
        reports.Add(new AcmeOrderProgress(Id, AcmeProgressStage.Failed, AcmeChallengeType.Http01, Start.AddMinutes(1), "The authorization for example.com ended invalid: connection refused", Start.AddHours(1)));

        IRenderedComponent<OrderProgressView> view = Render(context, AcmeChallengeType.Http01, reports, attempts: 1);

        Assert.Equal(["done", "done", "done", "failed", "waiting", "waiting"], view.FindAll(".pdk-step-item").Select(s => s.GetAttribute("data-state")));
        Assert.Single(view.FindAll(".pdk-progress-failed"));
        Assert.Contains("connection refused", view.Find(".pdk-progress-alert").TextContent, StringComparison.Ordinal);
        Assert.Contains("8 Nov 2026, 13:00 UTC (attempt 2)", view.Find(".pdk-progress-retry").TextContent, StringComparison.Ordinal);
    }

    private static IRenderedComponent<OrderProgressView> Render(DashboardTestContext context, AcmeChallengeType type, IReadOnlyList<AcmeOrderProgress> reports, int? attempts = null) =>
        context.Render<OrderProgressView>(p => p
            .Add(v => v.ChallengeType, type)
            .Add(v => v.Reports, reports)
            .Add(v => v.Attempts, attempts)
            .Add(v => v.Now, Start.AddSeconds(30)));

    /// <summary>The reports an uninterrupted run sends up to <paramref name="reached"/>.</summary>
    private static List<AcmeOrderProgress> Reports(AcmeProgressStage reached)
    {
        AcmeProgressStage[] order =
        [
            AcmeProgressStage.Starting, AcmeProgressStage.OrderCreated, AcmeProgressStage.Authorizing, AcmeProgressStage.ChallengePublished,
            AcmeProgressStage.Validating, AcmeProgressStage.Validated, AcmeProgressStage.Finalizing, AcmeProgressStage.Issued,
        ];
        return [.. order.TakeWhile(stage => stage <= reached).Select((stage, i) => new AcmeOrderProgress(Id, stage, AcmeChallengeType.Http01, Start.AddSeconds(i)))];
    }
}
