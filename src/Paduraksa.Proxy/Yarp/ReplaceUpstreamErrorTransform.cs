using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Answers with the host's error page in place of an upstream's own error, for a host that asked for it
/// (<see cref="UpstreamErrorReplacement"/>). It is a YARP response transform: it runs when the upstream's status and headers are in
/// hand and before any of its body is read, which is the moment to decide. The body is then never read: the transform answers itself and
/// suppresses the copy, so YARP disposes the upstream response unread and nothing is buffered.
/// </summary>
/// <remarks>
/// <para><b>What it replaces.</b> A status that has a page (<see cref="ErrorPageStatuses.Statuses"/>) whose content type the host's mode takes:
/// <c>text/html</c> for <see cref="UpstreamErrorReplacement.Html"/>, anything for <see cref="UpstreamErrorReplacement.Any"/>. It never touches an
/// upgrade request (the handshake's own failure is the client library's to read), an <c>text/event-stream</c> or gRPC response, or one that
/// has already started, however the host is set.</para>
/// <para><b>What stays.</b> The status, and every header that isn't about the body that was dropped: cookies, <c>Retry-After</c>, the gate's
/// own headers. The body's headers (<c>Content-Length</c>, <c>Content-Encoding</c>, <c>ETag</c> and the like) go, since they describe bytes
/// that are not sent.</para>
/// <para><b>Cost.</b> A successful response pays one integer comparison here; the rest is behind a status of 400 or more and a host
/// that turned the feature on. No wrapper, continuation or allocation is added to the path that doesn't replace.</para>
/// </remarks>
internal static class ReplaceUpstreamErrorTransform
{
    // The headers that describe a body this answer replaces.
    private static readonly string[] BodyHeaders =
    [
        HeaderNames.ContentLength, HeaderNames.ContentEncoding, HeaderNames.ContentLanguage, HeaderNames.ContentLocation, HeaderNames.ContentRange,
        HeaderNames.ContentDisposition, "Content-MD5", "Digest", "Content-Digest", HeaderNames.ETag, HeaderNames.LastModified, HeaderNames.AcceptRanges,
        HeaderNames.TransferEncoding, HeaderNames.ContentType,
    ];

    public static ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProxyResponse is not { } upstream || (int)upstream.StatusCode < 400)
        {
            return ValueTask.CompletedTask;
        }

        var http = context.HttpContext;
        if (http.Features.Get<IGateFeature>()?.State is not { Host: { } host }
            || host.ErrorPages.ReplaceUpstream == UpstreamErrorReplacement.Off
            || ErrorPages.Replacement((int)upstream.StatusCode) is not { } answer
            || http.Response.HasStarted
            || !Takes(host.ErrorPages.ReplaceUpstream, upstream)
            || http.Request.Headers.Upgrade.Count > 0)
        {
            return ValueTask.CompletedTask;
        }

        context.SuppressResponseBody = true;
        var headers = http.Response.Headers;
        foreach (var name in BodyHeaders)
        {
            headers.Remove(name);
        }

        var write = ResponseWriter.WriteAsync(http, answer);
        return write.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(write);
    }

    /// <summary>Whether the mode takes this response by its content type; an event stream and gRPC never.</summary>
    private static bool Takes(UpstreamErrorReplacement mode, HttpResponseMessage upstream)
    {
        var mediaType = upstream.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && (mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return mode == UpstreamErrorReplacement.Any
            || (mediaType is not null && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase));
    }
}
