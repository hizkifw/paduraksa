using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Certificates;

/// <summary>Where one step of the progress list stands.</summary>
public enum StepState
{
    Waiting,
    Active,
    Done,
    Failed,
    Skipped,
}

/// <summary>One line of the ACME progress list.</summary>
/// <param name="Title">What the step is.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Detail">The service's line for it (counts, record names), or the failure message.</param>
/// <param name="At">When it was reached.</param>
public sealed record ProgressStep(string Title, StepState State, string? Detail = null, DateTimeOffset? At = null);

/// <summary>
/// The ACME progress list (PLAN §12.1): order → authorizations → challenge published → propagation check (DNS-01 only) → validated →
/// finalized → issued, worked out from the reports a run sent (<see cref="AcmeOrderProgress"/>). A step is done once a later one was
/// reached, active while it is the latest, and the step a failed run was on is marked failed.
/// </summary>
public static class OrderProgressModel
{
    private sealed record StepDefinition(string Title, string ActiveTitle, AcmeProgressStage[] Stages, bool DnsOnly = false);

    private static readonly StepDefinition[] Definitions =
    [
        new("Order created", "Creating the order", [AcmeProgressStage.Starting, AcmeProgressStage.OrderCreated]),
        new("Authorizations read", "Reading the authorizations", [AcmeProgressStage.Authorizing]),
        new("Challenge published", "Publishing the challenge", [AcmeProgressStage.ChallengePublished]),
        new("DNS propagation checked", "Checking DNS propagation", [AcmeProgressStage.PropagationCheck], DnsOnly: true),
        new("Validated by the CA", "The CA is validating", [AcmeProgressStage.Validating, AcmeProgressStage.Validated]),
        new("Order finalized", "Finalizing the order", [AcmeProgressStage.Finalizing]),
        new("Certificate issued", "Downloading the certificate", [AcmeProgressStage.Issued]),
    ];

    /// <summary>The steps for <paramref name="challenge"/>, given the reports so far (oldest first).</summary>
    public static IReadOnlyList<ProgressStep> Steps(AcmeChallengeType challenge, IReadOnlyList<AcmeOrderProgress> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        StepDefinition[] definitions = [.. Definitions.Where(d => challenge == AcmeChallengeType.Dns01 || !d.DnsOnly)];
        AcmeOrderProgress? failure = reports.Count > 0 && reports[^1].Stage == AcmeProgressStage.Failed ? reports[^1] : null;
        AcmeOrderProgress? latest = reports.LastOrDefault(r => r.Stage != AcmeProgressStage.Failed);
        // A stage that completes its step (the order exists, the answers are up, the names are valid) makes the next step the active one.
        int current = latest is null ? (failure is null ? -1 : 0)
            : IndexOf(definitions, latest.Stage) + (latest.Stage is AcmeProgressStage.OrderCreated or AcmeProgressStage.ChallengePublished or AcmeProgressStage.Validated ? 1 : 0);
        bool issued = latest?.Stage == AcmeProgressStage.Issued;

        List<ProgressStep> steps = new(definitions.Length);
        for (int i = 0; i < definitions.Length; i++)
        {
            StepDefinition definition = definitions[i];
            AcmeOrderProgress? reached = reports.LastOrDefault(r => definition.Stages.Contains(r.Stage));
            StepState state =
                issued || i < current ? (reached is null && i > 0 ? StepState.Skipped : StepState.Done)
                : i == current ? (failure is not null ? StepState.Failed : StepState.Active)
                : StepState.Waiting;
            string title = state == StepState.Active ? definition.ActiveTitle : definition.Title;

            steps.Add(new ProgressStep(
                title,
                state,
                state == StepState.Failed ? failure!.Detail : reached?.Detail,
                state == StepState.Failed ? failure!.At : reached?.At));
        }

        return steps;
    }

    /// <summary>The running headline: what is happening now.</summary>
    public static string Headline(AcmeChallengeType challenge, IReadOnlyList<AcmeOrderProgress> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0)
        {
            return "Contacting the CA...";
        }

        return reports[^1].Stage switch
        {
            AcmeProgressStage.Starting => "Contacting the CA...",
            AcmeProgressStage.OrderCreated => "The CA accepted the order",
            AcmeProgressStage.Authorizing => "Reading the authorizations...",
            AcmeProgressStage.ChallengePublished => challenge == AcmeChallengeType.Dns01 ? "TXT records published" : "HTTP-01 answers published",
            AcmeProgressStage.PropagationCheck => "Waiting for DNS to propagate...",
            AcmeProgressStage.Validating => "The CA is validating...",
            AcmeProgressStage.Validated => "Every name is validated",
            AcmeProgressStage.Finalizing => "The CA is signing the certificate...",
            AcmeProgressStage.Issued => "Certificate issued",
            _ => "The CA did not issue the certificate",
        };
    }

    private static int IndexOf(StepDefinition[] definitions, AcmeProgressStage stage)
    {
        for (int i = 0; i < definitions.Length; i++)
        {
            if (definitions[i].Stages.Contains(stage))
            {
                return i;
            }
        }

        return 0;
    }
}
