using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Proxy.Net;
using Paduraksa.ProxyProtocol;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// The public pipeline's outermost middleware: it times the request, lets everything else run, and writes one
/// <see cref="AccessLogEntry"/> into the ring when the response completes - so a gate refusal (403, 421, the 404 for an
/// unknown host), the HTTPS redirect, a Redirect/Respond answer, a YARP forward and an exception all get a line.
/// </summary>
/// <remarks>
/// <para><b>0 B.</b> It reads strings the request already holds (method, path, query, host, protocol, headers), the client
/// address text the gate cached per connection (<see cref="GateRequestState.FormattedClientAddress"/>), the request ID the
/// gate already materialised for its headers, and YARP's destination address; it writes a struct into the ring. The
/// completion callback is a static method whose state is the <see cref="HttpContext"/>, so it captures no closure, and
/// Kestrel creates its per-connection callback stack once and reuses it. The ring and the start timestamp ride on the
/// gate's pooled state (<see cref="GateRequestState"/>). A request whose host turned the access log off costs a few reads
/// and nothing else, apart from the request counters, which every request feeds (<see cref="RequestMetricsRecorder"/>).</para>
/// <para><b>Bytes</b> are the body bytes the request really moved, counted by the pooled <see cref="Http.BodyCounter"/> that
/// <see cref="Begin"/> stands in for the body features before anything else runs: a chunked, streamed or compressed response
/// counts what went to the client, and an upload what was read from it.</para>
/// <para><b>Durations.</b> The total is <see cref="Stopwatch"/> from the top of the public pipeline to the end of the
/// response (Kestrel runs the completion callback after the response finished, before it disposes the context). The
/// upstream leg is measured by YARP's own transforms (<see cref="Yarp.AccessLogTransform"/>), which is the cheapest source
/// that covers the whole forward, and is <c>-</c> when nothing was forwarded. A forward that ends without a response
/// (YARP's own 502, say) is measured up to the failure here, and still names the destination YARP had chosen.</para>
/// <para><b>Status.</b> What the client got, as Kestrel has settled it by the time the callback runs: an unhandled
/// exception before anything went out is 500, a connection that ended without a response is 499, and a request body
/// Kestrel rejected is the 4xx it answers with. A request the gate
/// dropped without a response is <see cref="AccessLogEntry.NoResponse"/>, written as <c>-</c>: the connection was
/// aborted, so no status was sent and none is invented.</para>
/// <para><b>CF-Ray</b> is written only when the peer is a trusted Cloudflare edge or tunnel
/// (<see cref="ClientIpResolution.CloudflareHeadersTrusted"/>): from anyone else the header is theirs to forge, and it is
/// stripped before forwarding.</para>
/// </remarks>
public sealed class AccessLogMiddleware
{
    private readonly AccessLogRing _ring;
    private readonly MetricsRegistry? _metrics;
    private readonly Http.UpgradedConnections? _upgraded;

