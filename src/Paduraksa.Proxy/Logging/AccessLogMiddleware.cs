using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// The public pipeline's outermost middleware: it times the request, lets everything else run, and writes one
/// <see cref="AccessLogEntry"/> into the ring on the way out - in a <c>finally</c>, so a gate refusal (403, 421, the 404
/// for an unknown host), the HTTPS redirect, a Redirect/Respond answer, a YARP forward and an exception all get a line.
/// </summary>
/// <remarks>
/// <para><b>0 B.</b> It reads strings the request already holds (method, path, query, host, protocol, headers), the client
/// address text the gate cached per connection (<see cref="GateRequestState.FormattedClientAddress"/>), the request ID the
/// gate already materialised for its headers, and YARP's destination address; it writes a struct into the ring. No
/// closures, no <c>OnCompleted</c> registration, no timer. A request whose host turned the access log off costs a few
/// reads and nothing else.</para>
/// <para><b>Bytes.</b> Kestrel doesn't expose the bytes it wrote; without wrapping the response body there is no 0 B way to
/// count them. So the entry carries the response's <c>Content-Length</c> when it declares one, and -1 (written as
/// <c>-</c>) otherwise: a chunked or streamed response shows <c>-</c>, not a number nobody can read cheaply.</para>
/// <para><b>Durations.</b> The total is <see cref="Stopwatch"/> from entry to exit. The upstream leg is measured by YARP's
/// own transforms (<see cref="Yarp.AccessLogTransform"/>), which is the cheapest source that covers the whole forward, and
/// is <c>-</c> when nothing was forwarded. A forward that ends without a response (YARP's own 502, say) is measured up to
/// the failure here, and still names the destination YARP had chosen.</para>
/// <para><b>Status.</b> What the client got. A request that ended in an unhandled exception before anything went out is
/// 500 (Kestrel's answer, set here before the exception continues on its way), and a request the gate dropped without a
/// response is <see cref="AccessLogEntry.NoResponse"/>, written as <c>-</c>: the connection was aborted, so no status was
/// sent and none is invented.</para>
/// <para><b>CF-Ray</b> is written only when the peer is a trusted Cloudflare edge or tunnel
/// (<see cref="ClientIpResolution.CloudflareHeadersTrusted"/>): from anyone else the header is theirs to forge, and it is
/// stripped before forwarding.</para>
/// </remarks>
public sealed class AccessLogMiddleware
{
    private readonly AccessLogRing _ring;

    public AccessLogMiddleware(AccessLogRing ring)
    {
        ArgumentNullException.ThrowIfNull(ring);
        _ring = ring;
    }

    /// <summary>For <c>app.Use(accessLog.InvokeAsync)</c>, before the gate.</summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            // Nothing was sent yet, so the client's answer is still open: Kestrel would send 500, and the log says the same.
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            throw;
        }
        finally
        {
            Record(context, _ring, started);
        }
    }

    /// <summary>
    /// Fills the entry from the request as it ended and offers it to the ring. 0 B; internal so the allocation test can
    /// drive it directly.
    /// </summary>
    internal static void Record(HttpContext context, AccessLogRing ring, long started)
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
        var upstreamAddress = state?.UpstreamAddress;
        if (state is { UpstreamStartedTimestamp: not 0 } && upstreamTicks < 0)
        {
            // A forward that got no response back (YARP's own 502, say): the attempt still has a duration, up to where it
            // failed, and YARP keeps the destination it chose on the feature.
            upstreamTicks = Stopwatch.GetElapsedTime(state.UpstreamStartedTimestamp).Ticks;
            if (upstreamAddress is null && context.Features.Get<IReverseProxyFeature>() is { ProxiedDestination: { } destination })
            {
                upstreamAddress = destination.Model.Config.Address;
            }
        }

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
            ResponseBytes = response.ContentLength ?? AccessLogEntry.Unknown,
            TotalTicks = Stopwatch.GetElapsedTime(started).Ticks,
            UpstreamTicks = upstreamTicks,
            UpstreamAddress = upstreamAddress,
            RequestId = state?.RequestIdText(),
            CloudflareRay = state is not null && state.ClientIp.CloudflareHeadersTrusted ? FirstOf(request.Headers[CloudflareHeaders.Ray]) : null,
            Referer = FirstOf(referer),
            UserAgent = FirstOf(userAgent),
        };
        ring.TryWrite(entry);
    }

    /// <summary>A single-valued header's value, or null. 0 B.</summary>
    private static string? FirstOf(StringValues values) => values.Count == 1 ? values[0] : null;
}
