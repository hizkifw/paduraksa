using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>The caller's own account: password, second factor, sessions and API tokens. Every one needs a caller; the services decide what that caller may do.</summary>
internal static class AccountEndpoints
{
    private static readonly QueryParameter OwnerFilter = QueryParameter.Id<UserId>("userId", "List this user's instead of the caller's (the service checks the caller may); 404 when it names no user.");

    public static void Map(IEndpointRouteBuilder api)
    {
        var me = api.MapGroup("me").RequireAuthenticated();
        me.MapPost("password", ChangePasswordAsync).WithName("ChangePassword").WithSummary("Change your password")
            .WithDescription("Needs the current password (a wrong one is 422 and counts toward the lockout) and ends every other session of the caller.")
            .Accepts<ChangePasswordRequest>("application/json").Produces(StatusCodes.Status204NoContent).ProducesApiProblems(400, 403, 404, 415, 422, 503);
        me.MapPost("totp/enrollment", BeginTotpAsync).WithName("BeginTotpEnrollment").WithSummary("Start enrolling an authenticator")
            .WithDescription("Needs the password. The provisioning URI carries the secret and is shown once; the enrolment is not active until it is confirmed.")
            .Accepts<PasswordConfirmation>("application/json").Produces<TotpEnrollmentStarted>().ProducesApiProblems(400, 403, 404, 409, 415, 422, 503);
        me.MapPost("totp/enrollment/confirm", ConfirmTotpAsync).WithName("ConfirmTotpEnrollment").WithSummary("Confirm the authenticator with a code")
            .WithDescription("Activates the second factor and returns the recovery codes, shown once.")
            .Accepts<TotpConfirmation>("application/json").Produces<RecoveryCodes>().ProducesApiProblems(400, 403, 404, 409, 415, 422);
        me.MapPost("totp/disable", DisableTotpAsync).WithName("DisableTotp").WithSummary("Turn the second factor off")
            .WithDescription("Needs the password, so it is a POST with a body (a DELETE has no body to rely on).")
            .Accepts<PasswordConfirmation>("application/json").Produces(StatusCodes.Status204NoContent).ProducesApiProblems(400, 403, 404, 409, 415, 422, 503);
        me.MapPost("recovery-codes", RegenerateRecoveryCodesAsync).WithName("RegenerateRecoveryCodes").WithSummary("Make new recovery codes")
            .WithDescription("Needs the password. The old codes stop working; the new ones are shown once.")
            .Accepts<PasswordConfirmation>("application/json").Produces<RecoveryCodes>().ProducesApiProblems(400, 403, 404, 409, 415, 422, 503);

        var sessions = api.MapGroup("sessions").RequireAuthenticated().RequireSessionCaller();
        sessions.MapGet(string.Empty, ListSessionsAsync).WithName("ListSessions").WithSummary("List sessions")
            .WithDescription("A signed-in session is required: a token caller gets 403 auth.sessionRequired.")
            .WithQuery(OwnerFilter).WithPage().Produces<Page<SessionSummary>>().ProducesApiProblems(400, 403, 404);
        sessions.MapPost("revoke-others", RevokeOtherSessionsAsync).WithName("RevokeOtherSessions").WithSummary("End every other session of the caller")
            .Produces(StatusCodes.Status204NoContent).ProducesApiProblems(403, 404);
        sessions.MapDelete("{id:SessionId}", RevokeSessionAsync).WithName("RevokeSession").WithSummary("End a session")
            .Produces(StatusCodes.Status204NoContent).ProducesApiProblems(403, 404);

        var tokens = api.MapGroup("tokens").RequireAuthenticated().RequireSessionCaller();
        tokens.MapGet(string.Empty, ListTokensAsync).WithName("ListTokens").WithSummary("List API tokens")
            .WithDescription("A signed-in session is required: a token caller gets 403 auth.sessionRequired.")
            .WithQuery(OwnerFilter).WithPage().Produces<Page<ApiTokenSummary>>().ProducesApiProblems(400, 403, 404);
        tokens.MapPost(string.Empty, IssueTokenAsync).WithName("IssueToken").WithSummary("Issue an API token")
            .WithDescription("The token's secret is in the answer and nowhere else, ever again.")
            .Accepts<IssueApiTokenRequest>("application/json").Produces<IssuedApiToken>(StatusCodes.Status201Created).ProducesApiProblems(400, 403, 404, 415, 422);
        tokens.MapDelete("{id:ApiTokenId}", RevokeTokenAsync).WithName("RevokeToken").WithSummary("Revoke an API token")
            .Produces(StatusCodes.Status204NoContent).ProducesApiProblems(403, 404);
    }

