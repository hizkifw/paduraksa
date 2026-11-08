using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// What every resource group of the API shares. A resource endpoint writes through <see cref="Json{T}"/> with its own
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo{T}"/> from <c>AdminJsonContext</c>, so the body is exactly that
/// contract; the identity endpoints answer through the host's JSON options instead (<c>IdentityJsonContext.AddTo</c>), because their
/// DTOs and the problem bodies the auth pipeline shares with them are registered there and no typed helper exists per endpoint.
/// </summary>
internal static class EndpointSupport
{
    /// <summary>The origin of the authenticated caller. Only valid behind <c>RequirePermission</c>, which has answered an anonymous request by now.</summary>
    public static CommandOrigin OriginOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.GetCaller()?.Origin ?? throw new InvalidOperationException("The endpoint was reached without an authenticated caller; it must require a permission.");
    }

    /// <summary>A JSON answer, written through the API's own serializer contract.</summary>
    public static IResult Json<T>(T value, JsonTypeInfo<T> typeInfo, int status = StatusCodes.Status200OK) =>
        Results.Json(value, typeInfo, statusCode: status);

    /// <summary>The path of a resource of the API, for <c>Location</c> and for <c>instance</c>.</summary>
    public static string Path<TId>(string collection, TId id)
        where TId : struct, Paduraksa.Core.Ids.ITypedId<TId> =>
        $"/api/v1/{collection}/{id}";

    /// <summary><paramref name="inner"/> with a <c>Location</c> header (when there is one), set as the result executes.</summary>
    public static IResult Located(IResult inner, string? location) => new LocatedResult(inner, location);

    /// <summary>The optional <c>?message=</c> note stored with a revision; the command pipeline judges it (at most 1000 characters).</summary>
    public static string? MessageOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Request.Query["message"].ToString() is { Length: > 0 } message ? message : null;
    }
}

internal sealed class LocatedResult(IResult inner, string? location) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (location is not null)
        {
            httpContext.Response.Headers.Location = location;
        }

        return inner.ExecuteAsync(httpContext);
    }
}

/// <summary>The answer to a config command: the result as the body on success, a problem carrying the pipeline's errors otherwise.</summary>
internal static class ConfigCommandResponses
{
    /// <summary>The answer to <paramref name="result"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="result">What the pipeline said.</param>
    /// <param name="kind">Whether the request creates or changes (decides what a version conflict is).</param>
    /// <param name="location">The new resource's path, for a 201.</param>
    /// <param name="created">A change that turned out to create its resource (a PUT of a new route): committed is 201.</param>
    /// <param name="noContent">A delete: committed is 204 (a dry run still answers with its plan).</param>
    public static IResult ToResult(HttpContext http, ConfigCommandResult result, MutationKind kind, string? location = null, bool created = false, bool noContent = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(result);
        int status = ApiStatusMapping.Of(result.Status, kind);
        if (result.Status == ConfigCommandStatus.PublishFailed)
        {
            if (result.Version is { } stored)
            {
                http.Response.Headers.ETag = Preconditions.VersionTag(stored);
            }

            return ApiProblems.Problem(
                status,
                "The change is stored (see revision) but could not go live: the previous config still serves, and nothing retries the publish until the next change or a restart. "
                    + "Use the new ETag (version) for the next change; repeating this request with the old If-Match is a 412.",
                errors: [.. (result.Publish?.Errors ?? []).Select(message => new ConfigError(string.Empty, ApiCodes.Config.PublishFailed, message))],
                warnings: result.Warnings,
                code: ApiCodes.Config.PublishFailed,
                revision: result.Revision,
                version: result.Version);
        }

        if (ApiStatusMapping.IsSuccess(status))
        {
            if (noContent && result.Status == ConfigCommandStatus.Committed)
            {
                return Results.NoContent();
            }

            if (created && status == StatusCodes.Status200OK && result.Status == ConfigCommandStatus.Committed)
            {
                status = StatusCodes.Status201Created;
            }

            if (result.Version is { } version && result.Status != ConfigCommandStatus.DryRun)
            {
                http.Response.Headers.ETag = Preconditions.VersionTag(version);
            }

            if (location is not null && status == StatusCodes.Status201Created)
            {
                http.Response.Headers.Location = location;
            }

            return EndpointSupport.Json(result, Json.AdminJsonContext.Default.ConfigCommandResult, status);
        }

        return ApiProblems.Problem(status, DetailOf(result.Status), null, result.Errors, result.RelatedErrors, result.Warnings, result.Conflict);
    }

    private static string DetailOf(ConfigCommandStatus status) => status switch
    {
        ConfigCommandStatus.Invalid => "The change is well-formed but can't be applied; see errors (and relatedErrors for what it breaks elsewhere).",
        ConfigCommandStatus.Conflict => "The config changed since you read it (or the object exists already); see conflict for what is current.",
        ConfigCommandStatus.NotFound => "There is no such object.",
        ConfigCommandStatus.Denied => "You may not change the config.",
        ConfigCommandStatus.Unavailable => "The config store is unavailable (the proxy serves its last-known-good config); see errors.",
        _ => "The change could not be applied.",
    };
}
