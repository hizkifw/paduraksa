using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The server settings (docs/design/config.md "Server settings"): how long data is kept, the sign-in policy and the like. They are
/// <c>settings.server</c> in the config, so a change is a revision like any other (audited, in the history, undone by a rollback); these
/// endpoints read them with their values in force and replace them alone.
/// </summary>
internal static class ServerSettingsEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/server-settings", GetAsync).WithName("GetServerSettings").WithTags("Settings").WithSummary("The server settings")
            .WithDescription("Every server setting with its default, its bounds, what the config stores, the value in force and where it comes from: "
                + "the server's environment (an explicit Paduraksa__... variable or appsettings key, which wins), the config, or the default. "
                + "A setting that applies after a restart also names the value the process runs with. The ETag is the global settings' version.")
            .Produces<ServerSettingsView>().RequirePermission(Permission.ConfigRead);
        api.MapPut("/server-settings", PutAsync).WithName("PutServerSettings").WithTags("Settings").WithSummary("Replace the server settings")
            .WithDescription("The body is what the config stores: a member left out or null goes back to its default. Values are held to each setting's bounds (422, "
                + "server.setting.invalid). A value the environment sets is stored but not used while the environment sets it. Needs server:write besides "
                + "config:write, and If-Match with the global settings' version (428 without it, 412 when stale).")
            .Accepts<ServerSettings>("application/json").Produces<ConfigCommandResult>().ProducesApiProblems(400, 412, 415, 422, 428).MayFailToPublish()
            .RequirePermission(Permission.ServerWrite);
    }

    private static async Task<IResult> GetAsync(HttpContext http, ConfigService config, ServerSettingsState state, CancellationToken ct)
    {
        var settings = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Settings;
        http.Response.Headers.ETag = Preconditions.VersionTag(settings.Version);
        return EndpointSupport.Json(ServerSettingsViews.Of(state, settings), AdminJsonContext.Default.ServerSettingsView);
    }

    private static async Task<IResult> PutAsync(HttpContext http, AdminCaller caller, ConfigService config, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem)
            || !Preconditions.TryGetForce(http, out var force, out problem)
            || !Preconditions.TryGetVersion(http, out var version, out problem))
        {
            return problem!;
        }

        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.ServerSettings).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        // The rest of the global settings as they are now; the pipeline's version check (If-Match) is what keeps this from overwriting a change
        // made since the caller read them.
        var current = (await config.GetCurrentAsync(ct).ConfigureAwait(false)).Document.Settings;
        var message = EndpointSupport.MessageOf(http);
        UpdateSettingsCommand command = new(current with { Version = version, Server = body.Value })
        {
            DryRun = dryRun,
            Force = force,
            Message = message,
        };
        var result = await config.ExecuteAsync(command, caller.Origin, ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, MutationKind.Change, null);
    }
}
