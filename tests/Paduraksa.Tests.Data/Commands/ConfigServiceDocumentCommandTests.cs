using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Commands.Objects;

namespace Paduraksa.Tests.Data.Commands;

public sealed class ConfigServiceDocumentCommandTests : IAsyncLifetime
{
    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task ReplaceOnAnEmptyDatabaseNeedsNoBaseAndIgnoresTheSentVersions()
    {
        var upstream = UpstreamId.New();
        var document = new ConfigDocument(
            Settings: new GlobalSettings(Version: 40),
            Upstreams: [Upstream(upstream, "app") with { Version = 12 }],
            Hosts: [ProxyHost(HostId.New(), "app.example.com", upstream) with { Version = 7 }]);

        var result = await _h.RunAsync(new ReplaceConfigCommand(document, BaseRevision: null));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Null(result.Version);
        var current = await _h.CurrentAsync();
        // The settings are the defaults, so they didn't change and keep version 0; the new objects get #1.
        Assert.Equal([0L, 1L, 1L], [current.Settings.Version, current.Upstreams[0].Version, current.Hosts[0].Version]);
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task ReplaceKeepsTheVersionsOfObjectsItDoesntChange()
    {
        var (a, b) = (HostId.New(), HostId.New());
        await _h.RunAsync(new UpsertHostCommand(RespondHost(a, "a.example.com")));
        var second = await _h.RunAsync(new UpsertHostCommand(RespondHost(b, "b.example.com")));
        var exported = await _h.CurrentAsync();

        var imported = exported with
        {
            Hosts = [.. exported.Hosts.Select(h => h.Id == b ? h with { Notes = "imported", Version = 0 } : h with { Version = 0 })],
        };
        var result = await _h.RunAsync(new ReplaceConfigCommand(imported, second.Revision!.Id));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var versions = (await _h.CurrentAsync()).Hosts.ToDictionary(h => h.Id, h => h.Version);
        Assert.Equal(1, versions[a]);
        Assert.Equal(3, versions[b]);
        Assert.Equal(b, Assert.IsType<HostChange>(Assert.Single(result.Changes)).HostId);
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task ReplaceOnAStaleBaseConflictsWithWhatChangedSince()
    {
        var first = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var added = HostId.New();
        await _h.RunAsync(new UpsertHostCommand(RespondHost(added, "b.example.com")));

        var stale = await _h.RunAsync(new ReplaceConfigCommand(new ConfigDocument(), first.Revision!.Id));
        var none = await _h.RunAsync(new ReplaceConfigCommand(new ConfigDocument(), BaseRevision: null));

        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
        Assert.Equal(2, stale.Conflict!.CurrentRevision!.Seq);
        Assert.Null(stale.Conflict.CurrentVersion);
        Assert.Equal(added, Assert.IsType<HostChange>(Assert.Single(stale.Conflict.ChangedSinceBase)).HostId);
        Assert.Equal(2, stale.Changes.Count); // what the replace would remove
        Assert.Equal(ConfigCommandStatus.Conflict, none.Status);
        Assert.Empty(none.Conflict!.ChangedSinceBase);
        Assert.Equal(2, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task ReplaceReportsErrorsAtTheirPathsInTheDocumentAsSent()
    {
        var (low, high) = (HostId.New(), HostId.New());
        if (high < low)
        {
            (low, high) = (high, low);
        }

        // Sent out of canonical order: the bad host is listed first but sorts last.
        var document = new ConfigDocument(Hosts: [RespondHost(high, "bad name"), RespondHost(low, "ok.example.com")]);

        var result = await _h.RunAsync(new ReplaceConfigCommand(document, null));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal(("/hosts/0/names/0", ConfigErrorCodes.HostNameInvalid), (Assert.Single(result.Errors).Path, result.Errors[0].Code));
        Assert.Empty(result.RelatedErrors);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task RollbackCommitsTheOldConfigAsANewRevisionWithNewVersions()
    {
        var (steady, edited, later) = (HostId.New(), HostId.New(), HostId.New());
        await _h.RunAsync(new UpsertHostCommand(RespondHost(steady, "steady.example.com")));                     // #1
        var target = await _h.RunAsync(new UpsertHostCommand(RespondHost(edited, "edited.example.com")));        // #2
        var editorCopy = (await _h.CurrentAsync()).Hosts.Single(h => h.Id == edited);                             // an editor holds v2
        await _h.RunAsync(new UpsertHostCommand(editorCopy with { Notes = "newer" }));                           // #3
        var head = await _h.RunAsync(new UpsertHostCommand(RespondHost(later, "later.example.com")));            // #4

        var result = await _h.RunAsync(new RollbackConfigCommand(target.Revision!.Id, head.Revision!.Id));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(5, result.Revision!.Seq);
        Assert.Equal(result.Revision.Id, _h.Store.Current.Revision);
        var revision = (await _h.RevisionsAsync())[0];
        Assert.Equal(("Rollback to #2", head.Revision.Id), (revision.Message, revision.ParentId)); // history is appended to, not rewritten
        Assert.Equal(5, await _h.CountAsync("ConfigRevisions"));

        // The same config as #2, byte for byte once versions are left out.
        var restored = await _h.RevisionDocumentAsync(result.Revision.Id);
        var original = await _h.RevisionDocumentAsync(target.Revision.Id);
        Assert.Empty(ConfigDiff.Compare(original, restored));
        Assert.Equal(CanonicalConfigJson.Serialize(WithoutVersions(original)), CanonicalConfigJson.Serialize(WithoutVersions(restored)));

        // What it changed got #5 as its version, above anything it ever had; what it left alone kept its version.
        var versions = restored.Hosts.ToDictionary(h => h.Id, h => h.Version);
        Assert.Equal(5, versions[edited]);
        Assert.Equal(1, versions[steady]);
        Assert.False(versions.ContainsKey(later));

        // The editor still holding v2 (the version #2 had) must not overwrite the rollback.
        var stale = await _h.RunAsync(new UpsertHostCommand(editorCopy with { Notes = "stale" }));
        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
        Assert.Equal(5, stale.Conflict!.CurrentVersion);

        var audit = (await _h.AuditAsync()).Single(a => a.Action == "config.rollback");
        Assert.Equal((AuditOutcome.Success, target.Revision.Id.ToString(), (ConfigRevisionId?)result.Revision.Id), (audit.Outcome, audit.Target, audit.RevisionId));
        await _h.AssertProjectionMatchesAsync();
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task RollingBackPastADeleteRecreatesTheObjectAboveEveryVersionItHad()
    {
        var id = HostId.New();
        await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "a.example.com")));                     // #1, v1
        var withHost = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "a.example.com") with { Version = 1, Notes = "n" })); // #2, v2
        var deleted = await _h.RunAsync(new DeleteHostCommand(id, 2));                                    // #3

        var result = await _h.RunAsync(new RollbackConfigCommand(withHost.Revision!.Id, deleted.Revision!.Id) { Message = "undo delete" });

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(4, Assert.Single((await _h.CurrentAsync()).Hosts).Version);
        Assert.Equal("undo delete", (await _h.RevisionsAsync())[0].Message);
        Assert.Equal(ConfigCommandStatus.Conflict, (await _h.RunAsync(new DeleteHostCommand(id, 2))).Status);
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task RollbackToAnUnknownRevisionOrOnAStaleBaseIsRejected()
    {
        var first = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var second = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")));

        var unknown = await _h.RunAsync(new RollbackConfigCommand(ConfigRevisionId.New(), second.Revision!.Id));
        var stale = await _h.RunAsync(new RollbackConfigCommand(first.Revision!.Id, first.Revision.Id));
        var toHead = await _h.RunAsync(new RollbackConfigCommand(second.Revision.Id, second.Revision.Id));

        Assert.Equal(ConfigCommandStatus.NotFound, unknown.Status);
        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
        Assert.Single(stale.Conflict!.ChangedSinceBase);
        Assert.Equal(ConfigCommandStatus.Unchanged, toHead.Status);
        Assert.Equal(2, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(
            [AuditOutcome.Success, AuditOutcome.Success, AuditOutcome.Invalid, AuditOutcome.Conflict],
            (await _h.AuditAsync()).Select(a => a.Outcome));
    }

    [Fact]
    public async Task ARollbackDryRunPreviewsWithoutCommitting()
    {
        var first = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var second = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "b.example.com")));

        var result = await _h.RunAsync(new RollbackConfigCommand(first.Revision!.Id, second.Revision!.Id) { DryRun = true });

        Assert.Equal(ConfigCommandStatus.DryRun, result.Status);
        Assert.Equal(ConfigChangeKind.Removed, Assert.IsType<HostChange>(Assert.Single(result.Changes)).Kind);
        Assert.Equal(2, await _h.CountAsync("ConfigRevisions"));
    }

    private static ConfigDocument WithoutVersions(ConfigDocument document) => document with
    {
        Settings = document.Settings with { Version = 0 },
        Profiles = [.. document.Profiles.Select(p => p with { Version = 0 })],
        Upstreams = [.. document.Upstreams.Select(u => u with { Version = 0 })],
        Hosts = [.. document.Hosts.Select(h => h with { Version = 0 })],
    };
}
