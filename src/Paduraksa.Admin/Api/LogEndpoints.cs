using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Paduraksa.Admin.Auth;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Logging;
using Paduraksa.Contracts.Logging;
using Paduraksa.Core.Logging;
using Paduraksa.Core.Model;

namespace Paduraksa.Admin.Api;

/// <summary>
/// The log files: what is in the logs directory, the last lines of one file, and a download. Reading the logs is a
/// read like the audit log's, so it needs <c>logs:read</c> and is not itself audited. Only names the writer produces are served
/// (<see cref="LogFileNames"/>), never a path, a link or anything else in the directory.
/// </summary>
internal static class LogEndpoints
{
    /// <summary>The longest filter accepted for a tail (<c>?filter=</c>).</summary>
    private const int MaxFilterLength = 128;

    public static void Map(RouteGroupBuilder api)
    {
        RouteGroupBuilder logs = api.MapGroup("/logs").WithTags("Logs");

        logs.MapGet("/", List)
            .WithName("ListLogFiles").WithSummary("List the log files")
            .WithDescription("Every log file in the logs directory: the live access.log and error.log and their rotated, gzip-compressed predecessors. Newest first within each log.")
            .Produces<IReadOnlyList<LogFileSummary>>().ProducesApiProblems(500)
            .RequirePermission(Permission.LogsRead);

        logs.MapGet("/{name}/tail", Tail)
            .WithName("TailLogFile").WithSummary("Read the last lines of a log file")
            .WithDescription("At most 1000 lines (200 by default) out of the last 1 MiB of a plain file, or of the first 8 Mi characters a compressed one is decompressed from, each line cut at 4096 characters, oldest first. "
                + "Optional filter (at most 128 characters, case-insensitive substring) keeps only the matching lines. A live file and a .gz are both readable.")
            .Produces<LogFileTail>().ProducesApiProblems(400, 404)
            .RequirePermission(Permission.LogsRead);

        logs.MapGet("/{name}", Download)
            .WithName("DownloadLogFile").WithSummary("Download a log file")
            .WithDescription("The file itself, as an attachment (text/plain, or application/gzip for a compressed one), streamed. The access log holds client IPs: treat it accordingly.")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .Produces(StatusCodes.Status200OK, contentType: "application/gzip")
            .ProducesApiProblems(404)
            .RequirePermission(Permission.LogsRead);
    }

    /// <summary>The directory is listed; one that exists but can't be read is a 500 problem, never an empty list that reads as "no logs".</summary>
    private static IResult List(ILogFileStore store)
    {
        try
        {
            return EndpointSupport.Json(store.List(), AdminJsonContext.Default.IReadOnlyListLogFileSummary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ApiProblems.Problem(StatusCodes.Status500InternalServerError, "The logs directory couldn't be read.");
        }
    }

    private static IResult Tail(HttpContext http, string name, ILogFileStore store)
    {
        if (!TryGetLines(http, out int lines, out IResult? problem))
        {
            return problem!;
        }

        string filter = http.Request.Query["filter"].ToString();
        if (filter.Length > MaxFilterLength)
        {
            return ApiProblems.Problem(StatusCodes.Status400BadRequest, $"filter is at most {MaxFilterLength} characters.");
        }

        return store.ReadTail(name, lines, filter) is { } tail
            ? EndpointSupport.Json(tail, AdminJsonContext.Default.LogFileTail)
            : ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such log file.");
    }

    /// <summary>Streams the file and disposes it when the answer is complete (the framework doesn't own a stream it didn't open).</summary>
    private static IResult Download(HttpContext http, string name, ILogFileStore store)
    {
        if (store.OpenRead(name, out long length) is not { } stream)
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such log file.");
        }

        http.Response.OnCompleted(() =>
        {
            stream.Dispose();
            return Task.CompletedTask;
        });

        string contentType = name.EndsWith(".gz", StringComparison.Ordinal) ? "application/gzip" : "text/plain; charset=utf-8";
        return Results.Stream(stream, contentType, fileDownloadName: name, enableRangeProcessing: false);
    }

    /// <summary><c>?lines=</c> (1 to 1000; absent is <see cref="LogFileStore.DefaultTailLines"/>).</summary>
    private static bool TryGetLines(HttpContext http, out int lines, out IResult? problem)
    {
        problem = null;
        lines = LogFileStore.DefaultTailLines;
        string text = http.Request.Query["lines"].ToString();
        if (text.Length == 0)
        {
            return true;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out lines)
            || lines is < 1 or > LogFileStore.MaxTailLines)
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, $"lines must be between 1 and {LogFileStore.MaxTailLines}.");
            return false;
        }

        return true;
    }
}
