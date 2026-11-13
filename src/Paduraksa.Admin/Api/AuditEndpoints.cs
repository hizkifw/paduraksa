using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Audit;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Audit;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>The audit log: a filtered, cursor-paginated query and the hash-chain check.</summary>
internal static class AuditEndpoints
{
    private const int MaxFilterLength = 128;

    internal static readonly QueryParameter From = QueryParameter.Time("from", "Only events at or after this time (inclusive), ISO 8601 with a zone, e.g. 2026-11-08T12:00:00Z (write the + of an offset as %2B).");
    internal static readonly QueryParameter To = QueryParameter.Time("to", "Only events before this time (exclusive), ISO 8601 with a zone.");

    private static readonly QueryParameter Actor = QueryParameter.Text("actor", "Only events by this actor: 'system', 'cli', a user ID or an API token ID.", MaxFilterLength);
    private static readonly QueryParameter Action = QueryParameter.Text("action", "Only this action (config.host.update), or every action under a prefix with one trailing '*' (config.*).", MaxFilterLength);
    private static readonly QueryParameter Target = QueryParameter.Text("target", "Only events about this object: a typed ID such as host_...", MaxFilterLength);
    private static readonly QueryParameter Outcome = QueryParameter.Choice("outcome", "Only events with this outcome.", Enum.GetValues<AuditOutcome>().Select(o => o.ToName()));

    public static void Map(RouteGroupBuilder api)
    {
        var audit = api.MapGroup("/audit").WithTags("Audit");
        audit.MapGet("/", QueryAsync).WithName("QueryAudit").WithSummary("Query the audit log")
            .WithDescription("Newest first. The filters are all optional and all must match.")
            .WithQuery(From, To, Actor, Action, Target, Outcome).WithPage().Produces<Page<AuditEventSummary>>().ProducesApiProblems(400).RequirePermission(Permission.AuditRead);
        audit.MapGet("/verify", VerifyAsync).WithName("VerifyAudit").WithSummary("Check the audit log's hash chain")
            .WithDescription("Walks the whole chain and reports the first event that doesn't verify. Keep headHash somewhere else to detect the removal of the newest events.")
            .Produces<AuditVerification>().RequirePermission(Permission.AuditRead);
    }

    private static async Task<IResult> QueryAsync(HttpContext http, AuditQueryService audit, CancellationToken ct)
    {
        if (!Pagination.TryGetPage(http, out var limit, out var cursor, out var problem))
        {
            return problem!;
        }

        AuditEventId? after = null;
        if (cursor is not null)
        {
            if (!Pagination.TryDecode(cursor, out AuditEventId id))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "The cursor is not one this API issued for the audit log.");
            }

            after = id;
        }

        if (!TryGetTime(http, From, out var from) || !TryGetTime(http, To, out var to))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "from and to must be ISO 8601 times with a zone, e.g. 2026-11-08T12:00:00Z (write the + of an offset as %2B).");
        }

        if (from > to)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "from must not be after to.");
        }

        if (!TryGetFilter(http, Actor, out var actor) || !TryGetFilter(http, Action, out var action) || !TryGetFilter(http, Target, out var target))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, $"actor, action and target are at most {MaxFilterLength} characters.");
        }

        if (actor is not null && !IsActor(actor))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "actor must be 'system', 'cli', a user ID or an API token ID.");
        }

        if (action is not null && !IsActionFilter(action))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "action is a dotted name (config.host.update) or a prefix with one trailing '*' (config.*).");
        }

        if (target is not null && !target.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "target must be a typed ID such as host_...");
        }

        if (!TryGetOutcome(http, out var outcome))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "outcome must be success, denied, invalid, conflict or failed.");
        }

        var page = await audit.QueryAsync(new AuditFilter(from, to, actor, action, target, outcome), limit, after, ct).ConfigureAwait(false);
        return EndpointSupport.Json(
            new Page<AuditEventSummary>(page.Items, page.Next is { } next ? Pagination.Encode(next) : null),
            AdminJsonContext.Default.PageAuditEventSummary);
    }

    private static async Task<IResult> VerifyAsync(AuditQueryService audit, CancellationToken ct) =>
        EndpointSupport.Json(await audit.VerifyAsync(ct).ConfigureAwait(false), AdminJsonContext.Default.AuditVerification);

    /// <summary>A filter value (null when absent); false when it is longer than a filter can be, which is an error, not a filter that quietly isn't applied.</summary>
    private static bool TryGetFilter(HttpContext http, QueryParameter parameter, out string? value)
    {
        var text = parameter.Read(http).ToString();
        value = text.Length == 0 ? null : text;
        return text.Length <= MaxFilterLength;
    }

    /// <summary>The outcome filter by its name (<c>success</c>, <c>denied</c>, ...); null when absent, false for anything else.</summary>
    private static bool TryGetOutcome(HttpContext http, out AuditOutcome? outcome)
    {
        outcome = null;
        var text = Outcome.Read(http).ToString();
        if (text.Length == 0)
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<AuditOutcome>())
        {
            if (string.Equals(candidate.ToName(), text, StringComparison.Ordinal))
            {
                outcome = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsActionFilter(string action)
    {
        var name = action.EndsWith('*') ? action[..^1] : action;
        return name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.');
    }

    private static bool IsActor(string actor) =>
        actor is "system" or "cli" || UserId.TryParse(actor, null, out _) || ApiTokenId.TryParse(actor, null, out _);

    private static readonly string[] TimeFormats = ["yyyy'-'MM'-'dd'T'HH':'mm':'ssK", "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'FFFFFFFK"];

    /// <summary>An ISO 8601 date and time with a zone (<c>Z</c> or an offset), in the round-trip shapes, and nothing looser.</summary>
    internal static bool TryGetTime(HttpContext http, QueryParameter parameter, out DateTimeOffset? value)
    {
        value = null;
        var text = parameter.Read(http).ToString();
        if (text.Length == 0)
        {
            return true;
        }

        if (DateTimeOffset.TryParseExact(text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && text.Contains('T', StringComparison.Ordinal))
        {
            value = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }
}
