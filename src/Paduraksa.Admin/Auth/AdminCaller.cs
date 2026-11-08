using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// Who an admin request is from, as the admin app's authentication established it (a session cookie or an API
/// token). Set as a request feature by the authentication middleware and read by every endpoint; never taken from
/// anything the client sends in a body or query.
/// </summary>
/// <param name="Origin">The command origin: the actor, the <see cref="Channel.Api"/> or dashboard channel, the client IP.</param>
/// <param name="Session">The session the request authenticated with, if it used the session cookie.</param>
/// <param name="Token">The API token the request authenticated with, if it used a bearer token.</param>
public sealed record AdminCaller(CommandOrigin Origin, SessionId? Session = null, ApiTokenId? Token = null)
{
    /// <summary>The caller's user: the session's user, or the token's owner as recorded by authentication.</summary>
    public UserId? User { get; init; }
}

/// <summary>The request feature carrying the <see cref="AdminCaller"/>.</summary>
public interface IAdminCallerFeature
{
    AdminCaller Caller { get; }
}

/// <summary>The default <see cref="IAdminCallerFeature"/>.</summary>
public sealed class AdminCallerFeature(AdminCaller caller) : IAdminCallerFeature
{
    public AdminCaller Caller { get; } = caller;
}

/// <summary>Reading the caller, and requiring permissions on endpoints.</summary>
public static class AdminCallerExtensions
{
    /// <summary>The authenticated caller, or <c>null</c> for an anonymous request.</summary>
    public static AdminCaller? GetCaller(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IAdminCallerFeature>()?.Caller;
    }

    /// <summary>Sets the caller (authentication only).</summary>
    public static void SetCaller(this HttpContext context, AdminCaller caller)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caller);
        context.Features.Set<IAdminCallerFeature>(new AdminCallerFeature(caller));
    }

    /// <summary>
    /// Requires an authenticated caller holding <paramref name="permission"/>: anonymous is 401, a caller without it 403,
    /// both as RFC 9457 problem details. The check runs per request through <see cref="IPermissionEvaluator"/>, so a
    /// revoked token or a changed role takes effect at once. Services still authorize their own operations; this is the
    /// endpoint's first line, not the only one.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, Permission permission)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext http = invocation.HttpContext;
            if (http.GetCaller() is not { } caller)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");
            }

            IPermissionEvaluator permissions = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
            if (!await permissions.IsAllowedAsync(caller.Origin, permission, http.RequestAborted).ConfigureAwait(false))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Permission required", detail: $"This needs the '{permission.ToName()}' permission.");
            }

            return await next(invocation).ConfigureAwait(false);
        });
        builder.WithMetadata(new RequiredPermissionMetadata(permission));
        return builder;
    }
}

/// <summary>Endpoint metadata naming the permission an endpoint requires (for OpenAPI and tests).</summary>
public sealed record RequiredPermissionMetadata(Permission Permission);
