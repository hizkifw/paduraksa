using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>The one path the gate answers itself from the host's compiled <c>robots.txt</c>.</summary>
internal static class RobotsTargets
{
    public const string Path = "/robots.txt";

    /// <summary>
    /// Whether the path is <c>/robots.txt</c> in the canonical matching view, the one routes match on, so a crawler that
    /// sends a redundant spelling gets the file the host would serve at the plain one. A query string does not matter:
    /// crawlers send none, and the file is the same whatever follows the path.
    /// </summary>
    public static bool IsPath(ReadOnlySpan<char> path) =>
        path.Length >= Path.Length
        && path.Contains("robots", StringComparison.OrdinalIgnoreCase)
        && RoutePath.MatchesCanonical(path, Path);

    /// <summary>Only <c>GET</c> and <c>HEAD</c> are answered: any other method is a request for the upstream.</summary>
    public static bool IsRequest(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) && IsPath(request.Path.Value.AsSpan());
}
