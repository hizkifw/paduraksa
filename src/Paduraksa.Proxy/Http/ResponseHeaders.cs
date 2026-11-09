using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Http;

/// <summary>What a response-header operation does to its header.</summary>
public enum ResponseHeaderKind : byte
{
    /// <summary>Ours is set before the answer or the forward; an upstream's copy never replaces it (HSTS, <c>Alt-Svc</c>).</summary>
    Set,

    /// <summary>Ours is set and, after YARP copies the upstream's response, replaces that copy (the <c>Server</c> header,
    /// <c>X-Request-Id</c>).</summary>
    ReplaceUpstream,

    /// <summary>The header is removed, an upstream's own included.</summary>
    Remove,

    /// <summary>Nothing of ours: the upstream's own header passes through, and a gate answer carries none.</summary>
    LeaveToUpstream,

    /// <summary>The host doesn't have this header at all, for the reason the operation carries.</summary>
    NotSent,
}

/// <summary>Where in the gate's order an operation is applied.</summary>
public enum ResponseHeaderStage : byte
{
    /// <summary>Right after host lookup, so even the TLS 421 carries it (the <c>Server</c> header).</summary>
    AfterHostLookup,

    /// <summary>After the TLS 421 check: a misdirected connection's answer carries none of these.</summary>
    AfterTlsCheck,

    /// <summary>With the answer, once the decision is made (the response <c>X-Request-Id</c>).</summary>
    AfterDecision,
}

/// <summary>When an operation applies. Anything that doesn't hold leaves the header off, which the trace says why.</summary>
[Flags]
public enum ResponseHeaderConditions : byte
{
    /// <summary>Always.</summary>
    None = 0,

    /// <summary>The connection is TLS: <c>Alt-Svc</c> advertises the other transport only to a client on this one.</summary>
    Tls = 1,

    /// <summary>The trusted request scheme is HTTPS (a TLS-terminating trusted proxy counts).</summary>
    TrustedHttps = 2,

    /// <summary>The name is no IP literal or loopback name: a browser must not pin those.</summary>
    NotExemptName = 4,

    /// <summary>Not over HTTP/3: <c>Alt-Svc</c> invites a client onto it, and one already there needs no invitation.</summary>
    NotHttp3 = 8,
}

/// <summary>Where the operation's value comes from.</summary>
public enum ResponseHeaderValue : byte
{
    /// <summary>The precomputed value in the operation: no string is built on the request path.</summary>
    Precomputed,

    /// <summary>The request's ID, the one string a request materialises (<see cref="GateRequestState.RequestIdText"/>).</summary>
    RequestId,
}

/// <summary>
/// One response-header operation of a compiled host: a header's name, its precomputed value (or the
/// request ID), what to do with it, when, and under which conditions. A host has one array of these, and the gate, the
/// reapply after a forward and YARP's response transform all loop over it, so a header can't behave one way in the gate
/// and another after a forward.
/// </summary>
/// <param name="Name">The header's name, as written on the wire.</param>
/// <param name="Value">The precomputed value; empty for the request ID, <see cref="ResponseHeaderKind.Remove"/> and
/// <see cref="ResponseHeaderKind.NotSent"/>.</param>
/// <param name="Kind">What to do with the header.</param>
/// <param name="Stage">When in the gate's order it is applied.</param>
/// <param name="Conditions">What has to hold for it to be applied at all.</param>
/// <param name="Source">Where the value comes from.</param>
/// <param name="Reason">Why the host doesn't send it, for <see cref="ResponseHeaderKind.NotSent"/> and
/// <see cref="ResponseHeaderKind.LeaveToUpstream"/>: the trace's "why not".</param>
public readonly record struct ResponseHeaderOperation(
    string Name,
    StringValues Value,
    ResponseHeaderKind Kind,
    ResponseHeaderStage Stage,
    ResponseHeaderConditions Conditions,
    ResponseHeaderValue Source = ResponseHeaderValue.Precomputed,
    string? Reason = null);

/// <summary>What one operation did (or why it did nothing) for one request.</summary>
internal enum HeaderOutcome : byte
{
    /// <summary>Not applied in this pass (another stage's operation).</summary>
    None = 0,

    /// <summary>Ours is on the response.</summary>
    Sent,

    /// <summary>Ours is on the response, and replaces an upstream's copy after the forward.</summary>
    Replaced,

    Removed,

    /// <summary>The upstream's own passes through; a gate answer carries none.</summary>
    LeftToUpstream,

    /// <summary>The host doesn't send it, for the operation's reason.</summary>
    NotSent,

    /// <summary>Ours goes on HTTPS responses only, and this request isn't one.</summary>
    PlainHttp,

    /// <summary>The name is an IP literal or a loopback name, which must not be pinned.</summary>
    ExemptName,

    /// <summary>The header goes over TCP only, and the request came over HTTP/3.</summary>
    Http3,

    /// <summary>The value is the request's ID, materialised with the answer (<see cref="ResponseHeaderStage.AfterDecision"/>).</summary>
    Deferred,
}

