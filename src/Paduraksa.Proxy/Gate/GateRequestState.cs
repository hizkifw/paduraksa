using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Geo;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>Published by the gate so later stages (YARP transforms, telemetry, error pages) see its decisions.</summary>
public interface IGateFeature
{
    GateRequestState State { get; }
}

/// <summary>
/// Everything the gate resolved for one request. One instance lives in Kestrel's
/// <c>IPersistentStateFeature</c> (per connection on H1, per pooled stream on H2/H3) and is <see cref="Reset"/> at
/// the start of every request, so it's never shared by two concurrent requests and costs nothing in steady state.
/// <para><b>Lifetime rule:</b> nothing may keep a reference to it after the request ends; the next request on the
/// connection or stream reuses it.</para>
/// <para><b>Accepted cost:</b> the references are only cleared when the next request starts, so an idle keep-alive
/// connection (or pooled H2/H3 stream) keeps the snapshot of its last request alive. That's bounded: at most one
/// snapshot per idle connection or pooled stream, for at most Kestrel's keep-alive timeout (130 s by default; pooled
/// H2 streams are evicted after about 5 s). Clearing it after the response would need a continuation on every
/// proxied request, which allocates.</para>
/// </summary>
public sealed class GateRequestState : IGateFeature
{
    /// <summary>The snapshot read once at gate entry; every decision for this request comes from it.</summary>
    public RuntimeSnapshot? Snapshot { get; internal set; }

    public CompiledHost? Host { get; internal set; }

    public CompiledRoute? Route { get; internal set; }

    // Last client address formatted for the forwarding headers, cached per connection (H1) or pooled stream (H2/H3)
    // so repeated requests from one client format it once. Deliberately not cleared by Reset.
    private IpKey _formattedAddress;
    private string? _formatted;

    /// <summary>The client as resolved at gate entry: the address, where it came from, whether the peer is a trusted
    /// proxy and whether Cloudflare headers may be trusted.</summary>
    public ClientIpResolution ClientIp { get; internal set; }

    /// <summary>The scheme the client used: the trusted proxies' (<see cref="ClientIpResolution.Scheme"/>) or else
    /// the connection's.</summary>
    public bool IsHttps { get; internal set; }

    /// <summary>The resolved client address (IPv4 as IPv4-mapped, so TCP on <c>[::]</c> and QUIC agree);
    /// <see cref="IpKey.Unspecified"/> when unknown.</summary>
    public IpKey ClientAddress => ClientIp.Address;

    /// <summary>Where the request came from, per Cloudflare (<see cref="ClientIpResolution.Country"/>); unknown unless the peer is a
    /// trusted Cloudflare one.</summary>
    public CountryCode ClientCountry => ClientIp.Country;

    /// <summary>The request's ID, generated or adopted from a trusted proxy, as text. <b>The one allocation a request
    /// accepts</b>: an <c>X-Request-Id</c> header value can only be a <see cref="string"/>, so the generated ID is
    /// formatted once here (<see cref="RequestIds.Format(long)"/>) and shared by the response header, the upstream
    /// header and the access log.</summary>
    public string RequestIdText() => _requestId ??= RequestIds.Format(_requestIdCounter);

    /// <summary>Whether the ID came from the client (a trusted proxy's <c>X-Request-Id</c>) rather than being generated here.</summary>
    public bool HasClientRequestId => _requestIdFromClient;

    /// <summary>
    /// Whether <see cref="InitRequestId"/> ran: without the gate in the pipeline (a test app's <c>WithoutGate</c>) there is
    /// no ID to log, and materialising one here would invent an ID no response ever carried.
    /// </summary>
    internal bool HasRequestId { get; private set; }

    /// <summary>
    /// True when the gate dropped the request: the connection or stream is aborted and <b>no response is sent</b>, so the
    /// access log records no status.
    /// </summary>
    public bool Aborted { get; internal set; }

    GateRequestState IGateFeature.State => this;

    // The request ID: the adopted header value, or (when null) the counter a generated one is formatted from.
    private string? _requestId;
    private bool _requestIdFromClient;
    private long _requestIdCounter;

