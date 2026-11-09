using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Simulation;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>"Why was this blocked?" (PLAN §7.7): what a request would get, and why, against the live config, a revision or a draft.</summary>
internal static class SimulateEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPost("/simulate", SimulateAsync).WithName("Simulate").WithTags("Simulator").WithSummary("Simulate a request against the live config, a revision or a draft")
            .WithDescription("target is {kind: live} (the default), {kind: revision, revision}, {kind: draftDocument, document} or {kind: draftCommand, command}; a draft is validated and compiled as a real change would be, and never stored or published. Nothing here changes any counter or state.")
            .Accepts<SimulateBody>("application/json").Produces<SimulationResult>().ProducesApiProblems(400, 404, 409, 415, 422, 503)
            .WithAdminBodyLimit(ApiEndpointConventions.SimulateBodyLimit)
            .RequirePermission(Permission.Simulate);

    private static async Task<IResult> SimulateAsync(HttpContext http, AdminCaller caller, RequestSimulator simulator, CancellationToken ct)
    {
        BodyResult<SimulateBody> body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.SimulateBody).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        SimulationResult result = await simulator.SimulateAsync(body.Value!.Target ?? SimulationTarget.Live, body.Value.Request, caller.Origin, ct).ConfigureAwait(false);
        int status = ApiStatusMapping.Of(result.Status);
        if (ApiStatusMapping.IsSuccess(status))
        {
            return EndpointSupport.Json(result, AdminJsonContext.Default.SimulationResult, status);
        }

        string detail = result.Status switch
        {
            SimulationStatus.BadRequest => "The request to simulate is malformed; see errors.",
            SimulationStatus.Invalid => "The draft doesn't validate or compile, so there is no config to simulate against; see errors.",
            SimulationStatus.RevisionNotFound => "There is no such revision.",
            SimulationStatus.DraftRejected => "The draft command can't be applied to the current config as it stands. " + string.Join(' ', result.Notes),
            SimulationStatus.Denied => "You may not simulate against this target.",
            _ => "The simulator can't read the config store now.",
        };
        return ApiProblems.Problem(status, detail.TrimEnd(), null, result.Errors, result.RelatedErrors, result.Warnings);
    }
}
