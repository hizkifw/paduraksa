using System.IO.Compression;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Http;

/// <summary>
/// A host's compression, compiled (docs/design/hot-path.md "Response compression"): which forwarded responses are compressed. The
/// decision is made when the response starts, from its own headers: a text-like content type, no <c>Content-Encoding</c> (the
/// upstream's own compression passes through), no <c>Content-Range</c>, no <c>Cache-Control: no-transform</c> (the upstream asked
/// that intermediaries leave the body alone), a status that has a body, and a declared length of at least <see cref="MinimumSize"/>
/// (a body without one is compressed). 0 B: span comparisons on strings Kestrel already holds.
/// </summary>
public sealed class CompressionPolicy(int minimumSize)
{
    public int MinimumSize { get; } = minimumSize;

    /// <summary>Whether the response about to start is compressed.</summary>
    public bool Compresses(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var status = response.StatusCode;
        if (status is < 200 or 204 or 206 or 304)
        {
            return false;
        }

        var headers = response.Headers;
        if (headers.ContainsKey(HeaderNames.ContentEncoding) || headers.ContainsKey(HeaderNames.ContentRange))
        {
            return false;
        }

        if (response.ContentLength is { } length && length < MinimumSize)
        {
            return false;
        }

        foreach (var directive in headers.CacheControl)
        {
            if (directive is not null && directive.AsSpan().IndexOf("no-transform", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
        }

        return IsCompressible(headers.ContentType.Count == 1 ? headers.ContentType[0] : null);
    }

    /// <summary>Whether a response of <paramref name="contentType"/> is worth compressing: text, and the structured formats that are
    /// text. An event stream is text that must reach the client as it is written, so it never is; nor is anything without a type.</summary>
    public static bool IsCompressible(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        var media = contentType.AsSpan();
        var parameters = media.IndexOf(';');
        if (parameters >= 0)
        {
            media = media[..parameters];
        }

        media = media.Trim();
        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return !media.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);
        }

        if (media.EndsWith("+json", StringComparison.OrdinalIgnoreCase) || media.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/x-javascript", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/wasm", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/vnd.ms-fontobject", StringComparison.OrdinalIgnoreCase)
            || media.Equals("font/ttf", StringComparison.OrdinalIgnoreCase)
            || media.Equals("font/otf", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// ASP.NET's response compression, gated per host. The built-in middleware handles what is easy to get wrong (choosing the
/// encoding from <c>Accept-Encoding</c>'s q-values, <c>Vary: Accept-Encoding</c>, dropping <c>Content-Length</c>, flushing the
/// encoder when the response flushes, finishing it at the end); this stage only decides who gets to it, so a host with
/// compression off, a request without <c>Accept-Encoding</c>, a <c>HEAD</c> and an upgrade pay nothing.
/// </summary>
/// <remarks>
/// It sits after the gate, so it sees forwarded requests only: the gate's own answers are small, precomputed and written before
/// it. It sits inside the access log's body counter, so the bytes counted are the compressed ones. Brotli is preferred over gzip at
/// equal q-value, both at their fastest level; .NET 10 has no zstd built in.
/// </remarks>
public sealed class ResponseCompressionStage
{
    private readonly RequestDelegate _next;
    private readonly ResponseCompressionMiddleware _compression;

    public ResponseCompressionStage(RequestDelegate next, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(services);
        _next = next;
        ResponseCompressionOptions options = new() { EnableForHttps = true };
        options.Providers.Add(new BrotliCompressionProvider(Options.Create(new BrotliCompressionProviderOptions { Level = CompressionLevel.Fastest })));
        options.Providers.Add(new GzipCompressionProvider(Options.Create(new GzipCompressionProviderOptions { Level = CompressionLevel.Fastest })));
        _compression = new ResponseCompressionMiddleware(next, new HostCompressionProvider(services, Options.Create(options)));
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;
        if (context.Features.Get<IGateFeature>()?.State.Host?.Compression is null
            || request.Headers.AcceptEncoding.Count == 0
            || HttpMethods.IsHead(request.Method)
            || HttpMethods.IsConnect(request.Method)
            || context.Features.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true }
            || context.Features.Get<IHttpUpgradeFeature>() is { IsUpgradableRequest: true })
        {
            return _next(context);
        }

        return _compression.Invoke(context);
    }

    /// <summary>The built-in provider with the host's own decision of what is compressed.</summary>
    private sealed class HostCompressionProvider(IServiceProvider services, IOptions<ResponseCompressionOptions> options)
        : ResponseCompressionProvider(services, options)
    {
        public override bool ShouldCompressResponse(HttpContext context)
        {
            // The base provider's own checks are replaced, so the one that isn't about content is kept: a later middleware or
            // endpoint that says not to compress (IHttpsCompressionFeature) is honoured, on HTTP as on HTTPS.
            // The proxy's own pages (a replaced upstream error, YARP's 502) are written inside the forward, so they reach here too.
            if (context.Features.Get<IHttpsCompressionFeature>()?.Mode == HttpsCompressionMode.DoNotCompress
                || context.Features.Get<IGateFeature>()?.State is not { ProxyAnswered: false, Host.Compression: { } policy }
                || !policy.Compresses(context.Response))
            {
                return false;
            }

            // Only once an encoding is chosen is the response a different representation. The middleware asks for the encoding at its
            // own times (YARP's DisableBuffering asks before the upstream's headers are copied), so the question is asked again here,
            // when the headers are in: a client that takes only identity or deflate keeps the upstream's representation and headers.
            if (base.GetCompressionProvider(context) is null)
            {
                return false;
            }

            DescribeNewRepresentation(context.Response.Headers);
            return true;
        }

        /// <summary>
        /// The headers that describe the upstream's bytes, which are no longer what is sent. A strong ETag promises the same bytes: it
        /// is kept but made weak, which still lets <c>If-None-Match</c> revalidate (a weak comparison), at one string per compressed
        /// response that carried a strong one. <c>Accept-Ranges</c> would invite a range of bytes the client never sees whole, and a
        /// digest is of the uncompressed body. The middleware itself drops <c>Content-Length</c> and <c>Content-MD5</c>.
        /// </summary>
        private static void DescribeNewRepresentation(IHeaderDictionary headers)
        {
            if (headers.ETag is { Count: 1 } tag && tag[0] is ['"', ..] strong)
            {
                headers.ETag = "W/" + strong;
            }

            headers.Remove(HeaderNames.AcceptRanges);
            headers.Remove("Content-Digest");
            headers.Remove("Digest");
            headers.Remove("Repr-Digest");
        }
    }
}
