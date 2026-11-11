using System.Text.Json.Serialization;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Json;
using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Simulation;

/// <summary>Whether the simulation ran.</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationStatus>))]
public enum SimulationStatus
{
    /// <summary>The request went through the gate: <see cref="SimulationResult.Outcome"/> and the trace are set.</summary>
    Simulated,

    /// <summary>The draft didn't validate or compile (<see cref="SimulationResult.Errors"/>, with the rest of what the dry
    /// run reports in <see cref="SimulationResult.RelatedErrors"/>), so there is no config to simulate against.</summary>
    Invalid,

    /// <summary>The revision doesn't exist.</summary>
    RevisionNotFound,

    /// <summary>The draft command can't be applied as it stands (a stale version, an object that doesn't exist).
    /// <see cref="SimulationResult.Errors"/> and <see cref="SimulationResult.Notes"/> say why.</summary>
    DraftRejected,

    /// <summary>The config store is unavailable (a degraded start), so a draft or revision can't be read.</summary>
    Unavailable,

    /// <summary>The caller may not run this simulation.</summary>
    Denied,

    /// <summary>The request itself is malformed (a URL that isn't absolute http or https, an address that isn't an IP).
    /// <see cref="SimulationResult.Errors"/> names the fields.</summary>
    BadRequest,
}

/// <summary>What the gate does with the request.</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationOutcomeKind>))]
public enum SimulationOutcomeKind
{
    /// <summary>Handed to the route's upstream; which destination serves it is decided per request.</summary>
    ForwardToUpstream,

    /// <summary>The gate answers itself with a status: a policy's denial, a Respond host, a 503.</summary>
    FixedAnswer,

    /// <summary>The gate answers with a redirect: the HTTPS redirect, or a Redirect host.</summary>
    Redirect,

    /// <summary>421: the connection is weaker than the host's TLS settings, so the client should retry on one of its own.</summary>
    MisdirectedRequest,

    /// <summary>The request is aborted without an answer (a drop policy, or the default host set to close).</summary>
    Drop,

    /// <summary>The ACME HTTP-01 hook answers a certificate challenge.</summary>
    AcmeChallengeAnswer,

    /// <summary>A proxy host matched, but none of its routes takes the path: 404.</summary>
    NoRoute,

    /// <summary>No host takes the <c>Host</c>: the default-host behaviour answers (404 or 421; closing is <see cref="Drop"/>).</summary>
    DefaultHost,

    /// <summary>The TLS handshake never completes (no certificate for the name, or a TLS version or HTTP/3 the name doesn't
    /// allow), so no request reaches the gate.</summary>
    HandshakeRejected,

    /// <summary>The listener drops the connection before a request exists: it requires PROXY protocol and the request has none
    /// (or comes from a socket it doesn't trust), it has none and the request has a header, or there is no HTTP/3 listener.</summary>
    ConnectionRejected,

    /// <summary>The server itself answers 400 before the gate runs: an HTTP/1.1 request without exactly one valid <c>Host</c>,
    /// or a host that isn't one.</summary>
    RejectedByServer,
}

/// <summary>The gate's verdict.</summary>
/// <param name="Kind">What happens.</param>
/// <param name="Summary">Why, in one line: the step that decided.</param>
/// <param name="StatusCode">The status, when the gate answers.</param>
/// <param name="Location">The <c>Location</c> of a redirect, built as the real response builds it.</param>
/// <param name="Body">The body of a short text answer.</param>
public sealed record SimulationOutcome(
    SimulationOutcomeKind Kind,
    string Summary,
    int? StatusCode = null,
    string? Location = null,
    string? Body = null);

/// <summary>Which gate step a <see cref="SimulationStep"/> is, in the order they run.</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationStepKind>))]
public enum SimulationStepKind
{
    /// <summary>The listener's admission: PROXY protocol, and whether HTTP/3 is offered.</summary>
    Connection,

    /// <summary>The certificate selection for the name in the handshake, TLS version and ALPN.</summary>
    TlsHandshake,

    /// <summary>What the server checks and rewrites before the gate: the <c>Host</c> (or <c>:authority</c>), the decoded path.</summary>
    Request,

    /// <summary>The client address: which header and peer were trusted.</summary>
    ClientAddress,

    /// <summary>The ACME HTTP-01 hook.</summary>
    AcmeChallenge,

    /// <summary>The host lookup: exact, wildcard, or the default host.</summary>
    HostMatch,

    /// <summary>
    /// The host's response headers (<c>Alt-Svc</c>, <c>Strict-Transport-Security</c>, <c>Server</c> and the response
    /// <c>X-Request-Id</c>): each one the answer carries with its value, and each one it doesn't with the reason.
    /// </summary>
    ResponseHeaders,

    /// <summary>The host's origin lockdown: whether the connection's peer is Cloudflare's edge (or <c>cloudflared</c>).</summary>
    OriginLockdown,

    /// <summary>The connection against the host's TLS settings (the 421 reasons).</summary>
    TlsPolicy,

    /// <summary>A policy of the host's chain.</summary>
    HostPolicy,

    /// <summary>The HTTP to HTTPS redirect.</summary>
    HttpsRedirect,

    /// <summary>The managed <c>robots.txt</c>: served for the host, or passed through to the upstream.</summary>
    RobotsTxt,

    /// <summary>A Redirect or Respond host's fixed answer.</summary>
    FixedResponse,

