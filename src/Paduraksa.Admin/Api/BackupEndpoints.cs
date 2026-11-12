using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Backup;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The full backup: one passphrase-encrypted archive of the config and the vault. The Owner's alone (<c>backup:create</c>, which an API token
/// can carry as a scope), audited as <c>backup.create</c> with the actor and the size, never the passphrase. There is no restore endpoint:
/// a restore replaces the databases and needs the server stopped, so it is <c>paduraksa restore</c>.
/// </summary>
internal static class BackupEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapPost("/backup", CreateAsync).WithTags("Backup").WithName("CreateBackup").WithSummary("Download a full backup")
            .WithDescription("Streams one archive (application/octet-stream, .pdkbak) holding the config database, the vault database and the last-known-good config, all encrypted under the passphrase in the body (at least 12 characters) with AES-256-GCM and an Argon2id key. "
                + "The vault's keys in it are wrapped under a key made for this archive, so it restores on another machine with that machine's own master key, and it is useless without the passphrase, which nothing can recover. "
                + "The server keeps running and the databases are read consistently. Making one takes about a second of CPU and 64 MiB; one at a time (a second request while one runs gets 409). The passphrase goes in the JSON body over the admin's HTTPS, never in a URL.")
            .Accepts<Paduraksa.Contracts.Backup.BackupRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream").ProducesApiProblems(400, 409, 415, 422, 500)
            .RequirePermission(Permission.BackupCreate);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AdminCaller caller, BackupService service, CancellationToken ct)
    {
        var body = await ApiBody.ReadJsonAsync(http, AdminJsonContext.Default.BackupRequest).ConfigureAwait(false);
        if (!body.Ok)
        {
            return body.Problem!;
        }

        var outcome = await service.CreateDownloadAsync(caller.Origin, body.Value!.Passphrase, ct).ConfigureAwait(false);
        switch (outcome.Result.Status)
        {
            case BackupStatus.Created:
                var download = outcome.Download!;
                http.Response.OnCompleted(async () => await download.DisposeAsync().ConfigureAwait(false));
                http.Response.Headers.CacheControl = "no-store";
                http.Response.ContentLength = download.Length;
                return Results.Stream(download.Content, "application/octet-stream", download.FileName, enableRangeProcessing: false);
            case BackupStatus.Denied:
                return ApiProblems.Problem(StatusCodes.Status403Forbidden, outcome.Result.Problem, code: ApiCodes.Auth.PermissionRequired);
            case BackupStatus.Invalid:
                return ApiProblems.Problem(StatusCodes.Status422UnprocessableEntity, outcome.Result.Problem, code: ApiCodes.Backup.Passphrase);
            case BackupStatus.Busy:
                return ApiProblems.Problem(StatusCodes.Status409Conflict, outcome.Result.Problem, code: ApiCodes.Backup.Busy);
            default:
                return ApiProblems.Problem(StatusCodes.Status500InternalServerError, outcome.Result.Problem, code: ApiCodes.Backup.Failed);
        }
    }
}
