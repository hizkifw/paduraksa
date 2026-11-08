using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>The caller's own account: password, second factor, sessions and API tokens. Every one needs a caller; the services decide what that caller may do.</summary>
internal static class AccountEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        RouteGroupBuilder me = api.MapGroup("me").RequireAuthenticated();
        me.MapPost("password", ChangePasswordAsync).WithName("ChangePassword");
        me.MapPost("totp/enrollment", BeginTotpAsync).WithName("BeginTotpEnrollment");
        me.MapPost("totp/enrollment/confirm", ConfirmTotpAsync).WithName("ConfirmTotpEnrollment");
        me.MapDelete("totp", DisableTotpAsync).WithName("DisableTotp");
        me.MapPost("recovery-codes", RegenerateRecoveryCodesAsync).WithName("RegenerateRecoveryCodes");

        RouteGroupBuilder sessions = api.MapGroup("sessions").RequireAuthenticated().RequireSessionCaller();
        sessions.MapGet(string.Empty, ListSessionsAsync).WithName("ListSessions");
        sessions.MapPost("revoke-others", RevokeOtherSessionsAsync).WithName("RevokeOtherSessions");
        sessions.MapDelete("{id}", RevokeSessionAsync).WithName("RevokeSession");

        RouteGroupBuilder tokens = api.MapGroup("tokens").RequireAuthenticated().RequireSessionCaller();
        tokens.MapGet(string.Empty, ListTokensAsync).WithName("ListTokens");
        tokens.MapPost(string.Empty, IssueTokenAsync).WithName("IssueToken");
        tokens.MapDelete("{id}", RevokeTokenAsync).WithName("RevokeToken");
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

    private static Task<IResult> ChangePasswordAsync(ChangePasswordRequest request, HttpContext http, UserService users, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            AdminCaller caller = http.GetCaller()!;
            IdentityResult result = await users.ChangePasswordAsync(caller.Origin, request, caller.Session, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.NoContent();
        });

    private static Task<IResult> BeginTotpAsync(PasswordConfirmation request, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            TotpEnrollmentResult result = await twoFactor.BeginEnrollmentAsync(http.GetCaller()!.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Enrollment);
        });

    private static async Task<IResult> ConfirmTotpAsync(TotpConfirmation request, HttpContext http, TwoFactorService twoFactor)
    {
        RecoveryCodesResult result = await twoFactor.ConfirmEnrollmentAsync(http.GetCaller()!.Origin, request.Code, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "enrollment") ?? Results.Ok(result.Codes);
    }

    private static Task<IResult> DisableTotpAsync([FromBody] PasswordConfirmation request, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            IdentityResult result = await twoFactor.DisableAsync(http.GetCaller()!.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user", result.Problem) ?? Results.NoContent();
        });

    private static Task<IResult> RegenerateRecoveryCodesAsync(PasswordConfirmation request, HttpContext http, TwoFactorService twoFactor, AdminThrottle throttle) =>
        IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            RecoveryCodesResult result = await twoFactor.RegenerateRecoveryCodesAsync(http.GetCaller()!.Origin, request.Password, http.RequestAborted).ConfigureAwait(false);
            return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Codes);
        });

    private static async Task<IResult> ListSessionsAsync(HttpContext http, SessionService sessions, string? userId = null)
    {
        AdminCaller caller = http.GetCaller()!;
        if (!OwnerOf(caller, userId, out UserId owner))
        {
            return userId is null ? AdminProblems.Forbidden("Permission required", "This caller has no sessions of its own.") : AdminProblems.NotFound("user");
        }

        SessionListResult result = await sessions.ListAsync(caller.Origin, owner, caller.Session, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Sessions);
    }

    private static async Task<IResult> RevokeSessionAsync(string id, HttpContext http, SessionService sessions)
    {
        if (!IdentityApi.TryParseId(id, out SessionId sessionId))
        {
            return AdminProblems.NotFound("session");
        }

        IdentityResult result = await sessions.RevokeAsync(http.GetCaller()!.Origin, sessionId, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "session", result.Problem) ?? Results.NoContent();
    }

    private static async Task<IResult> RevokeOtherSessionsAsync(HttpContext http, SessionService sessions)
    {
        AdminCaller caller = http.GetCaller()!;
        if (caller.Session is not { } keep)
        {
            return AdminProblems.Invalid("Ending the other sessions needs a signed-in session to keep; a token has none.");
        }

        IdentityResult result = await sessions.RevokeOthersAsync(caller.Origin, keep, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "session", result.Problem) ?? Results.NoContent();
    }

    private static async Task<IResult> ListTokensAsync(HttpContext http, ApiTokenService tokens, string? userId = null)
    {
        AdminCaller caller = http.GetCaller()!;
        UserId? owner = null;
        if (userId is not null)
        {
            if (!IdentityApi.TryParseId(userId, out UserId parsed))
            {
                return AdminProblems.NotFound("user");
            }

            owner = parsed;
        }

        ApiTokenListResult result = await tokens.ListAsync(caller.Origin, owner ?? caller.User, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Tokens);
    }

    private static async Task<IResult> IssueTokenAsync(IssueApiTokenRequest request, HttpContext http, ApiTokenService tokens)
    {
        ApiTokenResult result = await tokens.IssueAsync(http.GetCaller()!.Origin, request, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user", result.Problem)
            ?? Results.Created($"/api/v1/tokens/{result.Issued!.Summary.Id}", result.Issued);
    }

    private static async Task<IResult> RevokeTokenAsync(string id, HttpContext http, ApiTokenService tokens)
    {
        if (!IdentityApi.TryParseId(id, out ApiTokenId tokenId))
        {
            return AdminProblems.NotFound("token");
        }

        ApiTokenResult result = await tokens.RevokeAsync(http.GetCaller()!.Origin, tokenId, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "token", result.Problem) ?? Results.NoContent();
    }

    /// <summary>The user whose sessions to list: the one asked for (the service checks the caller may), else the caller's own.</summary>
    private static bool OwnerOf(AdminCaller caller, string? requested, out UserId owner)
    {
        if (requested is not null)
        {
            return IdentityApi.TryParseId(requested, out owner);
        }

        owner = caller.User ?? default;
        return caller.User is not null;
    }
}
