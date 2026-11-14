using System.Text;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Drafts;

/// <summary>
/// A password typed into a drafted access list is a vault value no config names yet, bound to the draft: never in the draft's row, its output, the
/// audit log or a revision; live as a credential only once the draft is applied; and deleted with its entry, its draft or its expiry.
/// </summary>
public sealed class DraftSecretsTests : IAsyncLifetime
{
    private const string Secret = "correct horse battery staple";

    private static readonly byte[] Client = new byte[16];

    private DraftHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await DraftHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private static AccessListConfig List(AccessListId id, string name, params (string Name, string? Password)[] users) =>
        new(id, name, Ip: new AccessListIp(["10.0.0.0/8"]), BasicAuth: new AccessListBasicAuth(Users: [.. users.Select(u => new AccessListUserConfig(u.Name, Password: u.Password))]));

    private async Task<string> RowAsync() =>
        string.Join('\n', (await _h.Config.Db.QueryAsync("SELECT Entries, PendingCredentials FROM ConfigDrafts")).SelectMany(r => r.Select(c => c?.ToString())));

    private PasswordVerdict Check(PasswordCredentialId id, string user, string password) =>
        _h.Config.Passwords.Verifier.Verify(_h.Config.Passwords.Open(id), Encoding.UTF8.GetBytes(user), Encoding.UTF8.GetBytes(password), Client, remember: false);

    private async Task<(DraftId Draft, AccessListId List, PasswordCredentialId Credential)> DraftAListAsync(string password = Secret)
    {
        var list = AccessListId.New();
        var draft = (await _h.OpenAsync()).Draft!;
        var added = await _h.AddAsync(draft.Id, new UpsertAccessListCommand(List(list, "office", ("alice", password))));
        Assert.Equal(DraftStatus.Succeeded, added.Status);
        var user = Assert.Single(((UpsertAccessListCommand)Assert.Single(added.Draft!.Entries).Command).AccessList.BasicAuth!.Users);
        Assert.Null(user.Password);
        return (draft.Id, list, user.CredentialId!.Value);
    }

    [Fact]
    public async Task ThePasswordIsInTheVaultAndNotInTheDraftsRowItsOutputOrAnyLog()
    {
        var (_, _, credential) = await DraftAListAsync();

        var row = await RowAsync();
        Assert.DoesNotContain(Secret, row, StringComparison.Ordinal);
        Assert.DoesNotContain("pbkdf2", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\"", row, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(credential.ToString(), row, StringComparison.Ordinal);
        Assert.Contains(credential, await _h.Config.Passwords.ListAsync(Ct));
        Assert.Equal(PasswordVerdict.Verified, Check(credential, "alice", Secret));
        Assert.All((await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Entries, e =>
            Assert.Null(((UpsertAccessListCommand)e.Command).AccessList.BasicAuth!.Users.Single().Password));
        Assert.Empty((await _h.Config.CurrentAsync()).AccessLists);
        Assert.Empty(await _h.Config.AuditAsync());
    }

    [Fact]
    public async Task ADraftedListIsApplyingItMakesTheCredentialLiveAndKeepsNoSecretInTheRevisionOrAudit()
    {
        var (draft, list, credential) = await DraftAListAsync();

        var applied = await _h.ApplyAsync(draft);

        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        var stored = Assert.Single((await _h.Config.CurrentAsync()).AccessLists);
        Assert.Equal(list, stored.Id);
        Assert.Equal(credential, stored.BasicAuth!.Users.Single().CredentialId);
        Assert.Equal(PasswordVerdict.Verified, Check(credential, "alice", Secret));
        Assert.Equal([credential], await _h.Config.Passwords.ListAsync(Ct));
        var revision = Assert.Single(await _h.Config.RevisionsAsync());
        Assert.DoesNotContain(Secret, revision.Summary, StringComparison.Ordinal);
        var audit = Assert.Single(await _h.Config.AuditAsync());
        Assert.DoesNotContain(Secret, audit.Details ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangingAnExistingUsersPasswordInADraftLeavesTheLivePasswordUntilApplyThenReplacesIt()
    {
        var list = AccessListId.New();
        await _h.Config.RunAsync(new UpsertAccessListCommand(List(list, "office", ("alice", "old password 1"))));
        var live = Assert.Single((await _h.Config.CurrentAsync()).AccessLists);
        var liveCredential = live.BasicAuth!.Users.Single().CredentialId!.Value;
        var draft = (await _h.OpenAsync()).Draft!;

        var added = await _h.AddAsync(draft.Id, new UpsertAccessListCommand(live with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: Secret)]) }));

        var pending = ((UpsertAccessListCommand)Assert.Single(added.Draft!.Entries).Command).AccessList.BasicAuth!.Users.Single().CredentialId!.Value;
        Assert.NotEqual(liveCredential, pending);
        Assert.Equal(PasswordVerdict.Verified, Check(liveCredential, "alice", "old password 1"));
        Assert.Equal(PasswordVerdict.Verified, Check(pending, "alice", Secret));

        await _h.ApplyAsync(draft.Id);

        Assert.Equal(pending, Assert.Single((await _h.Config.CurrentAsync()).AccessLists).BasicAuth!.Users.Single().CredentialId);
        Assert.Equal([pending], await _h.Config.Passwords.ListAsync(Ct));
    }

