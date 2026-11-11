using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Contracts.Api;
using Paduraksa.Core.Ids;

namespace Paduraksa.Admin.Api;

/// <summary>Endpoint metadata: the endpoint's change can be stored and fail to publish (see <see cref="ApiEndpointConventions.MayFailToPublish"/>).</summary>
public sealed class PublishFailureMetadata
{
    public static PublishFailureMetadata Instance { get; } = new();

    private PublishFailureMetadata()
    {
    }
}

public static class ApiEndpointConventions
{
    /// <summary>The most a certificate upload request may be: the vault reads at most 1 MiB of key and chain, the rest is multipart framing.</summary>
    public const long UploadBodyLimit = 1024 * 1024 + 64 * 1024;

    /// <summary>The most a simulate request may be: a draft document can be a whole config.</summary>
    public const long SimulateBodyLimit = 1024 * 1024;

    /// <summary>The most an error page preview may be: a 64 KiB document, which JSON may escape to six bytes a character.</summary>
    public const long ErrorPagePreviewBodyLimit = 512 * 1024;

    /// <summary>Marks an endpoint whose change can be stored and still fail to go live: 500, code <c>config.publishFailed</c> (documented in the API description).</summary>
    public static TBuilder MayFailToPublish<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(PublishFailureMetadata.Instance);
        return builder;
    }

    /// <summary>Documents the problem answers an endpoint can give (<see cref="ApiProblem"/> bodies).</summary>
    public static RouteHandlerBuilder ProducesApiProblems(this RouteHandlerBuilder builder, params int[] statuses)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var status in statuses)
        {
            builder.Produces<ApiProblem>(status, ApiProblems.ContentType);
        }

        return builder;
    }
}

/// <summary>Reading a JSON request body: JSON only, strict, and every failure a problem.</summary>
internal static class ApiBody
{
    /// <summary>The body as <typeparamref name="T"/>, or the problem to answer with.</summary>
    public static async Task<BodyResult<T>> ReadJsonAsync<T>(HttpContext http, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (!IsJson(http.Request.ContentType))
        {
            return new(null, ApiProblems.Problem(StatusCodes.Status415UnsupportedMediaType, "Send the body as application/json (UTF-8)."));
        }

        try
        {
            var value = await JsonSerializer.DeserializeAsync(http.Request.Body, typeInfo, http.RequestAborted).ConfigureAwait(false);
            return value is null
                ? new(null, ApiProblems.Problem(StatusCodes.Status400BadRequest, "The request body is required."))
                : new(value, null);
        }
        catch (JsonException ex) when (ex.Message.Contains("'ipAllowList'", StringComparison.Ordinal))
        {
            // The one unknown member worth its own sentence: it was a setting, and the way to say it now is different.
            return new(null, ApiProblems.Problem(StatusCodes.Status400BadRequest, "The request body sets 'ipAllowList', which access lists replaced: create an access list (POST /api/v1/accessLists) and name it from the 'accessList' setting of the host, route, profile or global defaults."));
        }
        catch (JsonException ex) when (ex.Message.Contains("'passwordHash'", StringComparison.Ordinal))
        {
            // The other: a verifier can't be sent any more, because the config document no longer holds one.
            return new(null, ApiProblems.Problem(StatusCodes.Status400BadRequest, "The request body sets 'passwordHash', which no longer exists: a user's password lives in the vault. Send the plain 'password' (the vault hashes it), or leave it out to keep the password the user has."));
        }
        catch (JsonException ex)
        {
            // A fixed sentence and the path of the member: the serializer's own message names CLR types.
            return new(null, ApiProblems.Problem(StatusCodes.Status400BadRequest, "The request body is not valid: it is not well-formed JSON, or a member is missing, unknown, repeated, null or of the wrong type" + PathOf(ex) + "."));
        }
        catch (NotSupportedException)
        {
            // A polymorphic member (a simulation target, a config command) without its discriminator, or with one that names no known kind.
            return new(null, ApiProblems.Problem(StatusCodes.Status400BadRequest, "The request body is not valid: a member that takes one of several kinds ('kind' of a target, 'command' of a command) lacks it or names an unknown kind."));
        }
    }

