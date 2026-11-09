using System.Diagnostics;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;

using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Fills the upstream half of the access-log entry on a forwarded request: when the attempt started, how long
/// it took, and the destination it went to. Runs as YARP's first request transform and again after the response headers,
/// so the window covers the forward and the upstream's answer; load balancing picks the destination just before the
/// request transforms run, so that one step sits outside the measured window.
/// </summary>
/// <remarks>
/// <para>This is the cheapest source YARP offers for the upstream leg: an activity's timings would need the exporter and
/// the diagnostic listener, and a <see cref="Stopwatch"/> around the forwarder would mean replacing it.
/// <c>IForwarderErrorFeature</c> only says a forward failed, so the duration of a failed attempt is the time up to
/// the error (the destination is then unset, and the status says what happened).</para>
/// <para>Cost: two feature reads and a timestamp in, one subtraction and two field writes out. 0 B.</para>
/// </remarks>
internal static class AccessLogTransform
{
    public static ValueTask OnRequestAsync(RequestTransformContext context)
    {
        if (context.HttpContext.Features.Get<IGateFeature>()?.State is { } state)
        {
            state.UpstreamStartedTimestamp = Stopwatch.GetTimestamp();
            state.UpstreamTicks = AccessLogEntry.Unknown;
            state.UpstreamAddress = null;
        }

        return ValueTask.CompletedTask;
    }

    public static ValueTask OnResponseAsync(ResponseTransformContext context)
    {
        HttpContext httpContext = context.HttpContext;
        if (httpContext.Features.Get<IGateFeature>()?.State is not { } state)
        {
            return ValueTask.CompletedTask;
        }

        if (state.UpstreamStartedTimestamp != 0)
        {
            state.UpstreamTicks = Stopwatch.GetElapsedTime(state.UpstreamStartedTimestamp).Ticks;
        }

        if (httpContext.Features.Get<IReverseProxyFeature>() is { ProxiedDestination: { } destination })
        {
            state.UpstreamAddress = destination.Model.Config.Address;
        }

        return ValueTask.CompletedTask;
    }
}
