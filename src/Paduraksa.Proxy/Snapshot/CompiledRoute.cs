using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>
/// One entry of a proxy host's ordered route list. Matching runs in the gate on the request
/// path span; YARP never matches anything.
/// <para><b>What is matched:</b> the canonical matching view of the decoded path (<see cref="RoutePath"/>: separators
/// <c>\</c>/<c>%2F</c>/<c>%5C</c>, repeated <c>/</c>, <c>;</c> parameters, trailing dots and spaces, dot segments),
/// compared <b>ignoring ASCII case</b>. Upstreams differ in how they read such spellings (IIS/ASP.NET ignore case,
/// Tomcat drops <c>;</c> parameters, Windows trims trailing dots), so the gate matches leniently: <c>/ADMIN</c>,
/// <c>//admin</c> and <c>/admin;x</c> all reach the policies of <c>/admin</c>. Over-matching a policy-bearing route
/// is the safe side. The request itself is forwarded as received; the query string never takes part.</para>
/// </summary>
public sealed class CompiledRoute
{
    /// <summary>Stable identity; its canonical string is the YARP route ID.</summary>
    public required RouteId Id { get; init; }

    public required RouteMatchKind MatchKind { get; init; }

    /// <summary>Starts with <c>/</c> and is its own canonical view (the compiler rejects anything else).</summary>
    public required string Path { get; init; }

    /// <summary>Index into <see cref="RuntimeSnapshot.YarpEndpoints"/>; valid for this snapshot only.</summary>
    public required int Slot { get; init; }

    /// <summary>The route's own policies. The host's chain has already run when these do (see <c>GateMiddleware</c>).</summary>
    public required IRequestPolicy[] Policies { get; init; }

    /// <summary>The access list that guards this route (the host's, another, or none), wherever its policies sit in the chains.</summary>
    public AccessLists.CompiledAccessList? AccessList { get; init; }

    /// <summary>This route's effective proof of work: the host's values with the route's own switch applied.</summary>
    public HostProofOfWorkSpec ProofOfWork { get; init; } = HostProofOfWorkSpec.Off;

    /// <summary>True when the route's own layer decided <see cref="ProofOfWork"/>'s switch (the trace says so).</summary>
    public bool ProofOfWorkRouteOverride { get; init; }

    /// <summary>
    /// True for the implicit route every admin host gets: it takes every path and forwards to the
    /// <c>__admin</c> cluster. It has no <see cref="RouteId"/>, so <see cref="Id"/> is empty.
    /// </summary>
    public bool Admin { get; init; }

    /// <summary>Does this route take <paramref name="path"/> (a canonical view)? An empty path counts as <c>/</c>.</summary>
    public bool Matches(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty)
        {
            path = "/";
        }

        var route = Path;
        if (MatchKind == RouteMatchKind.Exact)
        {
            return path.Equals(route, StringComparison.OrdinalIgnoreCase);
        }

        return path.StartsWith(route, StringComparison.OrdinalIgnoreCase)
            && (path.Length == route.Length || route[^1] == '/' || path[route.Length] == '/');
    }
}
