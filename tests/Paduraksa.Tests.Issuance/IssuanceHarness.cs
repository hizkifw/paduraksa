using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Acme;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Acme.Fakes;
using Paduraksa.Tests.Data.Commands;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The issuance services on a real config database and the real config writer, talking to the fake CA through a hook
/// handler, with a fake DNS provider and a virtual clock. The orchestrator, the vault stand-in and the scheduler are added
/// by the other part of this class (<c>IssuanceHarness.Orchestration.cs</c>).
/// </summary>
internal sealed partial class IssuanceHarness : IAsyncDisposable
{
    public static readonly CommandOrigin Admin = ConfigServiceHarness.Admin;

    private readonly ConfigServiceHarness _config;
    private readonly List<AcmeClientProvider> _providers = [];

    private IssuanceHarness(ConfigServiceHarness config, FakeAcmeServer server)
    {
        _config = config;
        Server = server;
        Time = new VirtualTimeProvider { Now = DateTimeOffset.UtcNow };
        Hook = new HookHandler(server);
        Secrets = new FakeSecretStore();
        Accounts = new AcmeAccountService(
            config.Db.Factory, config.Service, NewClients(), Signer, null, Time, Loggers.CreateLogger<AcmeAccountService>(), Permissions, Secrets);
        DnsProviders = new DnsProviderService(config.Db.Factory, config.Service, Secrets, Dns, Time, Loggers.CreateLogger<DnsProviderService>(), Permissions);
        InitializeOrchestration();
    }

    public FakeAcmeServer Server { get; }

    /// <summary>Everything the services and the ACME clients logged.</summary>
    public CapturingLoggerFactory Loggers { get; } = new();

    public VirtualTimeProvider Time { get; }

    public HookHandler Hook { get; }

    public TrackingSigner Signer { get; } = new(new FakeJwsSigner());

    public FakeDnsProviderFactory Dns { get; } = new();

    public FakeSecretStore Secrets { get; }

    public AcmeAccountService Accounts { get; }

    public DnsProviderService DnsProviders { get; }

    public ConfigServiceHarness Config => _config;

    public static async Task<IssuanceHarness> CreateAsync(FakeAcmeServer? server = null)
    {
        var harness = new IssuanceHarness(await ConfigServiceHarness.CreateAsync(), server ?? new FakeAcmeServer());
        // Certificate changes republish the live config, and with no revision there is nothing to publish.
        await harness._config.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "seed.example.com")));
        return harness;
    }

    /// <summary>Registers an account at the fake CA through the account service.</summary>
    public async Task<AcmeAccountSummary> CreateAccountAsync()
    {
        var result = await Accounts.CreateAsync(
            new CreateAcmeAccountRequest(FakeAcmeServer.Url("directory").AbsoluteUri, ["mailto:ops@example.com"], TermsOfServiceAgreed: true), Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        return result.Account!;
    }

    /// <summary>A DNS provider row over a fresh DNS API token secret.</summary>
    public async Task<DnsProviderSummary> CreateDnsProviderAsync()
    {
        var secret = await Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("token", null, ["api.dns.test"]), Ct);
        var result = await DnsProviders.CreateAsync(new CreateDnsProviderRequest(DnsProviderKind.Cloudflare, "Cloudflare", secret), Admin, Ct);
        Assert.Equal(IssuanceChangeStatus.Committed, result.Status);
        return result.Provider!;
    }

    /// <summary>The provider's credential, read from its row: the reference is internal and never part of a summary.</summary>
    public async Task<SecretId> CredentialOfAsync(DnsProviderId id)
    {
        await using var context = await _config.Db.Factory.CreateDbContextAsync(Ct);
        return (await context.DnsProviders.AsNoTracking().SingleAsync(p => p.Id == id, Ct)).CredentialSecretId;
    }

    /// <summary>The account's external-account-binding secret, read from its row (never part of a summary).</summary>
    public async Task<SecretId?> EabSecretOfAsync(AcmeAccountId id)
    {
        await using var context = await _config.Db.Factory.CreateDbContextAsync(Ct);
        return (await context.AcmeAccounts.AsNoTracking().SingleAsync(a => a.Id == id, Ct)).EabSecretId;
    }

    public async Task<List<AuditRecord>> AuditAsync(string action) =>
        [.. (await _config.AuditAsync()).Where(e => e.Action == action)];

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        await _config.DisposeAsync();
    }

    private AcmeClientProvider NewClients()
    {
        var provider = new AcmeClientProvider(
            Signer,
            macKeys: null,
            new AcmeClientOptions
            {
                PollInterval = TimeSpan.FromSeconds(2),
                MinPollDelay = TimeSpan.FromSeconds(1),
                MaxPollDelay = TimeSpan.FromSeconds(10),
                RequestTimeout = TimeSpan.FromDays(1),
            },
            () => Hook,
            Time,
            Loggers);
        _providers.Add(provider);
        return provider;
    }

    /// <summary>Lets the orchestration part build what it adds; absent from builds of the account services alone.</summary>
    partial void InitializeOrchestration();
}