/// <summary>
/// Applies a host's <see cref="ResponseHeaderOperation"/>s. 0 B: every value is precomputed, so an
/// assignment or a removal is all that happens; the request ID is the one string a request materialises, and it is
/// applied with the answer rather than in the decision.
/// </summary>
internal static class ResponseHeaders
{
    /// <summary>
    /// The most operations a host's list may hold: the gate keeps their outcomes in a stack buffer of this size, so the
    /// compiler refuses a longer list rather than the gate overrunning it.
    /// </summary>
    public const int MaxResponseHeaderOperations = 16;

    /// <summary>The response <c>X-Request-Id</c>'s name, and the operations that carry it.</summary>
    public static bool IsRequestId(in ResponseHeaderOperation operation) => operation.Source == ResponseHeaderValue.RequestId;

    /// <summary>
    /// Applies the operations of <paramref name="stage"/> and records what each one did in
    /// <paramref name="outcomes"/> (indexed like <paramref name="operations"/>; <see cref="HeaderOutcome.None"/> for the
    /// others). The request ID is left to <see cref="Apply"/> with the answer.
    /// </summary>
    public static void ApplyStage(
        HttpContext context,
        ReadOnlySpan<ResponseHeaderOperation> operations,
        ResponseHeaderStage stage,
        GateRequestState state,
        Span<HeaderOutcome> outcomes,
        bool applyRequestId)
    {
        for (int i = 0; i < operations.Length; i++)
        {
            ref readonly ResponseHeaderOperation operation = ref operations[i];
            if (operation.Stage != stage)
            {
                continue;
            }

            HeaderOutcome outcome = IsRequestId(operation) && !applyRequestId
                ? HeaderOutcome.Deferred
                : Apply(context, in operation, state);
            if (i < outcomes.Length)
            {
                outcomes[i] = outcome;
            }
        }
    }

    /// <summary>
    /// Records what the operations of <paramref name="stage"/> will do when the answer is written, without applying them:
    /// the response <c>X-Request-Id</c> is the request's one string, so the decision leaves it to the answer and the trace
    /// says so rather than inventing a value there. A host that doesn't send the ID to the client has a removal there
    /// instead, which is a removal and not a deferred send.
    /// </summary>
    public static void DescribeDeferred(ReadOnlySpan<ResponseHeaderOperation> operations, ResponseHeaderStage stage, Span<HeaderOutcome> outcomes)
    {
        for (int i = 0; i < operations.Length; i++)
        {
            ref readonly ResponseHeaderOperation operation = ref operations[i];
            if (operation.Stage != stage || i >= outcomes.Length)
            {
                continue;
            }

            outcomes[i] = operation.Kind == ResponseHeaderKind.Remove ? HeaderOutcome.Removed
                : IsRequestId(operation) ? HeaderOutcome.Deferred
                : HeaderOutcome.NotSent;
        }
    }

    /// <summary>
    /// Applies one operation, whatever its stage: what the reapply after a forward and YARP's transform use, so a header
    /// a forward dropped comes back exactly as the gate set it.
    /// </summary>
    public static HeaderOutcome Apply(HttpContext context, in ResponseHeaderOperation operation, GateRequestState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        switch (operation.Kind)
        {
            case ResponseHeaderKind.NotSent:
                return HeaderOutcome.NotSent;
            case ResponseHeaderKind.LeaveToUpstream:
                return HeaderOutcome.LeftToUpstream;
        }

        if (!Holds(context, in operation, state, out HeaderOutcome refused))
        {
            return refused;
        }

        if (operation.Kind == ResponseHeaderKind.Remove)
        {
            context.Response.Headers.Remove(operation.Name);
            return HeaderOutcome.Removed;
        }

        context.Response.Headers[operation.Name] = IsRequestId(operation) ? state.RequestIdText() : operation.Value;
        return operation.Kind == ResponseHeaderKind.ReplaceUpstream ? HeaderOutcome.Replaced : HeaderOutcome.Sent;
    }

    /// <summary>
    /// Whether <paramref name="operation"/> applies to this request; when it doesn't, <paramref name="refused"/> says
    /// which condition refused it, for the trace.
    /// </summary>
    public static bool Holds(HttpContext context, in ResponseHeaderOperation operation, GateRequestState? state, out HeaderOutcome refused)
    {
        ArgumentNullException.ThrowIfNull(context);
        refused = HeaderOutcome.None;
        ResponseHeaderConditions conditions = operation.Conditions;
        if (conditions == ResponseHeaderConditions.None)
        {
            return true;
        }

        if ((conditions & ResponseHeaderConditions.Tls) != 0 && !context.Request.IsHttps)
        {
            refused = HeaderOutcome.PlainHttp;
            return false;
        }

        if ((conditions & ResponseHeaderConditions.TrustedHttps) != 0 && state?.IsHttps != true)
        {
            refused = HeaderOutcome.PlainHttp;
            return false;
        }

        if ((conditions & ResponseHeaderConditions.NotHttp3) != 0 && HttpProtocol.IsHttp3(context.Request.Protocol))
        {
            refused = HeaderOutcome.Http3;
            return false;
        }

        if ((conditions & ResponseHeaderConditions.NotExemptName) != 0)
        {
            StringValues hostHeader = context.Request.Headers.Host;
            if (hostHeader.Count != 1 || HstsHeader.IsExemptName(Snapshot.HostTable.HostName(hostHeader[0])))
            {
                refused = HeaderOutcome.ExemptName;
                return false;
            }
        }

        return true;
    }
}
