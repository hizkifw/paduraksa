using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Commands;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The config as YAML (docs/design/admin-surfaces.md "YAML import and export"): <c>GET /config/export</c> writes the latest revision, or the one in <c>?revision=</c>, and <c>POST /config/import</c> reads a YAML body into the
/// config in <c>merge</c> or <c>replace</c> mode as one revision, or, with <c>?dryRun=true</c>, only says what it would change. The import is a whole-config command, so it
/// answers like the others (<see cref="ConfigCommandResponses"/>), and its errors point into the YAML.
/// </summary>
internal static class ConfigTransferEndpoints
{
    /// <summary>The import body's limit: the YAML's own cap, which the import checks again on the bytes it reads.</summary>
    private const long ImportBodyLimit = 4 * 1024 * 1024 + 4096;

    // A session cookie may send only these: text/plain is a type a cross-site form can send, so it is for token callers (curl) and the cookie rule never lists it.
    private static readonly string[] YamlTypes = ["application/yaml", "application/x-yaml", "text/yaml", "text/x-yaml"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/config/export", ExportAsync)
            .WithName("ExportConfig").WithTags("Config").WithSummary("The config as YAML")
            .WithDescription("The latest revision's document in the YAML format of docs/design/admin-surfaces.md, YAML import and export, or the revision in ?revision= (an ID). The ETag is the revision exported. It holds no secret: an access list's users are a username and a credentialId, never a password. Importing it in replace mode on a fresh instance gives the same revision hash.")
            .Produces<string>(200, "application/yaml").ProducesApiProblems(400, 404)
            .RequirePermission(Permission.ConfigRead);

        api.MapPost("/config/import", ImportAsync)
            .WithName("ImportConfig").WithTags("Config").WithSummary("Import a YAML config")
            .WithDescription("The body is YAML (application/yaml), at most 4 MiB. ?mode=merge (the default) adds or updates the objects it names and deletes nothing; ?mode=replace makes the config the YAML, deleting what it leaves out. "
                + "Objects are matched by id, else by name (a host by its first host name). ?dryRun=true runs every check and returns the changes and warnings without committing. If-Match, when sent, is the revision the YAML was prepared against (412 when it is no longer the latest). "
                + "A user's credentialId must be one the vault holds a password under (422 otherwise, in a dry run too). Errors (422) point into the YAML, with its line.")
            .Accepts<string>(YamlTypes[0], [.. YamlTypes[1..], "text/plain"]).Produces<ConfigCommandResult>().ProducesApiProblems(400, 403, 409, 412, 413, 415, 422)
            .MayFailToPublish()
            .WithAdminBodyLimit(ImportBodyLimit).AcceptsAdminContentTypes(YamlTypes)
            .RequirePermission(Permission.ConfigWrite);
    }

    private static async Task<IResult> ExportAsync(HttpContext http, ConfigImportService import, CancellationToken ct)
    {
        ConfigRevisionId? revision = null;
        if (http.Request.Query["revision"] is { Count: > 0 } values)
        {
            if (values.Count != 1 || !ConfigRevisionId.TryParse(values[0], null, out var parsed))
            {
                return ApiProblems.Problem(StatusCodes.Status400BadRequest, "revision must be one revision ID (rev_...).");
            }

            revision = parsed;
        }

        var export = await import.ExportAsync(revision, ct).ConfigureAwait(false);
        if (export is null)
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such revision.");
        }

        if (export.Revision is { } exported)
        {
            http.Response.Headers.ETag = Preconditions.RevisionTag(exported.Id);
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.Text(export.Yaml, "application/yaml; charset=utf-8", Encoding.UTF8);
    }

    private static async Task<IResult> ImportAsync(HttpContext http, AdminCaller caller, ConfigImportService import, CancellationToken ct)
    {
        if (!Preconditions.TryGetDryRun(http, out var dryRun, out var problem) || !Preconditions.TryGetForce(http, out var force, out problem))
        {
            return problem!;
        }

        var mode = ConfigImportMode.Merge;
        if (http.Request.Query["mode"] is { Count: > 0 } modes
            && (modes.Count != 1 || !TryParseMode(modes[0], out mode)))
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, "mode must be 'merge' or 'replace'.");
        }

        ConfigRevisionId? baseRevision = null;
        if (http.Request.Headers.IfMatch.Count > 0)
        {
            if (!Preconditions.TryGetRevision(http, out ConfigRevisionId pinned, out problem))
            {
                return problem!;
            }

            baseRevision = pinned;
        }

        if (!IsYaml(http.Request.ContentType))
        {
            return ApiProblems.Problem(StatusCodes.Status415UnsupportedMediaType, "Send the body as application/yaml (UTF-8).");
        }

        var body = await ReadBoundedAsync(http.Request, ConfigImportService.MaxBytes, ct).ConfigureAwait(false);
        if (body is null)
        {
            return ApiProblems.Problem(StatusCodes.Status413PayloadTooLarge, $"The YAML is larger than {ConfigImportService.MaxBytes} bytes.");
        }

        var result = await import.ImportAsync(body, mode, caller.Origin, new ConfigImportOptions(dryRun, baseRevision, force, EndpointSupport.MessageOf(http)), ct).ConfigureAwait(false);
        return ConfigCommandResponses.ToResult(http, result, MutationKind.Change);
    }

    private static bool TryParseMode(string? text, out ConfigImportMode mode)
    {
        mode = ConfigImportMode.Merge;
        switch (text)
        {
            case "merge":
                return true;
            case "replace":
                mode = ConfigImportMode.Replace;
                return true;
            default:
                return false;
        }
    }

    private static bool IsYaml(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && (YamlTypes.Contains(parsed.MediaType, StringComparer.OrdinalIgnoreCase) || string.Equals(parsed.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase))
        && (parsed.CharSet is null || string.Equals(parsed.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));

    // At most limit bytes; null when the body has more. The server's body limit already stops a long body, this is the answer a client gets instead of a reset.
    private static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using MemoryStream buffer = new();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
