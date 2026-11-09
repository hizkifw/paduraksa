using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Gate;

/// <summary>Which chain a policy ran in.</summary>
internal enum PolicyScope : byte
{
    /// <summary>The host's chain, before the HTTPS redirect and route matching.</summary>
    Host,

    /// <summary>The matched route's own chain.</summary>
    Route,
}

/// <summary>
/// Where the gate reports each step it takes (PLAN §7.7). It is passed to <see cref="GateMiddleware"/>'s decision as a
/// <b>generic struct type parameter</b>, so production's <see cref="NoGateTracer"/> costs nothing: every call is an empty
/// method on a zero-size struct, which the JIT inlines away, and the arguments are values already at hand (nothing is
/// formatted or allocated to make a call). The request simulator passes a recording tracer instead; the decision code is
/// the same.
/// <para>Implementations must not change anything the gate decides on. A step is reported once the gate has made it, in the
/// order the gate takes them.</para>
/// </summary>
internal interface IGateTracer
{
    /// <summary>
    /// True for a tracer that runs the gate for the request simulator. It reaches every policy as
    /// <see cref="RequestView.IsSimulation"/>: a policy with state (a rate limit's bucket, a challenge's nonce table) must
    /// not change it then. A static constant of the type parameter, so the production tracer's <see langword="false"/> is
    /// folded away by the JIT.
    /// </summary>
    static abstract bool IsSimulation { get; }

    /// <summary>The client address and scheme were resolved (every request).</summary>
    void ClientResolved(HttpContext context, RuntimeSnapshot snapshot, in ClientIpResolution client, bool isHttps);

    /// <summary>The ACME HTTP-01 hook ran: <paramref name="answered"/> says whether it took the request.</summary>
    void AcmeChallenge(HttpContext context, bool answered);

    /// <summary>The host lookup ran. <paramref name="host"/> is null when no host takes the request (then the default
    /// behaviour answers).</summary>
    void HostLookup(StringValues hostHeader, CompiledHost? host, HostMatchKind match, DefaultHostBehaviour fallback);

    /// <summary>The connection was checked against the host's TLS settings. <paramref name="https"/> false: plain HTTP,
    /// nothing was checked.</summary>
    void TlsCheck(HttpContext context, CompiledHost host, bool https, TlsMismatch mismatch);

    /// <summary>The host's HSTS header was applied to the response, or <paramref name="decision"/> says why not.</summary>
    void Hsts(CompiledHost host, HstsDecision decision);

    /// <summary>One policy ran and returned <paramref name="verdict"/>.</summary>
    void PolicyEvaluated(PolicyScope scope, int index, IRequestPolicy policy, in RequestView request, PolicyDecision verdict);

    /// <summary>The host forces HTTPS: <paramref name="redirected"/> says whether this request was sent to it.</summary>
    void HttpsRedirect(CompiledHost host, bool isHttps, bool redirected);

    /// <summary>A Redirect or Respond host answered with its fixed response.</summary>
    void FixedResponse(CompiledHost host);

    /// <summary>Route matching ran over <paramref name="canonicalPath"/>, the matching view of <paramref name="path"/>;
    /// <paramref name="route"/> is the first route that takes it, or null.</summary>
    void RouteMatch(CompiledHost host, ReadOnlySpan<char> path, ReadOnlySpan<char> canonicalPath, CompiledRoute? route);

    /// <summary>The request was handed to YARP; <paramref name="endpointPresent"/> false: the snapshot has no endpoint
    /// for the route's slot (unbound), so it answered 503 instead.</summary>
    void Forward(RuntimeSnapshot snapshot, CompiledRoute route, bool endpointPresent);
}

/// <summary>The production tracer: reports nothing, costs nothing.</summary>
internal readonly struct NoGateTracer : IGateTracer
{
    public static bool IsSimulation => false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClientResolved(HttpContext context, RuntimeSnapshot snapshot, in ClientIpResolution client, bool isHttps)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AcmeChallenge(HttpContext context, bool answered)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HostLookup(StringValues hostHeader, CompiledHost? host, HostMatchKind match, DefaultHostBehaviour fallback)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TlsCheck(HttpContext context, CompiledHost host, bool https, TlsMismatch mismatch)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Hsts(CompiledHost host, HstsDecision decision)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PolicyEvaluated(PolicyScope scope, int index, IRequestPolicy policy, in RequestView request, PolicyDecision verdict)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HttpsRedirect(CompiledHost host, bool isHttps, bool redirected)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FixedResponse(CompiledHost host)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RouteMatch(CompiledHost host, ReadOnlySpan<char> path, ReadOnlySpan<char> canonicalPath, CompiledRoute? route)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Forward(RuntimeSnapshot snapshot, CompiledRoute route, bool endpointPresent)
    {
    }
}
