using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// Re-applies the host's response headers after a forward (PLAN §7.2 C). YARP's
/// <c>HttpForwarder.HandleResponseBodyErrorAsync</c> clears the whole response when an upstream sends headers and
/// then fails before the first body byte, and returns the bare 502 without running the response transforms again. The
/// host's compiled header list (<see cref="CompiledHost.ResponseHeaders"/>) is put back, by the same loop the gate and the
/// transform use, when the response can still be changed.
/// </summary>
internal static class GateResponseHeaders
{
    /// <summary>
    /// Puts the host's headers back on a response that hasn't started, exactly as the gate set them: the request ID
    /// included, whose text is materialised once and shared. No header value is built. A started response is left alone.
    /// </summary>
    public static void Reapply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GateRequestState? state = context.Features.Get<IGateFeature>()?.State;
        if (context.Response.HasStarted || state?.Host is not { } host)
        {
            return;
        }

        ResponseHeaderOperation[] headers = host.ResponseHeaders;
        for (int i = 0; i < headers.Length; i++)
        {
            _ = ResponseHeaders.Apply(context, in headers[i], state);
        }
    }
}
