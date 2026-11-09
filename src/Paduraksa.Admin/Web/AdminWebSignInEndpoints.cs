using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;

using Paduraksa.Admin.Api;
using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Identity;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Web;

/// <summary>
/// The form endpoints behind the dashboard's sign-in, second-factor, sign-out and setup pages. The session cookie can only be
/// set on an ordinary HTTP response, never over a circuit, so the pages are plain HTML forms that post here; each answer is a
/// 303 to the next page (post/redirect/get), carrying an error code (<see cref="AdminWebForm.ErrorCodes"/>) when a step
/// failed and never any of the values that were posted.
/// </summary>
/// <remarks>
/// They run the same steps as the JSON API (<see cref="SignInFlow"/>: throttle, bounded password derivation, lockout, audit,
/// the same cookie), audited as the dashboard channel. Every one needs the antiforgery token the page rendered and passes the
/// same-origin check (<see cref="AdminCsrfExtensions.RequireSameOrigin"/>). A browser that is already signed in is sent on
/// (its anonymous page's token would not validate for the signed-in user anyway).
/// </remarks>
internal static class AdminWebSignInEndpoints
{
    private const string FormType = "application/x-www-form-urlencoded";

    public static void Map(IEndpointRouteBuilder endpoints, AdminWebOptions web)
    {
        endpoints.MapPost(web.SignInAction, SignInAsync).AsDashboardForm().WithName("DashboardSignIn");
        endpoints.MapPost(web.SecondFactorAction, SecondFactorAsync).AsDashboardForm().WithName("DashboardSecondFactor");
        endpoints.MapPost(web.SignOutAction, SignOutAsync).AsDashboardForm().WithName("DashboardSignOut");
        endpoints.MapPost(web.SetupAction, SetupAsync).AsDashboardForm().WithName("DashboardSetup");
    }

    /// <summary>The conventions every form endpoint shares.</summary>
    public static TBuilder AsDashboardForm<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AllowAnonymousAdmin().RequireSameOrigin().AcceptsAdminContentTypes(FormType).ExcludeFromDescription();

    private static async Task<IResult> SignInAsync(HttpContext http, IAntiforgery antiforgery, LoginService login, AdminThrottle throttle, AdminWebOptions web)
    {
        IFormCollection? form = await ReadFormAsync(http, antiforgery).ConfigureAwait(false);
        string returnUrl = LocalOrHome(form?[AdminWebForm.ReturnUrl], web);
        if (http.GetCaller() is not null)
        {
            return SeeOther(http, returnUrl);
        }

        if (form is null)
        {
            return SeeOther(http, Page(web.SignInPage, ApiCodes.Csrf.Refused, returnUrl, web));
        }

        string username = form[AdminWebForm.Username].ToString();
        string password = form[AdminWebForm.Password].ToString();
        if (username.Length == 0 || password.Length == 0)
        {
            return SeeOther(http, Page(web.SignInPage, ApiCodes.Auth.InvalidCredentials, returnUrl, web));
        }

        SignInAttempt<LoginResult> attempt = await SignInFlow.LoginAsync(http, login, throttle, username, password, Channel.Dashboard).ConfigureAwait(false);
        return attempt switch
        {
            { HeldBack: not SignInHeldBack.No } => SeeOther(http, Page(web.SignInPage, HeldBackCode(attempt.HeldBack), returnUrl, web)),
            { Result.Status: LoginStatus.Succeeded, Session: not null } => SeeOther(http, returnUrl),
            { Result.Status: LoginStatus.SecondFactorRequired, Session: not null } => SeeOther(http, Page(web.SecondFactorPage, null, returnUrl, web)),
            _ => SeeOther(http, Page(web.SignInPage, ApiCodes.Auth.InvalidCredentials, returnUrl, web)),
        };
    }

    private static async Task<IResult> SecondFactorAsync(HttpContext http, IAntiforgery antiforgery, LoginService login, AdminThrottle throttle, AdminWebOptions web)
    {
        IFormCollection? form = await ReadFormAsync(http, antiforgery).ConfigureAwait(false);
        string returnUrl = LocalOrHome(form?[AdminWebForm.ReturnUrl], web);
        if (http.GetCaller() is not null)
        {
            return SeeOther(http, returnUrl);
        }

        if (http.GetPartialSession() is null)
        {
            return SeeOther(http, Page(web.SignInPage, ApiCodes.Auth.SessionExpired, returnUrl, web));
        }

        // A recovery code is whatever was typed; a TOTP code tolerates the spaces an authenticator app shows ("123 456").
        string? recoveryCode = NullIfEmpty(form?[AdminWebForm.RecoveryCode].ToString().Trim());
        string? code = recoveryCode is null ? NullIfEmpty(form?[AdminWebForm.Code].ToString().Replace(" ", string.Empty, StringComparison.Ordinal)) : null;
        string verifyPage = recoveryCode is null ? web.SecondFactorPage : QueryHelpers.AddQueryString(web.SecondFactorPage, AdminWebForm.RecoveryParameter, "1");
        if (form is null)
        {
            return SeeOther(http, Page(verifyPage, ApiCodes.Csrf.Refused, returnUrl, web));
        }

        if (code is null && recoveryCode is null)
        {
            return SeeOther(http, Page(verifyPage, ApiCodes.Auth.InvalidCode, returnUrl, web));
        }

        SignInAttempt<LoginResult> attempt = await SignInFlow.SecondFactorAsync(http, login, throttle, code, recoveryCode, Channel.Dashboard).ConfigureAwait(false);
        return attempt switch
        {
            { HeldBack: not SignInHeldBack.No } => SeeOther(http, Page(verifyPage, HeldBackCode(attempt.HeldBack), returnUrl, web)),
            { Result.Status: LoginStatus.Succeeded, Session: not null } => SeeOther(http, returnUrl),
            { Result.Status: LoginStatus.InvalidCode } => SeeOther(http, Page(verifyPage, ApiCodes.Auth.InvalidCode, returnUrl, web)),
            _ => SeeOther(http, Page(web.SignInPage, ApiCodes.Auth.SessionExpired, returnUrl, web)),
        };
    }

