using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Security;
using Paduraksa.Contracts.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>What protects the hosts and whether it is working. Read-only, like the host list, so it needs <c>config:read</c>.</summary>
internal static class SecurityEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var security = api.MapGroup("/security").WithTags("Security");

        security.MapGet("/overview", Overview)
            .WithName("GetSecurityOverview").WithSummary("Read the security overview")
            .WithDescription("Per-process counters of the proof-of-work gate and the rate limiter since Since, and each enabled host's protections as the live config compiled them. "
                + "Counters only grow: sample twice and divide the difference by the elapsed time between sampledAt values for a rate.")
            .Produces<SecurityOverview>().ProducesApiProblems(500)
            .RequirePermission(Permission.ConfigRead);
    }

    private static IResult Overview(SecurityOverviewService service) =>
        EndpointSupport.Json(service.Read(), AdminJsonContext.Default.SecurityOverview);
}
