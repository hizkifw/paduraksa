using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>How a route's <see cref="CompiledRoute.Path"/> is compared with the request path.</summary>
public enum RouteMatchKind : byte
{
    /// <summary>
    /// Segment-aware prefix: <c>/api</c> matches <c>/api</c>, <c>/api/</c> and <c>/api/x</c>, not <c>/apix</c>.
    /// A prefix ending in <c>/</c> matches by plain prefix, so <c>/</c> matches everything.
    /// </summary>
    Prefix,

    /// <summary>The whole path, nothing more.</summary>
    Exact,
}

/// <summary>
/// One entry of a proxy host's ordered route list (PLAN §6.2, §7.2 C). Matching runs in the gate on the request
/// path span; YARP never matches anything.
/// <para><b>Case:</b> paths are compared <b>ignoring ASCII case</b>. Upstreams such as IIS/ASP.NET treat paths
/// case-insensitively, so a case-sensitive match would let <c>/ADMIN</c> slip past a policy that guards
/// <c>/admin</c>. Over-matching a policy-bearing route is the safe side. The path is the one Kestrel decoded and
/// normalised (dot segments removed, <c>%2F</c> kept encoded); the query string never takes part.</para>
/// </summary>
public sealed class CompiledRoute
{
    /// <summary>Stable identity (the route's key; <c>RouteId</c> from M2). Also the YARP route ID.</summary>
    public required string Key { get; init; }

    public required RouteMatchKind MatchKind { get; init; }

    /// <summary>Starts with <c>/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Index into <see cref="RuntimeSnapshot.YarpEndpoints"/>; valid for this snapshot only.</summary>
    public required int Slot { get; init; }

    /// <summary>The effective chain for this route: the host's policies followed by the route's own, already flattened.</summary>
    public required IRequestPolicy[] Policies { get; init; }

    /// <summary>Does this route take <paramref name="path"/>? An empty path counts as <c>/</c>.</summary>
    public bool Matches(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            path = "/";
        }

        string route = Path;
        if (MatchKind == RouteMatchKind.Exact)
        {
            return path.Equals(route, StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith(route, StringComparison.OrdinalIgnoreCase)
            && (path.Length == route.Length || route[^1] == '/' || path[route.Length] == '/');
    }
}