    [Fact]
    public async Task EditingTheEntryAgainDropsTheValueItNoLongerNames()
    {
        var (draft, list, first) = await DraftAListAsync();

        var again = await _h.AddAsync(draft, new UpsertAccessListCommand(List(list, "office", ("alice", "another password 2"))));

        var second = ((UpsertAccessListCommand)Assert.Single(again.Draft!.Entries).Command).AccessList.BasicAuth!.Users.Single().CredentialId!.Value;
        Assert.NotEqual(first, second);
        Assert.Equal([second], await _h.Config.Passwords.ListAsync(Ct));
    }

    [Fact]
    public async Task AFormThatSendsTheUserBackWithoutAPasswordKeepsThePendingValue()
    {
        var (draft, list, credential) = await DraftAListAsync();

        // What the dashboard's form does with a user it read from the draft: the credential ID and no password.
        var again = await _h.AddAsync(draft, new UpsertAccessListCommand(new AccessListConfig(list, "office renamed", Ip: new AccessListIp(["10.0.0.0/8"]),
            BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", CredentialId: credential)]))));

        Assert.Equal(DraftStatus.Succeeded, again.Status);
        Assert.Equal([credential], await _h.Config.Passwords.ListAsync(Ct));
        Assert.Equal(PasswordVerdict.Verified, Check(credential, "alice", Secret));
    }

    [Fact]
    public async Task RemovingTheEntryDiscardingTheDraftAndItsExpiryEachDeleteTheValue()
    {
        var (draft, _, _) = await DraftAListAsync();
        await _h.Drafts.RemoveEntryAsync(_h.Alice, draft, 1, Ct);
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));

        await AddListAsync(draft);
        Assert.Single(await _h.Config.Passwords.ListAsync(Ct));
        await _h.Drafts.DiscardAsync(_h.Alice, draft, Ct);
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));

