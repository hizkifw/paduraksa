using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Acme;
using Paduraksa.Acme.Dns;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Issuance;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Gate;
using Paduraksa.Tests.Acme.Dns;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// DNS-01 end to end against a CA that really validates it (<see cref="PebbleDnsFixture"/>): the orchestrator publishes the TXT record
/// through a provider stand-in into the CA's resolver (challtestsrv) and a fake authoritative DNS server, the real propagation check finds the zone's
/// name servers and asks them directly, and Pebble then asks its resolver for the record and issues. With a CNAME at <c>_acme-challenge</c> the record goes to the
/// delegated zone, the check asks that zone, and Pebble follows the CNAME. They skip without Docker, like the other Pebble tests.
/// </summary>
public sealed class AcmePebbleDnsTests(PebbleDnsFixture pebble) : IClassFixture<PebbleDnsFixture>
{
    private static readonly CommandOrigin Admin = ConfigServiceHarness.Admin;

    private static readonly IPEndPoint Unused = new(IPAddress.Loopback, 0);

    [Fact]
    public async Task ACertificateIsIssuedOnlyBecauseThePublishedRecordIsReallyThere()
    {
        await using var rig = await StartAsync(pebble);

        var issued = await rig.IssueAsync("dns01.pebble-dns.test", "*.dns01.pebble-dns.test");

        Assert.True(issued.Outcome == IssuanceOutcome.Issued, issued.Message);
        Assert.Equal(["*.dns01.pebble-dns.test", "dns01.pebble-dns.test"], issued.Certificate!.Names);
        Assert.Equal(["create", "create", "delete", "delete"], rig.Calls.Select(c => c.Split(' ')[0]));
        Assert.All(rig.Calls, c => Assert.Contains("_acme-challenge.dns01.pebble-dns.test", c, StringComparison.Ordinal));
        Assert.Empty(rig.Zone.Records);

        // The propagation check asked the name server directly, without recursion.
        Assert.Contains(rig.Dns.Queries, q => q.Type == 16 && q.Name == "_acme-challenge.dns01.pebble-dns.test" && !q.RecursionDesired);
    }

