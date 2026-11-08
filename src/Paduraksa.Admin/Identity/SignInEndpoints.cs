using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Auth;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>Setup and the two sign-in steps, sign-out, and who-am-I. The only endpoints that take no credentials (PLAN §10.1, §12.1).</summary>
internal static class SignInEndpoints
{
    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("setup", GetSetupStatusAsync).AllowAnonymousAdmin().WithName("GetSetupStatus");
        api.MapPost("setup", CompleteSetupAsync).AllowAnonymousAdmin().WithName("CompleteSetup");

        RouteGroupBuilder auth = api.MapGroup("auth");
        auth.MapPost("login", LoginAsync).AllowAnonymousAdmin().WithName("Login");
        auth.MapPost("second-factor", SecondFactorAsync).AllowAnonymousAdmin().WithName("CompleteSecondFactor");
        auth.MapPost("logout", LogoutAsync).AllowAnonymousAdmin().WithName("Logout");
        auth.MapGet("me", GetMeAsync).RequireAuthenticated().WithName("GetMe");
    }

    private static async Task<IResult> GetSetupStatusAsync(SetupService setup, HttpContext http) =>
        Results.Ok(await setup.GetStatusAsync(http.RequestAborted).ConfigureAwait(false));

    private static async Task<IResult> CompleteSetupAsync(SetupRequest request, HttpContext http, SetupService setup, LoginService login, AdminThrottle throttle)
    {
        if (IdentityApi.Throttle(http, throttle, AdminThrottleScope.Setup, request.Username) is { } limited)
        {
            return limited;
        }

        return await IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            ClientContext client = http.GetClientContext();
            SetupResult result = await setup.CompleteSetupAsync(request.SetupToken, request.Username, request.Password, request.DisplayName, client, http.RequestAborted).ConfigureAwait(false);
            switch (result)
            {
                case { Outcome: SetupOutcome.Succeeded, Owner: { } owner }:
                    // The new Owner is signed in at once: they just proved the password by choosing it.
                    LoginResult signedIn = await login.LoginAsync(request.Username, request.Password, client, http.RequestAborted).ConfigureAwait(false);
                    SessionStarted? session = IdentityApi.StartSession(http, signedIn);
                    return Results.Created("/api/v1/auth/me", new SetupCompleted(owner, session));
                case { Outcome: SetupOutcome.AlreadyComplete }:
                    return ApiProblems.Problem(StatusCodes.Status409Conflict, "An Owner already exists; sign in instead.", code: "setup_complete", title: "Setup is complete");
                case { Outcome: SetupOutcome.InvalidToken }:
                    throttle.RecordFailure(AdminThrottleScope.Setup, request.Username);
                    return AdminProblems.Forbidden("Invalid setup token", "The setup token is wrong. Read it from the server log, or run `paduraksa setup-token`.", "invalid_setup_token");
                case { Outcome: SetupOutcome.Invalid }:
                    return AdminProblems.Invalid(result.Problem);
                default:
                    return ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The operation ended in a state this endpoint doesn't know.");
            }
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, LoginService login, AdminThrottle throttle)
    {
        if (IdentityApi.Throttle(http, throttle, AdminThrottleScope.Login, request.Username) is { } limited)
        {
            return limited;
        }

        return await IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            LoginResult result = await login.LoginAsync(request.Username, request.Password, http.GetClientContext(), http.RequestAborted).ConfigureAwait(false);
            if (result.Session is null)
            {
                throttle.RecordFailure(AdminThrottleScope.Login, request.Username);
            }

            return Outcome(http, result);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> SecondFactorAsync(SecondFactorRequest request, HttpContext http, LoginService login, AdminThrottle throttle)
    {
        // Throttled per user (the partial session's), not per typed value: a code guess is the thing being limited.
        string? subject = http.GetPartialSession()?.Principal.UserId.ToString();
        if (IdentityApi.Throttle(http, throttle, AdminThrottleScope.SecondFactor, subject) is { } limited)
        {
            return limited;
        }

        return await IdentityApi.WithPasswordCheckAsync(http, throttle, async () =>
        {
            string? cookie = AdminSessionCookie.Read(http.Request).Value;
            LoginResult result = await login.CompleteSecondFactorAsync(cookie, request.Code, request.RecoveryCode, http.GetClientContext(), http.RequestAborted).ConfigureAwait(false);
            if (result.Status == LoginStatus.InvalidCode)
            {
                throttle.RecordFailure(AdminThrottleScope.SecondFactor, subject);
            }

            return Outcome(http, result);
        }).ConfigureAwait(false);
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, SessionService sessions)
    {
        (bool present, _, string? cookie) = AdminSessionCookie.Read(http.Request);
        if (present)
        {
            await sessions.LogoutAsync(cookie, http.GetClientContext(), http.RequestAborted).ConfigureAwait(false);
            AdminSessionCookie.Clear(http.Response);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetMeAsync(HttpContext http, UserService users, IPermissionEvaluator permissions)
    {
        AdminCaller caller = http.GetCaller()!;
        if (caller.User is not { } userId)
        {
            return AdminProblems.Unauthorized(http, "The caller is not a user.", "authentication_required");
        }

        // A caller reads its own user whatever its token's scopes are: the service lets a user read themselves, so a token asks as its owner.
        CommandOrigin self = caller.Origin with { Actor = Actor.ForUser(userId) };
        UserResult user = await users.GetAsync(self, userId, http.RequestAborted).ConfigureAwait(false);
        if (user.User is null)
        {
            return AdminProblems.Unauthorized(http, "The user no longer exists.", "invalid_session");
        }

        IReadOnlySet<Permission> granted = await permissions.GetPermissionsAsync(caller.Origin.Actor, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new CurrentCaller(
            user.User,
            [.. granted.Order()],
            caller.Token is not null ? "token" : caller.Session is not null ? "session" : "unspecified",
            caller.Session,
            caller.Token));
    }

    /// <summary>The wire form of a sign-in step. A refused step is always a generic 401 (never which part was wrong).</summary>
    private static IResult Outcome(HttpContext http, LoginResult result)
    {
        switch (result.Status)
        {
            case LoginStatus.Succeeded or LoginStatus.SecondFactorRequired when result.Session is not null:
                return Results.Ok(IdentityApi.StartSession(http, result));
            case LoginStatus.InvalidCode:
                return AdminProblems.Unauthorized(http, "The code is not valid.", "invalid_code", challenge: false);
            case LoginStatus.SessionExpired:
                if (http.GetPartialSession() is not null)
                {
                    AdminSessionCookie.Clear(http.Response);
                }

                return AdminProblems.Unauthorized(http, "The sign-in expired. Start again with your password.", "session_expired", challenge: false);
            default:
                return AdminProblems.Unauthorized(http, "The username or password is wrong.", "invalid_credentials", challenge: false);
        }
    }
}