    private static async Task<IResult> SignOutAsync(HttpContext http, IAntiforgery antiforgery, SessionService sessions, AdminWebOptions web)
    {
        // A stale page (its token from before a restart) goes back home, where a fresh page and token are rendered.
        if (await ReadFormAsync(http, antiforgery).ConfigureAwait(false) is null)
        {
            return SeeOther(http, web.HomePage);
        }

        (bool present, _, string? cookie) = AdminSessionCookie.Read(http.Request);
        if (present)
        {
            await sessions.LogoutAsync(cookie, http.GetClientContext(Channel.Dashboard), http.RequestAborted).ConfigureAwait(false);
            AdminSessionCookie.Clear(http.Response);
        }

        return SeeOther(http, QueryHelpers.AddQueryString(web.SignInPage, AdminWebForm.NoticeParameter, AdminWebForm.SignedOutNotice));
    }

    private static async Task<IResult> SetupAsync(
        HttpContext http, IAntiforgery antiforgery, SetupService setup, LoginService login, AdminThrottle throttle, AdminWebOptions web)
    {
        IFormCollection? form = await ReadFormAsync(http, antiforgery).ConfigureAwait(false);
        if (form is null)
        {
            return SeeOther(http, Page(web.SetupPage, ApiCodes.Csrf.Refused, null, web));
        }

        string password = form[AdminWebForm.Password].ToString();
        if (!string.Equals(password, form[AdminWebForm.ConfirmPassword].ToString(), StringComparison.Ordinal))
        {
            return SeeOther(http, Page(web.SetupPage, AdminWebForm.PasswordMismatch, null, web));
        }

        SetupRequest request = new(
            form[AdminWebForm.SetupToken].ToString().Trim(),
            form[AdminWebForm.Username].ToString(),
            password,
            NullIfEmpty(form[AdminWebForm.DisplayName].ToString().Trim()));
        SignInAttempt<SetupResult> attempt = await SignInFlow.SetupAsync(http, setup, login, throttle, request, Channel.Dashboard).ConfigureAwait(false);
        return attempt switch
        {
            { HeldBack: not SignInHeldBack.No } => SeeOther(http, Page(web.SetupPage, HeldBackCode(attempt.HeldBack), null, web)),
            { Result.Outcome: SetupOutcome.Succeeded, Session: not null } => SeeOther(http, web.SetupCompletedPage ?? web.HomePage),
            { Result.Outcome: SetupOutcome.Succeeded or SetupOutcome.AlreadyComplete } => SeeOther(http, Page(web.SignInPage, ApiCodes.Setup.Complete, null, web)),
            { Result.Outcome: SetupOutcome.InvalidToken } => SeeOther(http, Page(web.SetupPage, ApiCodes.Setup.InvalidToken, null, web)),
            _ => SeeOther(http, Page(web.SetupPage, ApiCodes.Identity.Refused, null, web)),
        };
    }

    /// <summary>The posted form when it is a form and its antiforgery token is valid for this browser and user; null otherwise.</summary>
    private static async Task<IFormCollection?> ReadFormAsync(HttpContext http, IAntiforgery antiforgery)
    {
        if (!http.Request.HasFormContentType || !await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
        {
            return null;
        }

        return await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
    }

    private static string HeldBackCode(SignInHeldBack heldBack) =>
        heldBack == SignInHeldBack.RateLimited ? ApiCodes.Auth.RateLimited : ApiCodes.Auth.Busy;

    /// <summary><paramref name="page"/> with the error code and the page to return to (only when it is not the home page).</summary>
    private static string Page(string page, string? error, string? returnUrl, AdminWebOptions web)
    {
        Dictionary<string, string?> query = [];
        if (error is not null)
        {
            query[AdminWebForm.ErrorParameter] = error;
        }

        if (returnUrl is not null && !string.Equals(returnUrl, web.HomePage, StringComparison.Ordinal))
        {
            query[AdminWebForm.ReturnUrl] = returnUrl;
        }

        return QueryHelpers.AddQueryString(page, query);
    }

    /// <summary>
    /// <paramref name="returnUrl"/> when it is a path on this site (one leading slash, no scheme or authority, no backslash), else the
    /// home page: a sign-in link must never be usable to send someone elsewhere.
    /// </summary>
    internal static string LocalOrHome(string? returnUrl, AdminWebOptions web) =>
        IsLocalPath(returnUrl) ? returnUrl! : web.HomePage;

    internal static bool IsLocalPath(string? url) =>
        url is { Length: > 0 }
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Contains('\\', StringComparison.Ordinal)
        && !url.AsSpan().ContainsAnyInRange('\0', ' ');

    /// <summary>A 303 to a local URL: the browser follows it with a <c>GET</c>.</summary>
    private static IResult SeeOther(HttpContext http, string location)
    {
        http.Response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
