using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Security;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>What protects the hosts and whether it is working, over a range. Read-only, like the host list, so it needs <c>config:read</c>.</summary>
internal static class SecurityEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var security = api.MapGroup("/security").WithTags("Security");

        security.MapGet("/overview", Overview)
            .WithName("GetSecurityOverview").WithSummary("Read the security overview")
            .WithDescription("The gate's counters over the range (proof of work, rate limits, access lists, the connection stage, sign-in), what it refused by reason and over time, "
                + "and each enabled host's protections as the live config compiled them, with what it refused over the range. The counters are kept in the metrics' rollups, so a restart doesn't reset them; "
                + "from is the first moment of the range the server observed. A step of the series with coveredSeconds 0 is a time the server was not running, not a step with no refusals.")
            .WithQuery(Range).Produces<SecurityOverview>().ProducesApiProblems(400, 500)
            .RequirePermission(Permission.ConfigRead);
    }

    private static readonly QueryParameter Range = QueryParameter.Choice("range", "How far back to count: hour is from memory and the last hour of rollups, the rest from the one-minute rollups.", ["hour", "day", "week", "month"], "hour");

    private static async Task<IResult> Overview(HttpContext http, SecurityOverviewService service)
    {
        var text = Range.Read(http).ToString();
        MetricsRange range;
        if (text.Length == 0)
        {
            range = MetricsRange.Hour;
        }
        else if (!MetricsEndpoints.TryParseRange(text, out range))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "range must be hour, day, week or month.");
        }

        return EndpointSupport.Json(await service.ReadAsync(range, http.RequestAborted).ConfigureAwait(false), AdminJsonContext.Default.SecurityOverview);
    }
}
