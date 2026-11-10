using System.IO.Pipelines;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>Writes a <see cref="PrecomputedResponse"/>. 0 B on Kestrel for fixed responses; a request-dependent
/// <c>Location</c> costs that one string.</summary>
internal static class ResponseWriter
{
    /// <summary>Sets the status and headers. Synchronous and allocation-free unless the <c>Location</c> is per request.</summary>
    public static void ApplyHeaders(HttpContext context, PrecomputedResponse response) =>
        ApplyHeaders(context, response, retryAfter: null);

    /// <summary>Sets the status, headers and, for a refusal that names one, the precomputed <c>Retry-After</c>.
    /// Synchronous and allocation-free unless the <c>Location</c> is per request.</summary>
    public static void ApplyHeaders(HttpContext context, PrecomputedResponse response, string? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        // A page-only answer has no text representation: the plain path (a client that didn't ask for HTML) must not
        // send the page as text/plain. Callers that only describe an answer (the simulator) check HasPlainBody first.
        if (!response.HasPlainBody)
        {
            throw new InvalidOperationException("A page-only response has no plain body; it must be written through the page path.");
        }

        var http = context.Response;
        http.StatusCode = response.StatusCode;
        var headers = http.Headers;
        if (retryAfter is not null)
        {
            headers.RetryAfter = retryAfter;
        }

        if (response.ContentType.Count > 0)
        {
            headers.ContentType = response.ContentType;
        }

        if (response.CacheControl.Count > 0)
        {
            headers.CacheControl = response.CacheControl;
        }

        if (response.Location.Count > 0)
        {
            headers.Location = response.Location;
        }
        else if (response.Redirect is { } redirect)
        {
            headers.Location = new StringValues(BuildLocation(context, redirect));
        }

        foreach (var header in response.Headers)
        {
            headers[header.Name] = header.Value;
        }

        // 204/205/304 carry no body and no Content-Length (their bodies are rejected at compile time).
        if (!PrecomputedResponse.ForbidsBody(response.StatusCode))
        {
            headers.ContentLength = response.Body.Length;
        }
    }

    /// <summary>Sets the headers and writes the body (none for <c>HEAD</c>). Completes synchronously in the common case.</summary>
    public static Task WriteAsync(HttpContext context, PrecomputedResponse response) =>
        WriteAsync(context, response, retryAfter: null);

    /// <summary>
    /// Sets the headers (and <paramref name="retryAfter"/>, when it has one) and writes the body (none for
    /// <c>HEAD</c>). A request that accepts <c>text/html</c> gets the response's built-in page, streamed straight from its
    /// template; anything else gets the precomputed body unchanged. Completes synchronously in the common case.
    /// </summary>
    public static Task WriteAsync(HttpContext context, PrecomputedResponse response, string? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        if (response.HtmlPage is { } page && MediaTypes.AcceptsHtml(context.Request.Headers.Accept))
        {
            ApplyPageHeaders(context, response, page, retryAfter);
            var slot0 = response.HtmlSlot0IsRequestId ? RequestIdOf(context) : response.HtmlSlot0;
            return WritePageAsync(context, page, slot0, response.HtmlSlot1, response.HtmlSlot2);
        }

        ApplyHeaders(context, response, retryAfter);
        if (response.Body.Length == 0 || HttpMethods.IsHead(context.Request.Method))
        {
            return Task.CompletedTask;
        }

        var write = context.Response.BodyWriter.WriteAsync(response.Body, context.RequestAborted);
        return write.IsCompletedSuccessfully ? Task.CompletedTask : AwaitAsync(write);

        static async Task AwaitAsync(ValueTask<FlushResult> pending) => await pending.ConfigureAwait(false);
    }

