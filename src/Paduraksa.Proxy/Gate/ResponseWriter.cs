using System.IO.Pipelines;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// Writes a <see cref="PrecomputedResponse"/> (spike S3 §6). Headers go through the typed properties
/// (field-backed in Kestrel), the body straight into the output pipe. 0 B on Kestrel for fixed responses; a
/// request-dependent <c>Location</c> costs that one string.
/// </summary>
internal static class ResponseWriter
{
    /// <summary>Sets the status and headers. Synchronous and allocation-free unless the <c>Location</c> is per request.</summary>
    public static void ApplyHeaders(HttpContext context, PrecomputedResponse response)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(response);

        HttpResponse http = context.Response;
        http.StatusCode = response.StatusCode;
        IHeaderDictionary headers = http.Headers;
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

        // 204/205/304 carry no body and no Content-Length (their bodies are rejected at compile time).
        if (!PrecomputedResponse.ForbidsBody(response.StatusCode))
        {
            headers.ContentLength = response.Body.Length;
        }
    }

    /// <summary>Sets the headers and writes the body (none for <c>HEAD</c>). Completes synchronously in the common case.</summary>
    public static Task WriteAsync(HttpContext context, PrecomputedResponse response)
    {
        ApplyHeaders(context, response);
        if (response.Body.Length == 0 || HttpMethods.IsHead(context.Request.Method))
        {
            return Task.CompletedTask;
        }

        ValueTask<FlushResult> write = context.Response.BodyWriter.WriteAsync(response.Body, context.RequestAborted);
        return write.IsCompletedSuccessfully ? Task.CompletedTask : AwaitAsync(write);

        static async Task AwaitAsync(ValueTask<FlushResult> pending) => await pending.ConfigureAwait(false);
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
            StringValues hostHeader = context.Request.Headers.Host;
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
            string? raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
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
