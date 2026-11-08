using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Admin.Api;

namespace Paduraksa.Admin.Auth;

/// <summary>Endpoint metadata: content types, besides JSON, a cookie-authenticated request may send a body in (a PEM upload, a YAML import).</summary>
/// <param name="ContentTypes">Media types, e.g. <c>application/x-pem-file</c>.</param>
public sealed record AdminContentTypesMetadata(IReadOnlyList<string> ContentTypes);

/// <summary>Endpoint metadata: an endpoint outside <c>/api</c> (a dashboard form) that gets the same cross-site check as the API's unsafe methods.</summary>
public sealed class AdminSameOriginMetadata
{
    public static AdminSameOriginMetadata Instance { get; } = new();

    private AdminSameOriginMetadata()
    {
    }
}

/// <summary>Content types an endpoint takes from the dashboard, and the cross-site check for endpoints outside <c>/api</c>.</summary>
public static class AdminCsrfExtensions
{
    /// <summary>Puts an endpoint outside <c>/api</c> (a form post) under <see cref="AdminCsrfMiddleware"/>'s check for unsafe methods.</summary>
    public static TBuilder RequireSameOrigin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(AdminSameOriginMetadata.Instance);
        return builder;
    }

    /// <summary>Lets cookie-authenticated requests to the endpoints send these media types as well as JSON (see <see cref="AdminCsrfMiddleware"/>).</summary>
    public static TBuilder AcceptsAdminContentTypes<TBuilder>(this TBuilder builder, params string[] contentTypes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(contentTypes);
        builder.WithMetadata(new AdminContentTypesMetadata(contentTypes));
        return builder;
    }
}

/// <summary>
/// Cross-site request forgery defence for the session cookie (PLAN §3.1, §10.1), on top of <c>SameSite=Strict</c>. For an unsafe
/// method on <c>/api/</c>:
/// <list type="bullet">
/// <item>A bearer-token request is exempt: a browser never adds an <c>Authorization</c> header by itself.</item>
/// <item>A browser that sends <c>Sec-Fetch-Site</c> must say <c>same-origin</c>; one that sends only <c>Origin</c> must send the request's own
/// <c>scheme://host:port</c> (<c>null</c> never matches). Anything else is a 403.</item>
/// <item>A request with neither header comes from something that is not a browser: it is allowed when it carries no session cookie
/// (sign-in from a script), and refused when it does.</item>
/// <item>A request that carries the session cookie and a body must say <c>application/json</c> (or <c>+json</c>), or a content type the
/// endpoint listed with <see cref="AdminCsrfExtensions.AcceptsAdminContentTypes"/>: a cross-site form cannot send JSON without a
/// preflight.</item>
/// </list>
/// The same applies to an unsafe method on an endpoint marked <see cref="AdminCsrfExtensions.RequireSameOrigin"/> (the dashboard's sign-in
/// forms, which also carry an antiforgery token), and to every request, whatever its method, under
/// <see cref="AdminPipelineHooks.SameOriginPathPrefixes"/> (the circuit hub, whose WebSocket handshake is a <c>GET</c>; the body rule does not
/// apply there).
/// </summary>
internal sealed class AdminCsrfMiddleware(RequestDelegate next, AdminPipelineHooks hooks)
{
    public async Task InvokeAsync(HttpContext context)
    {
        HttpRequest request = context.Request;
        bool everyMethod = hooks.SameOriginPathPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
        bool covered = everyMethod
            || (!IsSafe(request.Method)
                && request.Headers.Authorization.Count == 0
                && (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                    || context.GetEndpoint()?.Metadata.GetMetadata<AdminSameOriginMetadata>() is not null));
        if (!covered)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        string? refusal = Check(context, checkBody: !everyMethod);
        if (refusal is not null)
        {
            await AdminProblems.Forbidden("Cross-site request refused", refusal, ApiCodes.Csrf.Refused).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    private static string? Check(HttpContext context, bool checkBody)
    {
        HttpRequest request = context.Request;
        bool hasCookie = AdminSessionCookie.Read(request).Present;
        string? site = request.Headers["Sec-Fetch-Site"].FirstOrDefault();
        string? origin = request.Headers.Origin.FirstOrDefault();

        if (!string.IsNullOrEmpty(site))
        {
            if (!string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase))
            {
                return "Requests must come from the same origin.";
            }
        }
        else if (!string.IsNullOrEmpty(origin))
        {
            if (!OriginMatches(origin, request))
            {
                return "The Origin header does not match this server.";
            }
        }
        else if (hasCookie)
        {
            return "A request with a session cookie must say where it comes from (Sec-Fetch-Site or Origin).";
        }

        if (checkBody && hasCookie && HasBody(request) && !HasAllowedContentType(context))
        {
            return "A request with a body must be application/json.";
        }

        return null;
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    /// <summary>Whether a body can follow: the server knows (HTTP/2 streams carry no Content-Length), else the headers say.</summary>
    private static bool HasBody(HttpRequest request) =>
        request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
        ?? (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"));

    private static bool HasAllowedContentType(HttpContext context)
    {
        if (!Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(context.Request.ContentType, out Microsoft.Net.Http.Headers.MediaTypeHeaderValue? type))
        {
            return false;
        }

        if (type.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        IReadOnlyList<string>? extra = context.GetEndpoint()?.Metadata.GetMetadata<AdminContentTypesMetadata>()?.ContentTypes;
        return extra is not null && extra.Any(t => type.MediaType.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    private static bool OriginMatches(string origin, HttpRequest request)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) || !request.Host.HasValue)
        {
            return false;
        }

        int defaultPort = request.IsHttps ? 443 : 80;
        int port = request.Host.Port ?? defaultPort;
        return string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == port;
    }
}
