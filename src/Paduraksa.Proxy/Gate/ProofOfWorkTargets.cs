using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Paduraksa.Proxy.Gate;

/// <summary>The paths and request shapes the proof-of-work gate treats specially.</summary>
internal static class ProofOfWorkTargets
{
    /// <summary>The endpoint the challenge page posts to; the gate answers it itself, before route matching.</summary>
    public const string VerifyPath = "/.well-known/paduraksa/pow";

    /// <summary>Never challenged: a crawler must be able to read the host's robots file.</summary>
    public const string RobotsPath = "/robots.txt";

    public static bool IsVerifyPath(ReadOnlySpan<char> path) => path.Equals(VerifyPath, StringComparison.OrdinalIgnoreCase);

    public static bool IsRobots(ReadOnlySpan<char> path) => path.Equals(RobotsPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the request is one a browser made for a page: <c>GET</c> or <c>HEAD</c> accepting <c>text/html</c>.</summary>
    public static bool IsBrowserRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && AcceptsHtml(request.Headers.Accept);

    /// <summary>
    /// The target to come back to: the raw request target (path and query as the client sent them) when it is origin-form,
    /// else the decoded path and query.
    /// </summary>
    public static string OriginalTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        if (raw is { Length: > 0 } && raw[0] == '/')
        {
            return raw;
        }

        return string.Concat(context.Request.Path.Value ?? "/", context.Request.QueryString.Value);
    }

    private static bool AcceptsHtml(Microsoft.Extensions.Primitives.StringValues accept)
    {
        for (var i = 0; i < accept.Count; i++)
        {
            if (accept[i] is not { } line)
            {
                continue;
            }

            var rest = line.AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.IndexOf(',');
                var media = comma < 0 ? rest : rest[..comma];
                rest = comma < 0 ? default : rest[(comma + 1)..];
                var semicolon = media.IndexOf(';');
                if (semicolon >= 0)
                {
                    media = media[..semicolon];
                }

                if (media.Trim().Equals("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
