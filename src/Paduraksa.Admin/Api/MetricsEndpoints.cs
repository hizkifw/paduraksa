using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Metrics;
using Paduraksa.Contracts.Metrics;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The request counters: the Prometheus scrape and the overview the dashboard draws. Both are reads of memory and the rollup table
/// and need <c>metrics:read</c>, which a token can carry as a scope, so a scraper authenticates with <c>Authorization: Bearer pdk_...</c>
/// like any API client.
/// </summary>
internal static class MetricsEndpoints
{
    /// <summary>The scrape's path in the API; <c>/metrics</c> is answered by rewriting to it (see <see cref="ApiPipelineMiddleware"/>).</summary>
    public const string ScrapePath = "/api/v1/metrics";

    private static readonly QueryParameter Range = QueryParameter.Choice("range", "How far back to look: hour is from memory and the last hour of rollups, the rest from the one-minute rollups.", ["hour", "day", "week", "month"], "hour");

    public static void Map(RouteGroupBuilder api)
    {
        var metrics = api.MapGroup("/metrics").WithTags("Metrics");

        metrics.MapGet("/", Scrape)
            .WithName("ScrapeMetrics").WithSummary("Scrape the metrics (Prometheus text format)")
            .WithDescription("Request counters and latency histograms by host, route, upstream and destination, and the gate's process totals, in the Prometheus text exposition format 0.0.4. "
                + "Also served at /metrics. Counters only grow, and a host keeps its series across config changes: its labels are its name and its typed ID.")
            .Produces(StatusCodes.Status200OK, contentType: MetricsService.PrometheusContentType).ProducesApiProblems(401, 403)
            .RequirePermission(Permission.MetricsRead);

        metrics.MapGet("/overview", Overview)
            .WithName("GetMetricsOverview").WithSummary("Read the metrics overview")
            .WithDescription("What the dashboard's overview draws: the pace over the last half minute, a chart, the busiest hosts and the upstreams' errors over the range. "
                + "The chart has every step from the first the server observed to the last; a step with coveredSeconds 0 is a time the server was not running, a gap, and not a zero.")
            .WithQuery(Range).Produces<MetricsOverview>().ProducesApiProblems(400)
            .RequirePermission(Permission.MetricsRead);
    }

    private static IResult Scrape(HttpContext http, MetricsService service)
    {
        // A scrape is a live reading: a cache that kept one would hand a later scraper stale counters.
        http.Response.Headers.CacheControl = "no-store";
        return Results.Text(service.PrometheusText(), MetricsService.PrometheusContentType);
    }

    private static async Task<IResult> Overview(HttpContext http, MetricsService service)
    {
        var text = Range.Read(http).ToString();
        MetricsRange range;
        if (text.Length == 0)
        {
            range = MetricsRange.Hour;
        }
        else if (!TryParseRange(text, out range))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "range must be hour, day, week or month.");
        }

        // A rollup table that can't be read is an exception the API pipeline answers as a 500 problem carrying a trace ID.
        return EndpointSupport.Json(await service.ReadOverviewAsync(range, http.RequestAborted).ConfigureAwait(false), AdminJsonContext.Default.MetricsOverview);
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
