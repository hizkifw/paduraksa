using System.Text.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using static Paduraksa.Tests.Data.Commands.Objects;

namespace Paduraksa.Tests.Data.Commands;

public sealed class ConfigServiceObjectCommandTests : IAsyncLifetime
{
    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task BeforeTheFirstCommandTheConfigIsTheEmptyDocumentWithNoRevision()
    {
        var current = await _h.Service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Null(current.Revision);
        Assert.Equal(CanonicalConfigJson.Serialize(new ConfigDocument()), CanonicalConfigJson.Serialize(current.Document));
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task CreatingAHostCommitsRevisionOneAndMakesItLive()
    {
        var id = HostId.New();

        var result = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "App.Example.com")) { Message = "  first host " });

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        Assert.Equal(1, result.Revision!.Seq);
        Assert.Equal(1, result.Version);
        Assert.Equal(PublishOutcomeStatus.Published, result.Publish!.Status);
        Assert.Equal(result.Revision.Id, _h.Store.Current.Revision);
        Assert.Equal(result.Publish.Generation, _h.Store.Current.Generation);
        var change = Assert.IsType<HostChange>(Assert.Single(result.Changes));
        Assert.Equal(ConfigChangeKind.Added, change.Kind);
        Assert.Equal(id, change.HostId);
        Assert.Equal("/hosts/0", change.Path);

        var current = await _h.Service.GetCurrentAsync(TestContext.Current.CancellationToken);
        Assert.Equal(result.Revision, current.Revision);
        var host = Assert.Single(current.Document.Hosts);
        Assert.Equal((1L, "app.example.com"), (host.Version, Assert.Single(host.Names))); // canonical, with the pipeline's version
        var revision = Assert.Single(await _h.RevisionsAsync());
        Assert.Equal("first host", revision.Message);
        Assert.Equal(JsonSerializer.Serialize(result.Changes, ConfigJsonContext.Default.IReadOnlyListConfigChange), revision.Summary);
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task EveryObjectCommandCommitsAndKeepsTheProjectionInStep()
    {
        var (profile, upstream, other, host) = (ProfileId.New(), UpstreamId.New(), UpstreamId.New(), HostId.New());
        var commands = new Func<ConfigDocument, ConfigCommand>[]
        {
            _ => new UpsertProfileCommand(Profile(profile, "LAN")),
            _ => new UpsertUpstreamCommand(Upstream(upstream, "app")),
            _ => new UpsertUpstreamCommand(Upstream(other, "other")),
            _ => new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { ProfileId = profile }),
            d => new UpdateSettingsCommand(d.Settings with { UnknownHost = UnknownHostBehaviour.Close }),
            d => new UpsertProfileCommand(d.Profiles[0] with { Notes = "renamed", Name = "LAN only" }),
            d => new UpsertUpstreamCommand(d.Upstreams.Single(u => u.Id == upstream) with { Destinations = [new DestinationConfig(DestinationId.New(), "http://app2:80")] }),
            d => new UpsertHostCommand(d.Hosts[0] with { Names = ["app.example.com", "www.app.example.com"], Routes = [d.Hosts[0].Routes[0] with { UpstreamId = other }] }),
            d => new UpsertHostCommand(d.Hosts[0] with { Enabled = false, ProfileId = null, Tags = ["Media"] }),
            d => new DeleteProfileCommand(profile, d.Profiles[0].Version),
            d => new DeleteUpstreamCommand(upstream, d.Upstreams.Single(u => u.Id == upstream).Version),
            d => new DeleteHostCommand(host, d.Hosts[0].Version),
            d => new DeleteUpstreamCommand(other, d.Upstreams[0].Version),
        };

        long seq = 0;
        foreach (var make in commands)
        {
            var command = make(await _h.CurrentAsync());
            var result = await _h.RunAsync(command);

            Assert.True(result.Status == ConfigCommandStatus.Committed, $"{command.GetType().Name}: {result.Status} {string.Join("; ", result.Errors.Concat(result.RelatedErrors))}");
            Assert.Equal(++seq, result.Revision!.Seq);
            Assert.NotEmpty(result.Changes);
            await _h.AssertProjectionMatchesAsync();
        }

        var empty = await _h.CurrentAsync();
        Assert.Empty(empty.Hosts);
        Assert.Empty(empty.Upstreams);
        Assert.Empty(empty.Profiles);
        Assert.Equal(UnknownHostBehaviour.Close, empty.Settings.UnknownHost);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task TheTouchedObjectGetsTheRevisionsSeqAsItsVersionAndOthersKeepTheirs()
    {
        var (a, b) = (HostId.New(), HostId.New());
        await _h.RunAsync(new UpsertHostCommand(RespondHost(a, "a.example.com") with { Version = 99 })); // ignored: a create names 0
        Assert.Empty((await _h.CurrentAsync()).Hosts);

        await _h.RunAsync(new UpsertHostCommand(RespondHost(a, "a.example.com")));
        await _h.RunAsync(new UpsertHostCommand(RespondHost(b, "b.example.com")));
        await _h.RunAsync(new UpsertHostCommand(RespondHost(a, "a.example.com", "a2.example.com") with { Version = 1 }));

        var hosts = (await _h.CurrentAsync()).Hosts.ToDictionary(h => h.Id, h => h.Version);
        Assert.Equal(3, hosts[a]);
        Assert.Equal(2, hosts[b]);
    }

    [Fact]
    public async Task TwoEditorsOfOneHostConflictAndTheSecondSeesTheCurrentVersion()
    {
        var id = HostId.New();
        var created = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com")));
        var seen = (await _h.CurrentAsync()).Hosts[0];

        var first = await _h.RunAsync(new UpsertHostCommand(seen with { Notes = "first" }));
        var second = await _h.RunAsync(new UpsertHostCommand(seen with { Notes = "second" }));

        Assert.Equal(ConfigCommandStatus.Committed, first.Status);
        Assert.Equal(ConfigCommandStatus.Conflict, second.Status);
        Assert.Equal(first.Version, second.Conflict!.CurrentVersion);
        Assert.Equal(first.Revision, second.Conflict.CurrentRevision);
        Assert.Empty(second.Conflict.ChangedSinceBase);
        var change = Assert.IsType<HostChange>(Assert.Single(second.Changes)); // what it would change on top of now
        Assert.Equal(["/hosts/0/notes"], change.Fields);
        Assert.Null(second.Revision);
        Assert.Equal("first", (await _h.CurrentAsync()).Hosts[0].Notes);
        Assert.Equal(2, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(created.Revision!.Seq + 1, first.Revision!.Seq);
    }

    [Fact]
    public async Task EditorsOfDifferentHostsDontConflict()
    {
        var (a, b) = (HostId.New(), HostId.New());
        await _h.RunAsync(new UpsertHostCommand(RespondHost(a, "a.example.com")));
        await _h.RunAsync(new UpsertHostCommand(RespondHost(b, "b.example.com")));
        var seen = await _h.CurrentAsync(); // both editors load the same config

        var first = await _h.RunAsync(new UpsertHostCommand(seen.Hosts.Single(h => h.Id == a) with { Notes = "a" }));
        var second = await _h.RunAsync(new UpsertHostCommand(seen.Hosts.Single(h => h.Id == b) with { Notes = "b" }));

        Assert.Equal(ConfigCommandStatus.Committed, first.Status);
        Assert.Equal(ConfigCommandStatus.Committed, second.Status);
        Assert.Equal(["a", "b"], (await _h.CurrentAsync()).Hosts.OrderBy(h => h.Notes).Select(h => h.Notes));
    }

    [Fact]
    public async Task StaleCreatesUpdatesAndDeletesAreRejected()
    {
        var id = HostId.New();
        await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com")));

        var createAgain = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com")));
        var staleDelete = await _h.RunAsync(new DeleteHostCommand(id, Version: 7));
        Assert.Equal(ConfigCommandStatus.Conflict, createAgain.Status);
        Assert.Equal(1, createAgain.Conflict!.CurrentVersion);
        Assert.Equal(ConfigCommandStatus.Conflict, staleDelete.Status);

        Assert.Equal(ConfigCommandStatus.Committed, (await _h.RunAsync(new DeleteHostCommand(id, 1))).Status);
        var updateDeleted = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com") with { Version = 1 }));
        var deleteDeleted = await _h.RunAsync(new DeleteHostCommand(id, 1));
        var staleSettings = await _h.RunAsync(new UpdateSettingsCommand(new GlobalSettings(Version: 5)));
        Assert.Equal(ConfigCommandStatus.NotFound, updateDeleted.Status);
        Assert.Equal(ConfigCommandStatus.NotFound, deleteDeleted.Status);
        Assert.Equal(ConfigCommandStatus.Conflict, staleSettings.Status);
        Assert.Equal(0, staleSettings.Conflict!.CurrentVersion);
        Assert.Equal(2, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task ACommandThatChangesNothingCommitsNothing()
    {
        var id = HostId.New();
        await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com")));

        // Re-spelled, but the same config.
        var result = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, " APP.example.com. ") with { Version = 1 }));
        var settings = await _h.RunAsync(new UpdateSettingsCommand(new GlobalSettings()));

        Assert.Equal(ConfigCommandStatus.Unchanged, result.Status);
        Assert.Equal(ConfigCommandStatus.Unchanged, settings.Status);
        Assert.Equal(1, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(1, await _h.CountAsync("AuditEvents"));
    }

    [Fact]
    public async Task ADryRunReturnsTheDiffAndPersistsNothing()
    {
        await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        var liveBefore = _h.Store.Current;
        var id = HostId.New();

        var result = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "b.example.com")) { DryRun = true });

        Assert.Equal(ConfigCommandStatus.DryRun, result.Status);
        Assert.Equal(2, result.Version);
        Assert.Null(result.Revision);
        Assert.Null(result.Publish);
        Assert.Equal(id, Assert.IsType<HostChange>(Assert.Single(result.Changes)).HostId);
        Assert.Equal(ConfigRevisionId.Empty, _h.Compiler.Compiled[^1]); // compiled, but labelled with no revision
        Assert.Same(liveBefore, _h.Store.Current);
        Assert.Equal(1, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(1, await _h.CountAsync("AuditEvents"));
        Assert.Single((await _h.CurrentAsync()).Hosts);
        await _h.AssertProjectionMatchesAsync();
    }

    [Fact]
    public async Task AnInvalidObjectIsRejectedWithPathsRelativeToIt()
    {
        var host = RespondHost(HostId.New(), "ok.example.com", "not a name!") with { Respond = new RespondConfig(99) };

        var result = await _h.RunAsync(new UpsertHostCommand(host));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Path == "/names/1" && e.Code == ConfigErrorCodes.HostNameInvalid);
        Assert.Contains(result.Errors, e => e.Path == "/respond/statusCode" && e.Code == ConfigErrorCodes.RespondStatusCodeInvalid);
        Assert.Empty(result.RelatedErrors);
        Assert.NotEmpty(result.Changes);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(0, await _h.CountAsync("Hosts"));
        var audit = Assert.Single(await _h.AuditAsync());
        Assert.Equal(("config.host.create", AuditOutcome.Invalid, host.Id.ToString(), (ConfigRevisionId?)null), (audit.Action, audit.Outcome, audit.Target, audit.RevisionId));
        Assert.Contains(ConfigErrorCodes.HostNameInvalid, audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANameClashIsReportedOnTheSentHostWhereverItSortsWhileBrokenReferencesAreRelated()
    {
        var upstream = UpstreamId.New();
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        var hosts = Enumerable.Range(0, 3).Select(_ => HostId.New()).Order().ToArray();
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(hosts[0], "zero.example.com", upstream)));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(hosts[2], "two.example.com", upstream)));

        // hosts[1] sorts between the two, so a clash with hosts[2] would be found on hosts[2] in canonical order.
        var clash = await _h.RunAsync(new UpsertHostCommand(RespondHost(hosts[1], "one.example.com", "TWO.example.com")));
        var dangling = await _h.RunAsync(new DeleteUpstreamCommand(upstream, 1));

        Assert.Equal(ConfigCommandStatus.Invalid, clash.Status);
        var error = Assert.Single(clash.Errors);
        Assert.Equal(("/names/1", ConfigErrorCodes.HostNameDuplicate), (error.Path, error.Code));
        Assert.Empty(clash.RelatedErrors);

        Assert.Equal(ConfigCommandStatus.Invalid, dangling.Status);
        Assert.Empty(dangling.Errors);
        // Pointers into the canonical document after the change: hosts[0] and hosts[2] are /hosts/0 and /hosts/1.
        Assert.Equal(["/hosts/0/routes/0/upstreamId", "/hosts/1/routes/0/upstreamId"], dangling.RelatedErrors.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task ACompileFailurePersistsNothingAndPointsIntoTheCanonicalObject()
    {
        var id = HostId.New();
        _h.Compiler.Fail = d => d.Hosts.Count > 0 ? [new ConfigError("/hosts/0/names/0", "compile.test", "no")] : null;

        var result = await _h.RunAsync(new UpsertHostCommand(RespondHost(id, "app.example.com")));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal(("/names/0", "compile.test"), (Assert.Single(result.Errors).Path, result.Errors[0].Code));
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
        Assert.Equal(0, await _h.CountAsync("Hosts"));
        Assert.False(_h.Store.HasPublished);
        Assert.Equal(AuditOutcome.Invalid, Assert.Single(await _h.AuditAsync()).Outcome);
    }

    [Fact]
    public async Task ATooLongMessageIsInvalid()
    {
        var result = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")) { Message = new string('m', 1001) });

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Equal(ConfigService.MessageInvalidCode, Assert.Single(result.Errors).Code);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
    }
}
