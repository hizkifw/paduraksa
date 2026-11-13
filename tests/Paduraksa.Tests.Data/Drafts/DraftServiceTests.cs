using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;
using Paduraksa.Data.Identity;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Commands.Objects;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Drafts;

/// <summary>
/// Drafts as a user sees them (docs/design/dashboard.md "Drafts (M10)"): one open draft each, entries checked as they are added and replaced by a later edit of the same object, the
/// head with the draft applied, a review that commits nothing, an apply that is one revision with one audit event, and an entry whose object
/// changed elsewhere blocking only itself.
/// </summary>
public sealed class DraftServiceTests : IAsyncLifetime
{
    private DraftHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await DraftHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task EachUserHasOneOpenDraftOfTheirOwn()
    {
        var first = await _h.OpenAsync();
        var again = await _h.Drafts.CreateAsync(_h.Alice, Ct);
        var other = await _h.OpenAsync(_h.Bob);

        Assert.Equal(DraftStatus.AlreadyOpen, again.Status);
        Assert.Equal(first.Draft!.Id, again.Draft!.Id);
        Assert.NotEqual(first.Draft.Id, other.Draft!.Id);
        Assert.Equal(first.Draft.Id, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Id);
        Assert.Equal(other.Draft.Id, (await _h.Drafts.GetAsync(_h.Bob, Ct)).Draft!.Id);
        Assert.Equal(DraftStatus.NoDraft, (await _h.Drafts.DiscardAsync(_h.Alice, other.Draft.Id, Ct)).Status);
    }

    [Fact]
    public async Task AnEntryIsCheckedWhenItIsAddedAndNothingIsCommittedOrAudited()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        var upstream = Upstream(UpstreamId.New(), "app");

