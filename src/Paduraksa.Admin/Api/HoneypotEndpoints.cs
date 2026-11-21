using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Honeypot;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Honeypot;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// What visitors did in the honeypot (docs/design/waf.md "Honeypot", "Recording"): a summary, a paged list of sessions, one session and a paged list of its requests. Behind
/// <c>metrics:read</c> like the rest of the analytics, though unlike them these hold client addresses in full.
/// </summary>
internal static class HoneypotEndpoints
{
    private static readonly QueryParameter Range = QueryParameter.Choice("range", "How far back to look: sessions whose latest request is within it.", ["hour", "day", "week", "month"], "hour");
    private static readonly QueryParameter Host = QueryParameter.Text(
        "host", "Only this host's honeypot: its ID (host_..., which may have been deleted since). All hosts if absent.");
    private static readonly QueryParameter Query = QueryParameter.Text(
        "q", "Only sessions whose client address starts with this text (an address typed in full also matches its other spellings), or whose user agent or canary phrase contains it, ignoring case.", HoneypotService.MaxQueryLength);

    private static IResult NoSuchSession => ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such honeypot session. It may be past retention.");

    public static void Map(RouteGroupBuilder api)
    {
        var honeypot = api.MapGroup("/honeypot").WithTags("Honeypot");

        honeypot.MapGet("/summary", SummaryAsync)
            .WithName("GetHoneypotSummary").WithSummary("Read what visitors did in the honeypot")
            .WithDescription("Sessions, requests and guessed URLs over a range, the behaviour matrix (how many sessions followed each kind of link, ran the page's script, fetched images, were shown, solved or passed the proof-of-work challenge, and read robots.txt first), and the user agents with the most requests, each with its bot class. "
                + "The counts are exact. Shed and dropped are process counters since the server started, not per range. Sessions are written a few seconds after their requests.")
            .WithQuery(Range, Host).Produces<HoneypotSummary>().ProducesApiProblems(400)
            .RequirePermission(Permission.MetricsRead);

        honeypot.MapGet("/sessions", ListAsync)
            .WithName("ListHoneypotSessions").WithSummary("List honeypot sessions")
            .WithDescription("Newest first. A session is one visitor (host, client address in full and user agent) from its first request until 30 idle minutes pass. Every filter is optional, and a session must match all that are set.")
            .WithQuery(Range, Host, Query).WithPage().Produces<Page<HoneypotSession>>().ProducesApiProblems(400)
            .RequirePermission(Permission.MetricsRead);

        honeypot.MapGet("/sessions/{id:HoneypotSessionId}", GetAsync)
            .WithName("GetHoneypotSession").WithSummary("Get a honeypot session")
            .WithDescription("The session's counters. Its requests are at /sessions/{id}/requests.")
            .Produces<HoneypotSession>().ProducesApiProblems(404)
            .RequirePermission(Permission.MetricsRead);

        honeypot.MapGet("/sessions/{id:HoneypotSessionId}/requests", ListRequestsAsync)
            .WithName("ListHoneypotSessionRequests").WithSummary("List a honeypot session's requests")
            .WithDescription("Oldest first. The tables keep 30 days and the newest 1 000 000 requests, so a long session may have lost its earliest requests.")
            .WithPage().Produces<Page<HoneypotRequest>>().ProducesApiProblems(400, 404)
            .RequirePermission(Permission.MetricsRead);
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, HoneypotService service)
    {
        if (!MetricsEndpoints.TryGetRange(http, Range, out var range, out var problem) || !TryGetHost(http, out var host, out problem))
        {
            return problem!;
        }

        return EndpointSupport.Json(await service.ReadSummaryAsync(range, host, http.RequestAborted).ConfigureAwait(false), AdminJsonContext.Default.HoneypotSummary);
    }

    private static async Task<IResult> ListAsync(HttpContext http, HoneypotService service)
    {
        if (!MetricsEndpoints.TryGetRange(http, Range, out var range, out var problem)
            || !TryGetHost(http, out var host, out problem)
            || !Pagination.TryGetKeyset(http, "The cursor is not one this API issued for the honeypot's sessions.", out var limit, out HoneypotSessionId? after, out problem))
        {
            return problem!;
        }

        var query = Query.Read(http).ToString().Trim();
        if (query.Length > HoneypotService.MaxQueryLength)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, $"q is at most {HoneypotService.MaxQueryLength} characters.");
        }

        var page = await service.ListSessionsAsync(new HoneypotSessionFilter(range, host, query.Length == 0 ? null : query), limit, after, http.RequestAborted).ConfigureAwait(false);
        return EndpointSupport.Json(
            new Page<HoneypotSession>(page.Items, page.Next is { } next ? Pagination.Encode(next) : null),
            AdminJsonContext.Default.PageHoneypotSession);
    }

    private static async Task<IResult> GetAsync(HoneypotSessionId id, HoneypotService service, HttpContext http) =>
        await service.ReadSessionAsync(id, http.RequestAborted).ConfigureAwait(false) is { } session
            ? EndpointSupport.Json(session, AdminJsonContext.Default.HoneypotSession)
            : NoSuchSession;

    private static async Task<IResult> ListRequestsAsync(HttpContext http, HoneypotSessionId id, HoneypotService service)
    {
        if (!Pagination.TryGetKeyset(http, "The cursor is not one this API issued for a honeypot session's requests.", out var limit, out HoneypotRequestId? after, out var problem))
        {
            return problem!;
        }

        return await service.ListRequestsAsync(id, limit, after, http.RequestAborted).ConfigureAwait(false) is { } page
            ? EndpointSupport.Json(new Page<HoneypotRequest>(page.Items, page.Next is { } next ? Pagination.Encode(next) : null), AdminJsonContext.Default.PageHoneypotRequest)
            : NoSuchSession;
    }

    private static bool TryGetHost(HttpContext http, out HostId? host, out IResult? problem)
    {
        host = null;
        problem = null;
        var text = Host.Read(http).ToString();
        if (text.Length == 0)
        {
            return true;
        }

        if (!HostId.TryParse(text, null, out var parsed))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "host must be a host ID (host_...).");
            return false;
        }

        host = parsed;
        return true;
    }
}
