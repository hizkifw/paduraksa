using System.Diagnostics;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;

using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// Counts a finished request into the <see cref="MetricsRegistry"/> from the access-log completion callback, which runs for
/// every request whether or not its host logs it (PLAN §7.4). The durations come from the same two clocks the access log
/// reads: the request's start timestamp and YARP's request and response transforms.
/// </summary>
internal static class RequestMetricsRecorder
{
    /// <summary>
    /// Reads what the request ended as and adds it. 0 B: a few field reads, feature lookups only for an answer that failed (the forwarder's error and the destination it had chosen), and <see cref="MetricsRegistry.RecordRequest"/>.
    /// </summary>
    public static void Record(HttpContext context, GateRequestState state, MetricsRegistry metrics, long started)
    {
        // No snapshot: the gate never ran (a test app without it), so there is no host to count against.
        if (state.Snapshot is null)
        {
            return;
        }

        var host = state.Host;
        var route = state.Route;
        var response = context.Response;
        var status = state.Aborted ? 0 : response.StatusCode;
        var outcome = GateOutcomes.Classify(state, status);

        long upstreamMicros = -1;
        var error = 0;
        var destinationKey = state.UpstreamDestinationKey;
        if (outcome == GateOutcome.Forwarded)
        {
            if (state.UpstreamStartedTimestamp != 0)
            {
                // A forward that got no response back is still timed, up to where it failed.
                upstreamMicros = (state.UpstreamTicks >= 0 ? state.UpstreamTicks : Stopwatch.GetElapsedTime(state.UpstreamStartedTimestamp).Ticks) / TimeSpan.TicksPerMicrosecond;
            }

            // The forwarder's error is a feature lookup, which costs more than the rest of this method on a successful request;
            // YARP's failures answer 4xx or 5xx or abort the request (a body that failed after the status was sent), so only those look.
            if ((status >= StatusCodes.Status400BadRequest || context.RequestAborted.IsCancellationRequested)
                && context.Features.Get<IForwarderErrorFeature>() is { } failed)
            {
                error = (int)failed.Error;
                if (destinationKey is null && context.Features.Get<IReverseProxyFeature>() is { ProxiedDestination: { } chosen })
                {
                    destinationKey = chosen.DestinationId;
                }
            }
        }

        var upstream = route?.Upstream;
        metrics.RecordRequest(new RequestMetrics
        {
            Host = host is null ? MetricsRegistry.UnmatchedHost : host.MetricSlot,
            Route = route is null ? MetricSlot.None : route.MetricSlot,
            Upstream = upstream is not null && outcome == GateOutcome.Forwarded ? upstream.Slot : MetricSlot.None,
            Destination = upstream is not null && outcome == GateOutcome.Forwarded ? upstream.DestinationSlot(destinationKey) : MetricSlot.None,
            Status = status,
            Outcome = outcome,
            ResponseBytes = response.ContentLength ?? -1,
            TotalMicros = Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMicrosecond,
            UpstreamMicros = upstreamMicros,
            ForwarderError = error,
        });
    }
}
