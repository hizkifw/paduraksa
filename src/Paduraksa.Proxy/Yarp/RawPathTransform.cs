using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Gate;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Forwards the request path exactly as the client sent it. YARP builds the upstream URI from the decoded
/// <c>HttpRequest.Path</c> and escapes it again, which turns an encoded slash into <c>%252F</c> and breaks
/// GitLab-style <c>group%2Fproject</c> paths. This request transform sets the URI itself from the raw request target
/// (path part only, before <c>?</c>) and the query as YARP would send it, with canonicalisation disabled so nothing is
/// re-escaped or unescaped. Without a rewrite the raw path is appended to the destination as is.
/// <para>A route's path rewrite (<see cref="Snapshot.PathRewrite"/>) strips and adds on that same raw path, so the URI is
/// still built in one concatenation and costs what it did without one. The gate already refused a request whose prefix
/// can't be stripped.</para>
/// <para>Absolute-form and asterisk-form targets (no leading <c>/</c>) keep YARP's default, a rewrite then working on the
/// path as YARP escapes it.</para>
/// </summary>
internal static class RawPathTransform
{
    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        var rewrite = context.HttpContext.Features.Get<IGateFeature>()?.State.Route?.Rewrite;
        var rawTarget = context.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        ReadOnlySpan<char> rawPath;
        if (rawTarget is { Length: > 0 } && rawTarget[0] == '/')
        {
            var queryStart = rawTarget.IndexOf('?', StringComparison.Ordinal);
            rawPath = queryStart < 0 ? rawTarget : rawTarget.AsSpan(0, queryStart);
        }
        else if (rewrite is not null)
        {
            rawPath = context.Path.ToUriComponent();
        }
        else
        {
            return ValueTask.CompletedTask;
        }

        var prefix = context.DestinationPrefix.AsSpan().TrimEnd('/');
        var query = context.Query.QueryString.Value ?? string.Empty;
        if (rewrite is null)
        {
            context.ProxyRequest.RequestUri = new Uri(string.Concat(prefix, rawPath, query), in Verbatim);
            return ValueTask.CompletedTask;
        }

        // The gate checked the strip; a path it couldn't strip never gets here, but forwarding it unchanged is the safe side.
        var end = Math.Max(rewrite.StripEnd(rawPath), 0);
        var rest = rawPath[end..];
        if (rest.IsEmpty && rewrite.AddPrefix.Length == 0)
        {
            rest = "/";
        }

        context.ProxyRequest.RequestUri = new Uri(string.Concat(prefix, rewrite.AddPrefix, rest, query), in Verbatim);
        return ValueTask.CompletedTask;
    }
}
