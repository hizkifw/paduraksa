using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// The orchestrator against a real CA: Pebble, with validation always valid (<see cref="PebbleFixture"/>), a real CSR from a
/// stand-in vault, the real config writer and the real clock. They skip when Docker (or Pebble) is not available. The
/// same flow with the real vault, and with Pebble fetching the challenge through the gate, comes when the vault certificate
/// flows are merged.
/// </summary>
public sealed class AcmePebbleTests(PebbleFixture pebble) : IClassFixture<PebbleFixture>
{
    private static readonly CommandOrigin Admin = ConfigServiceHarness.Admin;

    private static async Task<Rig> StartAsync(PebbleFixture pebble)
    {
        Requirements.RequireAvailable(Requirements.Pebble, pebble.SkipReason);
        return await Rig.CreateAsync();
    }

    [Fact]
    public async Task Http01IssuanceAndRenewalWorkAgainstARealCa()
    {
        await using var rig = await StartAsync(pebble);
        var account = (await rig.Accounts.CreateAsync(new CreateAcmeAccountRequest(pebble.Directory!.AbsoluteUri, ["mailto:ops@example.com"], true), Admin, Ct)).Account!;

        var issued = await rig.Issuance.RequestCertificateAsync(
            new CertificateIssueRequest(["pebble.example.com", "www.pebble.example.com", "192.0.2.10"], account.Id), Admin, Ct);

        Assert.True(issued.Outcome == IssuanceOutcome.Issued, issued.Message);
        var certificate = issued.Certificate!;
        Assert.Equal((CertificateSource.Acme, CertificateStatus.Active), (certificate.Source, certificate.Status));
        Assert.Equal(["192.0.2.10", "pebble.example.com", "www.pebble.example.com"], certificate.Names);
        Assert.NotNull(certificate.AuthorityKeyIdentifier);
        var order = (await rig.Issuance.GetOrderAsync(certificate.Id, Ct))!;
        Assert.Equal((AcmeOrderStatus.Valid, 0), (order.Status, order.Attempts));
        Assert.NotNull(order.NextRenewalAt);
        Assert.Equal(0, rig.Challenges.Count);

        // Pebble speaks ARI: the window for the certificate it just issued was found by the ID we computed.
        Assert.NotNull(order.AriWindowStart);
        Assert.True(order.NextRenewalAt <= order.AriWindowEnd);
        Assert.NotNull(order.AriCheckAt);

        // A renewal names the old certificate in `replaces`; Pebble checks it against what it issued.
        var renewed = await rig.Issuance.RenewAsync(certificate.Id, Admin, Ct);

        Assert.True(renewed.Outcome == IssuanceOutcome.Issued, renewed.Message);
        Assert.Equal(certificate.Id, renewed.CertificateId);
        Assert.NotEqual(certificate.Sha256Fingerprint, renewed.Certificate!.Sha256Fingerprint);
        Assert.NotEqual(certificate.SerialNumber, renewed.Certificate.SerialNumber);
    }

    [Fact]
    public async Task Dns01IssuanceForAWildcardWorksAgainstARealCa()
    {
        await using var rig = await StartAsync(pebble);
        var account = (await rig.Accounts.CreateAsync(new CreateAcmeAccountRequest(pebble.Directory!.AbsoluteUri, [], true, JwsAlgorithm.ES384), Admin, Ct)).Account!;
        var secret = await rig.Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("t", null, ["api.dns.test"]), Ct);
        var provider = (await rig.DnsProviders.CreateAsync(new CreateDnsProviderRequest(DnsProviderKind.Cloudflare, "cf", secret), Admin, Ct)).Provider!;

        var issued = await rig.Issuance.RequestCertificateAsync(
            new CertificateIssueRequest(["dns.pebble.example.com", "*.dns.pebble.example.com"], account.Id, AcmeChallengeType.Dns01, DnsProviderId: provider.Id), Admin, Ct);

        Assert.True(issued.Outcome == IssuanceOutcome.Issued, issued.Message);
        Assert.Equal(["*.dns.pebble.example.com", "dns.pebble.example.com"], issued.Certificate!.Names);
        Assert.Equal(["create", "create", "delete", "delete"], rig.Dns.Provider.Calls.Select(c => c.Split(' ')[0]));
        Assert.Empty(rig.Dns.Provider.Records);
    }

    /// <summary>The issuance services over a real database, the real clock, a stand-in vault and the real CA's transport.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConfigServiceHarness _config;
        private readonly AcmeClientProvider _clients;
        private readonly RealCsrIssuance _vault = new();

        private Rig(ConfigServiceHarness config)
        {
            _config = config;
            Signer = new TrackingSigner(new Tests.Acme.Fakes.FakeJwsSigner());
            Secrets = new FakeSecretStore();
            _clients = new AcmeClientProvider(
                Signer,
                macKeys: null,
                // The image's config asks for a 5 s Retry-After on an order still processing, which finalization sometimes is
                // when it's fetched: the bound must let the client wait that out.
                new AcmeClientOptions { PollInterval = TimeSpan.FromMilliseconds(200), MinPollDelay = TimeSpan.FromMilliseconds(100), MaxPollDelay = TimeSpan.FromSeconds(10) },
                PebbleFixture.CreateHandler,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            Accounts = new AcmeAccountService(config.Db.Factory, config.Service, _clients, Signer, null, TimeProvider.System, NullLogger<AcmeAccountService>.Instance, TestPermissions.AllowAll, Secrets);
            DnsProviders = new DnsProviderService(config.Db.Factory, config.Service, Secrets, Dns, TimeProvider.System, NullLogger<DnsProviderService>.Instance, TestPermissions.AllowAll);
            Issuance = new AcmeIssuanceService(
                config.Db.Factory,
                config.Service,
                new CertificateService(config.Db.Factory, config.Service, TimeProvider.System, NullLogger<CertificateService>.Instance, TestPermissions.AllowAll, null),
                _clients,
                _vault,
                Challenges,
                Dns,
                new AcmeIssuanceOptions { DnsPropagationDelay = TimeSpan.Zero },
                TimeProvider.System,
                NullLogger<AcmeIssuanceService>.Instance,
                TestPermissions.AllowAll,
                new TestConfigServices.NoCertificates(),
                null,
                null,
                Random.Shared.NextDouble,
                null);
        }

        public TrackingSigner Signer { get; }

        public FakeSecretStore Secrets { get; }

        public AcmeChallengeStore Challenges { get; } = new();

        public FakeDnsProviderFactory Dns { get; } = new();

        public AcmeAccountService Accounts { get; }

        public DnsProviderService DnsProviders { get; }

        public AcmeIssuanceService Issuance { get; }

        public static async Task<Rig> CreateAsync()
        {
            var config = await ConfigServiceHarness.CreateAsync();
            await config.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "seed.example.com")));
            return new Rig(config);
        }

        public async ValueTask DisposeAsync()
        {
            _clients.Dispose();
            _vault.Dispose();
            await _config.DisposeAsync();
        }
    }
}
