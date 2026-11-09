using Microsoft.Extensions.Logging;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Yarp;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;
using SnapshotStore = Paduraksa.Proxy.Snapshot.SnapshotStore;

namespace Paduraksa.Tests.Data.Commands;

public sealed class ConfigServiceStartupTests
{
    [Fact]
    public async Task AnEmptyDatabaseStartsOnTheEmptyConfig()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var status = await h.Service.StartAsync(Ct);

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, null), status);
        Assert.Equal(status, h.Service.Status);
        Assert.True(h.Store.HasPublished);
        Assert.Equal([ConfigRevisionId.Empty], h.Compiler.Compiled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.StartAsync(Ct));
    }

    [Fact]
    public async Task StartupPublishesTheLatestRevisionAndRemembersIt()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var latest = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")))).Revision!;
        var path = Path.Combine(Path.GetDirectoryName(h.Db.DatabasePath)!, LastKnownGoodFile.FileName);
        await using var lastGood = new LastKnownGoodFile(path);
        using var store = new SnapshotStore();
        await using var publisher = new SnapshotPublisher(store);
        using var restarted = new ConfigService(h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), publisher, lastKnownGood: lastGood);

        var status = await restarted.StartAsync(Ct);
        await restarted.StartupPublishFollowed;
        await lastGood.FlushAsync();

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, latest), status);
        Assert.Equal(latest.Id, store.Current.Revision);
        Assert.True(lastGood.TryRead(out var entry, out var problem), problem);
        Assert.Equal(latest, entry.Revision);
    }

    [Theory]
    [InlineData("DELETE FROM GlobalSettings; DELETE FROM Hosts")]
    [InlineData("UPDATE GlobalSettings SET RevisionId = (SELECT Id FROM ConfigRevisions WHERE Seq = 1); DELETE FROM Hosts WHERE rowid IN (SELECT rowid FROM Hosts LIMIT 1)")]
    public async Task AProjectionThatDoesNotShowTheLatestRevisionIsRewritten(string damage)
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")));
        await h.Db.ExecuteAsync(damage);

        await h.Service.StartAsync(Ct);

        await h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task ACorruptLatestRevisionWithNothingToFallBackOnFailsTheStart()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        await h.Db.ExecuteAsync("UPDATE ConfigRevisions SET DocumentHash = zeroblob(32)");
        var generation = h.Store.Current.Generation;

        var failure = await Assert.ThrowsAsync<ConfigStartupException>(() => h.Service.StartAsync(Ct));

        Assert.Contains("the latest config revision can't be read", failure.Message, StringComparison.Ordinal);
        Assert.Contains("there is no last-known-good file", failure.Message, StringComparison.Ordinal);
        Assert.Equal(generation, h.Store.Current.Generation); // nothing published
    }

    [Fact]
    public async Task WhileTheStoreIsUnusableCommandsAreRefusedWithoutBeingAudited()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var revision = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")))).Revision!;
        var path = Path.Combine(Path.GetDirectoryName(h.Db.DatabasePath)!, LastKnownGoodFile.FileName);
        await using (var writer = new LastKnownGoodFile(path))
        {
            using var store = new SnapshotStore();
            await using var publisher = new SnapshotPublisher(store);
            using var first = new ConfigService(h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), publisher, lastKnownGood: writer);
            await first.StartAsync(Ct);
            await first.StartupPublishFollowed;
        }

        await h.Db.ExecuteAsync("UPDATE ConfigRevisions SET DocumentHash = zeroblob(32)");
        var audited = await h.CountAsync("AuditEvents");
        await using var lastGood = new LastKnownGoodFile(path);
        using var degradedStore = new SnapshotStore();
        await using var degradedPublisher = new SnapshotPublisher(degradedStore);
        using var degraded = new ConfigService(h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), degradedPublisher, lastKnownGood: lastGood);

        var status = await degraded.StartAsync(Ct);
        var refused = await degraded.ExecuteAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(ConfigSource.LastKnownGood, status.Source);
        Assert.Equal(revision, status.Revision);
        Assert.False(status.AcceptsCommands);
        Assert.Equal(revision.Id, degradedStore.Current.Revision);
        Assert.Equal(ConfigCommandStatus.Unavailable, refused.Status);
        Assert.Equal(ConfigService.StoreUnavailableCode, Assert.Single(refused.Errors).Code);
        Assert.Equal(audited, await h.CountAsync("AuditEvents"));
    }

    [Theory]
    [InlineData("UPDATE ConfigRevisions SET CreatedAt = 'not a time' WHERE Seq = 2")]
    [InlineData("UPDATE ConfigRevisions SET Channel = 'carrier-pigeon' WHERE Seq = 2")]
    [InlineData("PRAGMA foreign_keys = OFF; UPDATE ConfigRevisions SET Id = x'0102' WHERE Seq = 2")]
    public async Task ALatestRevisionRowThatCantBeReadStartsDegradedOnTheLastKnownGood(string damage)
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var first = await CommitAndRememberAsync(h, "a.example.com");
        await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")));
        await h.Db.ExecuteUncheckedAsync(damage);
        await using var restarted = Restart(h);

        var status = await restarted.Service.StartAsync(Ct);

        Assert.Equal(ConfigSource.LastKnownGood, status.Source);
        Assert.Equal(first, status.Revision);
        Assert.False(status.AcceptsCommands);
        Assert.Equal(first.Id, restarted.Store.Current.Revision);
    }

    [Fact]
    public async Task ASettingsRowThatCantBeReadIsRewritten()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var latest = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")))).Revision!;
        await h.Db.ExecuteUncheckedAsync("PRAGMA foreign_keys = OFF; UPDATE GlobalSettings SET RevisionId = x'0102'");

        var status = await h.Service.StartAsync(Ct);

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, latest), status);
        await h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task ADamagedLastKnownGoodFileIsLoggedAndReplacedButNeverStopsAHealthyStart()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var latest = await CommitAndRememberAsync(h, "a.example.com");
        var path = LastKnownGoodPath(h);
        await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path, Ct)).Replace("\"seq\": 1", "\"seq\": \"1\"", StringComparison.Ordinal), Ct);
        await using var restarted = Restart(h);

        var status = await restarted.Service.StartAsync(Ct);
        await restarted.Service.StartupPublishFollowed;
        await restarted.LastGood.FlushAsync();

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, latest), status);
        Assert.Contains(restarted.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("its seq isn't a positive number", StringComparison.Ordinal));
        Assert.True(restarted.LastGood.TryRead(out var entry, out var problem), problem);
        Assert.Equal(latest, entry.Revision);
    }

    [Fact]
    public async Task ALastKnownGoodFileNewerThanTheDatabaseIsOnlyAWarning()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var latest = await CommitAndRememberAsync(h, "a.example.com");
        await WriteLastKnownGoodAsync(
            h, new RevisionRef(ConfigRevisionId.New(), latest.Seq + 2), new ConfigDocument(Hosts: [RespondHost(HostId.New(), "b.example.com")]));
        await using var restarted = Restart(h);

        var status = await restarted.Service.StartAsync(Ct);
        await restarted.Service.StartupPublishFollowed;

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, latest), status);
        Assert.Equal(latest.Id, restarted.Store.Current.Revision);
        Assert.Single(restarted.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("newer than the database's latest", StringComparison.Ordinal));
        Assert.Single(await h.RevisionsAsync());
    }

    [Fact]
    public async Task AnEmptyDatabaseIsRestoredFromTheLastKnownGoodFile()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var lost = new RevisionRef(ConfigRevisionId.New(), 7);
        await WriteLastKnownGoodAsync(h, lost, new ConfigDocument(Hosts: [RespondHost(HostId.New(), "kept.example.com")]));
        await using var restarted = Restart(h);

        var status = await restarted.Service.StartAsync(Ct);
        await restarted.Service.StartupPublishFollowed;
        await restarted.LastGood.FlushAsync();

        var restored = Assert.Single(await h.RevisionsAsync());
        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, new RevisionRef(restored.Id, 1)), status);
        Assert.Equal($"Restored from last-known-good #7 ({lost.Id})", restored.Message);
        Assert.Equal(Actor.System, restored.Actor);
        Assert.Equal(["kept.example.com"], (await h.CurrentAsync()).Hosts.Single().Names);
        Assert.Equal(restored.Id, restarted.Store.Current.Revision);
        Assert.Contains(await h.AuditAsync(), a => a.Action == "config.replace" && a.Actor == Actor.System && a.RevisionId == restored.Id);
        Assert.Contains(restarted.Logs.Entries, e => e.Level == LogLevel.Critical && e.Message.StartsWith("RESTORED", StringComparison.Ordinal));
        await h.AssertChainIntactAsync();
        Assert.True(restarted.LastGood.TryRead(out var entry, out var problem), problem);
        Assert.Equal(1, entry.Revision.Seq);
    }

    [Fact]
    public async Task ARestoreThatDoesNotValidateStartsDegradedOnTheLastKnownGood()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var lost = new RevisionRef(ConfigRevisionId.New(), 3);
        await WriteLastKnownGoodAsync(
            h, lost, new ConfigDocument(Hosts: [RespondHost(HostId.New(), "same.example.com"), RespondHost(HostId.New(), "same.example.com")]));
        await using var restarted = Restart(h);

        var status = await restarted.Service.StartAsync(Ct);

        Assert.Equal(ConfigSource.LastKnownGood, status.Source);
        Assert.Equal(lost, status.Revision);
        Assert.True(status.AcceptsCommands);
        Assert.Contains("restoring last-known-good #3", status.Problem, StringComparison.Ordinal);
        Assert.Empty(await h.RevisionsAsync());
        Assert.Equal(lost.Id, restarted.Store.Current.Revision);
    }

    [Fact]
    public async Task AFailedStartupPublishFallsBackOnTheLastKnownGood()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        var first = await CommitAndRememberAsync(h, "a.example.com");
        var second = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")))).Revision!;
        await using var restarted = Restart(h, new UnpublishableCompiler(second.Id));

        var status = await restarted.Service.StartAsync(Ct);
        await restarted.Service.StartupPublishFollowed;

        Assert.Equal(new ConfigStoreStatus(ConfigSource.Database, second), status); // as far as startup could tell
        var degraded = restarted.Service.Status;
        Assert.Equal(ConfigSource.LastKnownGood, degraded.Source);
        Assert.Equal(first, degraded.Revision);
        Assert.True(degraded.AcceptsCommands);
        Assert.Contains("at startup failed", degraded.Problem, StringComparison.Ordinal);
        Assert.Equal(first.Id, restarted.Store.Current.Revision);
    }

    private static string LastKnownGoodPath(ConfigServiceHarness h) =>
        Path.Combine(Path.GetDirectoryName(h.Db.DatabasePath)!, LastKnownGoodFile.FileName);

    /// <summary>Commits a host and starts a service once, so the revision ends up in last-good.json.</summary>
    private static async Task<RevisionRef> CommitAndRememberAsync(ConfigServiceHarness h, string name)
    {
        var revision = (await h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), name)))).Revision!;
        await using var remembering = Restart(h);
        await remembering.Service.StartAsync(Ct);
        await remembering.Service.StartupPublishFollowed;
        return revision;
    }

    private static async Task WriteLastKnownGoodAsync(ConfigServiceHarness h, RevisionRef revision, ConfigDocument document)
    {
        var bytes = Application.Config.CanonicalConfigJson.Serialize(document);
        await using var file = new LastKnownGoodFile(LastKnownGoodPath(h));
        file.Offer(new LastKnownGoodEntry(revision, bytes, System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    /// <summary>Another process on the harness's database: its own store, publisher, last-good file and logs.</summary>
    private static Restarted Restart(ConfigServiceHarness h, IConfigCompiler? compiler = null)
    {
        var logs = new CapturingLoggerFactory();
        var store = new SnapshotStore();
        var publisher = new SnapshotPublisher(store);
        var lastGood = new LastKnownGoodFile(LastKnownGoodPath(h), logs);
        var service = new ConfigService(
            h.Db.Factory, compiler ?? new ConfigServiceHarness.FakeCompiler(), publisher, loggerFactory: logs, lastKnownGood: lastGood);
        return new Restarted(store, publisher, lastGood, service, logs);
    }

    private sealed record Restarted(SnapshotStore Store, SnapshotPublisher Publisher, LastKnownGoodFile LastGood, ConfigService Service, CapturingLoggerFactory Logs)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Publisher.DisposeAsync();
            await LastGood.DisposeAsync();
            Store.Dispose();
        }
    }

    /// <summary>Compiles like <see cref="ConfigServiceHarness.FakeCompiler"/>, but hands back <paramref name="revision"/>
    /// already bound, which the publisher refuses: a startup publish that fails.</summary>
    private sealed class UnpublishableCompiler(ConfigRevisionId revision) : IConfigCompiler
    {
        private readonly ConfigServiceHarness.FakeCompiler _inner = new();

        public ConfigCompilation Compile(ConfigDocument document, ConfigRevisionId revisionId, CertificateInventory inventory)
        {
            var compiled = _inner.Compile(document, revisionId, inventory);
            return revisionId == revision ? compiled with { Snapshot = compiled.Snapshot!.Bind(1, []) } : compiled;
        }
    }
}
