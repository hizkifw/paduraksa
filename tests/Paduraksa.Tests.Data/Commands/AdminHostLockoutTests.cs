using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>
/// PLAN §3.1's lock-out guard: a command that arrives through the admin host is put through the real gate over the compiled
/// candidate. When the caller's own request would no longer be forwarded to the admin app (deleted, disabled, renamed, changed
/// kind, excluded by a policy, a non-covering certificate, changed trusted proxies), it is refused unless it carries
/// <c>Force</c>. The admin port is an ordinary origin and needs no force.
/// </summary>
public sealed class AdminHostLockoutTests : IAsyncLifetime
{
    private const string AdminName = "admin.example.com";
    private static readonly IPAddress Client = IPAddress.Parse("192.0.2.10");
    private static readonly AdminRequestFacts Facts = new(AdminName, Https: true, Client);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CommandOrigin Proxied = new(Actor.ForUser(UserId.New()), Channel.Api, IPAddress.Loopback, Facts);

    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync(
        compiler: new AdminCompiler(),
        options: new ConfigServiceOptions { PublishWait = TimeSpan.Zero });

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task DeletingTheAdminHostIsRefusedThroughTheProxyAndAllowedWithoutForceFromTheAdminPort()
    {
        HostId id = await CreateAdminHostAsync();

        ConfigCommandResult refused = await _h.RunAsync(new DeleteHostCommand(id, Assert.Single((await _h.CurrentAsync()).Hosts).Version), Proxied);

        AssertLockout(refused, "/hosts/0");
        Assert.Equal(1, await _h.CountAsync("ConfigRevisions"));
        Assert.Single((await _h.CurrentAsync()).Hosts);
        IReadOnlyList<AuditRecord> refusedAudit = await _h.AuditAsync();
        Assert.Contains(refusedAudit, e => e.Action == "config.host.delete" && e.Details?.Contains("\"viaAdminProxy\":true", StringComparison.Ordinal) == true);

        ConfigCommandResult forced = await _h.RunAsync(new DeleteHostCommand(id, 1) { Force = true }, Proxied);

        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
        Assert.Empty((await _h.CurrentAsync()).Hosts);
        IReadOnlyList<AuditRecord> forcedAudit = await _h.AuditAsync();
        Assert.Contains(forcedAudit, e => e.Action == "config.host.delete" && e.Details?.Contains("\"force\":true", StringComparison.Ordinal) == true);

        // On the admin port a deletion needs no force: the caller is not using the admin host to ask.
        HostId again = await CreateAdminHostAsync();
        ConfigCommandResult ordinary = await _h.RunAsync(new DeleteHostCommand(again, Assert.Single((await _h.CurrentAsync()).Hosts).Version));

        Assert.Equal(ConfigCommandStatus.Committed, ordinary.Status);
        Assert.Empty((await _h.CurrentAsync()).Hosts);
    }

