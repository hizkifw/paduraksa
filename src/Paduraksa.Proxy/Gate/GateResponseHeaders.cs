using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Http;

namespace Paduraksa.Proxy.Gate;

/// <summary>Re-applies the host's response headers when YARP answers with an error. Its
/// <c>HttpForwarder.HandleResponseBodyErrorAsync</c> clears the whole response when an upstream sends headers and
/// then fails before the first body byte, and returns the bare 502 without running the response transforms again; the
/// same is true of the answers that never reach the transforms (no healthy destination, a failed request).</summary>
internal static class GateResponseHeaders
{
    /// <summary>Puts the host's headers back on a response that hasn't started, exactly as the gate set them: the
    /// request ID included, whose text is materialised once and shared, and the custom response rules after them.</summary>
    public static void Reapply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = context.Features.Get<IGateFeature>()?.State;
        if (context.Response.HasStarted || state?.Host is not { } host)
        {
            return;
        }

        var headers = host.ResponseHeaders;
        for (var i = 0; i < headers.Length; i++)
        {
            _ = ResponseHeaders.Apply(context, in headers[i], state);
        }

        CompiledHeaders.ApplyResponse(context.Response.Headers, (state.Route?.Headers ?? host.Headers).Response, again: true);
    }
}
