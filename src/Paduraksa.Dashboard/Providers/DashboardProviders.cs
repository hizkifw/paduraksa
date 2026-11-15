using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Components;

namespace Paduraksa.Dashboard.Providers;

/// <summary>The real <see cref="IDashboardProviders"/>, over the account, DNS provider, issuance and certificate services.</summary>
public sealed class DashboardProviders(
    AcmeAccountService accounts,
    DnsProviderService dnsProviders,
    AcmeIssuanceService issuance,
    CertificateService certificates,
    ProviderActions actions,
    DashboardCaller caller,
    ConfigService config) : IDashboardProviders
{
    public Task<IReadOnlyList<AcmeAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) => accounts.ListAsync(cancellationToken);

    public Task<AcmeAccountSummary?> GetAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default) => accounts.GetAsync(id, cancellationToken);

    public async Task<IReadOnlyDictionary<AcmeAccountId, int>> AccountCertificateCountsAsync(CancellationToken cancellationToken = default) =>
        (await issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false)).GroupBy(o => o.AccountId).ToDictionary(g => g.Key, g => g.Count());

    public Task<IReadOnlyList<CertificateSummary>> CertificatesOfAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default) =>
        CertificatesOfAsync(o => o.AccountId == id, cancellationToken);

    public Task<AcmeAccountResult?> DeactivateAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default) => actions.DeactivateAccountAsync(id, cancellationToken);

    public async Task<IReadOnlyList<DnsProviderSummary>> ListDnsProvidersAsync(CancellationToken cancellationToken = default) => await dnsProviders.ListAsync(cancellationToken).ConfigureAwait(false);

    public Task<DnsProviderSummary?> GetDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) => dnsProviders.GetAsync(id, cancellationToken);

    public async Task<IReadOnlyDictionary<DnsProviderId, int>> DnsCertificateCountsAsync(CancellationToken cancellationToken = default) =>
        (await issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false))
        .Where(o => o.DnsProviderId is not null).GroupBy(o => o.DnsProviderId!.Value).ToDictionary(g => g.Key, g => g.Count());

    public Task<IReadOnlyList<CertificateSummary>> CertificatesOfDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) =>
        CertificatesOfAsync(o => o.DnsProviderId == id, cancellationToken);

    public Task<DnsProviderTestResult?> TestDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) => actions.TestAsync(id, cancellationToken);

    public async Task<DnsProviderResult?> RenameDnsProviderAsync(DnsProviderId id, string label, CancellationToken cancellationToken = default)
    {
        var origin = await caller.OriginAsync(cancellationToken).ConfigureAwait(false);
        return origin is null ? null : await dnsProviders.RenameAsync(id, label, origin, cancellationToken).ConfigureAwait(false);
    }

    public Task<DnsProviderResult?> DeleteDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) => actions.DeleteProviderAsync(id, cancellationToken);

    public IDisposable Watch(Func<Func<Task>, Task> dispatch, Func<Task> refresh) =>
        LiveUpdates.Start(config, dispatch, refresh, false, "acme.account.", "acme.dns_provider.", "acme.order.", "certificate.");

    private async Task<IReadOnlyList<CertificateSummary>> CertificatesOfAsync(Func<AcmeOrderSummary, bool> ordered, CancellationToken cancellationToken)
    {
        var ids = (await issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false)).Where(ordered).Select(o => o.CertificateId).ToHashSet();
        return ids.Count == 0 ? [] : [.. (await certificates.ListAsync(null, cancellationToken).ConfigureAwait(false)).Where(c => ids.Contains(c.Id))];
    }
}
