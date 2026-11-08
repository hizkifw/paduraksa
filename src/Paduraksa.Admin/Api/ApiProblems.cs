using System.Diagnostics;

using Microsoft.AspNetCore.Http;

using Paduraksa.Admin.Json;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;

namespace Paduraksa.Admin.Api;

/// <summary>
/// RFC 9457 problem details for every error of the API: one shape (<see cref="ApiProblem"/>), one content type, the same
/// <c>type</c> and <c>title</c> per status (what ASP.NET's own problem details use, so a 401 from the permission requirement and a 422 from here
/// differ only in what they add). The detail is always a sentence written for the caller, never an exception's text.
/// </summary>
public static class ApiProblems
{
    /// <summary>The media type of an error answer.</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>A problem with <paramref name="status"/>, as a result.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="detail">A sentence for the caller.</param>
    /// <param name="instance">The resource the problem is about.</param>
    /// <param name="errors">Validation errors.</param>
    /// <param name="relatedErrors">Errors elsewhere in the config.</param>
    /// <param name="warnings">What compiles but is probably not intended.</param>
    /// <param name="conflict">What is current, for a version mismatch.</param>
    /// <param name="code">A stable machine-readable kind.</param>
    /// <param name="title">A title other than the status's fixed one (the auth pipeline's, for example).</param>
    /// <param name="revision">The stored revision (publish failed).</param>
    /// <param name="version">The object's new version (publish failed).</param>
    public static IResult Problem(
        int status,
        string? detail = null,
        string? instance = null,
        IReadOnlyList<ConfigError>? errors = null,
        IReadOnlyList<ConfigError>? relatedErrors = null,
        IReadOnlyList<ConfigWarning>? warnings = null,
        ConfigConflict? conflict = null,
        string? code = null,
        string? title = null,
        RevisionRef? revision = null,
        long? version = null) =>
        new ProblemResult(status, detail, instance, errors, relatedErrors, warnings, conflict, code, title, revision, version);

    /// <summary>A 422 for what is wrong with a request body: the service's errors, verbatim.</summary>
    public static IResult Invalid(IReadOnlyList<ConfigError> errors, IReadOnlyList<ConfigError>? relatedErrors = null, IReadOnlyList<ConfigWarning>? warnings = null, string? instance = null, string? code = null) =>
        Problem(StatusCodes.Status422UnprocessableEntity, "The request is well-formed but can't be applied; see errors.", instance, errors, relatedErrors, warnings, code: code);

    /// <summary>The problem for <paramref name="status"/> as data, with the request's trace ID.</summary>
    public static ApiProblem Build(
        HttpContext http,
        int status,
        string? detail = null,
        string? instance = null,
        IReadOnlyList<ConfigError>? errors = null,
        IReadOnlyList<ConfigError>? relatedErrors = null,
        IReadOnlyList<ConfigWarning>? warnings = null,
        ConfigConflict? conflict = null,
        string? code = null,
        string? title = null,
        RevisionRef? revision = null,
        long? version = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new ApiProblem(
            TypeOf(status),
            title ?? TitleOf(status),
            status,
            detail,
            instance,
            TraceIdOf(http),
            errors is { Count: > 0 } ? errors : null,
            relatedErrors is { Count: > 0 } ? relatedErrors : null,
            warnings is { Count: > 0 } ? warnings : null,
            conflict,
            code,
            revision,
            version);
    }

    /// <summary>Writes <paramref name="problem"/> as the response (headers not yet sent).</summary>
    public static Task WriteAsync(HttpContext http, ApiProblem problem)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(problem);
        http.Response.StatusCode = problem.Status;
        return http.Response.WriteAsJsonAsync(problem, AdminJsonContext.Default.ApiProblem, ContentType, http.RequestAborted);
    }

    /// <summary>The id that ties a response to the log: the trace's, else ASP.NET's request identifier.</summary>
    public static string TraceIdOf(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Activity.Current?.Id ?? http.TraceIdentifier;
    }

    /// <summary>The <c>type</c> of a problem: the RFC 9110 section of the status.</summary>
    public static string TypeOf(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        406 => "https://tools.ietf.org/html/rfc9110#section-15.5.7",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        412 => "https://tools.ietf.org/html/rfc9110#section-15.5.13",
        413 => "https://tools.ietf.org/html/rfc9110#section-15.5.14",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        422 => "https://tools.ietf.org/html/rfc9110#section-15.5.21",
        428 => "https://tools.ietf.org/html/rfc6585#section-3",
        429 => "https://tools.ietf.org/html/rfc6585#section-4",
        500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
        502 => "https://tools.ietf.org/html/rfc9110#section-15.6.3",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        _ => "about:blank",
    };

    /// <summary>The <c>title</c> of a problem: fixed per status.</summary>
    public static string TitleOf(int status) => status switch
    {
        400 => "Bad request",
        401 => "Authentication required",
        403 => "Permission required",
        404 => "Not found",
        405 => "Method not allowed",
        406 => "Not acceptable",
        409 => "Conflict",
        412 => "Precondition failed",
        413 => "Payload too large",
        415 => "Unsupported media type",
        422 => "Unprocessable request",
        428 => "Precondition required",
        429 => "Too many requests",
        500 => "Unexpected error",
        502 => "Bad gateway",
        503 => "Service unavailable",
        _ => "Error",
    };

    private sealed class ProblemResult(
        int status,
        string? detail,
        string? instance,
        IReadOnlyList<ConfigError>? errors,
        IReadOnlyList<ConfigError>? relatedErrors,
        IReadOnlyList<ConfigWarning>? warnings,
        ConfigConflict? conflict,
        string? code,
        string? title,
        RevisionRef? revision,
        long? version) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            WriteAsync(httpContext, Build(httpContext, status, detail, instance, errors, relatedErrors, warnings, conflict, code, title, revision, version));
    }
}