    /// <summary>
    /// The page's headers instead of the text answer's: its content type, the CSP and its kin, no length (the template
    /// writes as many bytes as the encoded slot values need), and the same <c>no-store</c> and <c>Retry-After</c>.
    /// A page answer carries no <c>Content-Length</c>: the template writes exactly the bytes the encoded slots need, and
    /// measuring a <c>HEAD</c> page just to send a length would mean rendering it. A <c>HEAD</c> page therefore has no
    /// length, unlike a <c>HEAD</c> plain answer, whose precomputed body carries one.
    /// </summary>
    private static void ApplyPageHeaders(HttpContext context, PrecomputedResponse response, HtmlPageTemplate page, string? retryAfter)
    {
        var http = context.Response;
        http.StatusCode = response.StatusCode;
        var headers = http.Headers;
        if (retryAfter is not null)
        {
            headers.RetryAfter = retryAfter;
        }

        headers.ContentType = HtmlPageTemplate.ContentType;
        if (response.CacheControl.Count > 0)
        {
            headers.CacheControl = response.CacheControl;
        }

        // YARP may have copied an upstream's length or framing before it failed; the page's own bytes are not those.
        headers.ContentLength = null;
        headers.Remove(HeaderNames.TransferEncoding);
        foreach (var header in response.Headers)
        {
            headers[header.Name] = header.Value;
        }

        foreach (var header in page.SecurityHeaders)
        {
            headers[header.Name] = header.Value;
        }
    }

    /// <summary>
    /// Starts the response (headers first), writes the page into the body pipe and flushes it. Starting explicitly is what
    /// keeps the page's bytes behind its headers; advancing them into an unstarted response would put the body before the
    /// status line. The flush is not optional: HTTP/1.1 and HTTP/2 send bytes left in the pipe when the request ends, but
    /// HTTP/3 drops them, and Safari got the challenge page and the error pages with no body at all.
    /// </summary>
    private static Task WritePageAsync(HttpContext context, HtmlPageTemplate page, string? slot0, string? slot1, string? slot2)
    {
        var start = context.Response.StartAsync();
        return start.IsCompletedSuccessfully
            ? WritePage(context, page, slot0, slot1, slot2)
            : AwaitStartAsync(start, context, page, slot0, slot1, slot2);

        static Task WritePage(HttpContext http, HtmlPageTemplate template, string? s0, string? s1, string? s2)
        {
            if (!HttpMethods.IsHead(http.Request.Method))
            {
                var writer = http.Response.BodyWriter;
                switch (template.SlotCount)
                {
                    case 1:
                        template.Write(writer, s0 ?? string.Empty);
                        break;
                    case 2:
                        template.Write(writer, s0 ?? string.Empty, s1 ?? string.Empty);
                        break;
                    default:
                        template.Write(writer, s0 ?? string.Empty, s1 ?? string.Empty, s2 ?? string.Empty);
                        break;
                }

                var flush = writer.FlushAsync(http.RequestAborted);
                if (!flush.IsCompletedSuccessfully)
                {
                    return flush.AsTask();
                }
            }

            return Task.CompletedTask;
        }

        static async Task AwaitStartAsync(Task start, HttpContext http, HtmlPageTemplate template, string? s0, string? s1, string? s2)
        {
            await start.ConfigureAwait(false);
            await WritePage(http, template, s0, s1, s2).ConfigureAwait(false);
        }
    }

    private static string RequestIdOf(HttpContext context)
    {
        var state = context.Features.Get<IGateFeature>()?.State;
        return state is { HasRequestId: true } ? state.RequestIdText() : context.TraceIdentifier;
    }

    /// <summary>
    /// Builds the per-request <c>Location</c>. With an origin-form request target (the normal case) it allocates only
    /// the resulting string; the rare fallback also allocates the re-escaped path.
    /// </summary>
    public static string BuildLocation(HttpContext context, RedirectTarget target)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(target);

        ReadOnlySpan<char> host = default;
        if (target.IncludeRequestHost)
        {
            var hostHeader = context.Request.Headers.Host;
            host = hostHeader.Count == 1 ? HostTable.HostName(hostHeader[0]) : default;
        }

        string? pathAndQuery = null;
        ReadOnlySpan<char> path = default;
        ReadOnlySpan<char> query = default;
        if (target.AppendPathAndQuery)
        {
            // Use the request target exactly as the client sent it (path and query, original encoding). An
            // absolute-form or asterisk-form target has no usable path there, so fall back to the decoded path,
            // escaped again, plus the query string.
            var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            if (raw is { Length: > 0 } && raw[0] == '/')
            {
                pathAndQuery = raw;
            }
            else
            {
                path = context.Request.Path.HasValue ? context.Request.Path.ToUriComponent() : "/";
                query = context.Request.QueryString.Value;
            }
        }

        return pathAndQuery is not null
            ? string.Concat(target.Prefix, host, target.PortSuffix, pathAndQuery)
            : string.Concat(target.Prefix, host, target.PortSuffix, string.Concat(path, query));
    }
}
