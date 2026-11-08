using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.Setup;

/// <summary>
/// How far the instance is set up, read from what exists (not from what the wizard remembers), so the wizard can be left and resumed and
/// the overview's checklist says the same: the user's second factor, an active ACME account, a DNS provider, a certificate of the
/// person's own (not the admin's or the local CA root), a host, and the local CA root to trust. What the user may not read counts as
/// unknown (null) and is not listed.
/// </summary>
public sealed record SetupProgress(
    bool TwoStep,
    IReadOnlyList<AcmeAccountSummary>? Accounts,
    IReadOnlyList<DnsProviderSummary>? Providers,
    IReadOnlyList<CertificateSummary>? Certificates,
    int? Hosts,
    CertificateSummary? Root)
{
    public bool? HasAccount => Accounts?.Any(a => a.Status == AcmeAccountStatus.Active);

    public bool? HasProvider => Providers is null ? null : Providers.Count > 0;

    public bool? HasCertificate => Certificates is null ? null : Certificates.Count > 0;

    public bool? HasHost => Hosts is null ? null : Hosts > 0;

    /// <summary>Whether <paramref name="step"/> is done; null when the user can't tell (or it has nothing to be done).</summary>
    public bool? IsDone(WizardStep step) => step switch
    {
        WizardStep.TwoStep => TwoStep,
        WizardStep.AcmeAccount => HasAccount,
        WizardStep.DnsProvider => HasProvider,
        WizardStep.Certificate => HasCertificate,
        WizardStep.FirstHost => HasHost,
        _ => null,
    };

    /// <summary>The steps after the Owner that are not done yet (and that the user can see), in order.</summary>
    public IReadOnlyList<WizardStep> Remaining =>
        [.. SetupWizard.Steps.Select(s => s.Step).Where(s => s >= SetupWizard.First && s < WizardStep.Done && IsDone(s) == false)];

    /// <summary>Reads it as <paramref name="user"/> sees it.</summary>
    public static async Task<SetupProgress> LoadAsync(
        DashboardPrincipal user,
        UserService users,
        AcmeAccountService accounts,
        DnsProviderService providers,
        CertificateService certificates,
        ConfigService config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(config);
        CommandOrigin origin = user.Session.OriginFor(Channel.Dashboard);
        UserResult self = await users.GetAsync(origin, user.UserId, cancellationToken).ConfigureAwait(false);
        bool providersVisible = user.Has(Permission.ProvidersRead);
        IReadOnlyList<CertificateSummary>? inventory = user.Has(Permission.CertificatesRead)
            ? await certificates.ListAsync(null, cancellationToken).ConfigureAwait(false)
            : null;
        return new SetupProgress(
            self.User?.TwoFactorEnabled == true,
            providersVisible ? await accounts.ListAsync(cancellationToken).ConfigureAwait(false) : null,
            providersVisible ? await providers.ListAsync(cancellationToken).ConfigureAwait(false) : null,
            inventory is null ? null : [.. inventory.Where(c => c.Id != AdminCertificate.Id && c.Source != CertificateSource.LocalCaRoot)],
            user.Has(Permission.ConfigRead) ? (await config.GetCurrentAsync(cancellationToken).ConfigureAwait(false)).Document.Hosts.Count : null,
            inventory?.FirstOrDefault(c => c.Source == CertificateSource.LocalCaRoot && c.Status == CertificateStatus.Active));
    }
}
