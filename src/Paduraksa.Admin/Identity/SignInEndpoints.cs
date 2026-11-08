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
        SignInAttempt<SetupResult> attempt = await SignInFlow.SetupAsync(http, setup, login, throttle, request, Channel.Api).ConfigureAwait(false);
        if (HeldBack(http, attempt.HeldBack, attempt.RetryAfter) is { } heldBack)
        {
            return heldBack;
        }

        SetupResult result = attempt.Result!;
        return result switch
        {
            { Outcome: SetupOutcome.Succeeded, Owner: { } owner } => Results.Created("/api/v1/auth/me", new SetupCompleted(owner, attempt.Session)),
            { Outcome: SetupOutcome.AlreadyComplete } =>
                ApiProblems.Problem(StatusCodes.Status409Conflict, "An Owner already exists; sign in instead.", code: ApiCodes.Setup.Complete, title: "Setup is complete"),
            { Outcome: SetupOutcome.InvalidToken } =>
                AdminProblems.Forbidden("Invalid setup token", "The setup token is wrong. Read it from the server log, or run `paduraksa setup-token`.", ApiCodes.Setup.InvalidToken),
            { Outcome: SetupOutcome.Invalid } => AdminProblems.Invalid(result.Problem),
            _ => ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The operation ended in a state this endpoint doesn't know."),
        };
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, LoginService login, AdminThrottle throttle)
    {
        SignInAttempt<LoginResult> attempt = await SignInFlow.LoginAsync(http, login, throttle, request.Username, request.Password, Channel.Api).ConfigureAwait(false);
        return HeldBack(http, attempt.HeldBack, attempt.RetryAfter) ?? Outcome(http, attempt);
    }

    private static async Task<IResult> SecondFactorAsync(SecondFactorRequest request, HttpContext http, LoginService login, AdminThrottle throttle)
    {
        SignInAttempt<LoginResult> attempt = await SignInFlow.SecondFactorAsync(http, login, throttle, request.Code, request.RecoveryCode, Channel.Api).ConfigureAwait(false);
        return HeldBack(http, attempt.HeldBack, attempt.RetryAfter) ?? Outcome(http, attempt);
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
            return AdminProblems.Unauthorized(http, "The caller is not a user.", ApiCodes.Auth.Required);
        }

        // A caller reads its own user whatever its token's scopes are: the service lets a user read themselves, so a token asks as its owner.
        CommandOrigin self = caller.Origin with { Actor = Actor.ForUser(userId) };
        UserResult user = await users.GetAsync(self, userId, http.RequestAborted).ConfigureAwait(false);
        if (user.User is null)
        {
            return AdminProblems.Unauthorized(http, "The user no longer exists.", ApiCodes.Auth.InvalidSession);
        }

        IReadOnlySet<Permission> granted = await permissions.GetPermissionsAsync(caller.Origin.Actor, http.RequestAborted).ConfigureAwait(false);
        return Results.Ok(new CurrentCaller(
            user.User,
            [.. granted.Order()],
            caller.Token is not null ? AuthKind.Token : caller.Session is not null ? AuthKind.Session : AuthKind.Unspecified,
            caller.Session,
            caller.Token));
    }

    /// <summary>The 429 (with <c>Retry-After</c>) or 503 of a step that never ran; null when it ran.</summary>
    private static IResult? HeldBack(HttpContext http, SignInHeldBack heldBack, TimeSpan retryAfter) => heldBack switch
    {
        SignInHeldBack.RateLimited => AdminProblems.TooManyRequests(http, retryAfter),
        SignInHeldBack.Busy => AdminProblems.Busy(http),
        _ => null,
    };

    /// <summary>The wire form of a sign-in step. A refused step is always a generic 401 (never which part was wrong).</summary>
    private static IResult Outcome(HttpContext http, SignInAttempt<LoginResult> attempt) => attempt.Result!.Status switch
    {
        LoginStatus.Succeeded or LoginStatus.SecondFactorRequired when attempt.Session is not null => Results.Ok(attempt.Session),
        LoginStatus.InvalidCode => AdminProblems.Unauthorized(http, "The code is not valid.", ApiCodes.Auth.InvalidCode, challenge: false),
        LoginStatus.SessionExpired => AdminProblems.Unauthorized(http, "The sign-in expired. Start again with your password.", ApiCodes.Auth.SessionExpired, challenge: false),
        _ => AdminProblems.Unauthorized(http, "The username or password is wrong.", ApiCodes.Auth.InvalidCredentials, challenge: false),
    };
}
