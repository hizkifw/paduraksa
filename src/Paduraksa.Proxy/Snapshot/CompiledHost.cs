using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>A configured host, compiled for the gate. Immutable; belongs to one snapshot.</summary>
public sealed class CompiledHost
{
    /// <summary>Position in <see cref="RuntimeSnapshot.HostsByIndex"/>; valid for this snapshot only.</summary>
    public required int Index { get; init; }

    /// <summary>Stable identity across snapshots.</summary>
    public required HostId Id { get; init; }

    public required HostKind Kind { get; init; }

    /// <summary>The names as configured (the first is the one to show); the host table is what matches requests.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Proxy hosts: the routes in the order they are tried (most specific first); the first match wins.
    /// Empty for the other kinds.</summary>
    public required CompiledRoute[] Routes { get; init; }

    /// <summary>
    /// The host-level chain. It runs right after host lookup, before the HTTPS redirect, a Redirect/Respond answer
    /// and route matching, so <c>State.Route</c> is still <see langword="null"/> for these policies.
    /// </summary>
    public required IRequestPolicy[] Policies { get; init; }

    /// <summary>Redirect and Respond hosts: the whole answer. <see langword="null"/> for Proxy hosts.</summary>
    public PrecomputedResponse? FixedResponse { get; init; }

    /// <summary>
    /// The host's managed <c>/robots.txt</c>, encoded once at compile time; <see langword="null"/> means passthrough, so
    /// the request goes on to the upstream (or a fixed answer).
    /// </summary>
    public PrecomputedResponse? RobotsTxt { get; init; }

    /// <summary>The mode that produced <see cref="RobotsTxt"/>, for the trace: passthrough, a preset, or custom.</summary>
    public RobotsMode RobotsMode { get; init; } = RobotsMode.Passthrough;

    /// <summary>
    /// The host's response headers: <c>Alt-Svc</c>, <c>Strict-Transport-Security</c>, <c>Server</c> and the
    /// response <c>X-Request-Id</c>, each with its precomputed value, what to do with it, when, and what has to hold. This
    /// is the one list the gate (<see cref="Http.ResponseHeaders.ApplyStage"/>), the reapply after a forward and YARP's
    /// response transform all loop over (<see cref="Http.ResponseHeaderOperation"/>).
    /// </summary>
    public ResponseHeaderOperation[] ResponseHeaders { get; init; } = [];

    /// <summary>
    /// The host's TLS settings. The handshake applies the settings of the SNI's host; the gate re-checks this host's
    /// against the request's connection (<see cref="ConnectionTlsCheck"/>), since clients coalesce connections across
    /// names, and answers 421 when the connection is weaker.
    /// </summary>
    public TlsPolicy Tls { get; init; } = TlsPolicy.Default;

    /// <summary>
    /// False: the host isn't served over HTTPS, and the gate answers its HTTPS requests 421 (a connection negotiated
    /// for another name, or on the default certificate, must not reach it).
    /// </summary>
    public bool Https { get; init; } = true;

    /// <summary>Set when the host forces HTTPS: plain-HTTP requests get this redirect first.</summary>
    internal HttpsRedirect? HttpsRedirect { get; init; }

    /// <summary>
    /// Write a line per request of this host to the access log: the settings layers resolved it, built-in
    /// default on. Off, the request is served exactly as before, only without a line.
    /// </summary>
    public bool AccessLog { get; init; } = true;

    /// <summary>Where the request ID is sent upstream: the settings layers resolved it, built-in default upstream and
    /// response.</summary>
    public RequestIdPropagation RequestIdPropagation { get; init; } = RequestIdPropagation.UpstreamAndResponse;

    /// <summary>
    /// The host's own rate-limit policy (the one a route inherits), or null when it names none. A route's chain carries its
    /// own instance of the same object when it inherits, so a different or missing one marks an override.
    /// </summary>
    public RateLimitPolicy? RateLimit { get; init; }

    /// <summary>
    /// What the gate does with a request whose peer is neither Cloudflare's edge nor <c>cloudflared</c>, before anything
    /// else of the host's chain. Off unless the global trusted-proxy mode is a Cloudflare one: the compiler resolves it, so
    /// the gate never looks at the mode.
    /// </summary>
    public OriginLockdownMode OriginLockdown { get; init; } = OriginLockdownMode.Off;

    /// <summary>
    /// Authenticated Origin Pulls: the host's TLS handshake demands Cloudflare's client certificate, and the gate refuses a
    /// request that arrives without a verified one (a connection negotiated for another name, a non-SNI connection, plain
    /// HTTP). Off unless the trusted-proxy mode is a Cloudflare one.
    /// </summary>
    public bool AuthenticatedOriginPulls { get; init; }

    /// <summary>The host's proof-of-work settings; a route's own switch may override <see cref="HostProofOfWorkSpec.Enabled"/>.</summary>
    public HostProofOfWorkSpec ProofOfWork { get; init; } = HostProofOfWorkSpec.Off;

    /// <summary>
    /// True when the host or at least one of its routes has the proof-of-work gate on, so the gate answers its verify
    /// endpoint for this host instead of letting the path fall through.
    /// </summary>
    public bool ServesProofOfWork { get; init; }

    /// <summary>
    /// The host's stable scope for token and challenge tags, precomputed from <see cref="Id"/>'s bytes. A token signed
    /// for one host never verifies on another.
    /// </summary>
    public byte[] ProofOfWorkHostScope { get; init; } = [];

    /// <summary>
    /// Placeholder for the stable metric slot: the same host keeps the same slot across snapshots.
    /// -1 until the metrics registry exists.
    /// </summary>
    public int MetricSlot { get; init; } = -1;

    /// <summary>The first route that takes <paramref name="path"/>, or <see langword="null"/>.</summary>
    public CompiledRoute? Match(ReadOnlySpan<char> path)
    {
        foreach (var route in Routes)
        {
            if (route.Matches(path))
            {
                return route;
            }
        }

        return null;
    }
}

/// <summary>
/// The precomputed HTTP→HTTPS redirect of one host: <c>301</c> for <c>GET</c>/<c>HEAD</c>, <c>308</c> otherwise so the
/// method and body survive. The <c>Location</c> keeps the request host and target and uses the <b>public</b> HTTPS
/// port from settings, never the bound port.
/// </summary>
internal sealed class HttpsRedirect
{
    public HttpsRedirect(int publicHttpsPort)
    {
        var target = RedirectTarget.Https(publicHttpsPort);
        Safe = PrecomputedResponse.RedirectTo(StatusCodes.Status301MovedPermanently, target);
        Unsafe = PrecomputedResponse.RedirectTo(StatusCodes.Status308PermanentRedirect, target);
    }

    public PrecomputedResponse Safe { get; }

    public PrecomputedResponse Unsafe { get; }

    public PrecomputedResponse For(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? Safe : Unsafe;
}
