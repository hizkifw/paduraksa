using System.Text;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Security;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>Access lists through the command pipeline: a password is hashed before anything else sees the command, an edit
/// keeps the hashes it isn't given new passwords for, a list in use can't be deleted, and a stored document of the old shape
/// is converted at startup and when a revision is rolled back to.</summary>
public sealed class ConfigServiceAccessListTests : IAsyncLifetime
{
    private const string Secret = "correct horse battery staple";

    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task APasswordIsHashedAndNeverStoredLoggedOrAudited()
    {
        var id = AccessListId.New();

        var result = await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var stored = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var user = Assert.Single(stored.BasicAuth!.Users);
        Assert.Null(user.Password);
        Assert.True(PasswordHash.TryParse(user.PasswordHash, out var parsed));
        Assert.True(PasswordHash.Verify(Encoding.UTF8.GetBytes(Secret), parsed));

        // Not in the revision's bytes, not in the audit entry, not in the change summary.
        var revision = Assert.Single(await _h.RevisionsAsync());
        await using (var context = _h.Db.NewContext())
        {
            var bytes = (await ConfigRevisionStore.GetAsync(context, revision.Id, Ct))!.Document.ToArray();
            Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Secret, revision.Summary, StringComparison.Ordinal);
        var audit = await _h.WaitForAuditAsync(e => e.Action == "config.access_list.create");
        Assert.Equal(id.ToString(), audit.Target);
        Assert.DoesNotContain(Secret, audit.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash!, audit.Details ?? string.Empty, StringComparison.Ordinal);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task EveryPasswordGetsItsOwnSalt()
    {
        var id = AccessListId.New();

        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret), ("bob", Secret))));

        var users = Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users;
        Assert.NotEqual(users[0].PasswordHash, users[1].PasswordHash);
    }

    [Fact]
    public async Task AnEditThatSendsNoPasswordKeepsTheStoredHash()
    {
        var id = AccessListId.New();
        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret), ("bob", "another password"))));
        var before = Assert.Single((await _h.CurrentAsync()).AccessLists);

        // Rename the list and change bob's password; alice is sent by name only.
        var edit = before with
        {
            Name = "head office",
            BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice"), new AccessListUserConfig("bob", Password: "a third password")]),
        };
        var result = await _h.RunAsync(new UpsertAccessListCommand(edit));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var after = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var (aliceBefore, bobBefore) = (before.BasicAuth!.Users[0], before.BasicAuth.Users[1]);
        var (aliceAfter, bobAfter) = (after.BasicAuth!.Users[0], after.BasicAuth.Users[1]);
        Assert.Equal(aliceBefore.PasswordHash, aliceAfter.PasswordHash);
        Assert.NotEqual(bobBefore.PasswordHash, bobAfter.PasswordHash);
        Assert.True(PasswordHash.TryParse(bobAfter.PasswordHash, out var parsed));
        Assert.True(PasswordHash.Verify("a third password"u8, parsed));
    }

    [Fact]
    public async Task ANewUserWithNeitherAPasswordNorAHashIsRefused()
    {
        var result = await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", null))));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigErrorCodes.AccessListPasswordHashInvalid && e.Path == "/basicAuth/users/0/passwordHash");
        Assert.Empty((await _h.CurrentAsync()).AccessLists);
    }

    [Fact]
    public async Task AnEmptyPasswordIsRefusedAndNothingIsStored()
    {
        var result = await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", string.Empty))));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigErrorCodes.AccessListPasswordEmpty);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
        var audit = await _h.WaitForAuditAsync(e => e.Action == "config.access_list.create");
        Assert.Equal(AuditOutcome.Invalid, audit.Outcome);
    }

    [Fact]
    public async Task ReplacingTheWholeConfigHashesPasswordsToo()
    {
        var id = AccessListId.New();
        var document = new ConfigDocument(AccessLists: [List(id, "office", ("alice", Secret))]);

        var result = await _h.RunAsync(new ReplaceConfigCommand(document, BaseRevision: null));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var user = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users);
        Assert.Null(user.Password);
        Assert.True(PasswordHash.IsValid(user.PasswordHash));
    }

    [Fact]
    public async Task AListIsVersionedLikeAnyOtherObjectAndAStaleEditConflicts()
    {
        var id = AccessListId.New();
        var created = await _h.RunAsync(new UpsertAccessListCommand(new AccessListConfig(id, "office", Ip: new AccessListIp(["10.0.0.0/8"]))));
        Assert.Equal(1, created.Version);
        var change = Assert.IsType<AccessListChange>(Assert.Single(created.Changes));
        Assert.Equal((ConfigChangeKind.Added, id, "/accessLists/0"), (change.Kind, change.AccessListId, change.Path));

        var updated = await _h.RunAsync(new UpsertAccessListCommand(new AccessListConfig(id, "office", Version: 1, Ip: new AccessListIp(["10.0.0.0/8", "192.168.0.0/16"]))));
        Assert.Equal(2, updated.Version);

        var stale = await _h.RunAsync(new UpsertAccessListCommand(new AccessListConfig(id, "office", Version: 1)));
        Assert.Equal(ConfigCommandStatus.Conflict, stale.Status);
    }

    [Fact]
    public async Task AListInUseCantBeDeletedAndAnUnusedOneCan()
    {
        var (list, upstream, host) = (AccessListId.New(), UpstreamId.New(), HostId.New());
        await _h.RunAsync(new UpsertAccessListCommand(new AccessListConfig(list, "office", Ip: new AccessListIp(["10.0.0.0/8"]))));
        await _h.RunAsync(new UpsertUpstreamCommand(Upstream(upstream, "app")));
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = list } } }));
        var version = Assert.Single((await _h.CurrentAsync()).AccessLists).Version;

        var refused = await _h.RunAsync(new DeleteAccessListCommand(list, version));

        Assert.Equal(ConfigCommandStatus.Invalid, refused.Status);
        Assert.Contains(refused.RelatedErrors, e => e.Code == ConfigErrorCodes.HostAccessListUnknown);
        Assert.Single((await _h.CurrentAsync()).AccessLists);

        var hostVersion = Assert.Single((await _h.CurrentAsync()).Hosts).Version;
        await _h.RunAsync(new UpsertHostCommand(ProxyHost(host, "app.example.com", upstream) with { Version = hostVersion }));
        var deleted = await _h.RunAsync(new DeleteAccessListCommand(list, version));

        Assert.Equal(ConfigCommandStatus.Committed, deleted.Status);
        Assert.Empty((await _h.CurrentAsync()).AccessLists);
        Assert.Equal("config.access_list.delete", (await _h.WaitForAuditAsync(e => e.Action == "config.access_list.delete")).Action);
    }

    [Fact]
    public async Task ADryRunHashesButStoresNothing()
    {
        var result = await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", Secret))) { DryRun = true });

        Assert.Equal(ConfigCommandStatus.DryRun, result.Status);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task AStoredDocumentOfTheOldShapeIsConvertedAndStoredOnceAtStartup()
    {
        var host = HostId.New();
        await SeedOldRevisionAsync(host, "10.0.0.0/8", drop: true);

        var status = await _h.Service.StartAsync(Ct);

        // One more revision, by the system, saying what was converted.
        Assert.Equal(2, status.Revision!.Seq);
        var revisions = await _h.RevisionsAsync();
        Assert.Equal([2L, 1L], revisions.Select(r => r.Seq));
        Assert.Equal(ActorKind.System, revisions[0].Actor.Kind);
        Assert.Contains("Converted the inline IP allow lists to access lists: host-app-example-com", revisions[0].Message, StringComparison.Ordinal);
        var current = await _h.CurrentAsync();
        var list = Assert.Single(current.AccessLists);
        Assert.Equal(["10.0.0.0/8"], list.Ip!.Allow);
        Assert.True(list.Ip.DropDenied);
        Assert.Equal(list.Id, Assert.Single(current.Hosts).Settings.AccessList!.AccessListId);
        Assert.All(current.AccessLists.Select(l => l.Version).Concat(current.Hosts.Select(h => h.Version)), v => Assert.Equal(2, v));
        var audit = await _h.WaitForAuditAsync(e => e.Action == "config.upgrade");
        Assert.Equal((ActorKind.System, AuditOutcome.Success, revisions[0].Id), (audit.Actor.Kind, audit.Outcome, audit.RevisionId));
        await _h.AssertProjectionMatchesAsync();
        await _h.AssertChainIntactAsync();

        // The stored config is the new shape now, so the next start finds nothing to convert.
        using Paduraksa.Proxy.Snapshot.SnapshotStore store = new();
        await using Paduraksa.Proxy.Yarp.SnapshotPublisher publisher = new(store);
        using ConfigService restarted = new(_h.Db.Factory, new ConfigServiceHarness.FakeCompiler(), publisher);
        await restarted.StartAsync(Ct);
        Assert.Equal(2, (await _h.RevisionsAsync()).Count);
    }

    [Fact]
    public async Task ARevisionOfTheOldShapeCanStillBeRolledBackTo()
    {
        var host = HostId.New();
        var first = await SeedOldRevisionAsync(host, "10.0.0.0/8", drop: false);
        await _h.Service.StartAsync(Ct);
        await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "other.example.com")));
        Assert.Equal(2, (await _h.CurrentAsync()).Hosts.Count);

        var head = (await _h.Service.GetCurrentAsync(Ct)).Revision!.Id;
        var result = await _h.RunAsync(new RollbackConfigCommand(first, head));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var current = await _h.CurrentAsync();
        var list = Assert.Single(current.AccessLists);
        Assert.Equal(["10.0.0.0/8"], list.Ip!.Allow);
        Assert.Equal(list.Id, Assert.Single(current.Hosts).Settings.AccessList!.AccessListId);
        Assert.Empty(ConfigValidator.Validate(current));
    }

    [Fact]
    public async Task AStoredDocumentWithoutAnInlineListIsNeverRewrittenAtStartup()
    {
        await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));

        await _h.Service.StartAsync(Ct);

        Assert.Single(await _h.RevisionsAsync());
    }

    private static AccessListConfig List(AccessListId id, string name, params (string Name, string? Password)[] users) =>
        new(id, name, Ip: new AccessListIp(["10.0.0.0/8"]), BasicAuth: new AccessListBasicAuth(Users: [.. users.Select(u => new AccessListUserConfig(u.Name, Password: u.Password))]));

    /// <summary>Stores a revision as the build before access lists wrote it: a respond host with an inline allow list.</summary>
    private async Task<ConfigRevisionId> SeedOldRevisionAsync(HostId host, string address, bool drop)
    {
        var modern = new ConfigDocument(Hosts: [new HostConfig(host, ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"), Version: 1, Settings: new HostSettings { ForceHttps = true })]);
        var json = Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(modern))
            .Replace("\"settings\":{\"forceHttps\":true}", $"\"settings\":{{\"forceHttps\":true,\"ipAllowList\":{{\"addresses\":[\"{address}\"],\"dropDenied\":{(drop ? "true" : "false")}}}}}", StringComparison.Ordinal)
            .Replace(",\"accessLists\":[]", string.Empty, StringComparison.Ordinal);
        Assert.Contains("ipAllowList", json, StringComparison.Ordinal);
        await using var context = _h.Db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var info = await new ConfigRevisionStore(TimeProvider.System).AppendAsync(
            context, new NewRevision(Encoding.UTF8.GetBytes(json), "[]", Actor.Cli, Channel.Cli, "an old revision"), Ct);
        await transaction.CommitAsync(Ct);
        return info.Id;
    }
}
