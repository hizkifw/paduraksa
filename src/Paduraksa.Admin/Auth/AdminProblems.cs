using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Api;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// RFC 9457 problem results for the identity API, one place deciding how a service outcome reads on the wire. A problem names what went
/// wrong and never echoes a credential, a token or a cookie.
/// </summary>
internal static class AdminProblems
{
    /// <summary>
    /// A 401. With <paramref name="challenge"/> it names the scheme (<c>WWW-Authenticate: Bearer</c>), and <paramref name="bearerError"/> adds the
    /// RFC 6750 error: <c>invalid_request</c> for a malformed or ambiguous credential, <c>invalid_token</c> for one that is not valid.
    /// </summary>
    public static IResult Unauthorized(HttpContext http, string detail, string code, string? bearerError = null, bool challenge = true)
    {
        if (challenge)
        {
            http.Response.Headers.WWWAuthenticate = bearerError is null ? "Bearer" : $"Bearer error=\"{bearerError}\"";
        }

        return ApiProblems.Problem(StatusCodes.Status401Unauthorized, detail, code: code);
    }

    /// <summary>The server is busy deriving passwords: nothing was tried, try again shortly.</summary>
    public static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, "Too many password checks are running. Try again in a moment.", code: "busy", title: "Busy");
    }

    /// <summary>A token cannot manage tokens or sessions (it could mint itself a longer life, or end the session that is watching it).</summary>
    public static IResult TokenNotAllowed() =>
        Forbidden("Signed-in session required", "API tokens can't manage tokens or sessions; use a signed-in session.", "session_required");

    public static IResult Forbidden(string title, string? detail = null, string? code = null) =>
        ApiProblems.Problem(StatusCodes.Status403Forbidden, detail, code: code, title: title);

    public static IResult TooManyRequests(TimeSpan retryAfter, HttpContext context)
    {
        context.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ApiProblems.Problem(StatusCodes.Status429TooManyRequests, "Too many attempts. Wait before trying again.", code: "rate_limited", title: "Too many attempts");
    }

    /// <summary>A malformed or missing body or parameter (the JSON binder's own failures are 400s too).</summary>
    public static IResult BadRequest(string detail) =>
        ApiProblems.Problem(StatusCodes.Status400BadRequest, detail);

    /// <summary>An ID in the path that is not one of the expected kind: nothing by that name can exist.</summary>
    public static IResult NotFound(string what) =>
        ApiProblems.Problem(StatusCodes.Status404NotFound, $"No such {what}.");

    /// <summary>The input was refused; the service's message is safe to show.</summary>
    public static IResult Invalid(string? problem) =>
        ApiProblems.Invalid([new ConfigError(string.Empty, "identity.refused", problem ?? "The request was refused.")]);

    /// <summary>
    /// The failure statuses every identity operation shares: denied 403, not found 404, invalid 422, conflict 409 (a clash, or the last
    /// Owner), and 422 for a wrong password where the caller is signed in and must not be told the session is dead.
    /// </summary>
    public static IResult? OfFailure(IdentityStatus status, string what, string? problem = null) => status switch
    {
        IdentityStatus.Succeeded => null,
        IdentityStatus.Denied => Forbidden("Permission required", "You do not have permission to do this."),
        IdentityStatus.NotFound => NotFound(what),
        IdentityStatus.Invalid => Invalid(problem),
        IdentityStatus.Conflict => ApiProblems.Problem(StatusCodes.Status409Conflict, problem ?? "This clashes with something that exists.", code: "conflict"),
        IdentityStatus.LastOwner => ApiProblems.Problem(StatusCodes.Status409Conflict, "That would leave no enabled Owner.", code: "last_owner"),
        IdentityStatus.InvalidCredentials => ApiProblems.Invalid([new ConfigError("/password", "identity.password.wrong", "The password is wrong.")]),
        _ => ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The operation ended in a state this endpoint doesn't know."),
    };
}