    [Fact]
    public async Task ACnameAtTheChallengeNameSendsTheRecordToTheDelegatedZoneAndPebbleFollowsIt()
    {
        await using var rig = await StartAsync(pebble);
        rig.Zone.Cnames["_acme-challenge.deleg.pebble-dns.test"] = "token.acme.delegated-zone.test";
        await pebble.SetCnameAsync("_acme-challenge.deleg.pebble-dns.test", "token.acme.delegated-zone.test");

        var issued = await rig.IssueAsync("deleg.pebble-dns.test");

        Assert.True(issued.Outcome == IssuanceOutcome.Issued, issued.Message);
        Assert.Equal(["create", "delete"], rig.Calls.Select(c => c.Split(' ')[0]));
        Assert.All(rig.Calls, c => Assert.Contains("token.acme.delegated-zone.test", c, StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Calls, c => c.Contains("_acme-challenge", StringComparison.Ordinal));
        // The check looked for the record at the delegated name, in the delegated zone.
        Assert.Contains(rig.Dns.Queries, q => q.Type == 16 && q.Name == "token.acme.delegated-zone.test" && !q.RecursionDesired);
    }

    private static async Task<Rig> StartAsync(PebbleDnsFixture pebble)
    {
        Assert.SkipUnless(pebble.SkipReason is null, pebble.SkipReason ?? string.Empty);
        return await Rig.CreateAsync(pebble);
    }

    /// <summary>What the fake authoritative server knows: TXT records by name and CNAMEs, answered the way a resolver answers (a CNAME, then what its target holds).</summary>
    private sealed class Zone
    {
        private readonly Lock _gate = new();
        private readonly List<(string Name, string Value)> _records = [];

        public Dictionary<string, string> Cnames { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<(string Name, string Value)> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public void Add(string name, string value)
        {
            lock (_gate)
            {
                if (!_records.Contains((name, value)))
                {
                    _records.Add((name, value));
                }
            }
        }

        public void Remove(string name, string value)
        {
            lock (_gate)
            {
                _ = _records.Remove((name, value));
            }
        }

        public IReadOnlyList<byte[]>? Answer(FakeQuery query)
        {
            if (query.Type is not (16 or 5))
            {
                return null;
            }

            List<byte[]> answers = [];
            var name = query.Name;
            if (Cnames.TryGetValue(name, out var target))
            {
                answers.Add(DnsMessages.Record(DnsMessages.QuestionPointer, 5, DnsMessages.Name(target)));
                if (query.Type == 5)
                {
                    return [DnsMessages.Response(query.Id, query.Name, query.Type, answers)];
                }

                name = target;
            }
            else if (query.Type == 5)
            {
                return null;
            }

            lock (_gate)
            {
                foreach (var (recordName, value) in _records.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    answers.Add(DnsMessages.Record(DnsMessages.Name(recordName), 16, DnsMessages.Txt(value)));
                }
            }

            return [DnsMessages.Response(query.Id, query.Name, query.Type, answers)];
        }
    }

    /// <summary>A DNS provider that writes into <see cref="Zone"/>, what the fake authoritative server answers from.</summary>
    private sealed class ZoneProvider(Zone zone, List<string> calls, PebbleDnsFixture pebble) : IDnsProvider
    {
        public ValueTask CreateTxtAsync(string recordName, string value, CancellationToken cancellationToken)
        {
            lock (calls)
            {
                calls.Add($"create {recordName} {value}");
            }

            zone.Add(recordName, value);
            return new ValueTask(pebble.SetTxtAsync(recordName, value));
        }

        public ValueTask DeleteTxtAsync(string recordName, string value, CancellationToken cancellationToken)
        {
            lock (calls)
            {
                calls.Add($"delete {recordName} {value}");
            }

            zone.Remove(recordName, value);
            return new ValueTask(pebble.SetTxtAsync(recordName, null));
        }

        public ValueTask<DnsCredentialCheck> ValidateCredentialsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new DnsCredentialCheck(true));
    }

    private sealed class ZoneProviderFactory(IDnsProvider provider) : IDnsProviderFactory
    {
        public IDnsProvider Create(DnsProviderKind kind, SecretId credential) => provider;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConfigServiceHarness _config;
        private readonly AcmeClientProvider _clients;
        private readonly RealCsrIssuance _vault = new();
        private readonly FakeDnsServer _dns;
        private readonly Func<FakeQuery, IReadOnlyList<byte[]>?>? _previousOverride;

        private Rig(ConfigServiceHarness config, PebbleDnsFixture pebble)
        {
            _config = config;
            _dns = pebble.Dns;
            _previousOverride = _dns.Override;
            Pebble = pebble;
            _dns.Override = Zone.Answer;
            var signer = new TrackingSigner(new Tests.Acme.Fakes.FakeJwsSigner());
            var secrets = new FakeSecretStore();
            _clients = new AcmeClientProvider(
                signer,
                macKeys: null,
                new AcmeClientOptions { PollInterval = TimeSpan.FromMilliseconds(200), MinPollDelay = TimeSpan.FromMilliseconds(100), MaxPollDelay = TimeSpan.FromSeconds(10) },
                PebbleFixture.CreateHandler,
                TimeProvider.System,
                NullLoggerFactory.Instance);
            Factory = new ZoneProviderFactory(new ZoneProvider(Zone, Calls, pebble));
            Accounts = new AcmeAccountService(config.Db.Factory, config.Service, _clients, signer, null, TimeProvider.System, NullLogger<AcmeAccountService>.Instance, TestPermissions.AllowAll, secrets);
            DnsProviders = new DnsProviderService(config.Db.Factory, config.Service, secrets, Factory, TimeProvider.System, NullLogger<DnsProviderService>.Instance, TestPermissions.AllowAll);
            Secrets = secrets;

            // The real check and the real CNAME following, both asking the one fake server as the resolver and as the zone's name server.
            DnsPropagationOptions options = new()
            {
                Resolvers = [new IPEndPoint(IPAddress.Loopback, _dns.EndPoint.Port)],
                AuthoritativePort = _dns.EndPoint.Port,
                InitialInterval = TimeSpan.FromMilliseconds(100),
                MaxInterval = TimeSpan.FromMilliseconds(300),
                Timeout = TimeSpan.FromSeconds(30),
                QueryTimeout = TimeSpan.FromSeconds(2),
            };
            Issuance = new AcmeIssuanceService(
                config.Db.Factory,
                config.Service,
                new CertificateService(config.Db.Factory, config.Service, TimeProvider.System, NullLogger<CertificateService>.Instance, TestPermissions.AllowAll, null),
                _clients,
                _vault,
                new AcmeChallengeStore(),
                Factory,
                new AcmeIssuanceOptions { DnsPropagationDelay = TimeSpan.Zero },
                TimeProvider.System,
                NullLogger<AcmeIssuanceService>.Instance,
                TestPermissions.AllowAll,
                new TestConfigServices.NoCertificates(),
                new AuthoritativeDnsPropagationCheck(options),
                new CnameDelegation(options),
                Random.Shared.NextDouble,
                null);
        }

        public PebbleDnsFixture Pebble { get; }

        public Zone Zone { get; } = new();

        public List<string> Calls { get; } = [];

        public FakeDnsServer Dns => _dns;

        private ZoneProviderFactory Factory { get; }

        private AcmeAccountService Accounts { get; }

        private DnsProviderService DnsProviders { get; }

        private FakeSecretStore Secrets { get; }

        private AcmeIssuanceService Issuance { get; }

        public static async Task<Rig> CreateAsync(PebbleDnsFixture pebble)
        {
            var config = await ConfigServiceHarness.CreateAsync();
            await config.RunAsync(new UpsertHostCommand(Objects.RespondHost(HostId.New(), "seed.example.com")));
            var rig = new Rig(config, pebble);
            // The zones: the test's own, and the one a CNAME delegates to, each with a name server that is this fake.
            foreach (var zone in new[] { "pebble-dns.test", "delegated-zone.test" })
            {
                pebble.Dns.AddNs(zone, $"ns1.{zone}");
                pebble.Dns.AddA($"ns1.{zone}", IPAddress.Loopback);
            }

            return rig;
        }

        public async Task<IssuanceResult> IssueAsync(params string[] names)
        {
            var account = (await Accounts.CreateAsync(new CreateAcmeAccountRequest(Pebble.Directory!.AbsoluteUri, [], true, JwsAlgorithm.ES384), Admin, Ct)).Account!;
            var secret = await Secrets.PutAsync(SecretKind.DnsApiToken, new byte[] { 1 }, new SecretOptions("t", null, ["api.dns.test"]), Ct);
            var provider = (await DnsProviders.CreateAsync(new CreateDnsProviderRequest(DnsProviderKind.Cloudflare, "cf", secret), Admin, Ct)).Provider!;
            return await Issuance.RequestCertificateAsync(new CertificateIssueRequest(names, account.Id, AcmeChallengeType.Dns01, DnsProviderId: provider.Id), Admin, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            _dns.Override = _previousOverride;
            _clients.Dispose();
            _vault.Dispose();
            await _config.DisposeAsync();
        }
    }
}