    /// <summary>True for <c>application/json</c> with no charset or UTF-8.</summary>
    public static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        && (parsed.CharSet is null || string.Equals(parsed.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase));

    private static string PathOf(JsonException ex) => ex.Path is { Length: > 0 } path && path != "$" ? $" (at {path})" : string.Empty;
}

/// <summary>A parsed body, or the problem that rejected it.</summary>
internal readonly record struct BodyResult<T>(T? Value, IResult? Problem)
    where T : class
{
    public bool Ok => Problem is null;
}

/// <summary>
/// Optimistic concurrency: a read carries an <c>ETag</c> (an object's version, or the latest revision for the whole config),
/// and a change must send it back in <c>If-Match</c>. A missing header is 428 (a client that doesn't say what it read could
/// overwrite what it never saw), a stale one 412.
/// </summary>
internal static class Preconditions
{
    /// <summary>An entity tag as the header carries it: quoted.</summary>
    public static string Quote(string tag) => $"\"{tag}\"";

    /// <summary>The <c>ETag</c> of an object at <paramref name="version"/>.</summary>
    public static string VersionTag(long version) => Quote(version.ToString(CultureInfo.InvariantCulture));

    /// <summary>The <c>ETag</c> of the config at a revision.</summary>
    public static string RevisionTag<TId>(TId revision)
        where TId : struct, ITypedId<TId> => Quote(revision.ToString() ?? string.Empty);

    /// <summary>Reads the version a change is based on from <c>If-Match</c>.</summary>
    public static bool TryGetVersion(HttpContext http, out long version, out IResult? problem)
    {
        version = 0;
        if (!TryGetTag(http, out var tag, out problem))
        {
            return false;
        }

        if (tag.Length > 1 && tag[0] == '0' || !long.TryParse(tag, NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "If-Match must be the version you read, as in the ETag of the object (for example \"3\").");
            return false;
        }

        return true;
    }

