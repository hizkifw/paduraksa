using Microsoft.AspNetCore.Http;

using Paduraksa.Application.Identity;

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

        return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required", detail: detail, extensions: Code(code));
    }

    /// <summary>The server is busy deriving passwords: nothing was tried, try again shortly.</summary>
    public static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Busy",
            detail: "Too many password checks are running. Try again in a moment.",
            extensions: Code("busy"));
    }

    /// <summary>A token cannot manage tokens or sessions (it could mint itself a longer life, or end the session that is watching it).</summary>
    public static IResult TokenNotAllowed() =>
        Forbidden("Signed-in session required", "API tokens can't manage tokens or sessions; use a signed-in session.", "session_required");

    public static IResult Forbidden(string title, string? detail = null, string? code = null) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: title, detail: detail, extensions: code is null ? null : Code(code));

    public static IResult TooManyRequests(TimeSpan retryAfter, HttpContext context)
    {
        context.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many attempts",
            detail: "Too many attempts. Wait before trying again.",
            extensions: Code("rate_limited"));
    }

    /// <summary>A malformed or missing body or parameter (the JSON binder's own failures are 400s too).</summary>
    public static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Bad request", detail: detail);

    /// <summary>An ID in the path that is not one of the expected kind: nothing by that name can exist.</summary>
    public static IResult NotFound(string what) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: $"No such {what}.");

    /// <summary>The input was refused; the service's message is safe to show.</summary>
    public static IResult Invalid(string? problem) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { ["request"] = [problem ?? "The request was refused."] },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The request was refused");

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
        IdentityStatus.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: problem ?? "This clashes with something that exists.", extensions: Code("conflict")),
        IdentityStatus.LastOwner => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflict", detail: "That would leave no enabled Owner.", extensions: Code("last_owner")),
        IdentityStatus.InvalidCredentials => Results.ValidationProblem(
            new Dictionary<string, string[]> { ["password"] = ["The password is wrong."] },
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The request was refused"),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Unexpected result"),
    };

    private static Dictionary<string, object?> Code(string code) => new() { ["code"] = code };
}