    /// <summary>The proof-of-work verify endpoint, which the gate answers before route matching.</summary>
    ProofOfWork,

    /// <summary>The route match, on the canonical view of the path.</summary>
    RouteMatch,

    /// <summary>A policy of the matched route's chain.</summary>
    RoutePolicy,

    /// <summary>The hand-over to the route's upstream.</summary>
    Upstream,
}

/// <summary>What a step did to the request.</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationStepVerdict>))]
public enum SimulationStepVerdict
{
    /// <summary>Looked at it and let it go on.</summary>
    Passed,

    /// <summary>Decided the outcome: nothing after it ran.</summary>
    Stopped,

    /// <summary>Didn't apply to this request.</summary>
    Skipped,
}

/// <summary>A detail of a step.</summary>
/// <param name="Name">What it is (<c>peer</c>, <c>header</c>, <c>match</c>, ...).</param>
/// <param name="Value">Its value.</param>
public sealed record SimulationFact(string Name, string Value);

/// <summary>One step the gate took.</summary>
/// <param name="Kind">Which step.</param>
/// <param name="Verdict">What it did.</param>
/// <param name="Summary">One line saying it in words.</param>
/// <param name="Facts">The details behind the line.</param>
/// <param name="Host">The host it concerns, once one matched.</param>
/// <param name="Route">The route it concerns, once one matched.</param>
/// <param name="PolicyIndex">Policy steps: the policy's position in its chain, from 0.</param>
public sealed record SimulationStep(
    SimulationStepKind Kind,
    SimulationStepVerdict Verdict,
    string Summary,
    IReadOnlyList<SimulationFact> Facts,
    HostId? Host = null,
    RouteId? Route = null,
    int? PolicyIndex = null);

/// <summary>What is known about a destination's health.</summary>
[JsonConverter(typeof(ConfigEnumConverter<SimulationHealth>))]
public enum SimulationHealth
{
    /// <summary>The live proxy currently sends traffic to it.</summary>
    Healthy,

    /// <summary>The live proxy currently keeps traffic away from it.</summary>
    Unhealthy,

    /// <summary>The live proxy doesn't have this destination (a draft or old revision names it, or health checks haven't run).</summary>
    Unknown,
}

/// <summary>A destination of the route's upstream, as the target config lists it.</summary>
/// <param name="Id">The destination.</param>
/// <param name="Address">Its address.</param>
/// <param name="Health">Its health in the <b>live</b> proxy: health is live state, so for a draft or revision it is the live
/// proxy's answer for the destination with the same ID.</param>
public sealed record SimulationDestination(DestinationId Id, string Address, SimulationHealth Health);

/// <summary>The upstream the request is forwarded to. Load balancing is random: no destination is named as the one chosen.</summary>
/// <param name="Id">The upstream.</param>
/// <param name="Destinations">The upstream's destinations in the target config, with their live health.</param>
/// <param name="LoadBalancing">The YARP load-balancing policy; null for YARP's default.</param>
public sealed record SimulationUpstream(UpstreamId Id, IReadOnlyList<SimulationDestination> Destinations, string? LoadBalancing = null);

/// <summary>What the simulation ran against.</summary>
/// <param name="Kind">The kind of target.</param>
/// <param name="Revision">The revision the config is (the live one's, or the stored revision); null for a draft, and for the
/// live config before the first commit.</param>
public sealed record SimulationSource(SimulationTargetKind Kind, ConfigRevisionId? Revision = null);

/// <summary>
/// The answer to "what would this request get, and why": the outcome and the ordered trace of what the real
/// gate did, or, when there was nothing to simulate against, why not.
/// </summary>
/// <param name="Status">Whether it ran.</param>
/// <param name="Source">What it ran against.</param>
/// <param name="Outcome">The verdict; set exactly when <paramref name="Status"/> is <see cref="SimulationStatus.Simulated"/>.</param>
/// <param name="Trace">What the gate did, in order.</param>
/// <param name="Upstream">The route's upstream, when the outcome forwards.</param>
/// <param name="Errors">Why not, when it didn't run: the draft's validation and compile errors (paths as the dry run reports
/// them), or the request's problems.</param>
/// <param name="RelatedErrors">Draft commands: errors the change causes elsewhere in the config (a dry run's related errors).</param>
/// <param name="Warnings">A draft's compile warnings (a host no certificate covers).</param>
/// <param name="Notes">Caveats about this answer: that health is the live proxy's, that a draft command changed nothing.</param>
public sealed record SimulationResult(
    SimulationStatus Status,
    SimulationSource Source,
    SimulationOutcome? Outcome = null,
    IReadOnlyList<SimulationStep>? Trace = null,
    SimulationUpstream? Upstream = null,
    IReadOnlyList<ConfigError>? Errors = null,
    IReadOnlyList<ConfigError>? RelatedErrors = null,
    IReadOnlyList<ConfigWarning>? Warnings = null,
    IReadOnlyList<string>? Notes = null)
{
    public IReadOnlyList<SimulationStep> Trace { get; init; } = Trace ?? [];

    public IReadOnlyList<ConfigError> Errors { get; init; } = Errors ?? [];

    public IReadOnlyList<ConfigError> RelatedErrors { get; init; } = RelatedErrors ?? [];

    public IReadOnlyList<ConfigWarning> Warnings { get; init; } = Warnings ?? [];

    public IReadOnlyList<string> Notes { get; init; } = Notes ?? [];
}