    /// <param name="ring">Where the lines go.</param>
    /// <param name="metrics">The request counters, which this completion callback also feeds (docs/design/observability.md "Metrics (M9)"); null: none are kept.</param>
    /// <param name="upgraded">Where accepted upgrades are tracked for the stop to close; null: not tracked.</param>
    public AccessLogMiddleware(AccessLogRing ring, MetricsRegistry? metrics = null, Http.UpgradedConnections? upgraded = null)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _ring = ring;
        _metrics = metrics;
        _upgraded = upgraded;
    }

    /// <summary>For <c>app.Use(accessLog.InvokeAsync)</c>, before the gate.</summary>
    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        Begin(context);
        return next(context);
    }

    /// <summary>
    /// Everything this middleware does before the rest of the pipeline runs: the pooled state, the start timestamp, the body
    /// counter and the completion callback. Internal so the allocation tests can measure it on a real Kestrel request.
    /// </summary>
    internal void Begin(HttpContext context)
    {
        Http.KestrelHttp2StreamReuse.BeforeRequest(context);
        var state = GateMiddleware.BeginRequest(context);
        state.AccessLogRing = _ring;
        state.Metrics = _metrics;
        state.RequestStartedTimestamp = Stopwatch.GetTimestamp();
        state.CountOnConnection(context);
        state.CountBody(context, _upgraded);
        RegisterCompletion(context, state);
    }

    /// <summary>
    /// Registers the completion callback, whose only state is the context, and the response-start one, whose only state is the
    /// pooled request state (it runs before the request ends, so the state is still this request's). Internal so the allocation
    /// test can drive it on a real Kestrel response.
    /// </summary>
    internal static void RegisterCompletion(HttpContext context, GateRequestState state)
    {
        context.Response.OnStarting(static started => MarkResponseStarted(started), state);
        context.Response.OnCompleted(static completed => RecordCompleted(completed), context);
    }

    // Kestrel runs its response-start callbacks last-registered first, so this one, registered at the top, runs last: right
    // before the headers are written.
    internal static Task MarkResponseStarted(object started)
    {
        ((GateRequestState)started).ResponseStartedTimestamp = Stopwatch.GetTimestamp();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Kestrel runs this after the response finished, with the final status settled (500 for an unhandled exception, 499
    /// for a connection that ended without a response; a rejected request body is settled here), and before it disposes
    /// the context.
    /// </summary>
    private static Task RecordCompleted(object completed)
    {
        var context = (HttpContext)completed;
        if (context.Features.Get<IGateFeature>()?.State is { AccessLogRing: { } ring } state)
        {
            // A request body Kestrel rejected (malformed, too slow) gets its 4xx only after this callback, from the
            // exception it kept; without this the line would say the 200 the untouched response still reports.
            if (!context.Response.HasStarted && context.Features.Get<IBadRequestExceptionFeature>()?.Error is BadHttpRequestException rejected)
            {
                context.Response.StatusCode = rejected.StatusCode;
            }

            // One clock read for the end of every figure: the total, the response send, and the counters'.
            var ended = Stopwatch.GetTimestamp();
            Record(context, ring, state.RequestStartedTimestamp, ended);
            if (state.Metrics is { } metrics)
            {
                RequestMetricsRecorder.Record(context, state, metrics, state.RequestStartedTimestamp, ended);
            }

            // Request-scoped: the gate skips its reset only for a request a stage above it already started.
            state.AccessLogRing = null;
            state.Metrics = null;
            state.RequestStartedTimestamp = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Fills the entry from the request as it ended and offers it to the ring. 0 B; internal so the allocation test can
    /// drive it directly.
    /// </summary>
    internal static void Record(HttpContext context, AccessLogRing ring, long started) => Record(context, ring, started, Stopwatch.GetTimestamp());

    /// <summary><see cref="Record(HttpContext, AccessLogRing, long)"/> with the end already read.</summary>
    internal static void Record(HttpContext context, AccessLogRing ring, long started, long ended)
    {
        var state = context.Features.Get<IGateFeature>()?.State;
        if (state is { Snapshot: { } snapshot } && !snapshot.AccessLogFor(state.Host))
        {
            // A known host's own setting, else the global default for an unknown host; without a snapshot, log.
            return;
        }

        var request = context.Request;
        var response = context.Response;
        var host = request.Headers.Host;
        var userAgent = request.Headers.UserAgent;
        var referer = request.Headers.Referer;
        var upstreamTicks = state?.UpstreamTicks ?? AccessLogEntry.Unknown;
        var answered = upstreamTicks >= 0;
        var upstreamAddress = state?.UpstreamAddress;
        if (state is { UpstreamStartedTimestamp: not 0 } && upstreamTicks < 0)
        {
            // A forward that got no response back (YARP's own 502, say): the attempt still has a duration, up to where it
            // failed, and YARP keeps the destination it chose on the feature.
            upstreamTicks = Stopwatch.GetElapsedTime(state.UpstreamStartedTimestamp, ended).Ticks;
            if (upstreamAddress is null && context.Features.Get<IReverseProxyFeature>() is { ProxiedDestination: { } destination })
            {
                upstreamAddress = destination.Model.Config.Address;
            }
        }

        var connectTicks = state is { UpstreamRequest: { } sent } && upstreamTicks >= 0 ? Yarp.UpstreamConnects.TicksFor(sent, upstreamTicks, answered) : AccessLogEntry.Unknown;

        AccessLogEntry entry = new()
        {
            // One clock read, not the injected TimeProvider: this runs on every request, and a timestamp needs neither its
            // virtual call nor a DateTimeOffset. Either is 0 B.
            TimestampUtcTicks = DateTime.UtcNow.Ticks,
            ClientAddress = state is not null && state.ClientAddress != IpKey.Unspecified ? state.FormattedClientAddress() : null,
            Host = host.Count == 1 ? host[0] : null,
            Method = request.Method,
            Path = request.Path.Value,
            Query = request.QueryString.Value,
            Protocol = request.Protocol,
            StatusCode = state is { Aborted: true } ? AccessLogEntry.NoResponse : response.StatusCode,
            ResponseBytes = state?.CountedBody?.ResponseBytes ?? AccessLogEntry.Unknown,
            RequestBytes = state?.CountedBody?.RequestBytes ?? AccessLogEntry.Unknown,
            TotalTicks = Stopwatch.GetElapsedTime(started, ended).Ticks,
            UpstreamTicks = upstreamTicks,
            UpstreamAddress = upstreamAddress,
            RequestId = state is { HasRequestId: true } ? state.RequestIdText() : null,
            CloudflareRay = state is not null && state.ClientIp.CloudflareHeadersTrusted ? FirstOf(request.Headers[CloudflareHeaders.Ray]) : null,
            Referer = FirstOf(referer),
            UserAgent = FirstOf(userAgent),
            TlsHandshakeTicks = state?.TlsHandshakeTicks ?? AccessLogEntry.Unknown,
            ConnectionRequest = state?.ConnectionRequest ?? 0,
            RequestBodyTicks = state?.CountedBody?.RequestBodyTicks ?? AccessLogEntry.Unknown,
            GateTicks = state is { GateEndedTimestamp: not 0 } ? Stopwatch.GetElapsedTime(started, state.GateEndedTimestamp).Ticks : AccessLogEntry.Unknown,
            UpstreamConnectTicks = connectTicks,
            ResponseSendTicks = ResponseSendTicks(state, ended),
        };
        ring.TryWrite(entry);
    }

    /// <summary>The response's start to <paramref name="ended"/>; unknown when nothing was sent (a dropped request never starts one).</summary>
    internal static long ResponseSendTicks(GateRequestState? state, long ended) =>
        state is { ResponseStartedTimestamp: not 0 and var responseStarted, Aborted: false }
            ? Stopwatch.GetElapsedTime(responseStarted, ended).Ticks
            : AccessLogEntry.Unknown;

    /// <summary>A single-valued header's value, or null. 0 B.</summary>
    private static string? FirstOf(StringValues values) => values.Count == 1 ? values[0] : null;
}
