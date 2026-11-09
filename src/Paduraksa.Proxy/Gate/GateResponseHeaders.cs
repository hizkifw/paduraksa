using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// Re-applies the gate's per-host response headers after a forward. YARP's
/// <c>HttpForwarder.HandleResponseBodyErrorAsync</c> clears the whole response when an upstream sends headers and
/// then fails before the first body byte, and returns the bare 502 without running the response transforms again. The
/// gate's <c>Alt-Svc</c>, HSTS, <c>Server</c> and response <c>X-Request-Id</c> headers are put back, under the same
/// helpers, when the response can still be changed.
/// </summary>
internal static class GateResponseHeaders
{
    /// <summary>
    /// Puts the host's headers back on a response that hasn't started. No header value is built: the gate's own
    /// precomputed values are stored as they are, and <c>Remove</c> only removes. A started response is left alone.
    /// </summary>
    public static void Reapply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GateRequestState? state = context.Features.Get<IGateFeature>()?.State;
        if (context.Response.HasStarted || state?.Host is not { } host)
        {
            return;
        }

        AltSvcHeader.Apply(context, host.AltSvc);
        StringValues hostHeader = context.Request.Headers.Host;
        ReadOnlySpan<char> hostName = hostHeader.Count == 1 ? HostTable.HostName(hostHeader[0]) : default;
        HstsHeader.Apply(context, host.Hsts, state.IsHttps, hostName);
        ServerHeader.Apply(context, host.Server, host.ServerMode);
        RequestIdPropagation propagation = state.Snapshot is { } snapshot
            ? snapshot.RequestIdPropagationFor(host)
            : RequestIdPropagation.UpstreamAndResponse;
        if (propagation.SendsResponse())
        {
            context.Response.Headers[RequestIds.HeaderName] = state.RequestIdText();
        }
    }
}
