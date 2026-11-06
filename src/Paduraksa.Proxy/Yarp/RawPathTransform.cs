using Microsoft.AspNetCore.Http.Features;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Forwards the request path exactly as the client sent it. YARP builds the upstream URI from the decoded
/// <c>HttpRequest.Path</c> and escapes it again, which turns an encoded slash into <c>%252F</c> and breaks
/// GitLab-style <c>group%2Fproject</c> paths. This request transform sets the URI itself from the raw request target
/// (path part only, before <c>?</c>) and the query as YARP would send it, with canonicalisation disabled so nothing is
/// re-escaped or unescaped. M1 strips no route prefixes, so the raw path is appended to the destination as is.
/// <para>Absolute-form and asterisk-form targets (no leading <c>/</c>) keep YARP's default.</para>
/// </summary>
internal static class RawPathTransform
{
    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        string? rawTarget = context.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (rawTarget is not { Length: > 0 } || rawTarget[0] != '/')
        {
            return ValueTask.CompletedTask;
        }

        int queryStart = rawTarget.IndexOf('?', StringComparison.Ordinal);
        ReadOnlySpan<char> rawPath = queryStart < 0 ? rawTarget : rawTarget.AsSpan(0, queryStart);
        ReadOnlySpan<char> prefix = context.DestinationPrefix.AsSpan().TrimEnd('/');
        string query = context.Query.QueryString.Value ?? string.Empty;
        context.ProxyRequest.RequestUri = new Uri(string.Concat(prefix, rawPath, query), in Verbatim);
        return ValueTask.CompletedTask;
    }
}
