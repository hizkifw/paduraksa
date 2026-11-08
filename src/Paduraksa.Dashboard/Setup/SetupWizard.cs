using MudBlazor;

namespace Paduraksa.Dashboard.Setup;

/// <summary>The steps of the first-run wizard (PLAN §12.1), in order. The first two happen on the static setup page.</summary>
public enum WizardStep
{
    SetupToken,
    Owner,
    TwoStep,
    AcmeAccount,
    DnsProvider,
    Certificate,
    FirstHost,
    Done,
}

/// <summary>Where a step of the wizard stands for the person going through it.</summary>
public enum WizardStepState
{
    Upcoming,
    Current,
    Done,
    Skipped,
}

/// <summary>One step as the stepper shows it.</summary>
public sealed record WizardStepInfo(WizardStep Step, string Title, string Slug, string Icon, bool Skippable);

/// <summary>
/// The first-run wizard's steps, their addresses and the way through them (PLAN §12.1): setup token, Owner, two-step verification, ACME
/// account, DNS provider, certificate, first host, done. Everything after the Owner is a page of the dashboard (<see cref="Route"/>) that
/// runs as the signed-in Owner, so it can be left and resumed; the overview's checklist links back into it.
/// </summary>
public static class SetupWizard
{
    /// <summary>The wizard's pages: <c>/welcome</c> starts at the first step after the Owner, <c>/welcome/{slug}</c> opens one step.</summary>
    public const string Route = "/welcome";

    public static IReadOnlyList<WizardStepInfo> Steps { get; } =
    [
        new(WizardStep.SetupToken, "Setup token", "token", Icons.Material.Outlined.Key, false),
        new(WizardStep.Owner, "Owner", "owner", Icons.Material.Outlined.Person, false),
        new(WizardStep.TwoStep, "Two-step sign-in", "two-step", Icons.Material.Outlined.PhonelinkLock, true),
        new(WizardStep.AcmeAccount, "ACME account", "acme", Icons.Material.Outlined.Badge, true),
        new(WizardStep.DnsProvider, "DNS provider", "dns", Icons.Material.Outlined.Dns, true),
        new(WizardStep.Certificate, "Certificate", "certificate", Icons.Material.Outlined.WorkspacePremium, true),
        new(WizardStep.FirstHost, "First host", "host", Icons.Material.Outlined.Language, true),
        new(WizardStep.Done, "Done", "done", Icons.Material.Outlined.Flag, false),
    ];

    /// <summary>The first step after the Owner exists.</summary>
    public const WizardStep First = WizardStep.TwoStep;

    public static WizardStepInfo Info(WizardStep step) => Steps[(int)step];

    /// <summary>The page of <paramref name="step"/>.</summary>
    public static string PathOf(WizardStep step) => step == First ? Route : $"{Route}/{Info(step).Slug}";

    /// <summary>
    /// The step a route segment names, parsed at the page's edge: null or empty is the first step; a step before <see cref="First"/>
    /// (they are done once this page can be seen) or an unknown slug is null.
    /// </summary>
    public static WizardStep? Parse(string? slug)
    {
        if (string.IsNullOrEmpty(slug))
        {
            return First;
        }

        WizardStepInfo? info = Steps.FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));
        return info is null || info.Step < First ? null : info.Step;
    }

    /// <summary>The step after <paramref name="step"/>.</summary>
    public static WizardStep Next(WizardStep step) => step >= WizardStep.Done ? WizardStep.Done : step + 1;

    /// <summary>The step before <paramref name="step"/>, never before <see cref="First"/>.</summary>
    public static WizardStep Previous(WizardStep step) => step <= First ? First : step - 1;

    /// <summary>How the stepper shows <paramref name="step"/> while <paramref name="current"/> is open, given what was skipped.</summary>
    public static WizardStepState StateOf(WizardStep step, WizardStep current, IReadOnlySet<WizardStep> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);
        return step == current ? WizardStepState.Current
            : skipped.Contains(step) ? WizardStepState.Skipped
            : step < current ? WizardStepState.Done
            : WizardStepState.Upcoming;
    }
}
