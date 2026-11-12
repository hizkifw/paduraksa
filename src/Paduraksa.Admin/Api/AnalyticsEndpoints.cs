using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Analytics;
using Paduraksa.Contracts.Analytics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The traffic analytics the dashboard's Analytics page draws. A read of the rollup table and the drain's open buckets, behind
/// <c>metrics:read</c> like the metrics overview: the figures are aggregates with no client address in them.
/// </summary>
internal static class AnalyticsEndpoints
{
    /// <summary>The <c>host</c> value that selects the requests no host owns.</summary>
    public const string UnmatchedHost = "unmatched";

    public static void Map(RouteGroupBuilder api)
    {
        var analytics = api.MapGroup("/analytics").WithTags("Analytics");

        analytics.MapGet("/", Overview)
            .WithName("GetAnalytics").WithSummary("Read the traffic analytics")
            .WithDescription("Visitors and content, and clients and bots, over ?range=hour (the default), day, week or month, for every host or for one: ?host=host_... (a host's ID, which may have been deleted since) or ?host=unmatched (the requests no host owns). "
                + "Unique visitors, the top paths, referers and user agents, the slowest paths and the largest responses are approximate (sketches with a stated bound: each row carries its own error); the counts, the status classes and the distributions of agents are exact up to their table sizes. "
                + "Computed from the access-log drain, so a host with its access log off is absent. The chart has every step from the first the server observed to the last; a step with coveredSeconds 0 is a time the server was not running, a gap, and not a zero.")
            .Produces<AnalyticsOverview>().ProducesApiProblems(400)
            .RequirePermission(Permission.MetricsRead);
    }

    private static async Task<IResult> Overview(HttpContext http, AnalyticsService service)
    {
        var rangeText = http.Request.Query["range"].ToString();
        MetricsRange range;
        if (rangeText.Length == 0)
        {
            range = MetricsRange.Hour;
        }
        else if (!TryParseRange(rangeText, out range))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "range must be hour, day, week or month.");
        }

        var hostText = http.Request.Query["host"].ToString();
        HostId? host = null;
        var unmatched = false;
        if (hostText == UnmatchedHost)
        {
            unmatched = true;
        }
        else if (hostText.Length > 0)
        {
            if (!HostId.TryParse(hostText, null, out var parsed))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "host must be a host ID (host_...) or unmatched.");
            }

            host = parsed;
        }

        // A rollup table that can't be read is an exception the API pipeline answers as a 500 problem carrying a trace ID.
        return EndpointSupport.Json(await service.ReadOverviewAsync(range, host, unmatched, http.RequestAborted).ConfigureAwait(false), AdminJsonContext.Default.AnalyticsOverview);
    }

    private static bool TryParseRange(string text, out MetricsRange range)
    {
        switch (text)
        {
            case "hour":
                range = MetricsRange.Hour;
                return true;
            case "day":
                range = MetricsRange.Day;
                return true;
            case "week":
                range = MetricsRange.Week;
                return true;
            case "month":
                range = MetricsRange.Month;
                return true;
            default:
                range = default;
                return false;
        }
    }
}
