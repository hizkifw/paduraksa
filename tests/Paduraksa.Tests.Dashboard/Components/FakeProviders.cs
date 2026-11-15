using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Providers;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The accounts and DNS providers the pages read and change, in memory, with what each change was asked.</summary>
internal sealed class FakeProviders : IDashboardProviders
{
    public List<AcmeAccountSummary> Accounts { get; } = [];

    public List<DnsProviderSummary> DnsProviders { get; } = [];

    public List<CertificateSummary> Certificates { get; } = [];

    /// <summary>Which certificates were ordered with which account and through which provider.</summary>
    public Dictionary<CertificateId, (AcmeAccountId Account, DnsProviderId? Dns)> Orders { get; } = [];

    public List<string> Calls { get; } = [];

    public DnsProviderTestResult TestResult { get; set; } = new(Found: true, Valid: true);

    public IssuanceChangeStatus DeactivateStatus { get; set; } = IssuanceChangeStatus.Committed;

    private readonly List<Func<Task>> _watchers = [];

    /// <summary>Tells every page and drawer watching that something changed elsewhere.</summary>
    public Task ChangedAsync() => Task.WhenAll(_watchers.ToArray().Select(refresh => refresh()));

    public Task<IReadOnlyList<AcmeAccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AcmeAccountSummary>>([.. Accounts]);

    public Task<AcmeAccountSummary?> GetAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default) => Task.FromResult(Accounts.FirstOrDefault(a => a.Id == id));

    public Task<IReadOnlyDictionary<AcmeAccountId, int>> AccountCertificateCountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<AcmeAccountId, int>>(Orders.GroupBy(o => o.Value.Account).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<CertificateSummary>> CertificatesOfAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CertificateSummary>>([.. Certificates.Where(c => Orders.TryGetValue(c.Id, out var order) && order.Account == id)]);

    public Task<AcmeAccountResult?> DeactivateAccountAsync(AcmeAccountId id, CancellationToken cancellationToken = default)
    {
        Calls.Add($"deactivate {id}");
        var index = Accounts.FindIndex(a => a.Id == id);
        if (DeactivateStatus != IssuanceChangeStatus.Committed || index < 0)
        {
            return Task.FromResult<AcmeAccountResult?>(new AcmeAccountResult(DeactivateStatus == IssuanceChangeStatus.Committed ? IssuanceChangeStatus.NotFound : DeactivateStatus));
        }

        Accounts[index] = Accounts[index] with { Status = AcmeAccountStatus.Deactivated };
        return Task.FromResult<AcmeAccountResult?>(new AcmeAccountResult(IssuanceChangeStatus.Committed, Accounts[index]));
    }

    public Task<IReadOnlyList<DnsProviderSummary>> ListDnsProvidersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DnsProviderSummary>>([.. DnsProviders]);

    public Task<DnsProviderSummary?> GetDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) => Task.FromResult(DnsProviders.FirstOrDefault(p => p.Id == id));

    public Task<IReadOnlyDictionary<DnsProviderId, int>> DnsCertificateCountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<DnsProviderId, int>>(
            Orders.Where(o => o.Value.Dns is not null).GroupBy(o => o.Value.Dns!.Value).ToDictionary(g => g.Key, g => g.Count()));

    public Task<IReadOnlyList<CertificateSummary>> CertificatesOfDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CertificateSummary>>([.. Certificates.Where(c => Orders.TryGetValue(c.Id, out var order) && order.Dns == id)]);

    public Task<DnsProviderTestResult?> TestDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default)
    {
        Calls.Add($"test {id}");
        return Task.FromResult<DnsProviderTestResult?>(TestResult);
    }

    public Task<DnsProviderResult?> RenameDnsProviderAsync(DnsProviderId id, string label, CancellationToken cancellationToken = default)
    {
        Calls.Add($"rename {id} {label}");
        if (label.Trim().Length == 0)
        {
            return Task.FromResult<DnsProviderResult?>(new DnsProviderResult(
                IssuanceChangeStatus.Invalid, Errors: [new Paduraksa.Contracts.Config.ConfigError("/label", Paduraksa.Application.Issuance.IssuanceErrorCodes.LabelRequired, "a DNS provider needs a label")]));
        }

        var index = DnsProviders.FindIndex(p => p.Id == id);
        if (index < 0)
        {
            return Task.FromResult<DnsProviderResult?>(new DnsProviderResult(IssuanceChangeStatus.NotFound));
        }

        DnsProviders[index] = DnsProviders[index] with { Label = label.Trim() };
        return Task.FromResult<DnsProviderResult?>(new DnsProviderResult(IssuanceChangeStatus.Committed, DnsProviders[index]));
    }

    public Task<DnsProviderResult?> DeleteDnsProviderAsync(DnsProviderId id, CancellationToken cancellationToken = default)
    {
        Calls.Add($"delete {id}");
        DnsProviders.RemoveAll(p => p.Id == id);
        return Task.FromResult<DnsProviderResult?>(new DnsProviderResult(IssuanceChangeStatus.Committed));
    }

    public IDisposable Watch(Func<Func<Task>, Task> dispatch, Func<Task> refresh)
    {
        Func<Task> watcher = () => dispatch(refresh);
        _watchers.Add(watcher);
        return new Stop(() => _watchers.Remove(watcher));
    }

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
