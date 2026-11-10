using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Api;
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
        var me = api.MapGroup("me").RequireAuthenticated();
        me.MapPost("password", ChangePasswordAsync).WithName("ChangePassword");
        me.MapPost("totp/enrollment", BeginTotpAsync).WithName("BeginTotpEnrollment");
        me.MapPost("totp/enrollment/confirm", ConfirmTotpAsync).WithName("ConfirmTotpEnrollment");
        me.MapDelete("totp", DisableTotpAsync).WithName("DisableTotp");
        me.MapPost("recovery-codes", RegenerateRecoveryCodesAsync).WithName("RegenerateRecoveryCodes");

        var sessions = api.MapGroup("sessions").RequireAuthenticated().RequireSessionCaller();
        sessions.MapGet(string.Empty, ListSessionsAsync).WithName("ListSessions");
        sessions.MapPost("revoke-others", RevokeOtherSessionsAsync).WithName("RevokeOtherSessions");
        sessions.MapDelete("{id:SessionId}", RevokeSessionAsync).WithName("RevokeSession");

        var tokens = api.MapGroup("tokens").RequireAuthenticated().RequireSessionCaller();
        tokens.MapGet(string.Empty, ListTokensAsync).WithName("ListTokens");
        tokens.MapPost(string.Empty, IssueTokenAsync).WithName("IssueToken");
        tokens.MapDelete("{id:ApiTokenId}", RevokeTokenAsync).WithName("RevokeToken");
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

    private static async Task<IResult> ListSessionsAsync(AdminCaller caller, HttpContext http, SessionService sessions, string? userId = null)
    {
        if (!OwnerOf(caller, userId, out var owner))
        {
            return userId is null ? AdminProblems.Forbidden("Permission required", "This caller has no sessions of its own.") : AdminProblems.NotFound("user");
        }

        var result = await sessions.ListAsync(caller.Origin, owner, caller.Session, http.RequestAborted).ConfigureAwait(false);
        return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Sessions);
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

    private static async Task<IResult> ListTokensAsync(AdminCaller caller, HttpContext http, ApiTokenService tokens, string? userId = null)
    {
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
        return AdminProblems.OfFailure(result.Status, "user") ?? Results.Ok(result.Tokens);
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
