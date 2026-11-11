using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Simulation;

/// <summary>What the gate does with the request.</summary>
public enum GateOutcomeAction : byte
{
    /// <summary>Hands it to YARP (an upstream answers).</summary>
    Forward,

    /// <summary>Answers itself with <see cref="GateOutcome.StatusCode"/>.</summary>
    Respond,

    /// <summary>Aborts it: H1 closes the connection, H2/H3 reset the stream. Nothing is sent.</summary>
    Abort,
}

/// <summary>Which step of the gate decided.</summary>
public enum GateReason : byte
{
    /// <summary>Nothing stopped it: an upstream is next.</summary>
    Forwarded,

    /// <summary>The TLS handshake never completes (no certificate for the name, a TLS version or HTTP/3 the name doesn't allow).</summary>
    HandshakeRejected,

    /// <summary>The ACME HTTP-01 hook answered.</summary>
    AcmeChallenge,

    /// <summary>No host takes the <c>Host</c> header: the default-host behaviour applies.</summary>
    DefaultHost,

    /// <summary>The connection is weaker than the host's TLS settings (421).</summary>
    MisdirectedConnection,

    /// <summary>A policy of the host's chain stopped it.</summary>
    HostPolicy,

    /// <summary>The host forces HTTPS and the request isn't.</summary>
    HttpsRedirect,

    /// <summary>The host's managed <c>/robots.txt</c> answered the request.</summary>
    RobotsTxt,

    /// <summary>A Redirect or Respond host answers everything itself.</summary>
    FixedHost,

    /// <summary>No route of the host takes the path.</summary>
    NoRoute,

    /// <summary>A policy of the route's chain stopped it.</summary>
    RoutePolicy,

    /// <summary>The proof-of-work verify endpoint answered it.</summary>
    ProofOfWork,

    /// <summary>The host only takes Cloudflare's connections and the peer isn't one (a 403, or no answer at all).</summary>
    OriginLockdown,

    /// <summary>The host requires Cloudflare's client certificate (Authenticated Origin Pulls) and the connection has no verified one.</summary>
    ClientCertificate,

    /// <summary>The request is over the host's or route's limit: its target (414), its headers (431) or its declared body (413).</summary>
    RequestLimits,

    /// <summary>The route has no YARP endpoint (not possible for a bound snapshot).</summary>
    Unavailable,

    /// <summary>The listener never lets the connection through to a request (its PROXY protocol rules).</summary>
    ConnectionRejected,

    /// <summary>Kestrel answers 400 before the gate runs: an HTTP/1.1 request without exactly one valid Host, or a Host that isn't one.</summary>
    RejectedByServer,
}

/// <param name="Action">What happens.</param>
/// <param name="Reason">Which step decided.</param>
/// <param name="StatusCode">The status of a <see cref="GateOutcomeAction.Respond"/>.</param>
/// <param name="Location">The <c>Location</c> a redirect would carry, built the way the real response builds it.</param>
/// <param name="Body">The body of a text response (short ones), for display.</param>
public sealed record GateOutcome(GateOutcomeAction Action, GateReason Reason, int? StatusCode = null, string? Location = null, string? Body = null);

/// <summary>Which step of the gate a <see cref="GateStep"/> is.</summary>
public enum GateStepKind : byte
{
    /// <summary>The listener's admission: PROXY protocol, HTTP/3 availability.</summary>
    Connection,

    TlsHandshake,

    /// <summary>What the server checks before the gate: the Host, the path.</summary>
    Request,

    ClientAddress,
    AcmeChallenge,
    HostMatch,

    /// <summary>The host's response headers, applied in one pass: sent, removed, or why not.</summary>
    ResponseHeaders,

    /// <summary>The host's origin lockdown: is the peer Cloudflare's?</summary>
    OriginLockdown,

    /// <summary>The host's Authenticated Origin Pulls: did the handshake verify Cloudflare's client certificate?</summary>
    ClientCertificate,

    TlsPolicy,
    HostPolicy,

    /// <summary>The host's request limits: the headers and request target before routing, the declared body once the route is known.</summary>
    RequestLimits,

    HttpsRedirect,

    /// <summary>The managed <c>robots.txt</c>: served, or passed through to the upstream.</summary>
    RobotsTxt,

    FixedResponse,

    /// <summary>The proof-of-work verify endpoint, which the gate answers before route matching.</summary>
    ProofOfWork,

    RouteMatch,
    RoutePolicy,
    Upstream,
}

/// <summary>What a step did to the request.</summary>
public enum GateStepVerdict : byte
{
    /// <summary>Looked at it and let it go on.</summary>
    Passed,

    /// <summary>Decided the outcome: nothing after it ran.</summary>
    Stopped,

    /// <summary>Didn't apply to this request.</summary>
    Skipped,
}

/// <summary>A detail of a step: a fact the step used or found.</summary>
public readonly record struct GateFact(string Name, string Value);

/// <summary>One step of the gate, in the order it ran.</summary>
/// <param name="Kind">Which step.</param>
/// <param name="Verdict">What it did.</param>
/// <param name="Summary">One line saying it in words.</param>
/// <param name="Facts">The details behind the line.</param>
/// <param name="Host">The host it concerns, once one is matched.</param>
/// <param name="Route">The route it concerns, once one is matched.</param>
/// <param name="PolicyIndex">Policy steps: the position in its chain, from 0.</param>
public sealed record GateStep(
    GateStepKind Kind,
    GateStepVerdict Verdict,
    string Summary,
    IReadOnlyList<GateFact> Facts,
    HostId? Host = null,
    RouteId? Route = null,
    int? PolicyIndex = null);

/// <summary>A destination of the upstream, as configured.</summary>
public sealed record GateDestination(DestinationId Id, string Address);

/// <summary>The upstream a forwarded request goes to. Load balancing picks the destination per request: never one here.</summary>
/// <param name="Id">The upstream; empty for the admin app, which has no user upstream.</param>
/// <param name="LoadBalancing">The cluster's load-balancing policy name, if configured.</param>
/// <param name="Destinations">The cluster's destinations, as configured.</param>
/// <param name="Admin">True: the route forwards to the admin app's internal listener (<c>__admin</c>), not to a user upstream.</param>
public sealed record GateUpstream(UpstreamId Id, string? LoadBalancing, IReadOnlyList<GateDestination> Destinations, bool Admin = false);

/// <summary>The result of <see cref="GateSimulator.Run"/>.</summary>
/// <param name="Outcome">The verdict.</param>
/// <param name="Steps">What the gate did, in order.</param>
/// <param name="Upstream">The route's upstream; set when the outcome is a forward.</param>
public sealed record GateSimulation(GateOutcome Outcome, IReadOnlyList<GateStep> Steps, GateUpstream? Upstream);
