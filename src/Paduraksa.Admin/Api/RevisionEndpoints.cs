using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The config history: revisions newest first (cursor pagination over the revision ID), one revision with its document, the diff between two,
/// and rollback, which restores an earlier config as a new revision (history is never rewritten) and, like any whole-config change, names the
/// latest revision it was based on in <c>If-Match</c>.
/// </summary>
internal static class RevisionEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var revisions = api.MapGroup("/revisions").WithTags("Revisions");
        revisions.MapGet("/", ListAsync).WithName("ListRevisions").WithSummary("List revisions")
            .WithDescription("Newest first. Pass the nextCursor of a page as cursor for the next one; limit is 1 to 200 (default 50).")
            .Produces<Page<RevisionSummary>>().ProducesApiProblems(400).RequirePermission(Permission.ConfigRead);
        revisions.MapGet("/diff", DiffAsync).WithName("DiffRevisions").WithSummary("What changed between two revisions")
            .WithDescription("Query: from and to, revision IDs. The structural diff of their documents, in the form dry runs report.")
            .Produces<RevisionDiff>().ProducesApiProblems(400, 404).RequirePermission(Permission.ConfigRead);
        revisions.MapGet("/{id:ConfigRevisionId}", GetAsync).WithName("GetRevision").WithSummary("Get a revision with its document")
            .Produces<RevisionDetail>().ProducesApiProblems(404).RequirePermission(Permission.ConfigRead);
        revisions.MapPost("/{id:ConfigRevisionId}/rollback", RollbackAsync).WithName("RollbackToRevision").WithSummary("Restore an earlier revision as a new one")
            .WithDescription("Needs If-Match with the latest revision's ID, as the ETag of GET /config (428 without it, 412 when it is no longer the latest). Supports ?dryRun=true.")
            .Produces<ConfigCommandResult>().ProducesApiProblems(400, 404, 412, 422, 428).MayFailToPublish().RequirePermission(Permission.ConfigWrite);
    }

    private static async Task<IResult> ListAsync(HttpContext http, RevisionQueryService revisions, CancellationToken ct)
    {
        if (!Pagination.TryGetPage(http, out var limit, out var cursor, out var problem))
        {
            return problem!;
        }

        ConfigRevisionId? after = null;
        if (cursor is not null)
        {
            if (!Pagination.TryDecode(cursor, out ConfigRevisionId id))
            {
                return BadCursor();
            }

            after = id;
        }

        var page = await revisions.ListAsync(limit, after, ct).ConfigureAwait(false);
        return page is null
            ? BadCursor()
            : EndpointSupport.Json(new Page<RevisionSummary>(page.Items, page.Next is { } next ? Pagination.Encode(next) : null), AdminJsonContext.Default.PageRevisionSummary);
    }

    private static async Task<IResult> GetAsync(ConfigRevisionId id, RevisionQueryService revisions, CancellationToken ct) =>
        await revisions.GetAsync(id, ct).ConfigureAwait(false) is { } detail
            ? EndpointSupport.Json(detail, AdminJsonContext.Default.RevisionDetail)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such revision.");

    private static async Task<IResult> DiffAsync(HttpContext http, RevisionQueryService revisions, CancellationToken ct)
    {
        if (!TryGetRevision(http, "from", out var from) || !TryGetRevision(http, "to", out var to))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "Give 'from' and 'to' as revision IDs.");
        }

        return await revisions.DiffAsync(from, to, ct).ConfigureAwait(false) is { } diff
            ? EndpointSupport.Json(diff, AdminJsonContext.Default.RevisionDiff)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "One of the revisions doesn't exist.");
    }

    private static async Task<IResult> RollbackAsync(HttpContext http, AdminCaller caller, ConfigRevisionId id, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem)
            || !Preconditions.TryGetRevision(http, out ConfigRevisionId latest, out problem))
        {
            return problem!;
        }

        ConfigCommand command = new RollbackConfigCommand(id, latest) { DryRun = dryRun, Message = EndpointSupport.MessageOf(http), Force = force };
        var result = await config.ExecuteAsync(command, caller.Origin, ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, MutationKind.Change);
    }

    private static bool TryGetRevision(HttpContext http, string name, out ConfigRevisionId id)
    {
        id = default;
        return http.Request.Query[name] is { Count: 1 } values && ConfigRevisionId.TryParse(values[0], null, out id);
    }

    private static IResult BadCursor() => ApiProblems.Problem(StatusCodes.Status400BadRequest, "The cursor is not one this API issued for revisions, or its revision no longer exists.");
}
