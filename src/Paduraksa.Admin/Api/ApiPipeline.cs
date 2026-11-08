using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The first middleware of the admin app (<c>UsePaduraksaAdminApi</c>, called before <c>UsePaduraksaAdminAuth</c>), so an exception from any middleware or endpoint behind it lands here. It turns what escapes an endpoint into answers a client can use and an operator can trace:
/// <list type="bullet">
/// <item>An unhandled exception is logged with the request's trace ID and answered as a 500 problem that carries the ID and nothing of the
/// exception (no message, no type, no stack). A request the server couldn't read (<see cref="BadHttpRequestException"/>: a body past the limit, a
/// malformed one) is answered with its own status.</item>
/// <item>Under <c>/api</c>, an answer with an error status and no body (an unknown route's 404, a wrong method's 405, a binding failure's 400)
/// gets the problem body it lacks.</item>
/// <item>The reference page gets its own content security policy (set before the response starts, so the security-headers middleware, which only adds a
/// default when none is set, leaves it alone). The headers the security middleware adds are added when the response starts, so they survive the
/// reset of a failed response.</item>
/// </list>
/// </summary>
internal sealed partial class ApiPipelineMiddleware(RequestDelegate next, ILogger<ApiPipelineMiddleware> logger)
{
    private const string ApiPrefix = "/api";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool api = IsApiPath(context.Request.Path);
        if (OpenApi.DocsContentSecurityPolicy.Applies(context.Request.Path))
        {
            context.Response.Headers.ContentSecurityPolicy = OpenApi.DocsContentSecurityPolicy.For(context);
        }

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
            return;
        }
        catch (BadHttpRequestException ex)
        {
            string badRequestTrace = ApiProblems.TraceIdOf(context);
            LogBadRequest(logger, badRequestTrace, ex.StatusCode);
            await WriteFailureAsync(context, ex.StatusCode, ReadFailureDetail(ex.StatusCode)).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            string traceId = ApiProblems.TraceIdOf(context);
            LogUnhandled(logger, ex, traceId, context.Request.Method, context.Request.Path);
            await WriteFailureAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "The server could not complete the request. The details are in the server log under the trace ID.").ConfigureAwait(false);
            return;
        }

        if (api && !context.Response.HasStarted && context.Response.StatusCode >= 400 && context.Response.ContentLength is null or 0 && context.Response.ContentType is null)
        {
            await ApiProblems.WriteAsync(context, ApiProblems.Build(context, context.Response.StatusCode, EmptyAnswerDetail(context.Response.StatusCode))).ConfigureAwait(false);
        }
    }

    /// <summary><c>/api</c> and anything below it.</summary>
    internal static bool IsApiPath(PathString path) =>
        path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase);

    private static string? EmptyAnswerDetail(int status) => status switch
    {
        StatusCodes.Status404NotFound => "There is no such resource.",
        StatusCodes.Status405MethodNotAllowed => "This resource doesn't support that method; see the Allow header.",
        StatusCodes.Status400BadRequest => "The request could not be understood: a parameter is missing or malformed.",
        StatusCodes.Status406NotAcceptable => "The API answers with application/json.",
        StatusCodes.Status415UnsupportedMediaType => "Send the body as application/json (UTF-8).",
        _ => null,
    };

    private static string ReadFailureDetail(int status) => status switch
    {
        StatusCodes.Status413PayloadTooLarge => "The request body is larger than this endpoint accepts.",
        StatusCodes.Status408RequestTimeout => "The request body did not arrive in time.",
        _ => "The request could not be read.",
    };

    private static async Task WriteFailureAsync(HttpContext context, int status, string detail)
    {
        if (context.Response.HasStarted)
        {
            // Too late to say anything: cut the connection so the client doesn't take a half answer for a whole one.
            context.Abort();
            return;
        }

        context.Response.Clear();
        await ApiProblems.WriteAsync(context, ApiProblems.Build(context, status, detail)).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path} (trace {TraceId}).")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string traceId, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request {TraceId} could not be read: status {Status}.")]
    private static partial void LogBadRequest(ILogger logger, string traceId, int status);
}
