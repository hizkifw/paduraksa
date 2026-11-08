using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Identity;

/// <summary>Why a credential step did not run at all: the client or the subject is over its budget, or every derivation slot is busy.</summary>
internal enum SignInHeldBack
{
    /// <summary>The step ran; its result says how it went.</summary>
    No = 0,

    /// <summary>Too many attempts: see <see cref="SignInAttempt{T}.RetryAfter"/>.</summary>
    RateLimited = 1,

    /// <summary>No password-derivation slot freed up in time; nothing was derived.</summary>
    Busy = 2,
}

/// <summary>A credential step's outcome: the service's result and the session it started (the cookie is set), or why the step was held back before it ran.</summary>
internal readonly record struct SignInAttempt<T>(T? Result, SessionStarted? Session = null, SignInHeldBack HeldBack = SignInHeldBack.No, TimeSpan RetryAfter = default)
    where T : class;

/// <summary>
/// The credential steps shared by the JSON API (<c>/api/v1/setup</c>, <c>auth/login</c>, <c>auth/second-factor</c>) and the dashboard's
/// sign-in forms: the throttle, the bounded password derivation, the service call, failure counting and the session cookie. Each surface
/// only says how the outcome reads on its wire (a problem, or a redirect).
/// </summary>
internal static class SignInFlow
{
    /// <summary>Step one: the username and password. On success (full or partial) the session cookie is set.</summary>
    public static async Task<SignInAttempt<LoginResult>> LoginAsync(
        HttpContext http, LoginService login, AdminThrottle throttle, string username, string password, Channel channel)
    {
        if (throttle.TryEnter(AdminThrottleScope.Login, http.GetClientAddress(), username) is { } retryAfter)
        {
            return new(null, null, SignInHeldBack.RateLimited, retryAfter);
        }

        using IDisposable? slot = await throttle.EnterPasswordCheckAsync(http.RequestAborted).ConfigureAwait(false);
        if (slot is null)
        {
            return new(null, null, SignInHeldBack.Busy);
        }

        LoginResult result = await login.LoginAsync(username, password, http.GetClientContext(channel), http.RequestAborted).ConfigureAwait(false);
        if (result.Session is null)
        {
            throttle.RecordFailure(AdminThrottleScope.Login, username);
        }

        return new(result, IdentityApi.StartSession(http, result));
    }

    /// <summary>
    /// Step two: a TOTP code or a recovery code against the request's partial session. On success the cookie is rotated to the full
    /// session's; when the partial session is gone the cookie is cleared.
    /// </summary>
    public static async Task<SignInAttempt<LoginResult>> SecondFactorAsync(
        HttpContext http, LoginService login, AdminThrottle throttle, string? code, string? recoveryCode, Channel channel)
    {
        // Throttled per user (the partial session's), not per typed value: a code guess is the thing being limited.
        string? subject = http.GetPartialSession()?.Principal.UserId.ToString();
        if (throttle.TryEnter(AdminThrottleScope.SecondFactor, http.GetClientAddress(), subject) is { } retryAfter)
        {
            return new(null, null, SignInHeldBack.RateLimited, retryAfter);
        }

        using IDisposable? slot = await throttle.EnterPasswordCheckAsync(http.RequestAborted).ConfigureAwait(false);
        if (slot is null)
        {
            return new(null, null, SignInHeldBack.Busy);
        }

        string? cookie = AdminSessionCookie.Read(http.Request).Value;
        LoginResult result = await login.CompleteSecondFactorAsync(cookie, code, recoveryCode, http.GetClientContext(channel), http.RequestAborted).ConfigureAwait(false);
        if (result.Status == LoginStatus.InvalidCode)
        {
            throttle.RecordFailure(AdminThrottleScope.SecondFactor, subject);
        }

        if (result.Status == LoginStatus.SessionExpired && http.GetPartialSession() is not null)
        {
            AdminSessionCookie.Clear(http.Response);
        }

        return new(result, IdentityApi.StartSession(http, result));
    }

    /// <summary>
    /// First-run setup: the setup token, then the Owner, who is signed in at once (they just proved the password by choosing it); the
    /// session cookie is set and the started session returned with the result.
    /// </summary>
    public static async Task<SignInAttempt<SetupResult>> SetupAsync(
        HttpContext http, SetupService setup, LoginService login, AdminThrottle throttle, SetupRequest request, Channel channel)
    {
        if (throttle.TryEnter(AdminThrottleScope.Setup, http.GetClientAddress(), request.Username) is { } retryAfter)
        {
            return new(null, null, SignInHeldBack.RateLimited, retryAfter);
        }

        using IDisposable? slot = await throttle.EnterPasswordCheckAsync(http.RequestAborted).ConfigureAwait(false);
        if (slot is null)
        {
            return new(null, null, SignInHeldBack.Busy);
        }

        ClientContext client = http.GetClientContext(channel);
        SetupResult result = await setup.CompleteSetupAsync(request.SetupToken, request.Username, request.Password, request.DisplayName, client, http.RequestAborted).ConfigureAwait(false);
        SessionStarted? session = null;
        if (result.Outcome == SetupOutcome.Succeeded)
        {
            LoginResult signedIn = await login.LoginAsync(request.Username, request.Password, client, http.RequestAborted).ConfigureAwait(false);
            session = IdentityApi.StartSession(http, signedIn);
        }
        else if (result.Outcome == SetupOutcome.InvalidToken)
        {
            throttle.RecordFailure(AdminThrottleScope.Setup, request.Username);
        }

        return new(result, session);
    }
}