    /// <summary>Reads the revision a change is based on from <c>If-Match</c>.</summary>
    public static bool TryGetRevision<TId>(HttpContext http, out TId revision, out IResult? problem)
        where TId : struct, ITypedId<TId>
    {
        revision = default;
        if (!TryGetTag(http, out var tag, out problem))
        {
            return false;
        }

        if (!TId.TryParse(tag, null, out revision))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "If-Match must be the latest revision you read, as in the ETag of the config (for example \"rev_...\").");
            return false;
        }

        return true;
    }

    /// <summary>The single entity tag of <c>If-Match</c>: 428 without the header, 400 for a list, <c>*</c> or a weak tag.</summary>
    public static bool TryGetTag(HttpContext http, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? tag, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        var (state, parsed) = ParseIfMatch(http.Request.Headers.IfMatch);
        tag = parsed;
        problem = state switch
        {
            IfMatchState.Missing => ApiProblems.Problem(StatusCodes.Status428PreconditionRequired, "This change needs an If-Match header with the ETag of what you read, so it can't overwrite a change you haven't seen."),
            IfMatchState.Unsupported => ApiProblems.Problem(StatusCodes.Status400BadRequest, "If-Match must be one quoted entity tag, as in the ETag you read (for example \"3\"): not '*', not a list, not unquoted."),
            IfMatchState.Weak => ApiProblems.Problem(StatusCodes.Status412PreconditionFailed, "A weak entity tag never matches: If-Match compares strongly (RFC 9110). Send the tag as the ETag gave it, without W/."),
            _ => null,
        };
        return state == IfMatchState.Tag;
    }

    /// <summary>Parses the header: absent, unusable, or the one tag (quotes removed).</summary>
    internal static (IfMatchState State, string? Tag) ParseIfMatch(StringValues values)
    {
        if (values.Count == 0 || values.Count == 1 && string.IsNullOrWhiteSpace(values[0]))
        {
            return (IfMatchState.Missing, null);
        }

        if (values.Count != 1)
        {
            return (IfMatchState.Unsupported, null);
        }

        var value = values[0]!.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            return (IfMatchState.Weak, null);
        }

        // One quoted tag: not '*', not a list, not an unquoted token (the tags this API issues are always quoted).
        // The longest tag this API issues is a quoted typed ID: a prefix of up to 4 characters, '_' and 22 base62 characters
        // (27, so 29 with its quotes); a version is at most 20 digits quoted. 66 accepts both with room to spare and keeps an
        // absurd header from being parsed at all.
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' || value.Length > 66)
        {
            return (IfMatchState.Unsupported, null);
        }

        var tag = value[1..^1];
        return tag.Contains('"', StringComparison.Ordinal) || tag.Contains(',', StringComparison.Ordinal)
            ? (IfMatchState.Unsupported, null)
            : (IfMatchState.Tag, tag);
    }

    /// <summary>Reads <c>?dryRun=</c> (<c>true</c> or <c>false</c>; absent is false).</summary>
    public static bool TryGetDryRun(HttpContext http, out bool dryRun, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        dryRun = false;
        problem = null;
        var values = http.Request.Query["dryRun"];
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count == 1 && bool.TryParse(values[0], out dryRun))
        {
            return true;
        }

        problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "dryRun must be 'true' or 'false'.");
        return false;
    }

    /// <summary>
    /// Reads <c>?force=</c> (<c>true</c> or <c>false</c>; absent is false): the explicit permission the admin host's lock-out
    /// guard needs for a change that would remove the caller's own way in.
    /// </summary>
    public static bool TryGetForce(HttpContext http, out bool force, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        force = false;
        problem = null;
        var values = http.Request.Query["force"];
        if (values.Count == 0)
        {
            return true;
        }

        if (values.Count == 1 && bool.TryParse(values[0], out force))
        {
            return true;
        }

        problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "force must be 'true' or 'false'.");
        return false;
    }
}

internal enum IfMatchState
{
    Missing,
    Unsupported,
    Weak,
    Tag,
}

/// <summary>Cursor pagination: <c>?cursor=&amp;limit=</c>, an opaque cursor over the ordered typed ID of the last item.</summary>
internal static class Pagination
{
    /// <summary>Items per page unless the caller asks otherwise.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The most items per page.</summary>
    public const int MaxLimit = 200;

    /// <summary>The cursor for the page after the one ending at <paramref name="last"/>.</summary>
    public static string Encode<TId>(TId last)
        where TId : struct, ITypedId<TId> =>
        Base64Url(Encoding.ASCII.GetBytes(last.ToString() ?? string.Empty));

    /// <summary>Decodes a cursor made by <see cref="Encode{TId}"/>; false for anything else.</summary>
    public static bool TryDecode<TId>(string? cursor, out TId id)
        where TId : struct, ITypedId<TId>
    {
        id = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 96)
        {
            return false;
        }

        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(cursor);
            return TId.TryParse(Encoding.ASCII.GetString(bytes), null, out id);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Reads <c>limit</c> (1 to <see cref="MaxLimit"/>) and <c>cursor</c>.</summary>
    public static bool TryGetPage(HttpContext http, out int limit, out string? cursor, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        limit = DefaultLimit;
        cursor = null;
        problem = null;
        var limits = http.Request.Query["limit"];
        if (limits.Count > 0 && !(limits.Count == 1 && int.TryParse(limits[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= MaxLimit))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, $"limit must be a whole number from 1 to {MaxLimit}.");
            return false;
        }

        var cursors = http.Request.Query["cursor"];
        if (cursors.Count > 1)
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "Give cursor once.");
            return false;
        }

        cursor = cursors.Count == 1 && !string.IsNullOrEmpty(cursors[0]) ? cursors[0] : null;
        return true;
    }

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
