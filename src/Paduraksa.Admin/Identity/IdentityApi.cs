using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Ids;

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

    /// <summary>Takes an attempt from the client's budget and checks the subject's failures; a refusal is the 429 problem with <c>Retry-After</c>.</summary>
    internal static IResult? Throttle(HttpContext http, AdminThrottle throttle, AdminThrottleScope scope, string? subject) =>
        throttle.TryEnter(scope, http.GetClientAddress(), subject) is { } retryAfter ? AdminProblems.TooManyRequests(retryAfter, http) : null;

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

    /// <summary>Reads a typed ID from a route value or query; false for a value of another shape (which names nothing).</summary>
    internal static bool TryParseId<TId>(string? text, out TId id)
        where TId : struct, ITypedId<TId>
    {
        if (text is not null && TId.TryParse(text.AsSpan(), out id))
        {
            return true;
        }

        id = default;
        return false;
    }
}
