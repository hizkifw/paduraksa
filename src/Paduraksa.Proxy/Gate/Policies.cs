using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Gate;

/// <summary>What a policy tells the gate to do.</summary>
public enum PolicyAction : byte
{
    /// <summary>Run the next policy; after the last one, forward to YARP.</summary>
    Continue,

    /// <summary>Answer with <see cref="PolicyDecision.Response"/> and stop.</summary>
    Respond,

    /// <summary>Abort the request without a response (H1: the connection closes; H2/H3: the stream is reset).</summary>
    Drop,

    /// <summary>The verdict needs asynchronous work: the gate calls <see cref="IDeferredPolicy.CompleteAsync"/> and, if that
    /// continues, resumes the chain after this policy. Never returned for a simulation.</summary>
    Defer,
}

/// <summary>A policy's verdict, 24 bytes: two references and the action. The second reference is the precomputed
/// <c>Retry-After</c> a rate-limit refusal carries; it is a value type, so the hot path boxes nothing.</summary>
public readonly struct PolicyDecision : IEquatable<PolicyDecision>
{
    private PolicyDecision(PolicyAction action, PrecomputedResponse? response, string? retryAfter)
    {
        Action = action;
        Response = response;
        RetryAfter = retryAfter;
    }

    public PrecomputedResponse? Response { get; }

    public PolicyAction Action { get; }

    /// <summary>
    /// For a 429 answer: the precomputed <c>Retry-After</c> value (see <see cref="Gate.RateLimitPolicy"/>). Null
    /// for any other answer. The string is precomputed by the policy, so the refusal path stays allocation-free.
    /// </summary>
    public string? RetryAfter { get; }

    public static PolicyDecision Continue => default;

    public static PolicyDecision Drop => new(PolicyAction.Drop, null, null);

    public static PolicyDecision Defer => new(PolicyAction.Defer, null, null);

    public static PolicyDecision Respond(PrecomputedResponse response) => Respond(response, null);

    public static PolicyDecision Respond(PrecomputedResponse response, string? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new(PolicyAction.Respond, response, retryAfter);
    }

    public static bool operator ==(PolicyDecision left, PolicyDecision right) => left.Equals(right);

    public static bool operator !=(PolicyDecision left, PolicyDecision right) => !left.Equals(right);

    public bool Equals(PolicyDecision other) =>
        Action == other.Action && ReferenceEquals(Response, other.Response) && string.Equals(RetryAfter, other.RetryAfter, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PolicyDecision other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Action, Response, RetryAfter);
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

    /// <summary>The request is a simulation, not a real one: the verdict must be what a real request would get, but a
    /// policy must change nothing to give it.</summary>
    public bool IsSimulation { get; }
}

/// <summary>
/// One step of a host's or a route's policy chain. Implementations are sealed classes with everything
/// precompiled into the instance; <see cref="Evaluate"/> runs on the hot path, so it must not allocate, lock or await.
/// Host policies run before any route is matched, so <see cref="GateRequestState.Route"/> may be <see langword="null"/>.
/// </summary>
public interface IRequestPolicy
{
    PolicyDecision Evaluate(in RequestView request);

    /// <summary>A short, stable kind label for the request simulator's trace (<c>accessList</c>). Never called on
    /// the hot path.</summary>
    string Name => "policy";

    /// <summary>Says why <paramref name="verdict"/> (what <see cref="Evaluate"/> just returned for
    /// <paramref name="request"/>) came out as it did, for the simulator's trace. Must have no side effects (a rate
    /// limit reports "would allow, N left" without taking a token). Never called on the hot path.</summary>
    string Explain(in RequestView request, PolicyDecision verdict) =>
        verdict.Action == PolicyAction.Continue ? "lets the request through" : verdict.Action == PolicyAction.Drop ? "drops the request" : "answers it";
}

/// <summary>
/// A policy that can't always decide synchronously (an access list's password check waits for a verification slot). Its
/// <see cref="IRequestPolicy.Evaluate"/> returns <see cref="PolicyDecision.Defer"/> for the requests that need the wait, and
/// every other verdict stays synchronous and allocation-free. The gate then awaits <see cref="CompleteAsync"/> and carries on
/// with the policies after this one.
/// </summary>
internal interface IDeferredPolicy : IRequestPolicy
{
    /// <summary>The final verdict of a request <see cref="IRequestPolicy.Evaluate"/> deferred; never <see cref="PolicyAction.Defer"/>.</summary>
    ValueTask<PolicyDecision> CompleteAsync(HttpContext http, GateRequestState state);
}
