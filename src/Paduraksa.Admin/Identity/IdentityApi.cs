using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;

namespace Paduraksa.Admin.Identity;

/// <summary>The identity endpoints of <c>/api/v1</c> (PLAN §10.1, §11.1): setup, sign-in, the caller's own account, sessions, tokens and users.</summary>
public static class IdentityApi
{
    /// <summary>
    /// Maps <c>setup</c>, <c>auth/*</c>, <c>me/*</c>, <c>sessions</c>, <c>tokens</c> and <c>users</c> under <c>/api/v1</c>. The services do the work and the
    /// permission checks; an endpoint only translates the request and maps the service's status to HTTP: denied 403, not found 404,
    /// invalid 422 (with the reason), conflict 409. Needs <see cref="AdminAuthExtensions.AddPaduraksaAdminAuth"/> and
    /// <see cref="AdminAuthExtensions.UsePaduraksaAdminAuth"/>.
    /// </summary>
    public static IEndpointRouteBuilder MapPaduraksaIdentityApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        RouteGroupBuilder api = endpoints.MapGroup("/api/v1").WithTags("Identity");
        SignInEndpoints.Map(api);
        AccountEndpoints.Map(api);
        UserEndpoints.Map(api);
        return endpoints;
    }

    /// <summary>Runs <paramref name="run"/>, which derives a password, in one of the limited derivation slots; when none frees up in time it is a 503 and nothing is derived.</summary>
    internal static async Task<IResult> WithPasswordCheckAsync(HttpContext http, AdminThrottle throttle, Func<Task<IResult>> run)
    {
        using IDisposable? slot = await throttle.EnterPasswordCheckAsync(http.RequestAborted).ConfigureAwait(false);
        return slot is null ? AdminProblems.Busy(http) : await run().ConfigureAwait(false);
    }

    /// <summary>Sets the session cookie for a granted session and describes it; null when the step granted none.</summary>
    internal static SessionStarted? StartSession(HttpContext http, LoginResult result)
    {
        if (result.Session is not { } grant)
        {
            return null;
        }

        AdminSessionCookie.Set(http.Response, grant.CookieValue, grant.ExpiresAt);
        return new SessionStarted(grant.Id, result.Status == LoginStatus.SecondFactorRequired, grant.ExpiresAt);
    }
}
