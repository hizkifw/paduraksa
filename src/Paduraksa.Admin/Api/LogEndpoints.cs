using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    private static readonly byte[] OpenComment = ": open\n\n"u8.ToArray();
    private static readonly byte[] HeartbeatComment = ": heartbeat\n\n"u8.ToArray();

    public static void Map(RouteGroupBuilder api)
    {
        var logs = api.MapGroup("/logs").WithTags("Logs");

        logs.MapGet("/", List)
            .WithName("ListLogFiles").WithSummary("List the log files")
            .WithDescription("Every log file in the logs directory: the live access.log and error.log and their rotated, gzip-compressed predecessors. Newest first within each log.")
            .Produces<IReadOnlyList<LogFileSummary>>().ProducesApiProblems(500)
            .RequirePermission(Permission.LogsRead);

        logs.MapGet("/tail", Stream)
            .WithName("StreamAccessLog").WithSummary("Stream the access log live")
            .WithDescription("Server-Sent Events (text/event-stream) of the requests as they end, newest last: each default event is one JSON entry (id: its sequence number), the entries as access.log would show them "
                + "(same caps, control characters escaped, sensitive query parameters redacted); a host with its access log off never appears. "
                + "Filters: host (a configured host's name), unmatched=true (requests no host owned), statusClass (1-5, or 2xx..5xx), status, ip (the client address or a leading part), method, path (contains). "
                + "backlog (0-1000, default 0) first sends the newest matching entries that the process still holds. "
                + "A reader that falls behind loses its oldest entries and is told with an event: skipped whose data is {\"count\":N}. A comment line (: heartbeat) every 15 seconds keeps the stream open. "
                + "The stream ends when the client disconnects. Each stream holds a buffer of 2048 entries and the process serves 32 at once; the 33rd is refused with 503.")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream").ProducesApiProblems(400, 503)
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
        if (!TryGetLines(http, out var lines, out var problem))
        {
            return problem!;
        }

        var filter = http.Request.Query["filter"].ToString();
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
        if (store.OpenRead(name, out var length) is not { } stream)
        {
            return ApiProblems.Problem(StatusCodes.Status404NotFound, "There is no such log file.");
        }

        http.Response.OnCompleted(() =>
        {
            stream.Dispose();
            return Task.CompletedTask;
        });

        var contentType = name.EndsWith(".gz", StringComparison.Ordinal) ? "application/gzip" : "text/plain; charset=utf-8";
        return Results.Stream(stream, contentType, fileDownloadName: name, enableRangeProcessing: false);
    }

    /// <summary>Streams the entries as they come, until the client goes away (see the endpoint's description).</summary>
    private static async Task<IResult> Stream(HttpContext http, LogTailService service)
    {
        if (!TryReadFilter(http.Request.Query, out var filter, out var backlog, out var problem))
        {
            return problem!;
        }

        using var stream = service.Open(filter, backlog);
        if (stream is null)
        {
            http.Response.Headers.RetryAfter = "5";
            return ApiProblems.Problem(StatusCodes.Status503ServiceUnavailable, "Too many live tails are open; close one and try again.");
        }

        var response = http.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        // A reverse proxy in front of the admin would otherwise hold the events back until its buffer fills.
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ct = http.RequestAborted;
        try
        {
            // Headers go out with the first write: a client sees the stream is open before the first request arrives.
            await response.BodyWriter.WriteAsync(OpenComment, ct).ConfigureAwait(false);
            foreach (var entry in stream.Backlog)
            {
                await WriteEntryAsync(response, entry, ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(service.Heartbeat);
                bool available;
                try
                {
                    available = await stream.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await response.BodyWriter.WriteAsync(HeartbeatComment, ct).ConfigureAwait(false);
                    continue;
                }

                if (!available)
                {
                    break;
                }

                if (stream.TakeSkipped() is > 0 and var skipped)
                {
                    await WriteSkippedAsync(response, skipped, ct).ConfigureAwait(false);
                }

                // One flush for what is waiting, bounded so a busy stream still yields between writes.
                for (var batch = 0; batch < 256 && stream.TryRead(out var entry); batch++)
                {
                    await WriteEntryAsync(response, entry, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away: the stream is disposed with the request.
        }
        catch (IOException) when (ct.IsCancellationRequested || http.Response.HasStarted)
        {
            // A write to a connection that closed.
        }

        return Results.Empty;
    }

    private static async Task WriteEntryAsync(HttpResponse response, LogTailEntry entry, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(entry, AdminJsonContext.Default.LogTailEntry);
        var writer = response.BodyWriter;
        writer.Write("id: "u8);
        Utf8Formatter.TryFormat(entry.Sequence, writer.GetSpan(24), out var written);
        writer.Advance(written);
        writer.Write("\ndata: "u8);
        writer.Write(json);
        writer.Write("\n\n"u8);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSkippedAsync(HttpResponse response, long skipped, CancellationToken cancellationToken)
    {
        var writer = response.BodyWriter;
        writer.Write("event: skipped\ndata: "u8);
        writer.Write(JsonSerializer.SerializeToUtf8Bytes(new LogTailSkipped(skipped), AdminJsonContext.Default.LogTailSkipped));
        writer.Write("\n\n"u8);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tail's query parameters; every refusal is a 400 problem naming the parameter.</summary>
    private static bool TryReadFilter(IQueryCollection query, out LogTailFilter filter, out int backlog, out IResult? problem)
    {
        filter = new LogTailFilter();
        backlog = 0;
        problem = null;
        int? statusClass = null;
        int? status = null;
        var unmatched = false;
        if (query["unmatched"].ToString() is { Length: > 0 } flag && !bool.TryParse(flag, out unmatched))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "unmatched must be true or false.");
            return false;
        }

        if (query["statusClass"].ToString() is { Length: > 0 } classText)
        {
            var digit = classText.Length == 3 && classText.EndsWith("xx", StringComparison.OrdinalIgnoreCase) ? classText[..1] : classText;
            if (!int.TryParse(digit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 1 or > 5)
            {
                problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "statusClass must be 1 to 5 (or 1xx to 5xx).");
                return false;
            }

            statusClass = parsed;
        }

        if (query["status"].ToString() is { Length: > 0 } statusText)
        {
            if (!int.TryParse(statusText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 100 or > 599)
            {
                problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, "status must be 100 to 599.");
                return false;
            }

            status = parsed;
        }

        if (query["backlog"].ToString() is { Length: > 0 } backlogText
            && (!int.TryParse(backlogText, NumberStyles.None, CultureInfo.InvariantCulture, out backlog) || backlog > LogTailService.MaxBacklog))
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, $"backlog must be between 0 and {LogTailService.MaxBacklog}.");
            return false;
        }

        filter = new LogTailFilter(
            Text(query["host"]), unmatched, statusClass, status, Text(query["ip"]), Text(query["method"]), Text(query["path"]));
        if (filter.Problem() is { } wrong)
        {
            problem = ApiProblems.Problem(StatusCodes.Status400BadRequest, wrong);
            return false;
        }

        return true;
    }

    private static string? Text(Microsoft.Extensions.Primitives.StringValues values) => values.ToString() is { Length: > 0 } text ? text : null;

    /// <summary><c>?lines=</c> (1 to 1000; absent is <see cref="LogFileStore.DefaultTailLines"/>).</summary>
    private static bool TryGetLines(HttpContext http, out int lines, out IResult? problem)
    {
        problem = null;
        lines = LogFileStore.DefaultTailLines;
        var text = http.Request.Query["lines"].ToString();
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
