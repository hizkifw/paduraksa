using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// Security headers on every admin response (PLAN §10.1): <c>nosniff</c>, no referrer, no framing, a CSP of
/// <c>default-src 'none'; frame-ancestors 'none'</c>, <c>no-store</c> on <c>/api/</c>, and HSTS where the request came in over TLS (not for a loopback host name).
/// Headers are added when the response starts, and a response that set its own CSP (the docs page, the dashboard) keeps it.
/// </summary>
internal sealed class AdminSecurityHeadersMiddleware(RequestDelegate next, AdminPipelineHooks hooks)
{
    internal const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    private const string Hsts = "max-age=31536000";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            (HttpContext http, AdminPipelineHooks hookList) = ((HttpContext, AdminPipelineHooks))state;
            IHeaderDictionary headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XFrameOptions = "DENY";
            if (!headers.ContainsKey("Content-Security-Policy") && !IsCspExempt(http.Request.Path, hookList))
            {
                headers.ContentSecurityPolicy = ApiContentSecurityPolicy;
            }

            if (http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-store";
            }

            // Not for a loopback host: a browser would pin http://localhost for a year, and it is the address of the plain-HTTP fallback too.
            if (http.Request.IsHttps && !IsLoopbackHost(http.Request.Host.Host))
            {
                headers.StrictTransportSecurity = Hsts;
            }

            return Task.CompletedTask;
        }, (context, hooks));
        return next(context);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]";

    private static bool IsCspExempt(PathString path, AdminPipelineHooks hooks) =>
        hooks.CspExemptPathPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Endpoint metadata raising (or lowering) the request body limit of one endpoint.</summary>
/// <param name="Bytes">The limit in bytes.</param>
public sealed record AdminBodyLimitMetadata(long Bytes);

/// <summary>Request body limits of the admin app: a small default suited to JSON, raised per endpoint where a body can be large.</summary>
public static class AdminRequestLimits
{
    /// <summary>Lets <paramref name="builder"/>'s endpoints accept bodies up to <paramref name="bytes"/> (a certificate upload, an import).</summary>
    public static TBuilder WithAdminBodyLimit<TBuilder>(this TBuilder builder, long bytes)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        builder.WithMetadata(new AdminBodyLimitMetadata(bytes));
        return builder;
    }
}

/// <summary>
/// Applies <see cref="AdminSecurityOptions.MaxRequestBodyBytes"/>, or the endpoint's <see cref="AdminBodyLimitMetadata"/>, before anything reads a
/// body. Runs after routing. Kestrel refuses a body whose declared length is over the limit before reading it; a body of unknown length
/// (chunked, or HTTP/2 without Content-Length) is counted here as it is read, because Kestrel's own enforcement of the limit mid-body
/// surfaces from the JSON reader as an unrelated 500.
/// </summary>
internal sealed class AdminRequestLimitMiddleware(RequestDelegate next, AdminSecurityOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        long limit = context.GetEndpoint()?.Metadata.GetMetadata<AdminBodyLimitMetadata>()?.Bytes ?? options.MaxRequestBodyBytes;
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        if (context.Request.ContentLength is null && context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: true })
        {
            context.Request.Body = new LimitedBodyStream(context.Request.Body, limit);
        }

        return next(context);
    }

    private sealed class LimitedBodyStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > limit ? throw new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge) : read;
        }
    }
}

/// <summary>
/// DNS rebinding defence for the loopback plain-HTTP listener: a page on <c>evil.example</c> whose name is re-pointed at 127.0.0.1 would
/// reach the admin app from the victim's browser, with the right <c>Origin</c> for its own name. On that listener only a <c>Host</c> of
/// <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c> with the listener's port is accepted; anything else is a 400 before anything else runs.
/// </summary>
internal sealed class AdminLoopbackHostMiddleware(RequestDelegate next, AdminSecurityOptions options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (options.LoopbackHttpPort > 0 && context.Connection.LocalPort == options.LoopbackHttpPort && !IsLoopbackHost(context.Request.Host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        return next(context);
    }

    private bool IsLoopbackHost(HostString host) =>
        host.HasValue
        && (host.Port ?? 80) == options.LoopbackHttpPort
        && (host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Host == "127.0.0.1" || host.Host == "[::1]");
}
