using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>Access lists through the command pipeline: a password goes to the vault before anything else sees the command and the
/// document keeps the credential's ID, an edit keeps the credentials it isn't given new passwords for, a credential lives as long
/// as the live config names it, a list in use can't be deleted, and a stored document of an old shape (inline allow lists, inline
/// password hashes) is converted at startup and when a revision is rolled back to.</summary>
public sealed class ConfigServiceAccessListTests : IAsyncLifetime
{
    private const string Secret = "correct horse battery staple";

    private ConfigServiceHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await ConfigServiceHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static readonly byte[] Client = new byte[16];

    private PasswordVerdict Check(PasswordCredentialId? credential, string user, string password) =>
        _h.Passwords.Verifier.Verify(_h.Passwords.Open(credential!.Value), Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(password), Client, remember: false);

    [Fact]
    public async Task APasswordGoesToTheVaultAndIsNeverStoredLoggedOrAudited()
    {
        var id = AccessListId.New();

        var result = await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var stored = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var user = Assert.Single(stored.BasicAuth!.Users);
        Assert.Null(user.Password);
        Assert.Equal(PasswordVerdict.Verified, Check(user.CredentialId, "alice", Secret));
        Assert.Equal(PasswordVerdict.Rejected, Check(user.CredentialId, "alice", "wrong"));

        // Not in the revision's bytes, not in the audit entry, not in the change summary: the document names the credential only.
        var revision = Assert.Single(await _h.RevisionsAsync());
        await using (var context = _h.Db.NewContext())
        {
            var text = Encoding.UTF8.GetString((await ConfigRevisionStore.GetAsync(context, revision.Id, Ct))!.Document.ToArray());
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("pbkdf2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("passwordHash", text, StringComparison.Ordinal);
            Assert.Contains(user.CredentialId!.Value.ToString(), text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Secret, revision.Summary, StringComparison.Ordinal);
        var audit = await _h.WaitForAuditAsync(e => e.Action == "config.access_list.create");
        Assert.Equal(id.ToString(), audit.Target);
        Assert.DoesNotContain(Secret, audit.Details ?? string.Empty, StringComparison.Ordinal);
        await _h.AssertChainIntactAsync();
    }

    [Fact]
    public async Task EveryUserHasACredentialOfTheirOwnEvenWithTheSamePassword()
    {
        await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", Secret), ("bob", Secret))));

        var users = Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users;
        Assert.NotEqual(users[0].CredentialId, users[1].CredentialId);
        Assert.Equal(2, (await _h.Passwords.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task AnEditThatSendsNoPasswordKeepsTheCredentialAndAChangedPasswordKeepsItsID()
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
        Assert.Equal(aliceBefore.CredentialId, aliceAfter.CredentialId);
        Assert.Equal(bobBefore.CredentialId, bobAfter.CredentialId);
        Assert.Equal(PasswordVerdict.Verified, Check(aliceAfter.CredentialId, "alice", Secret));
        Assert.Equal(PasswordVerdict.Rejected, Check(bobAfter.CredentialId, "bob", "another password"));
        Assert.Equal(PasswordVerdict.Verified, Check(bobAfter.CredentialId, "bob", "a third password"));
    }

    [Fact]
    public async Task ACommandThatOnlyChangesAPasswordStillCommitsARevisionAndMovesTheListsVersion()
    {
        var id = AccessListId.New();
        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        var before = Assert.Single((await _h.CurrentAsync()).AccessLists);

        var result = await _h.RunAsync(new UpsertAccessListCommand(before with
        {
            BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "a new password")]),
        }));

        // The document's users are the same (the credential is the same); the revision says the list's users changed, no more.
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var change = Assert.IsType<AccessListChange>(Assert.Single(result.Changes));
        Assert.Equal((ConfigChangeKind.Modified, id, "/accessLists/0"), (change.Kind, change.AccessListId, change.Path));
        Assert.Equal(["/accessLists/0/basicAuth/users"], change.Fields);
        var after = Assert.Single((await _h.CurrentAsync()).AccessLists);
        Assert.True(after.Version > before.Version);
        Assert.Equal(before.BasicAuth!.Users, after.BasicAuth!.Users);
        Assert.Equal(2, (await _h.RevisionsAsync()).Count);
        Assert.Equal(PasswordVerdict.Verified, Check(after.BasicAuth.Users[0].CredentialId, "alice", "a new password"));
        Assert.Equal(PasswordVerdict.Rejected, Check(after.BasicAuth.Users[0].CredentialId, "alice", Secret));
    }

    [Fact]
    public async Task ANewUserWithNeitherAPasswordNorACredentialIsRefused()
    {
        var result = await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", null))));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigErrorCodes.AccessListPasswordMissing && e.Path == "/basicAuth/users/0/password");
        Assert.Empty((await _h.CurrentAsync()).AccessLists);
    }

    [Fact]
    public async Task AnEmptyPasswordIsRefusedAndNothingIsStoredOrSet()
    {
        var result = await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", string.Empty))));

        Assert.Equal(ConfigCommandStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Code == ConfigErrorCodes.AccessListPasswordEmpty);
        Assert.Equal(0, await _h.CountAsync("ConfigRevisions"));
        Assert.Empty(await _h.Passwords.ListAsync(Ct));
        var audit = await _h.WaitForAuditAsync(e => e.Action == "config.access_list.create");
        Assert.Equal(AuditOutcome.Invalid, audit.Outcome);
    }

    [Fact]
    public async Task AnInvalidCommandOrADryRunChangesNoPassword()
    {
        var id = AccessListId.New();
        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        var before = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var credential = before.BasicAuth!.Users[0].CredentialId;

        // A dry run and a command another rule refuses (the realm) both carry a new password for alice.
        var dryRun = await _h.RunAsync(new UpsertAccessListCommand(before with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "dry run password")]) }) { DryRun = true });
        var invalid = await _h.RunAsync(new UpsertAccessListCommand(before with { BasicAuth = new AccessListBasicAuth("\u0001", [new AccessListUserConfig("alice", Password: "refused password")]) }));

        Assert.Equal(ConfigCommandStatus.DryRun, dryRun.Status);
        Assert.Equal(ConfigCommandStatus.Invalid, invalid.Status);
        Assert.Equal(PasswordVerdict.Verified, Check(credential, "alice", Secret));
        Assert.Equal(PasswordVerdict.Rejected, Check(credential, "alice", "dry run password"));
        Assert.Equal(1, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task ReplacingTheWholeConfigSendsPasswordsToTheVaultToo()
    {
        var id = AccessListId.New();
        var document = new ConfigDocument(AccessLists: [List(id, "office", ("alice", Secret))]);

        var result = await _h.RunAsync(new ReplaceConfigCommand(document, BaseRevision: null));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var user = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users);
        Assert.Null(user.Password);
        Assert.Equal(PasswordVerdict.Verified, Check(user.CredentialId, "alice", Secret));
    }

    [Fact]
    public async Task ACredentialLivesAsLongAsTheLiveConfigNamesIt()
    {
        var id = AccessListId.New();
        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret), ("bob", Secret))));
        var list = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var (alice, bob) = (list.BasicAuth!.Users[0].CredentialId!.Value, list.BasicAuth.Users[1].CredentialId!.Value);

        // Removing a user takes their credential with them; deleting the list takes the rest.
        await _h.RunAsync(new UpsertAccessListCommand(list with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("bob")]) }));
        Assert.Equal([bob], await _h.Passwords.ListAsync(Ct));
        Assert.False(_h.Passwords.Open(alice).IsSet);
        var current = Assert.Single((await _h.CurrentAsync()).AccessLists);
        await _h.RunAsync(new DeleteAccessListCommand(id, current.Version));
        Assert.Empty(await _h.Passwords.ListAsync(Ct));
    }

    [Fact]
    public async Task ARollbackRestoresTheUserButNotAPasswordTheVaultNoLongerHolds()
    {
        var passwords = new TestPasswords();
        await using var h = await ConfigServiceHarness.CreateAsync(compiler: new ConfigCompiler(SnapshotCompilerOptions.Default, passwords), passwords: passwords);
        var id = AccessListId.New();
        await h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        var first = (await h.Service.GetCurrentAsync(Ct)).Revision!.Id;
        var list = Assert.Single((await h.CurrentAsync()).AccessLists);
        var credential = list.BasicAuth!.Users[0].CredentialId!.Value;
        await h.RunAsync(new UpsertAccessListCommand(list with { BasicAuth = null }));
        Assert.False(passwords.Open(credential).IsSet);

        var head = (await h.Service.GetCurrentAsync(Ct)).Revision!.Id;
        var result = await h.RunAsync(new RollbackConfigCommand(first, head));

        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var restored = Assert.Single((await h.CurrentAsync()).AccessLists).BasicAuth!.Users[0];
        Assert.Equal((credential, "alice"), (restored.CredentialId!.Value, restored.Username));
        Assert.False(passwords.Open(credential).IsSet);
        var warning = Assert.Single(result.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.Equal("/accessLists/0/basicAuth/users/0", warning.Path);
        Assert.Contains("'alice'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADryRunOrANewUsersPendingPasswordIsNotReportedAsMissing()
    {
        var passwords = new TestPasswords();
        await using var h = await ConfigServiceHarness.CreateAsync(compiler: new ConfigCompiler(SnapshotCompilerOptions.Default, passwords), passwords: passwords);

        var dryRun = await h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", Secret))) { DryRun = true });
        var created = await h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "other", ("bob", Secret))));

        Assert.Equal(ConfigCommandStatus.DryRun, dryRun.Status);
        Assert.DoesNotContain(dryRun.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.DoesNotContain(created.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.Single(await passwords.ListAsync(Ct));
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
        await _h.AssertChainIntactAsync();

        // The stored config is the new shape now, so the next start finds nothing to convert.
        using Paduraksa.Proxy.Snapshot.SnapshotStore store = new();
        await using Paduraksa.Proxy.Yarp.SnapshotPublisher publisher = new(store);
        using var restarted = TestConfigServices.Create(_h.Db, publisher);
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

    private const string FailRevisions = "CREATE TRIGGER fail_revisions BEFORE INSERT ON ConfigRevisions BEGIN SELECT RAISE(ABORT, 'disk full'); END";

    [Fact]
    public async Task ACommitThatFailsAfterThePasswordsWereSetPutsThemBackExactly()
    {
        var id = AccessListId.New();
        await _h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        var before = Assert.Single((await _h.CurrentAsync()).AccessLists);
        var alice = before.BasicAuth!.Users[0].CredentialId!.Value;
        await _h.Db.ExecuteAsync(FailRevisions);

        // alice's password changes and bob is new; the revision can't be stored.
        await Assert.ThrowsAnyAsync<Exception>(async () => await _h.RunAsync(new UpsertAccessListCommand(before with
        {
            BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "a new password"), new AccessListUserConfig("bob", Password: "bob password")]),
        })));

        Assert.Equal(PasswordVerdict.Verified, Check(alice, "alice", Secret));
        Assert.Equal(PasswordVerdict.Rejected, Check(alice, "alice", "a new password"));
        Assert.Equal([alice], await _h.Passwords.ListAsync(Ct));
        Assert.Equal(1, await _h.CountAsync("ConfigRevisions"));
    }

    [Fact]
    public async Task ARestoreThatFailsIsAuditedByItselfNamingTheUserAndNeverThePassword()
    {
        var loggers = new CapturingLoggerFactory();
        var passwords = new TestPasswords { RevertFailure = new InvalidOperationException("The vault is gone.") };
        await using var h = await ConfigServiceHarness.CreateAsync(passwords: passwords, loggers: loggers);
        var id = AccessListId.New();
        await h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        await h.Db.ExecuteAsync(FailRevisions);

        await Assert.ThrowsAnyAsync<Exception>(async () => await h.RunAsync(new UpsertAccessListCommand(
            Assert.Single((await h.CurrentAsync()).AccessLists) with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "a new password")]) })));

        var audit = await h.WaitForAuditAsync(e => e.Action == "config.access_list.password.restore_failed");
        Assert.Equal(AuditOutcome.Failed, audit.Outcome);
        Assert.Equal(id.ToString(), audit.Target);
        Assert.Contains("alice", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("a new password", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, audit.Details, StringComparison.Ordinal);
        var logged = Assert.Single(loggers.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("putting them back failed", StringComparison.Ordinal));
        Assert.Contains("alice", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a new password", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADraftsPasswordsAreSimulatedAsApplyingItWouldAndNothingReachesTheVaultOrItsCache()
    {
        var passwords = new TestPasswords();
        var runtime = new AccessListRuntime(TimeProvider.System, passwords.Verifier);
        await using var h = await ConfigServiceHarness.CreateAsync(
            compiler: new ConfigCompiler(new SnapshotCompilerOptions { AccessLists = runtime }, passwords), passwords: passwords);
        var id = AccessListId.New();
        await h.RunAsync(new UpsertAccessListCommand(List(id, "office", ("alice", Secret))));
        var host = Objects.RespondHost(HostId.New(), "app.example.com") with { Settings = new HostSettings { AccessList = new AccessListSettings { AccessListId = id } } };
        Assert.Equal(ConfigCommandStatus.Committed, (await h.RunAsync(new UpsertHostCommand(host))).Status);
        var list = Assert.Single((await h.CurrentAsync()).AccessLists);

        var draft = await h.Service.Drafts.PlanDraftAsync(new UpsertAccessListCommand(list with
        {
            BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice"), new AccessListUserConfig("bob", Password: "bob password")]),
        }), ConfigServiceHarness.Admin, Ct);

        Assert.Equal(ConfigCommandStatus.DryRun, draft.Result.Status);
        Assert.DoesNotContain(draft.Result.Warnings, w => w.Code == ConfigWarningCodes.AccessListUserNoPassword);
        Assert.Equal("ok", Credentials(draft.Candidate!, "bob", "bob password"));
        Assert.Equal("wrongPassword", Credentials(draft.Candidate!, "bob", "not bob's"));
        Assert.Equal("ok", Credentials(draft.Candidate!, "alice", Secret));

        // Nothing was stored, set or cached for the draft...
        Assert.Single(await passwords.ListAsync(Ct));
        Assert.Equal(0, passwords.Registry.CachedCount());
        Assert.Equal(1, await h.CountAsync("ConfigRevisions") - 1);

        // ...and applying it for real gives the verdicts the simulation gave.
        var applied = await h.RunAsync(new UpsertAccessListCommand(list with
        {
            BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice"), new AccessListUserConfig("bob", Password: "bob password")]),
        }));
        Assert.Equal(ConfigCommandStatus.Committed, applied.Status);
        var live = await h.Service.Drafts.CompileForSimulationAsync(await h.CurrentAsync(), ConfigRevisionId.Empty, Ct);
        Assert.Equal("ok", Credentials(live.Compilation.Snapshot!, "bob", "bob password"));
        Assert.Equal("wrongPassword", Credentials(live.Compilation.Snapshot!, "bob", "not bob's"));
    }

    private static string Credentials(Paduraksa.Proxy.Snapshot.RuntimeSnapshot snapshot, string user, string password)
    {
        var run = GateSimulator.Run(snapshot, new GateProbe
        {
            Host = "app.example.com",
            Path = "/",
            ClientAddress = IPAddress.Parse("10.1.1.1"),
            Headers = [new ProbeHeader("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")))],
        });
        var step = Assert.Single(run.Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "accessList" }));
        return Assert.Single(step.Facts, f => f.Name == "credentials").Value;
    }

    [Fact]
    public async Task AHeadThatStillHasInlineHashesIsNotImportedByADryRunOnlyByACommandThatCommits()
    {
        var hash = TestPasswords.Hash(Secret);
        await SeedOldPasswordRevisionAsync("alice", hash);

        var dry = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")) { DryRun = true });
        Assert.Equal(ConfigCommandStatus.DryRun, dry.Status);
        Assert.Empty(await _h.Passwords.ListAsync(Ct));

        var real = await _h.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "a.example.com")));
        Assert.Equal(ConfigCommandStatus.Committed, real.Status);
        var user = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users);
        Assert.Equal(PasswordVerdict.Verified, Check(user.CredentialId, "alice", Secret));
    }

    [Fact]
    public async Task TheStartupRevisionSaysWhatItConvertedForEachKindAndForBoth()
    {
        var hash = TestPasswords.Hash(Secret);
        await SeedOldPasswordRevisionAsync("alice", hash);
        await _h.Service.StartAsync(Ct);
        Assert.Equal("Moved 1 access-list password into the vault", (await _h.RevisionsAsync())[0].Message);

        await using var both = await ConfigServiceHarness.CreateAsync();
        await SeedBothAsync(both, hash);
        await both.Service.StartAsync(Ct);
        var message = (await both.RevisionsAsync())[0].Message!;
        Assert.StartsWith("Converted the inline IP allow lists to access lists: ", message, StringComparison.Ordinal);
        Assert.EndsWith("; moved 1 access-list password into the vault", message, StringComparison.Ordinal);
    }

    private static async Task SeedBothAsync(ConfigServiceHarness h, string hash)
    {
        var modern = new ConfigDocument(
            AccessLists: [new AccessListConfig(AccessListId.New(), "office", Version: 1, BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", PasswordCredentialId.New())]))],
            Hosts: [new HostConfig(HostId.New(), ["app.example.com"], Kind: HostKind.Respond, Respond: new RespondConfig(200, "ok"), Version: 1, Settings: new HostSettings { ForceHttps = true })]);
        var json = System.Text.RegularExpressions.Regex.Replace(
                Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(modern)), "\"credentialId\":\"[^\"]+\"", _ => $"\"passwordHash\":\"{hash}\"")
            .Replace("\"settings\":{\"forceHttps\":true}", "\"settings\":{\"forceHttps\":true,\"ipAllowList\":{\"addresses\":[\"10.0.0.0/8\"]}}", StringComparison.Ordinal);
        Assert.Contains("ipAllowList", json, StringComparison.Ordinal);
        await using var context = h.Db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        await new ConfigRevisionStore(TimeProvider.System).AppendAsync(
            context, new NewRevision(Encoding.UTF8.GetBytes(json), "[]", Actor.Cli, Channel.Cli, "an old revision"), Ct);
        await transaction.CommitAsync(Ct);
    }

    [Fact]
    public async Task InlinePasswordHashesAreImportedIntoTheVaultAndTheConvertedDocumentIsStoredOnceAtStartup()
    {
        var hash = TestPasswords.Hash(Secret);
        var (list, first) = await SeedOldPasswordRevisionAsync("alice", hash);

        var status = await _h.Service.StartAsync(Ct);

        Assert.Equal(2, status.Revision!.Seq);
        var revisions = await _h.RevisionsAsync();
        Assert.Equal(ActorKind.System, revisions[0].Actor.Kind);
        var user = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users);
        Assert.Equal(PasswordVerdict.Verified, Check(user.CredentialId, "alice", Secret));
        Assert.Equal(PasswordVerdict.Rejected, Check(user.CredentialId, "alice", "wrong"));
        await using (var context = _h.Db.NewContext())
        {
            var text = Encoding.UTF8.GetString((await ConfigRevisionStore.GetAsync(context, revisions[0].Id, Ct))!.Document.ToArray());
            Assert.DoesNotContain("passwordHash", text, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
        }

        // The old revision is untouched, and the next start finds nothing to convert (the same credential, the same bytes).
        using Paduraksa.Proxy.Snapshot.SnapshotStore store = new();
        await using Paduraksa.Proxy.Yarp.SnapshotPublisher publisher = new(store);
        using var restarted = TestConfigServices.Create(_h.Db, publisher, passwords: _h.Passwords);
        await restarted.StartAsync(Ct);
        Assert.Equal(2, (await _h.RevisionsAsync()).Count);
        Assert.Equal([user.CredentialId!.Value], await _h.Passwords.ListAsync(Ct));
        Assert.Equal(list, Assert.Single((await _h.CurrentAsync()).AccessLists).Id);
        Assert.NotEqual(ConfigRevisionId.Empty, first);
    }

    [Fact]
    public async Task ConvertingAnOldDocumentTwiceGivesTheSameCredential()
    {
        var hash = TestPasswords.Hash(Secret);
        await SeedOldPasswordRevisionAsync("alice", hash);

        var once = CanonicalConfigJson.Deserialize(await OldDocumentAsync());
        var twice = CanonicalConfigJson.Deserialize(await OldDocumentAsync());

        Assert.Equal(once.AccessLists[0].BasicAuth!.Users[0].CredentialId, twice.AccessLists[0].BasicAuth!.Users[0].CredentialId);
        Assert.NotNull(once.AccessLists[0].BasicAuth!.Users[0].CredentialId);
    }

    [Fact]
    public async Task ARevisionWithInlineHashesCanStillBeRolledBackToAndItsPasswordsComeBack()
    {
        var hash = TestPasswords.Hash(Secret);
        var (_, first) = await SeedOldPasswordRevisionAsync("alice", hash);
        await _h.Service.StartAsync(Ct);
        var list = Assert.Single((await _h.CurrentAsync()).AccessLists);
        await _h.RunAsync(new UpsertAccessListCommand(list with { BasicAuth = null }));
        Assert.Empty(await _h.Passwords.ListAsync(Ct));

        var head = (await _h.Service.GetCurrentAsync(Ct)).Revision!.Id;
        var result = await _h.RunAsync(new RollbackConfigCommand(first, head));

        // The old revision still holds the verifier, so the rollback imports it: the user is back with the password they had then.
        Assert.Equal(ConfigCommandStatus.Committed, result.Status);
        var user = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users);
        Assert.Equal(PasswordVerdict.Verified, Check(user.CredentialId, "alice", Secret));
    }

    [Fact]
    public async Task StartupDeletesCredentialsTheLiveConfigDoesNotNameButOnlyWithARevisionToJudgeBy()
    {
        var stray = PasswordCredentialId.New();
        await _h.Passwords.SetAsync(stray, "left behind".AsMemory(), Ct);

        // No revision yet: nothing says what is stray, so nothing is deleted.
        await _h.Service.StartAsync(Ct);
        Assert.Equal([stray], await _h.Passwords.ListAsync(Ct));

        using Paduraksa.Proxy.Snapshot.SnapshotStore store = new();
        await using Paduraksa.Proxy.Yarp.SnapshotPublisher publisher = new(store);
        await _h.RunAsync(new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", Secret))));
        using var restarted = TestConfigServices.Create(_h.Db, publisher, passwords: _h.Passwords);
        await restarted.StartAsync(Ct);

        var kept = Assert.Single(Assert.Single((await _h.CurrentAsync()).AccessLists).BasicAuth!.Users).CredentialId!.Value;
        Assert.Equal([kept], await _h.Passwords.ListAsync(Ct));
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

    /// <summary>Stores a revision as the build before the vault wrote it: an access list whose user carries a <c>passwordHash</c>.</summary>
    private async Task<(AccessListId List, ConfigRevisionId Revision)> SeedOldPasswordRevisionAsync(string username, string hash)
    {
        var list = AccessListId.New();
        var modern = new ConfigDocument(AccessLists: [new AccessListConfig(list, "office", Version: 1, BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig(username, PasswordCredentialId.New())]))]);
        var json = System.Text.RegularExpressions.Regex.Replace(
            Encoding.UTF8.GetString(CanonicalConfigJson.Serialize(modern)), "\"credentialId\":\"[^\"]+\"", _ => $"\"passwordHash\":\"{hash}\"");
        Assert.Contains("passwordHash", json, StringComparison.Ordinal);
        await using var context = _h.Db.NewContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var info = await new ConfigRevisionStore(TimeProvider.System).AppendAsync(
            context, new NewRevision(Encoding.UTF8.GetBytes(json), "[]", Actor.Cli, Channel.Cli, "an old revision"), Ct);
        await transaction.CommitAsync(Ct);
        _oldDocument = Encoding.UTF8.GetBytes(json);
        return (list, info.Id);
    }

    private byte[]? _oldDocument;

    private Task<byte[]> OldDocumentAsync() => Task.FromResult(_oldDocument ?? throw new InvalidOperationException("Seed first."));
}
