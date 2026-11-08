using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Gate;

/// <summary>What a policy tells the gate to do (PLAN §7.2 D).</summary>
public enum PolicyAction : byte
{
    /// <summary>Run the next policy; after the last one, forward to YARP.</summary>
    Continue,

    /// <summary>Answer with <see cref="PolicyDecision.Response"/> and stop.</summary>
    Respond,

    /// <summary>Abort the request without a response (H1: the connection closes; H2/H3: the stream is reset).</summary>
    Drop,
}

/// <summary>
/// A policy's verdict: 16 bytes (a reference plus the action), returned by value. <c>Challenge</c> (PoW) and a
/// "needs async" action (forward-auth miss) join later.
/// </summary>
public readonly struct PolicyDecision : IEquatable<PolicyDecision>
{
    private PolicyDecision(PolicyAction action, PrecomputedResponse? response)
    {
        Action = action;
        Response = response;
    }

    public PrecomputedResponse? Response { get; }

    public PolicyAction Action { get; }

    public static PolicyDecision Continue => default;

    public static PolicyDecision Drop => new(PolicyAction.Drop, null);

    public static PolicyDecision Respond(PrecomputedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(PolicyAction.Respond, response);
    }

    public static bool operator ==(PolicyDecision left, PolicyDecision right) => left.Equals(right);

    public static bool operator !=(PolicyDecision left, PolicyDecision right) => !left.Equals(right);

    public bool Equals(PolicyDecision other) => Action == other.Action && ReferenceEquals(Response, other.Response);

    public override bool Equals(object? obj) => obj is PolicyDecision other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Action, Response);
}

/// <summary>What a policy sees: the request and the gate's per-request state. Passed by <c>in</c>.</summary>
public readonly ref struct RequestView
{
    public RequestView(HttpContext http, GateRequestState state, bool isSimulation = false)
    {
        Http = http;
        State = state;
        IsSimulation = isSimulation;
    }

    public HttpContext Http { get; }

    public GateRequestState State { get; }

    /// <summary>
    /// The request is a simulation (PLAN §7.7), not a real one: the verdict must be what a real request would get, but a
    /// policy must change nothing to give it. A stateful policy peeks (a rate limit says "would allow, N left" without taking
    /// a token) and starts no outbound call. Stateless policies ignore it.
    /// </summary>
    public bool IsSimulation { get; }
}

/// <summary>
/// One step of a host's or a route's policy chain (PLAN §7.2 D). Implementations are sealed classes with everything
/// precompiled into the instance; <see cref="Evaluate"/> runs on the hot path, so it must not allocate, lock or await.
/// Host policies run before any route is matched, so <see cref="GateRequestState.Route"/> may be <see langword="null"/>.
/// </summary>
public interface IRequestPolicy
{
    PolicyDecision Evaluate(in RequestView request);

    /// <summary>A short, stable kind label for the request simulator's trace (<c>ipAllowList</c>). Never called on the hot
    /// path. Each policy added later (M7) overrides it and <see cref="Explain"/>.</summary>
    string Name => "policy";

    /// <summary>Says why <paramref name="verdict"/> (what <see cref="Evaluate"/> just returned for
    /// <paramref name="request"/>) came out as it did, for the simulator's trace. Must have no side effects (a rate
    /// limit reports "would allow, N left" without taking a token). Never called on the hot path.</summary>
    string Explain(in RequestView request, PolicyDecision verdict) =>
        verdict.Action == PolicyAction.Continue ? "lets the request through" : verdict.Action == PolicyAction.Drop ? "drops the request" : "answers it";
}
