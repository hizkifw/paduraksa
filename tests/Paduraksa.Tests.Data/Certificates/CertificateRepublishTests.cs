using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Data.Commands;
using Paduraksa.Tests.Data.Config;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using SnapshotStore = Paduraksa.Proxy.Snapshot.SnapshotStore;

namespace Paduraksa.Tests.Data.Certificates;

public sealed class CertificateRepublishTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ACertificateValidFromTheFutureIsActivatedByTheScheduledRepublish()
    {
        await using var env = await Env.CreateAsync();
        await env.RegisterAsync("a", ["a.example.com"], Start.AddHours(1), Start.AddDays(10));
        await env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        Assert.Equal("certificate.auto.noCoverage", Assert.Single(env.Service.Warnings).Code);
        Assert.Null(env.Served("a.example.com"));
        Assert.Equal(1, env.Time.ArmedTimers);
        var generation = env.Store.Current.Generation;

        // Not yet: the boundary is NotBefore less the five-minute tolerance.
        env.Time.Advance(TimeSpan.FromMinutes(54));
        // Absence window: the clock's timers fire inline, but the republish they would start runs on, and nothing signals one that must not happen.
        await Task.Delay(100, Ct);
        Assert.Equal(generation, env.Store.Current.Generation);

        env.Time.Advance(TimeSpan.FromMinutes(2));
        await Env.WaitAsync(() => env.Service.Warnings.Count == 0 && env.Served("a.example.com") is not null);

        Assert.Same(env.ContextOf("a"), env.Served("a.example.com"));
        Assert.Equal(1L, await env.Db.ScalarAsync("SELECT count(*) FROM ConfigRevisions"));
        Assert.Equal(1, env.Time.ArmedTimers); // the next boundary: its expiry
    }

    [Fact]
    public async Task AnExpiringCertificateStopsBeingServedWithoutAnyOtherTrigger()
    {
        await using var env = await Env.CreateAsync();
        await env.RegisterAsync("a", ["a.example.com"], Start.AddDays(-1), Start.AddDays(2));
        await env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        Assert.Empty(env.Service.Warnings);
        Assert.NotNull(env.Served("a.example.com"));
        var generation = env.Store.Current.Generation;

        env.Time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(2));
        await Env.WaitAsync(() => env.Store.Current.Generation > generation && env.Service.Warnings.Count == 1);

        Assert.Null(env.Served("a.example.com"));
        Assert.Equal("certificate.auto.noCoverage", env.Service.Warnings.Single().Code);
        Assert.Equal(0, env.Time.ArmedTimers);
    }

    [Fact]
    public async Task ACertificateIssuedAMomentAheadOfOurClockIsValidAtOnce()
    {
        await using var env = await Env.CreateAsync();
        await env.RegisterAsync("a", ["a.example.com"], Start.AddMinutes(3), Start.AddDays(90));
        await env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));

        Assert.Empty(env.Service.Warnings);
        Assert.Same(env.ContextOf("a"), env.Served("a.example.com"));
    }

    [Fact]
    public async Task ARenewalThatStartsInTheFutureLeavesTheNamesUncoveredUntilItsBoundary()
    {
        await using var env = await Env.CreateAsync();
        await env.RegisterAsync("a", ["a.example.com"], Start.AddDays(-30), Start.AddDays(1));
        await env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var first = env.Served("a.example.com");
        Assert.NotNull(first);

        var renewed = await env.RegisterAsync("a", ["a.example.com"], Start.AddHours(2), Start.AddDays(90), fingerprint: "f2");

        Assert.True(renewed.Publish!.Succeeded);
        Assert.Null(env.Served("a.example.com")); // the row now describes the new certificate, which isn't valid yet
        Assert.Equal("certificate.auto.noCoverage", Assert.Single(env.Service.Warnings).Code);

        env.Time.Advance(TimeSpan.FromMinutes(116));
        await Env.WaitAsync(() => env.Served("a.example.com") is not null);
        Assert.Same(env.ContextOf("a"), env.Served("a.example.com"));
        Assert.Empty(env.Service.Warnings);
    }

    [Fact]
    public async Task ACertificateChangeRacingACommandLeavesBothInTheFinalSnapshot()
    {
        await using var env = await Env.CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            var (x, y) = ($"x{i}.example.com", $"y{i}.example.com");
            await env.RegisterAsync($"x{i}", [x], Start.AddDays(-1), Start.AddDays(30));
            await env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), y)));
            var revisions = (long)(await env.Db.ScalarAsync("SELECT count(*) FROM ConfigRevisions"))!;

            var command = env.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), x)));
            var certificate = env.RegisterAsync($"y{i}", [y], Start.AddDays(-1), Start.AddDays(30));
            await Task.WhenAll(command, certificate);

            Assert.Equal(ConfigCommandStatus.Committed, (await command).Status);
            Assert.Equal(CertificateChangeStatus.Committed, (await certificate).Status);
            await Env.WaitAsync(() => env.Service.Warnings.Count == 0);
            Assert.NotNull(env.Served(x));
            Assert.NotNull(env.Served(y));
            Assert.Equal(revisions + 1, (long)(await env.Db.ScalarAsync("SELECT count(*) FROM ConfigRevisions"))!);
        }
    }

    [Fact]
    public async Task ACertificateChangeWhileDegradedRepublishesTheLastKnownGoodAndStaysDegraded()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var first = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")))).Revision!;
        await using (var remembering = Degraded.Start(h, fail: false))
        {
            await remembering.Service.StartAsync(Ct);
            await remembering.Service.StartupPublishFollowed;
            await remembering.LastGood.FlushAsync();
        }

        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")));
        await using var degraded = Degraded.Start(h, fail: true); // the latest revision doesn't compile
        var status = await degraded.Service.StartAsync(Ct);
        await degraded.Service.StartupPublishFollowed;
        Assert.Equal(ConfigSource.LastKnownGood, status.Source);
        Assert.Equal(first, status.Revision);
        var generation = degraded.Store.Current.Generation;

        var result = await degraded.Certificates.RegisterAsync(Registration("a"), ConfigServiceHarness.Admin, Ct);

        Assert.True(result.Publish!.Succeeded);
        Assert.True(degraded.Store.Current.Generation > generation);
        Assert.Equal(first.Id, degraded.Store.Current.Revision); // the last-known-good document, not the empty config
        Assert.Equal((ConfigSource.LastKnownGood, true), (degraded.Service.Status.Source, degraded.Service.Status.IsDegraded));
    }

    [Fact]
    public async Task ACertificateChangeAfterALostDatabaseNeverPublishesTheEmptyConfig()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var lost = new RevisionRef(ConfigRevisionId.New(), 3);
        var document = new ConfigDocument(Hosts: [RespondHost(HostId.New(), "same.example.com"), RespondHost(HostId.New(), "same.example.com")]); // the restore is refused
        var bytes = Application.Config.CanonicalConfigJson.Serialize(document);
        await using (var file = new LastKnownGoodFile(Path.Combine(Path.GetDirectoryName(h.Db.DatabasePath)!, LastKnownGoodFile.FileName), NullLoggerFactory.Instance))
        {
            file.Offer(new LastKnownGoodEntry(lost, bytes, SHA256.HashData(bytes)));
        }

        await using var degraded = Degraded.Start(h, fail: false);
        var status = await degraded.Service.StartAsync(Ct);
        Assert.Equal((ConfigSource.LastKnownGood, lost), (status.Source, status.Revision));
        var generation = degraded.Store.Current.Generation;

        var result = await degraded.Certificates.RegisterAsync(Registration("a"), ConfigServiceHarness.Admin, Ct);

        Assert.True(result.Publish!.Succeeded);
        Assert.True(degraded.Store.Current.Generation > generation);
        Assert.Equal(lost.Id, degraded.Store.Current.Revision);
        Assert.True(degraded.Service.Status.IsDegraded);
        Assert.Empty(await h.RevisionsAsync());
    }

    private static CertificateRegistration Registration(string label) => new(
        CertificateId.FromValue(new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)).AsSpan(0, 16))),
        CertificateSource.Uploaded,
        new CertificateInfo($"CN={label}", [$"{label}.example.com"], Start.AddDays(-1), Start.AddDays(60), "CN=Test", "01", null, "f1", "ECDSA P-256", [], "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"));

    /// <summary>A service started on the harness's database, with its own store and last-good file.</summary>
    private sealed class Degraded : IAsyncDisposable
    {
        private readonly SnapshotPublisher _publisher;

        private Degraded(ConfigServiceHarness h, bool fail)
        {
            Store = new SnapshotStore();
            _publisher = new SnapshotPublisher(Store);
            LastGood = new LastKnownGoodFile(Path.Combine(Path.GetDirectoryName(h.Db.DatabasePath)!, LastKnownGoodFile.FileName), NullLoggerFactory.Instance);
            ConfigServiceHarness.FakeCompiler compiler = new();
            if (fail)
            {
                compiler.Fail = document => document.Hosts.Any(host => host.Names.Contains("b.example.com"))
                    ? [new ConfigError("/hosts/1", "compile.failed", "broken")]
                    : null;
            }

            Service = TestConfigServices.Create(h.Db, _publisher, compiler: compiler, lastKnownGood: LastGood);
            Certificates = new CertificateService(h.Db.Factory, Service, TimeProvider.System, NullLogger<CertificateService>.Instance, TestPermissions.AllowAll, null);
        }

        public SnapshotStore Store { get; }

        public LastKnownGoodFile LastGood { get; }

        public ConfigService Service { get; }

        public CertificateService Certificates { get; }

        public static Degraded Start(ConfigServiceHarness h, bool fail) => new(h, fail);

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _publisher.DisposeAsync();
            await LastGood.DisposeAsync();
            Store.Dispose();
        }
    }

    /// <summary>The real compiler on a manual clock, with a fake source whose contexts are looked up by label.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly List<X509Certificate2> _owned = [];
        private readonly Dictionary<CertificateId, SslStreamCertificateContext> _contexts = [];
        private readonly SnapshotPublisher _publisher;

        private Env(TestConfigDatabase db)
        {
            Db = db;
            Time = new ManualTimeProvider(Start);
            Store = new SnapshotStore();
            _publisher = new SnapshotPublisher(Store);
            Service = TestConfigServices.Create(
                db, _publisher, new ConfigCompiler(SnapshotCompilerOptions.Default, new TestPasswords()), time: Time, certificateSource: new Source(_contexts));
            Certificates = new CertificateService(db.Factory, Service, Time, NullLogger<CertificateService>.Instance, TestPermissions.AllowAll, null);
        }

        public TestConfigDatabase Db { get; }

        public ManualTimeProvider Time { get; }

        public SnapshotStore Store { get; }

        public ConfigService Service { get; }

        public CertificateService Certificates { get; }

        public static async Task<Env> CreateAsync() => new(await TestConfigDatabase.CreateAsync());

        public Task<ConfigCommandResult> RunAsync(ConfigCommand command) => Service.ExecuteAsync(command, ConfigServiceHarness.Admin, Ct);

        public async Task<CertificateChangeResult> RegisterAsync(
            string label, string[] names, DateTimeOffset notBefore, DateTimeOffset notAfter, string fingerprint = "f1")
        {
            var registration = Registration(label) with
            {
                Info = new CertificateInfo($"CN={label}", names, notBefore, notAfter, "CN=Test", "01", null, fingerprint, "ECDSA P-256", [], "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"),
            };
            lock (_contexts)
            {
                _contexts[registration.Id] = NewContext(names);
            }

            return await Certificates.RegisterAsync(registration, ConfigServiceHarness.Admin, Ct);
        }

        public SslStreamCertificateContext ContextOf(string label)
        {
            lock (_contexts)
            {
                return _contexts[Registration(label).Id];
            }
        }

        public SslStreamCertificateContext? Served(string name) => Store.Current.Certificates.Find(name)?.Certificate.Context;

        public static async Task WaitAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "The condition never held.");
                // Poll step of a wait bounded to 10 s.
                await Task.Delay(20, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _publisher.DisposeAsync();
            Store.Dispose();
            await Db.DisposeAsync();
            foreach (var certificate in _owned)
            {
                certificate.Dispose();
            }
        }

        private SslStreamCertificateContext NewContext(string[] names)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in names)
            {
                san.AddDnsName(name);
            }

            request.CertificateExtensions.Add(san.Build());
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _owned.Add(certificate);
            return SslStreamCertificateContext.Create(certificate, null, offline: true);
        }
    }

    private sealed class Source(Dictionary<CertificateId, SslStreamCertificateContext> contexts) : IServerCertificateSource
    {
        public ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken cancellationToken)
        {
            lock (contexts)
            {
                return ValueTask.FromResult(contexts.GetValueOrDefault(id));
            }
        }
    }
}
