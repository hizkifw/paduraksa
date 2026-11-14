using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// <c>/api/v1/drafts</c> end to end on a real host: a draft is opened, added to, reviewed and applied as one revision with one audit event;
/// <c>If-Match</c> carries the draft's version; an entry whose object changed elsewhere blocks only itself; a draft is its owner's alone; and the
/// whole surface needs <c>config:write</c>.
/// </summary>
public sealed class DraftApiTests(SharedAdminApi shared) : IClassFixture<SharedAdminApi>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string label) => $"{label}-{UpstreamId.New().ToString()[^8..].ToLowerInvariant()}";

    /// <summary>A draft with nothing in it: the caller's old one is discarded first (a user has one).</summary>
    private static async Task<ConfigDraft> FreshAsync(AdminApiHarness api, string caller = "owner")
    {
        using var listed = await api.GetAsync("/api/v1/drafts", caller);
        foreach (var open in (await AdminApiHarness.ReadAsync(listed, AdminJsonContext.Default.PageConfigDraft, Ct)).Items)
        {
            using var discarded = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{open.Id}", caller);
            Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        }

        using var created = await api.SendAsync(HttpMethod.Post, "/api/v1/drafts", caller);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await AdminApiHarness.ReadAsync(created, AdminJsonContext.Default.ConfigDraft, Ct);
    }

    private static Task<HttpResponseMessage> AddAsync(AdminApiHarness api, DraftId draft, string caller, params ConfigCommand[] commands) =>
        api.SendJsonAsync(HttpMethod.Post, $"/api/v1/drafts/{draft}/entries", new AddDraftEntriesRequest(commands), AdminJsonContext.Default.AddDraftEntriesRequest, caller);

    private static async Task<ConfigDraft> AddedAsync(AdminApiHarness api, DraftId draft, string caller, params ConfigCommand[] commands)
    {
        using var response = await AddAsync(api, draft, caller, commands);
        return await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ConfigDraft, Ct);
    }

    private static async Task<long> HeadSeqAsync(AdminApiHarness api)
    {
        using var response = await api.GetAsync("/api/v1/config");
        return (await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.CurrentConfig, Ct)).Revision?.Seq ?? 0;
    }

    private static async Task<IReadOnlyList<AuditEventSummary>> AuditAsync(AdminApiHarness api, string query)
    {
        using var response = await api.GetAsync("/api/v1/audit?" + query);
        return (await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.PageAuditEventSummary, Ct)).Items;
    }

    [Fact]
    public async Task ADraftIsAddedToReviewedAndAppliedAsOneRevisionWithOneAuditEvent()
    {
        var api = shared.Api;
        var label = Unique("flow");
        var upstream = ConfigTestSupport.Upstream(label, "http://10.0.0.5:8080");
        var host = ConfigTestSupport.ProxyHost(label, $"{label}.test", label);
        var draft = await FreshAsync(api);
        var before = await HeadSeqAsync(api);

        var added = await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(upstream), new UpsertHostCommand(host));

        Assert.Equal(2, added.Entries.Count);
        Assert.All(added.Entries, e => Assert.Equal(DraftEntryState.Ready, e.State));
        Assert.Equal(before, await HeadSeqAsync(api));
        using (var live = await api.GetAsync($"/api/v1/hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, live.StatusCode);
        }

        using var review = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review");
        var reviewed = await AdminApiHarness.ReadAsync(review, AdminJsonContext.Default.DraftOutcome, Ct);
        Assert.Equal(ConfigCommandStatus.DryRun, reviewed.Result.Status);
        Assert.NotNull(reviewed.ReviewToken);
        Assert.Contains(reviewed.Result.Changes, c => c is HostChange { Kind: ConfigChangeKind.Added });
        Assert.Equal(before, await HeadSeqAsync(api));
        Assert.Empty(await AuditAsync(api, $"action=config.draft.*&target={draft.Id}"));

        Assert.Equal($"\"{reviewed.ReviewToken}\"", AdminApiHarness.ETag(review));
        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply?message=Go+live", ifMatch: $"\"{reviewed.ReviewToken}\"");
        var outcome = await AdminApiHarness.ReadAsync(applied, AdminJsonContext.Default.DraftOutcome, Ct);
        Assert.Equal(ConfigCommandStatus.Committed, outcome.Result.Status);
        Assert.Null(outcome.Draft);
        Assert.Equal(before + 1, await HeadSeqAsync(api));
        using (var live = await api.GetAsync($"/api/v1/hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }

        var events = await AuditAsync(api, $"action=config.draft.apply&target={draft.Id}");
        var audit = Assert.Single(events);
        Assert.Equal(outcome.Result.Revision!.Id, audit.RevisionId);
        var listed = audit.Details!.Value.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(["config.upstream.create", "config.host.create"], listed);
        Assert.Empty(await AuditAsync(api, $"action=config.host.create&target={host.Id}"));
        using var gone = await api.SendAsync(HttpMethod.Get, $"/api/v1/drafts/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AnEntryIsCheckedWhenAddedAndARefusedOneAddsNothing()
    {
        var api = shared.Api;
        var label = Unique("bad");
        var draft = await FreshAsync(api);

        using var refused = await AddAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(ConfigTestSupport.Upstream(label, "http://10.0.0.6:80") with { Name = string.Empty }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(refused, Ct);
        Assert.NotEmpty(problem.Errors!);
        Assert.Empty((await GetDraftAsync(api, draft.Id)).Entries);
    }

    private static async Task<string> TokenAsync(AdminApiHarness api, DraftId id)
    {
        using var review = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{id}/review");
        return Assert.IsType<string>((await AdminApiHarness.ReadAsync(review, AdminJsonContext.Default.DraftOutcome, Ct)).ReviewToken);
    }

    [Fact]
    public async Task ApplyNeedsTheTokenOfTheReviewAndAnythingElseCommitsNothingAndReturnsTheFreshReview()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(ConfigTestSupport.Upstream(Unique("v1"), "http://10.0.0.7:80")));
        var before = await HeadSeqAsync(api);

        using var missing = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply");
        using var unquoted = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: "3");
        using var invented = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: "\"1.AAAAAAAAAAAAAAAAAAAAAA\"");

        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        var asked = await AdminApiHarness.ProblemAsync(missing, Ct);
        Assert.NotNull(asked.Review?.ReviewToken);
        Assert.Equal(HttpStatusCode.BadRequest, unquoted.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, invented.StatusCode);
        var refused = await AdminApiHarness.ProblemAsync(invented, Ct);
        Assert.Equal("draft.changedSinceReview", refused.Code);
        Assert.Equal(ConfigCommandStatus.DryRun, refused.Review!.Result.Status);
        Assert.Equal(asked.Review!.ReviewToken, refused.Review.ReviewToken);
        Assert.Equal($"\"{refused.Review.ReviewToken}\"", AdminApiHarness.ETag(invented));
        Assert.Single((await GetDraftAsync(api, draft.Id)).Entries);
        Assert.Equal(before, await HeadSeqAsync(api));
    }

    [Fact]
    public async Task AConcurrentCommitToAnObjectTheDraftRefersToRefusesTheApplyWithTheFreshReview_AndAnUnrelatedOneDoesNot()
    {
        var api = shared.Api;
        var label = Unique("conc");
        var web = ConfigTestSupport.Upstream(label, "http://10.0.0.13:80");
        using (var made = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", web, AdminJsonContext.Default.UpstreamConfig))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        var draft = await FreshAsync(api);
        var host = ConfigTestSupport.ProxyHost(label, $"{label}.test", label);
        await AddedAsync(api, draft.Id, "owner", new UpsertHostCommand(host));
        var token = await TokenAsync(api, draft.Id);

        // An unrelated commit between the review and the apply: the review is still the review.
        using (var unrelated = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", ConfigTestSupport.RespondHost(Unique("rel"), $"{Unique("rel")}.test", "ok"), AdminJsonContext.Default.HostConfig, "admin"))
        {
            Assert.Equal(HttpStatusCode.Created, unrelated.StatusCode);
        }

        Assert.Equal(token, await TokenAsync(api, draft.Id));

        // The upstream the host uses changes: the person confirmed the host against it as it was.
        var liveWeb = await ReadUpstreamAsync(api, web.Id);
        using (var moved = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/upstreams/{web.Id}", liveWeb with { Name = "web, moved" }, AdminJsonContext.Default.UpstreamConfig, "admin", $"\"{liveWeb.Version}\""))
        {
            Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        }

        var before = await HeadSeqAsync(api);
        using var refused = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{token}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(refused, Ct);
        Assert.Equal("draft.changedSinceReview", problem.Code);
        Assert.Contains(problem.Review!.Result.Changes, c => c is HostChange { Kind: ConfigChangeKind.Added });
        Assert.Equal(before, await HeadSeqAsync(api));
        using (var notLive = await api.GetAsync($"/api/v1/hosts/{host.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, notLive.StatusCode);
        }

        // The fresh review, confirmed on purpose, applies.
        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{problem.Review.ReviewToken}\"");
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(before + 1, await HeadSeqAsync(api));
    }

    [Fact]
    public async Task AnUnrelatedCommitBetweenTheReviewAndTheApplyStillApplies()
    {
        var api = shared.Api;
        var label = Unique("free");
        var draft = await FreshAsync(api);
        await AddedAsync(api, draft.Id, "owner", new UpsertHostCommand(ConfigTestSupport.RespondHost(label, $"{label}.test", "ok")));
        var token = await TokenAsync(api, draft.Id);
        using (var other = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", ConfigTestSupport.RespondHost(Unique("o"), $"{Unique("o")}.test", "ok"), AdminJsonContext.Default.HostConfig, "admin"))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }

        var before = await HeadSeqAsync(api);
        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{token}\"");

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(before + 1, await HeadSeqAsync(api));
    }

    [Fact]
    public async Task AnEntryChangedElsewhereRefusesTheWholeDraftAndNothingIsCommitted()
    {
        var api = shared.Api;
        var mine = ConfigTestSupport.Upstream(Unique("mine"), "http://10.0.0.9:80");
        var other = ConfigTestSupport.Upstream(Unique("other"), "http://10.0.0.10:80");
        foreach (var upstream in new[] { mine, other })
        {
            using var made = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", upstream, AdminJsonContext.Default.UpstreamConfig);
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        var liveMine = await ReadUpstreamAsync(api, mine.Id);
        var liveOther = await ReadUpstreamAsync(api, other.Id);
        var draft = await FreshAsync(api);
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(liveMine with { Name = "mine, from the draft" }));
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(liveOther with { Name = "other, from the draft" }));
        var token = await TokenAsync(api, draft.Id);

        // Another admin edits one of them first.
        using (var elsewhere = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/upstreams/{mine.Id}", liveMine with { Name = "theirs" }, AdminJsonContext.Default.UpstreamConfig,
            caller: "admin", ifMatch: $"\"{liveMine.Version}\""))
        {
            Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        }

        var seen = await GetDraftAsync(api, draft.Id);
        Assert.Equal([DraftEntryState.ChangedElsewhere, DraftEntryState.Ready], seen.Entries.Select(e => e.State));
        Assert.False(string.IsNullOrWhiteSpace(seen.Entries[0].Reason));
        var before = await HeadSeqAsync(api);
        using var refused = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{token}\"");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(refused, Ct);
        Assert.Equal("draft.blocked", problem.Code);
        Assert.Equal("/entries/1", Assert.Single(problem.Errors!).Path);
        Assert.Equal(before, await HeadSeqAsync(api));
        Assert.Equal("theirs", (await ReadUpstreamAsync(api, mine.Id)).Name);
        Assert.Equal(liveOther.Name, (await ReadUpstreamAsync(api, other.Id)).Name);

        // A review shows the rest, without a token; reloading the entry (taking it out) makes the draft applicable again.
        using var review = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review");
        Assert.Null((await AdminApiHarness.ReadAsync(review, AdminJsonContext.Default.DraftOutcome, Ct)).ReviewToken);
        using var removed = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{draft.Id}/entries/{seen.Entries[0].Number}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.NotNull(removed.Headers.ETag);
        Assert.Single((await GetDraftAsync(api, draft.Id)).Entries);
        using var noEntry = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{draft.Id}/entries/{seen.Entries[0].Number}");
        Assert.Equal(HttpStatusCode.NotFound, noEntry.StatusCode);
        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: $"\"{await TokenAsync(api, draft.Id)}\"");
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("other, from the draft", (await ReadUpstreamAsync(api, other.Id)).Name);
    }

    private static async Task<HttpResponseMessage> SimulateDraftAsync(AdminApiHarness api, DraftId draft, string url, string caller = "owner") =>
        await api.SendJsonAsync(
            HttpMethod.Post, "/api/v1/simulate", new SimulateBody(new SimulationRequest(url, "203.0.113.9"), new DraftSimulationTarget(draft)), AdminJsonContext.Default.SimulateBody, caller);

    [Fact]
    public async Task ASimulationAgainstTheDraftShowsWhatApplyingItWouldDoAndChangesNothing()
    {
        var api = shared.Api;
        var label = Unique("sim");
        using (var made = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/hosts", ConfigTestSupport.RespondHost(label, $"{label}.test", "live"), AdminJsonContext.Default.HostConfig))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        var draft = await FreshAsync(api);
        var added = await AddedAsync(api, draft.Id, "owner", new UpsertHostCommand(ConfigTestSupport.RespondHost(label + "-new", $"{label}-new.test", "drafted")));
        var before = await HeadSeqAsync(api);

        using var drafted = await SimulateDraftAsync(api, draft.Id, $"http://{label}-new.test/");
        using var normal = await SimulateDraftAsync(api, draft.Id, $"http://{label}.test/");
        using var live = await api.SendJsonAsync(
            HttpMethod.Post, "/api/v1/simulate", new SimulateBody(new SimulationRequest($"http://{label}-new.test/", "203.0.113.9")), AdminJsonContext.Default.SimulateBody);

        Assert.Equal(HttpStatusCode.OK, drafted.StatusCode);
        var withDraft = await AdminApiHarness.ReadAsync(drafted, AdminJsonContext.Default.SimulationResult, Ct);
        Assert.Equal(SimulationTargetKind.Draft, withDraft.Source.Kind);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, withDraft.Outcome!.Kind);
        Assert.Equal(SimulationOutcomeKind.FixedAnswer, (await AdminApiHarness.ReadAsync(normal, AdminJsonContext.Default.SimulationResult, Ct)).Outcome!.Kind);
        var without = await AdminApiHarness.ReadAsync(live, AdminJsonContext.Default.SimulationResult, Ct);
        Assert.Equal(SimulationOutcomeKind.DefaultHost, without.Outcome!.Kind);

        // Reading the draft is all it did: the draft keeps its entry and the head did not move.
        Assert.Equal(added.Version, (await GetDraftAsync(api, draft.Id)).Version);
        Assert.Equal(before, await HeadSeqAsync(api));
    }

    [Fact]
    public async Task ASimulationAgainstAnEmptyDraftIsTheCurrentConfig()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);

        using var response = await SimulateDraftAsync(api, draft.Id, "http://nothing-here.test/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.SimulationResult, Ct);
        Assert.Contains(result.Notes!, n => n.Contains("changes nothing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASimulationAgainstADraftNeedsTheDraftToBeTheCallersOwnAndToExist()
    {
        var api = shared.Api;
        var owners = await FreshAsync(api);
        var admins = await FreshAsync(api, "admin");
        var gone = await FreshAsync(api, "operator");
        using (var discarded = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{gone.Id}", "operator"))
        {
            Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        }

        using var theirs = await SimulateDraftAsync(api, owners.Id, "http://a.test/", "admin");
        using var unknown = await SimulateDraftAsync(api, DraftId.New(), "http://a.test/");
        using var discardedOne = await SimulateDraftAsync(api, gone.Id, "http://a.test/", "operator");
        using var ownAdmins = await SimulateDraftAsync(api, admins.Id, "http://a.test/", "admin");

        Assert.All(new[] { theirs, unknown, discardedOne }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, ownAdmins.StatusCode);
    }

    [Fact]
    public async Task ASimulationAgainstADraftNeedsTheSimulatePermissionAndTheDraftsOwn()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);

        // A viewer may simulate but can't read a draft.
        using var viewer = await SimulateDraftAsync(api, draft.Id, "http://a.test/", "viewer");

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task ASimulationAgainstADraftThatNoLongerAppliesGivesTheReviewsProblem()
    {
        var api = shared.Api;
        var mine = ConfigTestSupport.Upstream(Unique("sim-mine"), "http://10.0.0.19:80");
        using (var made = await api.SendJsonAsync(HttpMethod.Post, "/api/v1/upstreams", mine, AdminJsonContext.Default.UpstreamConfig))
        {
            Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        }

        var liveMine = await ReadUpstreamAsync(api, mine.Id);
        var draft = await FreshAsync(api);
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(liveMine with { Name = Unique("from the draft") }));
        using (var elsewhere = await api.SendJsonAsync(HttpMethod.Put, $"/api/v1/upstreams/{mine.Id}", liveMine with { Name = Unique("theirs") }, AdminJsonContext.Default.UpstreamConfig,
            caller: "admin", ifMatch: $"\"{liveMine.Version}\""))
        {
            Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        }

        using var simulated = await SimulateDraftAsync(api, draft.Id, "http://a.test/");
        using var review = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review");

        Assert.Equal(HttpStatusCode.Conflict, simulated.StatusCode);
        var problem = await AdminApiHarness.ProblemAsync(simulated, Ct);
        Assert.Equal("draft.blocked", problem.Code);
        Assert.Equal("/entries/1", Assert.Single(problem.Errors!).Path);
        Assert.Equal(HttpStatusCode.Conflict, review.StatusCode);
        Assert.Equal(problem.Code, (await AdminApiHarness.ProblemAsync(review, Ct)).Code);
    }

    [Fact]
    public async Task AnEmptyDraftHasNothingToApply()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);

        using var applied = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply", ifMatch: "\"1.AAAAAAAAAAAAAAAAAAAAAA\"");

        Assert.Equal(HttpStatusCode.Conflict, applied.StatusCode);
        Assert.Equal("draft.nothingToApply", (await AdminApiHarness.ProblemAsync(applied, Ct)).Code);
    }

    [Fact]
    public async Task ADraftIsItsOwnersAloneAndAUserHasOne()
    {
        var api = shared.Api;
        var owners = await FreshAsync(api);
        var admins = await FreshAsync(api, "admin");

        using var again = await api.SendAsync(HttpMethod.Post, "/api/v1/drafts");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("draft.exists", (await AdminApiHarness.ProblemAsync(again, Ct)).Code);
        Assert.Equal($"/api/v1/drafts/{owners.Id}", again.Headers.Location!.ToString());

        Assert.NotEqual(owners.Id, admins.Id);
        using var theirs = await api.SendAsync(HttpMethod.Get, $"/api/v1/drafts/{owners.Id}", "admin");
        using var theirConfig = await api.SendAsync(HttpMethod.Get, $"/api/v1/drafts/{owners.Id}/config", "admin");
        Assert.Equal(HttpStatusCode.NotFound, theirConfig.StatusCode);
        using var ownConfigOfAnother = await api.SendAsync(HttpMethod.Get, $"/api/v1/drafts/{admins.Id}/config", "owner");
        Assert.Equal(HttpStatusCode.NotFound, ownConfigOfAnother.StatusCode);
        using var addTheirs = await AddAsync(api, owners.Id, "admin", new UpsertUpstreamCommand(ConfigTestSupport.Upstream(Unique("x"), "http://10.0.0.11:80")));
        using var discardTheirs = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{owners.Id}", "admin");
        Assert.All(new[] { theirs, addTheirs, discardTheirs }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(owners.Id, (await GetDraftAsync(api, owners.Id)).Id);
    }

    [Fact]
    public async Task TheConfigWithTheDraftAppliedShowsWhatTheDraftAddsAndTheHeadDoesNot()
    {
        var api = shared.Api;
        var label = Unique("view");
        var upstream = ConfigTestSupport.Upstream(label, "http://10.0.0.12:80");
        var draft = await FreshAsync(api);
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(upstream));

        using var response = await api.GetAsync($"/api/v1/drafts/{draft.Id}/config");
        var working = await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.DraftConfig, Ct);

        Assert.Contains(working.Document.Upstreams, u => u.Id == upstream.Id);
        using var head = await api.GetAsync($"/api/v1/upstreams/{upstream.Id}");
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
    }

    [Fact]
    public async Task DiscardingDropsTheDraftAndAnotherCanBeOpened()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);

        using var discarded = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{draft.Id}");

        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
        using var list = await api.GetAsync("/api/v1/drafts");
        Assert.Empty((await AdminApiHarness.ReadAsync(list, AdminJsonContext.Default.PageConfigDraft, Ct)).Items);
        Assert.NotEqual(draft.Id, (await FreshAsync(api)).Id);
    }

    [Fact]
    public async Task APasswordTypedIntoADraftedAccessListIsNeverStoredOrReturned()
    {
        var api = shared.Api;
        const string Secret = "a draft-only secret 12345";
        var list = new AccessListConfig(TestIds.AccessList(Unique("acl")), "office", Ip: new AccessListIp(["10.0.0.0/8"]),
            BasicAuth: new AccessListBasicAuth(Users: [new AccessListUserConfig("alice", Password: Secret)]));
        var draft = await FreshAsync(api);

        using var added = await AddAsync(api, draft.Id, "owner", new UpsertAccessListCommand(list));
        var body = await added.Content.ReadAsStringAsync(Ct);
        using var read = await api.GetAsync($"/api/v1/drafts/{draft.Id}");
        var readBody = await read.Content.ReadAsStringAsync(Ct);
        using var config = await api.GetAsync($"/api/v1/drafts/{draft.Id}/config");
        var configBody = await config.Content.ReadAsStringAsync(Ct);
        using var review = await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review");
        var reviewBody = await review.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        foreach (var text in new[] { body, readBody, configBody, reviewBody })
        {
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"password\"", text, StringComparison.Ordinal);
        }

        Assert.Contains("credentialId", readBody, StringComparison.Ordinal);
        var row = Convert.ToString(await ConfigTestSupport.ScalarAsync(api.Host.Options.DataDirectory, "SELECT group_concat(Entries || PendingCredentials, ' ') FROM ConfigDrafts", Ct)) ?? string.Empty;
        Assert.DoesNotContain(Secret, row, StringComparison.Ordinal);
        Assert.Contains("pwc_", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("auditor")]
    public async Task WithoutConfigWriteNoDraftEndpointIsOpen(string role)
    {
        var api = shared.Api;
        var id = DraftId.New();

        var answers = new[]
        {
            await api.GetAsync("/api/v1/drafts", role),
            await api.SendAsync(HttpMethod.Post, "/api/v1/drafts", role),
            await api.GetAsync($"/api/v1/drafts/{id}", role),
            await api.GetAsync($"/api/v1/drafts/{id}/config", role),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{id}/entries", role, content: AdminApiHarness.JsonBody("""{"commands":[]}""")),
            await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{id}/entries/1", role),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{id}/review", role),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{id}/apply", role, ifMatch: "\"1.AAAAAAAAAAAAAAAAAAAAAA\""),
            await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{id}", role),
        };
        Assert.All(answers, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        foreach (var answer in answers)
        {
            answer.Dispose();
        }

        using var anonymous = await api.GetAsync("/api/v1/drafts", caller: null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task AWholeConfigCommandOrAnEmptyListIsNotAnEntry()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);

        using var replace = await AddAsync(api, draft.Id, "owner", new ReplaceConfigCommand(new ConfigDocument()));
        using var empty = await AddAsync(api, draft.Id, "owner");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, replace.StatusCode);
        Assert.Equal("draft.entry.command", Assert.Single((await AdminApiHarness.ProblemAsync(replace, Ct)).Errors!).Code);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    private static async Task<ConfigDraft> GetDraftAsync(AdminApiHarness api, DraftId id)
    {
        using var response = await api.GetAsync($"/api/v1/drafts/{id}");
        Assert.NotNull(AdminApiHarness.ETag(response));
        var draft = await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.ConfigDraft, Ct);
        Assert.Equal($"\"{draft.Version}\"", AdminApiHarness.ETag(response));
        return draft;
    }

    private static async Task<UpstreamConfig> ReadUpstreamAsync(AdminApiHarness api, UpstreamId id)
    {
        using var response = await api.GetAsync($"/api/v1/upstreams/{id}");
        return await AdminApiHarness.ReadAsync(response, AdminJsonContext.Default.UpstreamConfig, Ct);
    }

    /// <summary>
    /// An expired draft reads as expired on every endpoint, and none of them writes: the reads, the review and the simulation are what MCP dispatches without a confirm,
    /// and each used to delete the draft, write its notice and clear its vault values on the way.
    /// </summary>
    [Fact]
    public async Task AnExpiredDraftIs410OnEveryEndpointAndNoneOfThemChangesTheDatabase()
    {
        var api = shared.Api;
        var draft = await FreshAsync(api);
        var label = Unique("lapsed");
        await AddedAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(ConfigTestSupport.Upstream(label, "http://10.0.0.7:80")));
        var contexts = api.Host.GetServices(HostRole.Core).GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        async Task<string> SqlAsync(string sql)
        {
            await using var context = await contexts.CreateDbContextAsync(Ct);
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            // The row is found by what it holds: its id is stored as bytes.
            var holding = command.CreateParameter();
            holding.ParameterName = "$label";
            holding.Value = "%" + label + "%";
            command.Parameters.Add(holding);
            return Convert.ToString(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        Task<string> StoredAsync() => SqlAsync("SELECT UpdatedAt || ' ' || Version || ' ' || (SELECT COUNT(*) FROM ConfigDraftNotices) FROM ConfigDrafts WHERE Entries LIKE $label");

        var past = (DateTimeOffset.UtcNow - Paduraksa.Application.Drafts.DraftService.Lifetime - TimeSpan.FromDays(1)).UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        await SqlAsync($"UPDATE ConfigDrafts SET UpdatedAt = '{past}' WHERE Entries LIKE $label");
        Assert.StartsWith(past, await StoredAsync(), StringComparison.Ordinal);

        var before = await StoredAsync();
        using (var listed = await api.GetAsync("/api/v1/drafts"))
        {
            Assert.Empty((await AdminApiHarness.ReadAsync(listed, AdminJsonContext.Default.PageConfigDraft, Ct)).Items);
        }

        HttpResponseMessage[] answers =
        [
            await api.GetAsync($"/api/v1/drafts/{draft.Id}"),
            await api.GetAsync($"/api/v1/drafts/{draft.Id}/config"),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/review"),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply"),
            await api.SendAsync(HttpMethod.Post, $"/api/v1/drafts/{draft.Id}/apply?dryRun=true"),
            await AddAsync(api, draft.Id, "owner", new UpsertUpstreamCommand(ConfigTestSupport.Upstream(Unique("late"), "http://10.0.0.8:80"))),
            await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{draft.Id}/entries/1"),
            await SimulateDraftAsync(api, draft.Id, "http://a.test/"),
        ];
        foreach (var answer in answers)
        {
            using (answer)
            {
                Assert.True(answer.StatusCode == HttpStatusCode.Gone, $"{answer.RequestMessage!.Method} {answer.RequestMessage.RequestUri}: {(int)answer.StatusCode}");
                Assert.Equal(ApiCodes.Draft.Expired, (await AdminApiHarness.ProblemAsync(answer, Ct)).Code);
            }
        }

        Assert.Equal(before, await StoredAsync());

        using var discarded = await api.SendAsync(HttpMethod.Delete, $"/api/v1/drafts/{draft.Id}");
        Assert.Equal(HttpStatusCode.NoContent, discarded.StatusCode);
    }
}