    // The upstream leg, filled by the YARP access-log transform (Yarp.AccessLogTransform) and read by
    // Logging.AccessLogMiddleware once the request is over. Ticks are TimeSpan ticks; -1 means "not forwarded".
    internal long UpstreamStartedTimestamp { get; set; }

    internal long UpstreamTicks { get; set; } = -1;

    internal string? UpstreamAddress { get; set; }

    /// <summary>
    /// When the request entered the public pipeline, as a <see cref="System.Diagnostics.Stopwatch"/> timestamp, set by the
    /// access log at the top so the entry's total covers the whole request. Non-zero marks the state as this request's
    /// already; the gate then reuses it without resetting it again (see <see cref="GateMiddleware.BeginRequest"/>).
    /// </summary>
    internal long RequestStartedTimestamp { get; set; }

    /// <summary>
    /// The ring the completion callback writes to. The callback is static and its only state is the
    /// <see cref="Microsoft.AspNetCore.Http.HttpContext"/>, so the ring comes from this pooled state rather than from a
    /// closure or a per-request object.
    /// </summary>
    internal AccessLogRing? AccessLogRing { get; set; }

    /// <summary>Set by an access list that accepted the request's basic-auth credentials: the upstream must not see the proxy's
    /// <c>Authorization</c> header (<see cref="Yarp.ForwardedHeadersTransform"/> removes it).</summary>
    internal bool StripAuthorization { get; set; }

    /// <summary>The policy whose verdict the gate is waiting for, where it sits (<see cref="DeferredScope"/>, <see cref="DeferredIndex"/>),
    /// so the chain resumes right after it.</summary>
    internal IDeferredPolicy? DeferredPolicy { get; set; }

    internal PolicyScope DeferredScope { get; set; }

    internal int DeferredIndex { get; set; }

    /// <summary>The last access-list verdict of a simulated request, for the trace's sentence; never set on a real one.</summary>
    internal AccessLists.AccessListOutcome AccessListOutcome { get; set; }

    /// <summary>Clears every field at the start of a request.</summary>
    internal void Reset()
    {
        StripAuthorization = false;
        DeferredPolicy = null;
        AccessListOutcome = default;
        Snapshot = null;
        Host = null;
        Route = null;
        ClientIp = default;
        IsHttps = false;
        Aborted = false;
        HasRequestId = false;
        _requestId = null;
        _requestIdFromClient = false;
        _requestIdCounter = 0;
        UpstreamStartedTimestamp = 0;
        UpstreamTicks = -1;
        UpstreamAddress = null;
        RequestStartedTimestamp = 0;
        AccessLogRing = null;
    }

    /// <summary>
    /// Picks the request ID: the client's <c>X-Request-Id</c> when <paramref name="peerTrusted"/> (the trusted-proxy
    /// decision the forwarding headers use) and the value is one valid header line (<see cref="RequestIds.IsValid"/>),
    /// else a fresh counter for a generated ID. 0 B either way; the text is materialised on first use.
    /// </summary>
    internal void InitRequestId(HttpRequest request, bool peerTrusted)
    {
        _requestId = null;
        _requestIdFromClient = false;
        HasRequestId = true;
        if (peerTrusted)
        {
            var candidate = request.Headers[RequestIds.HeaderName];
            if (candidate.Count == 1 && candidate[0] is { } value && RequestIds.IsValid(value))
            {
                _requestId = value;
                _requestIdFromClient = true;
                return;
            }
        }

        _requestIdCounter = RequestIds.NextCounter();
    }

    /// <summary>
    /// <see cref="ClientAddress"/> as text (<c>192.0.2.1</c>, <c>2001:db8::1</c>), for <c>X-Forwarded-For</c> and
    /// <c>X-Real-IP</c>. Allocates the string only when the address differs from the previous request's on this
    /// connection or stream.
    /// </summary>
    public string FormattedClientAddress()
    {
        var address = ClientAddress;
        if (_formatted is null || address != _formattedAddress)
        {
            _formatted = address.ToString();
            _formattedAddress = address;
        }

        return _formatted;
    }
}