        var added = await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        var refused = await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream with { Id = UpstreamId.New(), Name = string.Empty }));

        Assert.Equal(DraftStatus.Succeeded, added.Status);
        var entry = Assert.Single(added.Draft!.Entries);
        Assert.Equal((1, DraftEntryAction.Create, $"upstreams/{upstream.Id}"), (entry.Number, entry.Action, entry.Target));
        Assert.Equal(DraftStatus.Refused, refused.Status);
        Assert.Equal(ConfigCommandStatus.Invalid, refused.Command!.Status);
        Assert.NotEmpty(refused.Command.Errors);
        Assert.Equal(0, refused.FailedCommand);
        Assert.Single((await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Entries);
        Assert.Empty(await _h.Config.RevisionsAsync());
        Assert.Empty(await _h.Config.AuditAsync());
    }

    [Fact]
    public async Task AnEditOfTheSameObjectReplacesItsEntryAndKeepsItsPlace()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));

        var edited = await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream with { Name = "app v2" }));

        Assert.Equal(DraftStatus.Succeeded, edited.Status);
        Assert.Equal([1, 2], edited.Draft!.Entries.Select(e => e.Number));
        Assert.Equal("app v2", ((UpsertUpstreamCommand)edited.Draft.Entries[0].Command).Upstream.Name);
        Assert.Equal(DraftEntryAction.Create, edited.Draft.Entries[0].Action);
    }

    [Fact]
    public async Task DeletingAnObjectTheDraftCreatesRemovesItsEntryAndAnEditBackToTheHeadDoes()
    {
        var kept = Upstream(UpstreamId.New(), "kept");
        Assert.Equal(ConfigCommandStatus.Committed, (await _h.Config.RunAsync(new UpsertUpstreamCommand(kept))).Status);
        var created = Upstream(UpstreamId.New(), "new");
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(created));
        var live = (await _h.Config.CurrentAsync()).Upstreams.Single();
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(live with { Name = "renamed" }));
        Assert.Equal(2, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Entries.Count);

        await _h.AddAsync(draft.Id, new DeleteUpstreamCommand(created.Id, 0));
        var back = await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(live));

        Assert.Empty(back.Draft!.Entries);
    }

    [Fact]
    public async Task TheHeadWithTheDraftAppliedShowsWhatTheDraftAddsAndHostsCanUseIt()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;

        // The host uses an upstream only the draft has: its entry is checked against the head with the draft applied.
        Assert.Equal(DraftStatus.Refused, (await _h.AddAsync(draft.Id, new UpsertHostCommand(host))).Status);
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        Assert.Equal(DraftStatus.Succeeded, (await _h.AddAsync(draft.Id, new UpsertHostCommand(host))).Status);

        var working = await _h.Drafts.GetConfigAsync(_h.Alice, cancellationToken: Ct);
        Assert.Equal([upstream.Id], working!.Document.Upstreams.Select(u => u.Id));
        Assert.Equal([host.Id], working.Document.Hosts.Select(h => h.Id));
        Assert.Empty((await _h.Config.CurrentAsync()).Hosts);
        Assert.Null(await _h.Drafts.GetConfigAsync(_h.Bob, cancellationToken: Ct));
        Assert.Null(await _h.Drafts.GetConfigAsync(_h.Bob, draft.Id, Ct));
    }

    [Fact]
    public async Task AReviewIsADryRunOverTheWholeSetAndCommitsNothing()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));

        var review = await _h.Drafts.ReviewAsync(_h.Alice, draft.Id, cancellationToken: Ct);

        Assert.Equal(DraftStatus.Succeeded, review.Status);
        Assert.Equal(ConfigCommandStatus.DryRun, review.Command!.Status);
        Assert.Contains(review.Command.Changes, c => c is UpstreamChange { Kind: ConfigChangeKind.Added });
        Assert.Contains(review.Command.Changes, c => c is HostChange { Kind: ConfigChangeKind.Added });
        Assert.Empty(await _h.Config.RevisionsAsync());
        Assert.Empty(await _h.Config.AuditAsync());
        Assert.Equal(2, review.Draft!.Entries.Count);
    }

    [Fact]
    public async Task ApplyCommitsOneRevisionAuditedOnceWithEveryEntryAndTheDraftIsGone()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));

        var applied = await _h.ApplyAsync(draft.Id);

        Assert.Equal(DraftStatus.Succeeded, applied.Status);
        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        Assert.Null(applied.Draft);
        var revision = Assert.Single(await _h.Config.RevisionsAsync());
        Assert.Equal(applied.Command.Revision!.Id, revision.Id);
        Assert.Equal("Draft (2 changes): add upstream app, add host app.example.com", revision.Message);
        var current = await _h.Config.CurrentAsync();
        Assert.Equal([upstream.Id], current.Upstreams.Select(u => u.Id));
        Assert.Equal([host.Id], current.Hosts.Select(h => h.Id));
        var audit = Assert.Single(await _h.Config.AuditAsync());
        Assert.Equal("config.draft.apply", audit.Action);
        Assert.Equal(draft.Id.ToString(), audit.Target);
        Assert.Contains("config.upstream.create", audit.Details, StringComparison.Ordinal);
        Assert.Contains("config.host.create", audit.Details, StringComparison.Ordinal);
        Assert.Contains(upstream.Id.ToString(), audit.Details, StringComparison.Ordinal);
        Assert.Equal(DraftStatus.NoDraft, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Status);
        Assert.Equal(0, await _h.RowsAsync("ConfigDrafts"));
        await _h.Config.AssertChainIntactAsync();
    }

    [Fact]
    public async Task ApplyTakesAnEditedNoteAndUndoingTheRevisionUndoesTheWholeDraft()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));

        var applied = await _h.ApplyAsync(draft.Id, "Go live");

        Assert.Equal("Go live", (await _h.Config.RevisionsAsync()).Single().Message);
        // What the dashboard's Undo does for revision #1: the empty config, based on the revision it undoes.
        var undo = await _h.Config.RunAsync(new ReplaceConfigCommand(new ConfigDocument(), applied.Command!.Revision!.Id));
        Assert.Equal(ConfigCommandStatus.Committed, undo.Status);
        var current = await _h.Config.CurrentAsync();
        Assert.Empty(current.Hosts);
        Assert.Empty(current.Upstreams);
    }

    [Fact]
    public async Task AnApplyOfAReviewThatNoLongerMatchesCommitsNothingAndReturnsTheFreshReview()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "one")));
        var reviewed = await _h.TokenAsync(draft.Id);
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "two")));

        var refused = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, reviewed, cancellationToken: Ct);

        Assert.Equal(DraftStatus.ChangedSinceReview, refused.Status);
        Assert.Empty(await _h.Config.RevisionsAsync());
        Assert.Empty(await _h.Config.AuditAsync());
        Assert.Equal(2, refused.Draft!.Entries.Count);
        Assert.Equal(2, refused.Command!.Changes.OfType<UpstreamChange>().Count());
        Assert.NotNull(refused.ReviewToken);
        Assert.NotEqual(reviewed, refused.ReviewToken);

        // The fresh review is what a deliberate second apply names.
        var applied = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, refused.ReviewToken, cancellationToken: Ct);
        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        Assert.Single(await _h.Config.RevisionsAsync());
    }

    [Fact]
    public async Task AnApplyWithNoTokenOrAnInventedOneCommitsNothing()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "one")));

        var none = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, null, cancellationToken: Ct);
        var invented = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, "2.AAAAAAAAAAAAAAAAAAAAAA", cancellationToken: Ct);

        Assert.All([none, invented], r => Assert.Equal(DraftStatus.ChangedSinceReview, r.Status));
        Assert.Empty(await _h.Config.RevisionsAsync());
    }

    [Fact]
    public async Task ACommitThatDoesNotTouchWhatTheDraftDoesLeavesTheReviewApplicable()
    {
        var host = RespondHost(HostId.New(), "mine.example.com");
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));
        var token = await _h.TokenAsync(draft.Id);

        // Others commit around it, including a host whose ID sorts before the draft's (it shifts every position in the document).
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(ConfigCommandStatus.Committed, (await _h.Config.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), $"other{i}.example.com")), _h.Bob)).Status);
        }

        var applied = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, token, cancellationToken: Ct);

        Assert.Equal(DraftStatus.Succeeded, applied.Status);
        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        Assert.Equal(4, (await _h.Config.CurrentAsync()).Hosts.Count);
    }

    [Fact]
    public async Task AChangeToAnObjectTheDraftRefersToMakesTheReviewStale()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        await _h.Config.RunAsync(new UpsertUpstreamCommand(upstream));
        var live = (await _h.Config.CurrentAsync()).Upstreams.Single();
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertHostCommand(ProxyHost(HostId.New(), "app.example.com", upstream.Id)));
        var token = await _h.TokenAsync(draft.Id);

        // The draft doesn't write the upstream, so its entry is not blocked; but the person confirmed the host against the upstream as it was.
        await _h.Config.RunAsync(new UpsertUpstreamCommand(live with { Name = "app, moved" }), _h.Bob);

        var refused = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, token, cancellationToken: Ct);
        Assert.Equal(DraftStatus.ChangedSinceReview, refused.Status);
        Assert.Single(await _h.Config.RevisionsAsync(), r => r.Seq == 2);
    }

    [Fact]
    public async Task ADraftWithABlockedEntryIsRefusedWholeAndNeverAppliedAroundIt()
    {
        var kept = Upstream(UpstreamId.New(), "kept");
        var other = Upstream(UpstreamId.New(), "other");
        await _h.Config.RunAsync(new UpsertUpstreamCommand(kept));
        await _h.Config.RunAsync(new UpsertUpstreamCommand(other));
        var current = await _h.Config.CurrentAsync();
        var liveKept = current.Upstreams.Single(u => u.Id == kept.Id);
        var liveOther = current.Upstreams.Single(u => u.Id == other.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(liveKept with { Name = "mine" }));
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(liveOther with { Name = "also mine" }));
        var token = await _h.TokenAsync(draft.Id);

        // Bob changes one of them while Alice's draft is open.
        Assert.Equal(ConfigCommandStatus.Committed, (await _h.Config.RunAsync(new UpsertUpstreamCommand(liveKept with { Name = "theirs" }), _h.Bob)).Status);

        var view = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;
        Assert.Equal([DraftEntryState.ChangedElsewhere, DraftEntryState.Ready], view.Entries.Select(e => e.State));
        Assert.False(string.IsNullOrWhiteSpace(view.Entries[0].Reason));
        var refused = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, token, cancellationToken: Ct);

        Assert.Equal(DraftStatus.Blocked, refused.Status);
        var after = await _h.Config.CurrentAsync();
        Assert.Equal("theirs", after.Upstreams.Single(u => u.Id == kept.Id).Name);
        Assert.Equal("other", after.Upstreams.Single(u => u.Id == other.Id).Name);
        Assert.Equal(3, (await _h.Config.RevisionsAsync()).Count);
        Assert.Equal(2, refused.Draft!.Entries.Count);
        Assert.DoesNotContain(await _h.Config.AuditAsync(), a => a.Action == "config.draft.apply");

        // A review of it still shows the rest, without a token; reloading the entry (taking it out) makes it applicable again.
        var review = await _h.Drafts.ReviewAsync(_h.Alice, draft.Id, cancellationToken: Ct);
        Assert.Equal(DraftStatus.Succeeded, review.Status);
        Assert.Null(review.ReviewToken);
        await _h.Drafts.RemoveEntryAsync(_h.Alice, draft.Id, 1, Ct);
        var applied = await _h.ApplyAsync(draft.Id);
        Assert.Equal(ConfigCommandStatus.Committed, applied.Command!.Status);
        Assert.Equal("also mine", (await _h.Config.CurrentAsync()).Upstreams.Single(u => u.Id == other.Id).Name);
    }

    [Fact]
    public async Task AnEntryThatDependsOnABlockedOneIsBlockedWithAReasonNamingIt()
    {
        // The upstream is created under an ID the draft chose, and then somebody else takes that ID.
        var upstream = Upstream(UpstreamId.New(), "mine");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));
        var token = await _h.TokenAsync(draft.Id);
        Assert.Equal(ConfigCommandStatus.Committed, (await _h.Config.RunAsync(new UpsertUpstreamCommand(upstream with { Name = "theirs" }), _h.Bob)).Status);

        var view = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;

        Assert.All(view.Entries, e => Assert.Equal(DraftEntryState.ChangedElsewhere, e.State));
        Assert.Contains("add upstream mine", view.Entries[1].Reason, StringComparison.Ordinal);
        Assert.Equal(DraftStatus.Blocked, (await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, token, cancellationToken: Ct)).Status);
        Assert.Empty((await _h.Config.CurrentAsync()).Hosts);
    }

    [Fact]
    public async Task ABlockedDeleteLeavesAReferenceTheReviewOfTheRestRefusesAsInvalid()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        await _h.Config.RunAsync(new UpsertUpstreamCommand(upstream));
        await _h.Config.RunAsync(new UpsertHostCommand(host));
        var current = await _h.Config.CurrentAsync();
        var liveUpstream = current.Upstreams.Single();
        var liveHost = current.Hosts.Single();
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new DeleteHostCommand(host.Id, liveHost.Version));
        await _h.AddAsync(draft.Id, new DeleteUpstreamCommand(upstream.Id, liveUpstream.Version));

        // The host's deletion is blocked (changed elsewhere), the upstream's is not: the rest would leave a host pointing at nothing.
        await _h.Config.RunAsync(new UpsertHostCommand(liveHost with { Enabled = false }), _h.Bob);

        var review = await _h.Drafts.ReviewAsync(_h.Alice, draft.Id, cancellationToken: Ct);
        Assert.Equal(DraftStatus.Refused, review.Status);
        Assert.Equal(ConfigCommandStatus.Invalid, review.Command!.Status);
        Assert.Equal(DraftStatus.Blocked, (await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, "x", cancellationToken: Ct)).Status);
        Assert.Single((await _h.Config.CurrentAsync()).Upstreams);
    }

    [Fact]
    public async Task AnObjectDeletedElsewhereBlocksItsEntryToo()
    {
        var kept = Upstream(UpstreamId.New(), "kept");
        await _h.Config.RunAsync(new UpsertUpstreamCommand(kept));
        var live = (await _h.Config.CurrentAsync()).Upstreams.Single();
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(live with { Name = "mine" }));

        await _h.Config.RunAsync(new DeleteUpstreamCommand(kept.Id, live.Version), _h.Bob);

        Assert.Equal(DraftEntryState.ChangedElsewhere, Assert.Single((await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Entries).State);
    }

    [Fact]
    public async Task ASetThatIsInvalidAsAWholeIsRefusedAtApplyWithTheCompilersAndValidatorsErrors()
    {
        var upstream = Upstream(UpstreamId.New(), "app");
        var host = ProxyHost(HostId.New(), "app.example.com", upstream.Id);
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(upstream));
        await _h.AddAsync(draft.Id, new UpsertHostCommand(host));

        // Every entry still applies on its own (no object it edits moved), but Bob now serves the name the draft's host takes: the set is invalid.
        Assert.Equal(ConfigCommandStatus.Committed, (await _h.Config.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "app.example.com")), _h.Bob)).Status);

        var applied = await _h.Drafts.ApplyAsync(_h.Alice, draft.Id, "any token", cancellationToken: Ct);

        Assert.Equal(DraftStatus.Refused, applied.Status);
        Assert.Equal(ConfigCommandStatus.Invalid, applied.Command!.Status);
        Assert.Equal(2, applied.Draft!.Entries.Count);
        Assert.Single(await _h.Config.RevisionsAsync());
    }

    [Fact]
    public async Task ADraftSurvivesARestartOfTheProcess()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));

        var restarted = _h.Restarted();

        var seen = (await restarted.GetAsync(_h.Alice, Ct)).Draft!;
        Assert.Equal(draft.Id, seen.Id);
        Assert.Single(seen.Entries);
    }

    [Fact]
    public async Task ADraftNobodyAnsweredFor30DaysIsDroppedAndItsOwnerIsToldOnce()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));

        _h.Time.Now += TimeSpan.FromDays(29);
        Assert.Equal(0, await _h.Drafts.DropExpiredAsync(Ct));
        Assert.Equal(DraftStatus.Succeeded, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Status);
        _h.Time.Now += TimeSpan.FromDays(1);
        Assert.Equal(DraftStatus.NoDraft, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Status);

        var notices = await _h.Drafts.TakeNoticesAsync(_h.Alice, Ct);
        var notice = Assert.Single(notices);
        Assert.Equal((draft.Id, 1), (notice.Id, notice.Entries));
        Assert.Empty(await _h.Drafts.TakeNoticesAsync(_h.Alice, Ct));
        Assert.Empty(await _h.Drafts.TakeNoticesAsync(_h.Bob, Ct));
        Assert.Equal(0, await _h.RowsAsync("ConfigDrafts"));
    }

    [Fact]
    public async Task EveryChangeToADraftRestartsItsThirtyDays()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        _h.Time.Now += TimeSpan.FromDays(25);
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));
        _h.Time.Now += TimeSpan.FromDays(25);

        Assert.Equal(0, await _h.Drafts.DropExpiredAsync(Ct));
        Assert.Equal(_h.Time.Now + TimeSpan.FromDays(5), (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.ExpiresAt);
    }

    [Fact]
    public async Task OnlyAUserWhoMayChangeConfigHasADraft()
    {
        var viewer = _h.Restarted();
        DraftService denied = new(_h.Config.Db.Factory, _h.Config.Service, _h.Config.Passwords, TestPermissions.Holding(Permission.ConfigRead), _h.Time, NullLoggerFactory.Instance);

        Assert.Equal(DraftStatus.Denied, (await denied.CreateAsync(_h.Alice, Ct)).Status);
        Assert.Equal(DraftStatus.Denied, (await denied.GetAsync(_h.Alice, Ct)).Status);
        Assert.Equal(DraftStatus.Succeeded, (await viewer.CreateAsync(_h.Alice, Ct)).Status);
        Assert.Equal(DraftStatus.Denied, (await viewer.CreateAsync(CommandOrigin.Cli, Ct)).Status);
    }

    [Fact]
    public async Task ATokenActsOnItsOwnersDraft()
    {
        var token = ApiTokenId.New();
        await using (var context = _h.Config.Db.NewContext())
        {
            context.ApiTokens.Add(new ApiTokenRow
            {
                Id = token,
                UserId = _h.Alice.Actor.UserId!.Value,
                Name = "agent",
                Scopes = "config:write",
                SecretHash = new byte[32],
                CreatedAt = _h.Time.GetUtcNow(),
            });
            await context.SaveChangesAsync(Ct);
        }

        var viaToken = new CommandOrigin(Actor.ForToken(token), Channel.Api);
        var draft = (await _h.Drafts.CreateAsync(viaToken, Ct)).Draft!;

        Assert.Equal(draft.Id, (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!.Id);
        Assert.Equal(DraftStatus.Succeeded, (await _h.Drafts.AddAsync(viaToken, draft.Id, [new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "agent's"))], Ct)).Status);
    }

    [Fact]
    public async Task AWholeConfigCommandIsNotAnEntry()
    {
        var draft = (await _h.OpenAsync()).Draft!;

        var refused = await _h.AddAsync(draft.Id, new ReplaceConfigCommand(new ConfigDocument()));

        Assert.Equal(DraftStatus.Refused, refused.Status);
        Assert.Equal("draft.entry.command", Assert.Single(refused.Command!.Errors).Code);
    }

    [Fact]
    public async Task ReadingTheSameDraftTwiceReplaysItOnceAndACommitOrAnEditReplaysItAgain()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));

        var first = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;
        var second = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;
        var config = await _h.Drafts.GetConfigAsync(_h.Alice, draft.Id, Ct);
        Assert.Same(first, second);
        Assert.Same(first, config!.Draft);

        await _h.Config.RunAsync(new UpsertHostCommand(RespondHost(HostId.New(), "x.example.com")), _h.Bob);
        var afterCommit = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "more")));
        var afterEdit = (await _h.Drafts.GetAsync(_h.Alice, Ct)).Draft!;

        Assert.NotSame(first, afterCommit);
        Assert.NotSame(afterCommit, afterEdit);
        Assert.Equal(2, afterEdit.Entries.Count);
    }

    [Fact]
    public async Task AnExpiredDraftItsOwnerRefreshedBeforeTheDropWasDecidedIsNotDropped()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));
        RacingClock racing = new(_h.Time.GetUtcNow());
        var reader = new DraftService(_h.Config.Db.Factory, _h.Config.Service, _h.Config.Passwords, TestPermissions.AllowAll, racing, NullLoggerFactory.Instance);

        // The first time the reader asks, the draft looks 30 days old; by the time it re-checks under the writer's lock it has been touched.
        var seen = await reader.GetAsync(_h.Alice, Ct);

        Assert.Equal(DraftStatus.Succeeded, seen.Status);
        Assert.Single(seen.Draft!.Entries);
        Assert.Equal(1, await _h.RowsAsync("ConfigDrafts"));
        Assert.Empty(await reader.TakeNoticesAsync(_h.Alice, Ct));
    }

    [Fact]
    public async Task ARowMadeBeforeTheStateStoppedBeingStoredStillReadsAndNewRowsCarryNone()
    {
        var draft = (await _h.OpenAsync()).Draft!;
        await _h.AddAsync(draft.Id, new UpsertUpstreamCommand(Upstream(UpstreamId.New(), "app")));
        var stored = Convert.ToString(await _h.Config.Db.ScalarAsync("SELECT Entries FROM ConfigDrafts"))!;
        Assert.DoesNotContain("\"state\"", stored, StringComparison.Ordinal);

        var legacy = stored.Replace("\"addedAt\"", "\"state\":\"ready\",\"addedAt\"", StringComparison.Ordinal);
        Assert.Contains("\"state\":\"ready\"", legacy, StringComparison.Ordinal);
        await using (var context = _h.Config.Db.NewContext())
        {
            await context.Database.ExecuteSqlRawAsync("UPDATE ConfigDrafts SET Entries = {0}", legacy);
        }

        var read = (await _h.Restarted().GetAsync(_h.Alice, Ct)).Draft!;

        Assert.Equal(DraftEntryState.Ready, Assert.Single(read.Entries).State);
    }

    private sealed class RacingClock(DateTimeOffset now) : TimeProvider
    {
        private int _calls;

        // 30 days on for the first look, as if the draft had not been touched; the time it really is for every look after.
        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _calls) == 1 ? now + DraftService.Lifetime : now;
    }
}
