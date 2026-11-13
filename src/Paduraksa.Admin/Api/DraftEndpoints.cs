using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Drafts;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// Drafts: a user's change set across pages, applied as one revision (docs/design/dashboard.md "Drafts (M10)"). One open draft per user (a token acts on its owner's), kept until
/// it is applied or discarded or goes 30 days untouched. Every endpoint needs <c>config:write</c>. A read carries the draft's version as
/// <c>ETag</c>, and apply must send it back as <c>If-Match</c> (428 without, 412 when the draft changed since). Nothing in a draft is committed,
/// audited or live until apply, which is one revision and one audit event.
/// </summary>
internal static class DraftEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var drafts = api.MapGroup("/drafts").WithTags("Drafts");
        drafts.MapGet("/", ListAsync).WithName("ListDrafts").WithSummary("The caller's open draft")
            .WithDescription("A list of at most one: the caller's open draft (a token's is its owner's), each entry marked Ready or ChangedElsewhere against the latest revision. Empty when there is none.")
            .Produces<Page<ConfigDraft>>().ProducesApiProblems(400).RequirePermission(Permission.ConfigWrite);
        drafts.MapPost("/", CreateAsync).WithName("CreateDraft").WithSummary("Open a draft")
            .WithDescription("Starts an empty draft on the latest revision. 409 when the caller has one open already (see Location).")
            .Produces<ConfigDraft>(201).ProducesApiProblems(403, 409, 503).RequirePermission(Permission.ConfigWrite);
        drafts.MapGet("/{id:DraftId}", GetAsync).WithName("GetDraft").WithSummary("Get the open draft")
            .WithDescription("The draft with each entry's state against the latest revision. The ETag is the draft's version.")
            .Produces<ConfigDraft>().ProducesApiProblems(404).RequirePermission(Permission.ConfigWrite);
        drafts.MapGet("/{id:DraftId}/config", GetConfigAsync).WithName("GetDraftConfig").WithSummary("The config with the draft applied")
            .WithDescription("The latest revision's document with every entry that is not changed elsewhere applied in order: what a page reads while the draft is open. Versions are the latest revision's.")
            .Produces<DraftConfig>().ProducesApiProblems(404).RequirePermission(Permission.ConfigWrite);
        drafts.MapPost("/{id:DraftId}/entries", AddAsync).WithName("AddDraftEntries").WithSummary("Add or replace entries")
            .WithDescription("The body is a list of object commands (upsert or delete of a host, upstream, profile, rate limit, access list, rule set or auth provider, or the settings), each with the version it was made against, like the object endpoints' If-Match. "
                + "Each is checked as a dry run on the latest revision with the draft's other entries applied, with the same errors a live command gives, and replaces the draft's earlier entry for its object; one that leaves its object as it is takes the entry out, and deleting an object the draft creates removes its entry. "
                + "If one is refused (422, 412 or 404 like the live command) none is added. A password in an access list goes to the vault as a pending value bound to the draft; it is never stored in the draft or returned.")
            .Accepts<AddDraftEntriesRequest>("application/json").Produces<ConfigDraft>().ProducesApiProblems(400, 403, 404, 412, 415, 422, 503)
            .RequirePermission(Permission.ConfigWrite);
        drafts.MapDelete("/{id:DraftId}/entries/{number:int}", RemoveEntryAsync).WithName("RemoveDraftEntry").WithSummary("Remove an entry")
            .WithDescription("Takes one entry out of the draft, whatever its state: this is also how an entry that is changed elsewhere is reloaded (read the object again and add it afresh). The ETag of the answer is the draft's new version.")
            .Produces(204).ProducesApiProblems(404).RequirePermission(Permission.ConfigWrite);
        drafts.MapPost("/{id:DraftId}/review", ReviewAsync).WithName("ReviewDraft").WithSummary("Review the draft as a dry run")
            .WithDescription("Replays the entries on the latest revision and dry-runs them as one change: the diff, the compiler's warnings, the admin lock-out guard (?force=true overrides it) and the errors of the whole set. "
                + "The answer's reviewToken (also its ETag) is what apply must send back as If-Match to commit exactly this: it covers what the draft would do, so a change to the draft, to an object it writes or refers to, or to what the compiler says about it makes it stale, while an unrelated commit does not. "
                + "A draft with entries that are changed elsewhere has no token (409 draft.blocked when every entry is); the answer shows the rest. Commits and audits nothing.")
            .Produces<DraftOutcome>().ProducesApiProblems(404, 409, 422, 503).RequirePermission(Permission.ConfigWrite);
        drafts.MapPost("/{id:DraftId}/apply", ApplyAsync).WithName("ApplyDraft").WithSummary("Apply the draft as one revision")
            .WithDescription("Commits the whole draft as ONE revision, audited once with every entry listed, or nothing: what is confirmed is exactly what is applied. Needs If-Match with the review's token (428 without it, with the fresh review in the problem's review). "
                + "Under the writer's lock the draft is replayed on the current head; unless that is what the review showed, 412 draft.changedSinceReview commits nothing and returns the fresh review (and its token) in the problem's review. "
                + "Any entry that is changed elsewhere, or depends on one that is, refuses the whole draft (409 draft.blocked, errors naming each); remove or reload them and review again. "
                + "?message= is the revision's note (it defaults to a summary of the entries). ?dryRun=true is the review. Undo of the revision undoes the whole draft.")
            .Produces<DraftOutcome>().ProducesApiProblems(404, 409, 412, 422, 428, 503).MayFailToPublish().RequirePermission(Permission.ConfigWrite);
        drafts.MapDelete("/{id:DraftId}", DiscardAsync).WithName("DiscardDraft").WithSummary("Discard the draft")
            .WithDescription("Drops the draft and the vault values bound to it. Nothing was committed, so nothing is undone.")
            .Produces(204).ProducesApiProblems(404).RequirePermission(Permission.ConfigWrite);
    }

    private static async Task<IResult> ListAsync(HttpContext http, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var result = await drafts.GetAsync(caller.Origin, ct).ConfigureAwait(false);
        return result.Status switch
        {
            DraftStatus.NoDraft => Pagination.Respond(http, Array.Empty<ConfigDraft>(), d => d.Id, AdminJsonContext.Default.PageConfigDraft),
            DraftStatus.Succeeded => Pagination.Respond(http, [result.Draft!], d => d.Id, AdminJsonContext.Default.PageConfigDraft),
            _ => Problem(http, result),
        };
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var result = await drafts.CreateAsync(caller.Origin, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case DraftStatus.Succeeded:
                http.Response.Headers.ETag = Preconditions.VersionTag(result.Draft!.Version);
                http.Response.Headers.Location = EndpointSupport.Path("drafts", result.Draft.Id);
                return EndpointSupport.Json(result.Draft, AdminJsonContext.Default.ConfigDraft, StatusCodes.Status201Created);
            case DraftStatus.AlreadyOpen:
                http.Response.Headers.Location = EndpointSupport.Path("drafts", result.Draft!.Id);
                return ApiProblems.Problem(StatusCodes.Status409Conflict, "You have an open draft already (see Location): add to it, apply it or discard it.", code: ApiCodes.Draft.Exists);
            default:
                return Problem(http, result);
        }
    }

    private static async Task<IResult> GetAsync(HttpContext http, DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var result = await OwnAsync(caller, id, drafts, ct).ConfigureAwait(false);
        return result.Status == DraftStatus.Succeeded ? Draft(http, result.Draft!) : Problem(http, result);
    }

    private static async Task<IResult> GetConfigAsync(DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct) =>
        await drafts.GetConfigAsync(caller.Origin, id, ct).ConfigureAwait(false) is { } config
            ? EndpointSupport.Json(config, AdminJsonContext.Default.DraftConfig)
            : NoSuchDraft();

    private static async Task<IResult> AddAsync(HttpContext http, DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.AddDraftEntriesRequest).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        if (body.Value!.Commands is not { Count: > 0 })
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The body needs at least one command.");
        }

        var result = await drafts.AddAsync(caller.Origin, id, body.Value.Commands, ct).ConfigureAwait(false);
        return result.Status == DraftStatus.Succeeded ? Draft(http, result.Draft!) : Problem(http, result);
    }

    private static async Task<IResult> RemoveEntryAsync(HttpContext http, DraftId id, int number, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var own = await OwnAsync(caller, id, drafts, ct).ConfigureAwait(false);
        if (own.Status != DraftStatus.Succeeded)
        {
            return Problem(http, own);
        }

        if (own.Draft!.Entries.All(e => e.Number != number))
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such entry in the draft.");
        }

        var result = await drafts.RemoveEntryAsync(caller.Origin, id, number, ct).ConfigureAwait(false);
        if (result.Status != DraftStatus.Succeeded)
        {
            return Problem(http, result);
        }

        // 204 like every delete; the ETag is the draft's new version, so a client need not read it again.
        http.Response.Headers.ETag = Preconditions.VersionTag(result.Draft!.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> ReviewAsync(HttpContext http, DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        if (!Preconditions.TryGetForce(http, out var force, out var problem))
        {
            return problem!;
        }

        var result = await drafts.ReviewAsync(caller.Origin, id, force, ct).ConfigureAwait(false);
        return Outcome(http, result);
    }

    private static async Task<IResult> ApplyAsync(HttpContext http, DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem) || !Preconditions.TryGetForce(http, out var force, out problem))
        {
            return problem!;
        }

        if (dryRun)
        {
            return Outcome(http, await drafts.ReviewAsync(caller.Origin, id, force, ct).ConfigureAwait(false));
        }

        if (http.Request.Headers.IfMatch.Count == 0)
        {
            // Nothing is applied without a review's token; the review is given, so a client can confirm what it shows.
            var review = await drafts.ReviewAsync(caller.Origin, id, force, ct).ConfigureAwait(false);
            return review.Status == DraftStatus.Succeeded && review.Command is { } planned
                ? ApiProblems.Problem(StatusCodes.Status428PreconditionRequired, "Applying needs an If-Match with the token of the review you confirmed (the problem's review is the current one).",
                    review: new DraftOutcome(planned, review.Draft, review.ReviewToken))
                : Problem(http, review);
        }

        if (!Preconditions.TryGetTag(http, out var token, out problem))
        {
            return problem!;
        }

        var result = await drafts.ApplyAsync(caller.Origin, id, token, EndpointSupport.MessageOf(http), force, ct).ConfigureAwait(false);
        return Outcome(http, result);
    }

    private static async Task<IResult> DiscardAsync(HttpContext http, DraftId id, AdminCaller caller, DraftService drafts, CancellationToken ct)
    {
        var result = await drafts.DiscardAsync(caller.Origin, id, ct).ConfigureAwait(false);
        return result.Status == DraftStatus.Succeeded ? Results.NoContent() : Problem(http, result);
    }

    private static async Task<DraftResult> OwnAsync(AdminCaller caller, DraftId id, DraftService drafts, CancellationToken ct)
    {
        var result = await drafts.GetAsync(caller.Origin, ct).ConfigureAwait(false);
        return result.Status == DraftStatus.Succeeded && result.Draft!.Id != id ? new DraftResult(DraftStatus.NoDraft) : result;
    }

    private static IResult Draft(HttpContext http, ConfigDraft draft)
    {
        http.Response.Headers.ETag = Preconditions.VersionTag(draft.Version);
        return EndpointSupport.Json(draft, AdminJsonContext.Default.ConfigDraft);
    }

    /// <summary>A review's or an apply's answer: the outcome when the pipeline passed it, a problem when it refused it (as every command endpoint does).</summary>
    private static IResult Outcome(HttpContext http, DraftResult result)
    {
        if (result.Status != DraftStatus.Succeeded)
        {
            return Problem(http, result);
        }

        var command = result.Command!;
        if (command.Status is ConfigCommandStatus.Committed or ConfigCommandStatus.DryRun or ConfigCommandStatus.Unchanged)
        {
            if (result.ReviewToken is { } token)
            {
                http.Response.Headers.ETag = Preconditions.Quote(token);
            }

            return EndpointSupport.Json(new DraftOutcome(command, result.Draft, result.ReviewToken), AdminJsonContext.Default.DraftOutcome);
        }

        return ConfigCommandResponses.ToResult(http, command, MutationKind.Change);
    }

    private static IResult Problem(HttpContext http, DraftResult result) => result.Status switch
    {
        DraftStatus.Denied => ApiProblems.Problem(StatusCodes.Status403Forbidden, "Drafts belong to a user: sign in, or use a token (it acts on its owner's draft). You also need the permission to change the config.", code: ApiCodes.Draft.NotAUser),
        DraftStatus.NoDraft => NoSuchDraft(),
        DraftStatus.AlreadyOpen => ApiProblems.Problem(StatusCodes.Status409Conflict, "You have an open draft already.", code: ApiCodes.Draft.Exists),
        DraftStatus.ChangedSinceReview => ChangedSinceReview(http, result),
        DraftStatus.Blocked => ApiProblems.Problem(StatusCodes.Status409Conflict,
            "Some entries are changed elsewhere (or depend on one that is), and a draft is applied whole or not at all: nothing was committed. Remove them or reload them from the config as it is now, then review again.",
            errors: [.. (result.Draft?.Entries ?? []).Where(static e => e.State == DraftEntryState.ChangedElsewhere)
                .Select(static e => new ConfigError($"/entries/{e.Number}", DraftErrorCodes.EntryBlocked, $"{e.Target}: {e.Reason}"))],
            code: ApiCodes.Draft.Blocked),
        DraftStatus.NothingToApply => ApiProblems.Problem(StatusCodes.Status409Conflict, "The draft has no entries, so there is nothing to apply.", code: ApiCodes.Draft.NothingToApply),
        _ => Refused(result),
    };

    private static IResult ChangedSinceReview(HttpContext http, DraftResult result)
    {
        if (result.ReviewToken is { } token)
        {
            http.Response.Headers.ETag = Preconditions.Quote(token);
        }

        return ApiProblems.Problem(StatusCodes.Status412PreconditionFailed,
            "What this apply would commit is not what the review you confirmed showed (the draft, the config it touches or the compiler's warnings changed). Nothing was committed; the problem's review is the current one.",
            code: ApiCodes.Draft.ChangedSinceReview,
            review: result.Command is { } planned ? new DraftOutcome(planned, result.Draft, result.ReviewToken) : null);
    }

    private static IResult Refused(DraftResult result)
    {
        var command = result.Command!;
        var status = ApiStatusMapping.Of(command.Status, MutationKind.Change);
        var which = result.FailedCommand is { } index ? $"Command {index + 1} was refused: " : string.Empty;
        return ApiProblems.Problem(status, which + ConfigCommandResponses.Detail(command.Status), null, command.Errors, command.RelatedErrors, command.Warnings, command.Conflict);
    }

    private static IResult NoSuchDraft() => ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such draft (a draft is only ever the caller's own).");
}
