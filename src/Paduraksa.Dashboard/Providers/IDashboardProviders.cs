using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Providers;

/// <summary>
/// What the ACME accounts and DNS providers pages and their drawers ask of the Application layer, so they can be tested without a database, a
/// vault and a CA. Reads are not authorized by the services; the pages check <c>providers:write</c> for what they offer and
/// <c>certificates:read</c> for the certificates. A null result of a change is a session that ended under the call.
/// </summary>
public interface IDashboardProviders
{
    Task<IReadOnlyList<AcmeAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default);

    Task<AcmeAccountSummary?> GetAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default);

    /// <summary>How many certificates are ordered with each account (accounts with none are absent).</summary>
    Task<IReadOnlyDictionary<AcmeAccountId, int>> AccountCertificateCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>The certificates ordered with the account.</summary>
    Task<IReadOnlyList<CertificateSummary>> CertificatesOfAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default);

    /// <summary>Deactivates the account at the CA.</summary>
    Task<AcmeAccountResult?> DeactivateAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DnsProviderSummary>> ListDnsProvidersAsync(CancellationToken cancellationToken = default);

    Task<DnsProviderSummary?> GetDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default);

    /// <summary>How many certificates are ordered through each DNS provider (providers with none are absent).</summary>
    Task<IReadOnlyDictionary<DnsProviderId, int>> DnsCertificateCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>The certificates ordered through the provider.</summary>
    Task<IReadOnlyList<CertificateSummary>> CertificatesOfDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default);

    Task<DnsProviderTestResult?> TestDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default);

    Task<DnsProviderResult?> RenameDnsProviderAsync(DnsProviderId id, string label, CancellationToken cancellationToken = default);

    Task<DnsProviderResult?> DeleteDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <paramref name="refresh"/> (through <paramref name="dispatch"/>, the component's <c>InvokeAsync</c>) once per burst of changes to
    /// the accounts, the providers and the certificates; dispose it with the component.
    /// </summary>
    IDisposable Watch(Func<Func<Task>, Task> dispatch, Func<Task> refresh);
}
