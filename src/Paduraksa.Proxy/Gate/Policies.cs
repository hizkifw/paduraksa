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
    public RequestView(HttpContext http, GateRequestState state)
    {
        Http = http;
        State = state;
    }

    public HttpContext Http { get; }

    public GateRequestState State { get; }
}

/// <summary>
/// One step of a host's or a route's policy chain (PLAN §7.2 D). Implementations are sealed classes with everything
/// precompiled into the instance; <see cref="Evaluate"/> runs on the hot path, so it must not allocate, lock or await.
/// Host policies run before any route is matched, so <see cref="GateRequestState.Route"/> may be <see langword="null"/>.
/// </summary>
public interface IRequestPolicy
{
    PolicyDecision Evaluate(in RequestView request);
}