    [Fact]
    public async Task DisablingTheAdminHostIsRefusedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);

        ConfigCommandResult refused = await _h.RunAsync(new UpsertHostCommand(admin with { Enabled = false }), Proxied);

        AssertLockout(refused, "/hosts/0");
        Assert.True(Assert.Single((await _h.CurrentAsync()).Hosts).Enabled);

        ConfigCommandResult forced = await _h.RunAsync(new UpsertHostCommand(admin with { Enabled = false }) { Force = true }, Proxied);

        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
        Assert.False(Assert.Single((await _h.CurrentAsync()).Hosts).Enabled);
    }

    [Fact]
    public async Task DroppingTheAdminHostsHttpsIsRefusedEvenWithForce()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);

        ConfigCommandResult refused = await _h.RunAsync(
            new UpsertHostCommand(admin with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } }), Proxied);

        Assert.Contains(refused.Errors, error => error.Code == ConfigErrorCodes.HostAdminHttpsRequired);
        ConfigCommandResult forced = await _h.RunAsync(
            new UpsertHostCommand(admin with { Certificate = new CertificateBinding { Mode = CertificateBindingMode.None } }) { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Invalid, forced.Status);
        Assert.Contains(forced.Errors, error => error.Code == ConfigErrorCodes.HostAdminHttpsRequired);
    }

    [Fact]
    public async Task RenamingTheAdminHostIsRefusedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);

        ConfigCommandResult refused = await _h.RunAsync(new UpsertHostCommand(admin with { Names = ["new-admin.example.com"] }), Proxied);

        AssertLockout(refused, "/hosts/0");
        Assert.Equal(AdminName, Assert.Single(Assert.Single((await _h.CurrentAsync()).Hosts).Names));

        ConfigCommandResult forced = await _h.RunAsync(new UpsertHostCommand(admin with { Names = ["new-admin.example.com"] }) { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
    }

    [Fact]
    public async Task AHostPolicyThatExcludesTheCallerIsRefusedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);

        ConfigCommandResult refused = await _h.RunAsync(
            new UpsertHostCommand(admin with { Settings = new HostSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]) } }), Proxied);

        AssertLockout(refused, "/hosts/0");

        ConfigCommandResult forced = await _h.RunAsync(
            new UpsertHostCommand(admin with { Settings = new HostSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]) } }) { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
    }

    [Fact]
    public async Task AProfilePolicyThatExcludesTheCallerIsRefusedThroughTheProxy()
    {
        ProfileId profileId = ProfileId.New();
        ConfigCommandResult profile = await _h.RunAsync(new UpsertProfileCommand(
            new ProfileConfig(profileId, "locked", Settings: new HostSettings { IpAllowList = new IpAllowListSettings([Client.ToString()]) })));
        Assert.Equal(ConfigCommandStatus.Committed, profile.Status);

        HostId id = HostId.New();
        ConfigCommandResult admin = await _h.RunAsync(new UpsertHostCommand(
            new HostConfig(id, [AdminName], Kind: HostKind.Admin, ProfileId: profileId)));
        Assert.Equal(ConfigCommandStatus.Committed, admin.Status);

        ConfigCommandResult refused = await _h.RunAsync(new UpsertProfileCommand(
            new ProfileConfig(profileId, "locked", Version: 1, Settings: new HostSettings { IpAllowList = new IpAllowListSettings(["10.0.0.0/8"]) })), Proxied);

        AssertLockout(refused, "/hosts/0");
    }

    [Fact]
    public async Task ChangingTheAdminHostsKindIsRefusedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);
        HostConfig respond = admin with { Kind = HostKind.Respond, Respond = new RespondConfig(200, "not the admin") };

        ConfigCommandResult refused = await _h.RunAsync(new UpsertHostCommand(respond), Proxied);

        AssertLockout(refused, "/hosts/0");

        ConfigCommandResult forced = await _h.RunAsync(new UpsertHostCommand(respond) { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
    }
    [Fact]
    public async Task ARollbackThatDropsTheAdminHostIsRefusedThroughTheProxy()
    {
        ConfigCommandResult before = await _h.RunAsync(new UpsertHostCommand(
            new HostConfig(HostId.New(), ["before.test"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "before"))));
        Assert.Equal(ConfigCommandStatus.Committed, before.Status);
        await CreateAdminHostAsync();
        CurrentConfig head = await _h.Service.GetCurrentAsync(Ct);

        ConfigCommandResult refused = await _h.RunAsync(new RollbackConfigCommand(before.Revision!.Id, head.Revision!.Id), Proxied);

        AssertLockout(refused, "/hosts/1");
        Assert.Equal(2, (await _h.CurrentAsync()).Hosts.Count);

        ConfigCommandResult forced = await _h.RunAsync(new RollbackConfigCommand(before.Revision.Id, head.Revision.Id) { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
        Assert.DoesNotContain((await _h.CurrentAsync()).Hosts, h => h.Kind == HostKind.Admin);
    }

    [Fact]
    public async Task RecreatingTheAdminHostUnderANewIdIsAllowedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        CurrentConfig head = await _h.Service.GetCurrentAsync(Ct);
        HostConfig old = Assert.Single(head.Document.Hosts);

        ConfigCommandResult result = await _h.RunAsync(
            new ReplaceConfigCommand(head.Document with { Hosts = [old with { Id = HostId.New() }] }, head.Revision?.Id), Proxied);

        Assert.True(result.Status == ConfigCommandStatus.Committed, string.Join("; ", result.Errors.Select(e => e.Path + " " + e.Code + " " + e.Message)));
        Assert.NotEqual(old.Id, Assert.Single((await _h.CurrentAsync()).Hosts).Id);
    }

    [Fact]
    public async Task ChangingTrustedProxiesIsRefusedThroughTheProxy()
    {
        await CreateAdminHostAsync();
        CurrentConfig current = await _h.Service.GetCurrentAsync(Ct);

        ConfigCommandResult refused = await _h.RunAsync(new UpdateSettingsCommand(
            current.Document.Settings with
            {
                TrustedProxies = new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"]),
            }), Proxied);

        AssertLockout(refused, "/settings/trustedProxies");

        ConfigCommandResult forced = await _h.RunAsync(new UpdateSettingsCommand(
            current.Document.Settings with
            {
                TrustedProxies = new TrustedProxySettings(TrustedProxyMode.Generic, ["127.0.0.1/32"]),
            })
        { Force = true }, Proxied);
        Assert.Equal(ConfigCommandStatus.Committed, forced.Status);
    }

    [Fact]
    public async Task APreExistingFailingProbeDoesNotBlockUnrelatedCommands()
    {
        // The current and candidate snapshots both fail the TLS probe (the compiler has no certificate here), so the caller
        // never had an admin route to lose: the guard must not blame an unrelated change for that.
        await using ConfigServiceHarness harness = await ConfigServiceHarness.CreateAsync(
            compiler: new AdminCompiler(omitCertificate: true),
            options: new ConfigServiceOptions { PublishWait = TimeSpan.Zero });
        ConfigCommandResult admin = await harness.RunAsync(new UpsertHostCommand(
            new HostConfig(HostId.New(), [AdminName], Kind: HostKind.Admin)));
        Assert.Equal(ConfigCommandStatus.Committed, admin.Status);

        ConfigCommandResult unrelated = await harness.RunAsync(new UpsertHostCommand(
            new HostConfig(HostId.New(), ["unrelated.test"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"))), Proxied);

        Assert.True(unrelated.Status == ConfigCommandStatus.Committed, string.Join("; ", unrelated.Errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task ADraftSimulationIsNotBlockedByTheLockOutGuard()
    {
        await CreateAdminHostAsync();
        HostConfig admin = Assert.Single((await _h.CurrentAsync()).Hosts);

        ConfigService.DraftPlan draft = await _h.Service.PlanDraftAsync(
            new DeleteHostCommand(admin.Id, admin.Version), Proxied, Ct);

        Assert.Equal(ConfigCommandStatus.DryRun, draft.Result.Status);
        Assert.DoesNotContain(draft.Result.Errors, e => e.Code == ConfigErrorCodes.HostAdminLockout);
        Assert.NotNull(draft.Candidate);
    }

    private static void AssertLockout(ConfigCommandResult result, string path)
    {
        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        ConfigError error = Assert.Single(result.Errors);
        Assert.Equal(path, error.Path);
        Assert.Equal(ConfigErrorCodes.HostAdminLockout, error.Code);
        Assert.Contains("force=true", error.Message, StringComparison.Ordinal);
    }

    private async Task<HostId> CreateAdminHostAsync()
    {
        HostId id = HostId.New();
        ConfigCommandResult result = await _h.RunAsync(new UpsertHostCommand(
            new HostConfig(id, [AdminName], Kind: HostKind.Admin)));

        Assert.True(result.Status == ConfigCommandStatus.Committed, $"{result.Status}: {string.Join("; ", result.Errors.Select(e => e.Message))}");
        return id;
    }

    /// <summary>The real mapping and compiler with the internal listener enabled, so the guard's simulator sees the host.</summary>
    private sealed class AdminCompiler : IConfigCompiler
    {
        private readonly CertificateId _certificateId = CertificateId.New();
        private readonly ServerCertificate _certificate;
        private readonly bool _omitCertificate;

        public AdminCompiler(bool omitCertificate = false)
        {
            _omitCertificate = omitCertificate;
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=admin", key, HashAlgorithmName.SHA256);
            SubjectAlternativeNameBuilder san = new();
            san.AddDnsName(AdminName);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
            X509Certificate2 leaf = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = ServerCertificate.Create(leaf, intermediates: null);
        }

        public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revision, CertificateInventory inventory)
        {
            MappedConfig mapped = ProxyConfigMapping.Map(document, revision, inventory);
            ProxyConfigModel model = _omitCertificate
                ? mapped.Model
                : mapped.Model with { Defaults = mapped.Model.Defaults with { Certificate = _certificateId } };
            CompileResult result = SnapshotCompiler.Compile(
                model,
                new SnapshotCompilerOptions { AdminUpstream = true, Certificates = new Single(_certificateId, _certificate) });
            return result.Snapshot is { } snapshot && mapped.Errors.Count == 0
                ? new ConfigCompilation(snapshot, [])
                : new ConfigCompilation(null, [.. mapped.Errors.Select(e => new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, e.Message)), .. result.Errors.Select(e => new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, e.Message))]);
        }
    }

    private sealed class Single(CertificateId id, ServerCertificate certificate) : IServerCertificateResolver
    {
        public bool TryResolve(CertificateId candidate, [NotNullWhen(true)] out ServerCertificate? resolved)
        {
            resolved = candidate == id ? certificate : null;
            return resolved is not null;
        }
    }
}
