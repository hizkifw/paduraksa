using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

/// <summary>The orchestrator, its stand-in vault, the challenge store and the scheduler of the harness.</summary>
internal sealed partial class IssuanceHarness
{
    public FakeCertificateIssuance Vault { get; private set; } = null!;

    public AcmeChallengeStore Challenges { get; } = new();

    public CertificateService Certificates { get; private set; } = null!;

    public AcmeIssuanceService Issuance { get; private set; } = null!;

    public AcmeRenewalScheduler Scheduler { get; private set; } = null!;

    /// <summary>The propagation check the orchestrator is given; none (the fixed delay) by default.</summary>
    public Paduraksa.Acme.Dns.IDnsPropagationCheck? Propagation { get; set; }

    public AcmeIssuanceOptions Options { get; set; } = new() { DnsPropagationDelay = TimeSpan.FromSeconds(30) };

    /// <summary>What the services' random shares are (0.5: the middle of a window, half the jitter).</summary>
    public double Random { get; set; } = 0.5;

    public static CertificateIssueRequest Http01(AcmeAccountId account, params string[] names) => new(names, account);

    public static CertificateIssueRequest Dns01(AcmeAccountId account, DnsProviderId provider, params string[] names) =>
        new(names, account, AcmeChallengeType.Dns01, DnsProviderId: provider);

    /// <summary>A new orchestrator on the same database, CA, vault and store: what the process has after a restart.</summary>
    public AcmeIssuanceService NewIssuance(AcmeChallengeStore? challenges = null) => new(
        _config.Db.Factory,
        _config.Service,
        Certificates,
        NewClients(),
        Vault,
        challenges ?? Challenges,
        Dns,
        Options,
        Time,
        Loggers.CreateLogger<AcmeIssuanceService>(),
        catalog: Vault,
        propagation: Propagation,
        random: () => Random);

    public AcmeRenewalScheduler NewScheduler(AcmeIssuanceService issuance) =>
        new(issuance, Options, Time, Loggers.CreateLogger<AcmeRenewalScheduler>());

    public async Task<AcmeOrderSummary> OrderAsync(CertificateId id) => (await Issuance.GetOrderAsync(id, Ct))!;

    public async Task<CertificateSummary> CertificateAsync(CertificateId id) => (await Certificates.GetAsync(id, Ct))!.Summary;

    partial void InitializeOrchestration()
    {
        Vault = new FakeCertificateIssuance(Time);
        Certificates = new CertificateService(_config.Db.Factory, _config.Service, Time, Loggers.CreateLogger<CertificateService>());
        Issuance = NewIssuance();
        Scheduler = NewScheduler(Issuance);
    }
}