    /// <summary>
    /// Tokens and sessions are managed by a person at a session: a token that could list, mint or revoke tokens could keep itself alive, and
    /// one that could end sessions could end the session that is watching it. A token caller gets a plain 403 that says so.
    /// </summary>
    private static TBuilder RequireSessionCaller<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(static (invocation, next) => invocation.HttpContext.GetCaller()?.Token is not null
            ? ValueTask.FromResult<object?>(AdminProblems.TokenNotAllowed())
            : next(invocation));
        return builder;
    }

    private static Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, AdminCaller caller, HttpContext http, UserService users, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            var result = await users.ChangePasswordAsync(caller.Origin, request, caller.Session, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.NoContent();
        });

    private static Task<IResult> BeginTotpAsync(PasswordConfirmation request, AdminCaller caller, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            var result = await twoFactor.BeginEnrollmentAsync(caller.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Enrollment);
        });

    private static async Task<IResult> ConfirmTotpAsync(TotpConfirmation request, AdminCaller caller, HttpContext http, TwoFactorService twoFactor)
    {
        var result = await twoFactor.ConfirmEnrollmentAsync(caller.Origin, request.Code, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "enrollment") ?? Results.Ok(result.Codes);
    }

    private static Task<IResult> DisableTotpAsync([FromBody] PasswordConfirmation request, AdminCaller caller, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            var result = await twoFactor.DisableAsync(caller.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.NoContent();
        });

    private static Task<IResult> RegenerateRecoveryCodesAsync(PasswordConfirmation request, AdminCaller caller, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            var result = await twoFactor.RegenerateRecoveryCodesAsync(caller.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Codes);
        });

    private static async Task<IResult> ListSessionsAsync(AdminCaller caller, HttpContext http, SessionService sessions)
    {
        var userId = OwnerFilter.Read(http).ToString() is { Length: > 0 } requested ? requested : null;
        if (!OwnerOf(caller, userId, out var owner))
        {
            return userId is null ? AdminProblems.Forbidden("Permission required", "This caller has no sessions of its own.") : AdminProblems.NotFound("user");
        }

        var result = await sessions.ListAsync(caller.Origin, owner, caller.Session, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user")
            ?? (Pagination.TryPage(http, result.Sessions, s => s.Id, out var page, out var problem) ? Results.Ok(page) : problem!);
    }

    private static async Task<IResult> RevokeSessionAsync(SessionId id, AdminCaller caller, HttpContext http, SessionService sessions)
    {
        var result = await sessions.RevokeAsync(caller.Origin, id, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "session", result.Problem) ?? Results.NoContent();
    }

    private static async Task<IResult> RevokeOtherSessionsAsync(AdminCaller caller, HttpContext http, SessionService sessions)
    {
        // RequireSessionCaller has answered a token caller with its 403 already, so the session to keep is always here.
        var keep = caller.Session!.Value;
        var result = await sessions.RevokeOthersAsync(caller.Origin, keep, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "session", result.Problem) ?? Results.NoContent();
    }

    private static async Task<IResult> ListTokensAsync(AdminCaller caller, HttpContext http, ApiTokenService tokens)
    {
        var userId = OwnerFilter.Read(http).ToString() is { Length: > 0 } requested ? requested : null;
        UserId? owner = null;
        if (userId is not null)
        {
            if (!TypedIdRouting.TryParse(userId, out UserId parsed))
            {
                return AdminProblems.NotFound("user");
            }

            owner = parsed;
        }

        var result = await tokens.ListAsync(caller.Origin, owner ?? caller.User, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user")
            ?? (Pagination.TryPage(http, result.Tokens, t => t.Id, out var page, out var problem) ? Results.Ok(page) : problem!);
    }

    private static async Task<IResult> IssueTokenAsync(IssueApiTokenRequest request, AdminCaller caller, HttpContext http, ApiTokenService tokens)
    {
        var result = await tokens.IssueAsync(caller.Origin, request, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem)
            ?? Results.Created($"/api/v1/tokens/{result.Issued!.Summary.Id}", result.Issued);
    }

    private static async Task<IResult> RevokeTokenAsync(ApiTokenId id, AdminCaller caller, HttpContext http, ApiTokenService tokens)
    {
        var result = await tokens.RevokeAsync(caller.Origin, id, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "token", result.Problem) ?? Results.NoContent();
    }

    /// <summary>The user whose sessions to list: the one asked for (the service checks the caller may), else the caller's own.</summary>
    private static bool OwnerOf(AdminCaller caller, string? requested, out UserId owner)
    {
        if (requested is not null)
        {
            return TypedIdRouting.TryParse(requested, out owner);
        }

        owner = caller.User ?? default;
        return caller.User is not null;
    }
}