        var fresh = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(fresh.Id, new UpsertAccessListCommand(List(AccessListId.New(), "late", ("bob", Secret))));
        Assert.Single(await _h.Config.Passwords.ListAsync(Ct));
        _h.Time.Now += DraftService.Lifetime;
        Assert.Equal(1, await _h.Drafts.DropExpiredAsync(Ct));
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
    }

    /// <summary>
    /// What the API dispatches to an MCP script as a read (the draft's reads, its review, a simulation of it) must not write: before, each of them dropped an
    /// expired draft, wrote its notice and deleted its vault values. Only the expiry job does now, and the changes to the draft are refused as expired too.
    /// </summary>
    [Fact]
    public async Task EveryCallOnAnExpiredDraftButTheJobLeavesTheDatabaseAndTheVaultAsTheyAre()
    {
        var (draft, _, credential) = await DraftAListAsync();
        _h.Time.Now += DraftService.Lifetime;
        var before = (await RowAsync(), await _h.Config.Db.ScalarAsync("SELECT Version FROM ConfigDrafts"));
        var revisions = (await _h.Config.RevisionsAsync()).Count;

        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Status);
        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.GetAsync(_h.Alice, draft, Ct)).Status);
        Assert.Null(await _h.Drafts.GetConfigAsync(_h.Alice, draft, Ct));
        Assert.Null(await _h.Drafts.GetConfigAsync(_h.Alice, null, Ct));
        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.ReadForSimulationAsync(_h.Alice, draft, Ct)).Result.Status);
        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.ReviewAsync(_h.Alice, draft, cancellationToken: Ct)).Status);
        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.ApplyAsync(_h.Alice, draft, "token", cancellationToken: Ct)).Status);
        Assert.Equal(DraftStatus.Expired, (await _h.AddAsync(draft, new UpsertAccessListCommand(List(AccessListId.New(), "late", ("bob", Secret))))).Status);
        Assert.Equal(DraftStatus.Expired, (await _h.Drafts.RemoveEntryAsync(_h.Alice, draft, 1, Ct)).Status);

        Assert.Equal(before, (await RowAsync(), await _h.Config.Db.ScalarAsync("SELECT Version FROM ConfigDrafts")));
        Assert.Equal(0, await _h.RowsAsync("ConfigDraftNotices"));
        Assert.Equal([credential], await _h.Config.Passwords.ListAsync(Ct));
        Assert.Equal(revisions, (await _h.Config.RevisionsAsync()).Count);

        Assert.Equal(1, await _h.Drafts.DropExpiredAsync(Ct));
        Assert.Equal(0, await _h.RowsAsync("ConfigDrafts"));
        Assert.Equal(1, await _h.RowsAsync("ConfigDraftNotices"));
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
    }

    [Fact]
    public async Task DiscardingAnExpiredDraftOrOpeningANewOneOverItRemovesItAndItsValues()
    {
        var (draft, _, _) = await DraftAListAsync();
        _h.Time.Now += DraftService.Lifetime;
        Assert.Equal(DraftStatus.Succeeded, (await _h.Drafts.DiscardAsync(_h.Alice, draft, Ct)).Status);
        Assert.Equal(0, await _h.RowsAsync("ConfigDrafts"));
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
        Assert.Equal(0, await _h.RowsAsync("ConfigDraftNotices"));

        var (second, _, _) = await DraftAListAsync();
        _h.Time.Now += DraftService.Lifetime;
        var opened = await _h.Drafts.CreateAsync(_h.Alice, Ct);
        Assert.Equal(DraftStatus.Succeeded, opened.Status);
        Assert.NotEqual(second, opened.Draft!.Id);
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
        Assert.Equal(second, Assert.Single(await _h.Drafts.TakeNoticesAsync(_h.Alice, Ct)).Id);
    }

    [Fact]
    public async Task TheStartupSweepKeepsWhatADraftHoldsAndDeletesTheRest()
    {
        await _h.Config.RunAsync(new UpsertUpstreamCommand(Paduraksa.Tests.Data.Commands.Objects.Upstream(UpstreamId.New(), "app")));
        var (_, _, credential) = await DraftAListAsync();
        var stray = PasswordCredentialId.New();
        await _h.Config.Passwords.SetAsync(stray, "a stray password".AsMemory(), Ct);

        await _h.Config.Service.StartAsync(Ct);

        Assert.Equal([credential], await _h.Config.Passwords.ListAsync(Ct));
    }

    [Fact]
    public async Task APasswordTheValidatorRefusesIsNotStoredAnywhere()
    {
        var draft = (await _h.OpenAsync()).Draft!;

        var refused = await _h.AddAsync(draft.Id, new UpsertAccessListCommand(List(AccessListId.New(), "office", ("alice", new string('x', 5000)))));

        Assert.Equal(DraftStatus.Refused, refused.Status);
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
        Assert.DoesNotContain("xxxxxxxx", await RowAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APasswordReplacedByALaterCommandOfTheSameAddIsNeverSetInTheVault()
    {
        var list = AccessListId.New();
        var draft = (await _h.OpenAsync()).Draft!;

        // The second command replaces the first's entry for the same list, with no password in it: the first's has no entry to belong to.
        var added = await _h.Drafts.AddAsync(_h.Alice, draft.Id,
            [new UpsertAccessListCommand(List(list, "office", ("alice", Secret))), new UpsertAccessListCommand(new AccessListConfig(list, "office", Ip: new AccessListIp(["10.0.0.0/8"])))], Ct);

        Assert.Equal(DraftStatus.Succeeded, added.Status);
        Assert.Empty(await _h.Config.Passwords.ListAsync(Ct));
        Assert.DoesNotContain("pwc_", await RowAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValueStaysPendingWhileItsEntryIsBlockedAndOnlyAnApplyOfAnEntryThatNamesItMakesItLive()
    {
        var list = AccessListId.New();
        await _h.Config.RunAsync(new UpsertAccessListCommand(List(list, "office", ("alice", "old password 1"))));
        var live = Assert.Single((await _h.Config.CurrentAsync()).AccessLists);
        var liveCredential = live.BasicAuth!.Users.Single().CredentialId!.Value;
        var draft = (await _h.OpenAsync()).Draft!;
        var added = await _h.AddAsync(draft.Id, new UpsertAccessListCommand(live with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: Secret)]) }));
        var pending = ((UpsertAccessListCommand)Assert.Single(added.Draft!.Entries).Command).AccessList.BasicAuth!.Users.Single().CredentialId!.Value;
        var token = await _h.TokenAsync(draft.Id);

        // Somebody else changes the list: the entry is blocked, and the whole apply is refused.
        await _h.Config.RunAsync(new UpsertAccessListCommand(live with { Name = "office, renamed" }), _h.Bob);
        var refused = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, token, cancellationToken: Ct);

        Assert.Equal(DraftStatus.Blocked, refused.Status);
        Assert.Equal(PasswordVerdict.Verified, Check(pending, "alice", Secret));
        Assert.Contains(pending.ToString(), await RowAsync(), StringComparison.Ordinal);
        Assert.Equal(liveCredential, Assert.Single((await _h.Config.CurrentAsync()).AccessLists).BasicAuth!.Users.Single().CredentialId);
        Assert.Equal(PasswordVerdict.Verified, Check(liveCredential, "alice", "old password 1"));

        // Reloading the entry drops the value; a new password for the changed list is then the one that goes live with its apply.
        await _h.Drafts.RemoveEntryAsync(_h.Alice, draft.Id, 1, Ct);
        Assert.DoesNotContain(pending, await _h.Config.Passwords.ListAsync(Ct));
        var current = Assert.Single((await _h.Config.CurrentAsync()).AccessLists);
        var again = await _h.AddAsync(draft.Id, new UpsertAccessListCommand(current with { BasicAuth = new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: "newer password 2")]) }));
        var second = ((UpsertAccessListCommand)Assert.Single(again.Draft!.Entries).Command).AccessList.BasicAuth!.Users.Single().CredentialId!.Value;
        Assert.Equal(ConfigCommandStatus.Committed, (await _h.ApplyAsync(draft.Id)).Command!.Status);
        Assert.Equal(second, Assert.Single((await _h.Config.CurrentAsync()).AccessLists).BasicAuth!.Users.Single().CredentialId);
        Assert.Equal([second], await _h.Config.Passwords.ListAsync(Ct));
    }

    private async Task AddListAsync(DraftId draft)
    {
        var added = await _h.AddAsync(draft, new UpsertAccessListCommand(List(AccessListId.New(), "again", ("alice", Secret))));
        Assert.Equal(DraftStatus.Succeeded, added.Status);
    }
}
