using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Http;

namespace Paduraksa.Proxy.Gate;

/// <summary>Re-applies the host's response headers after a forward. YARP's
/// <c>HttpForwarder.HandleResponseBodyErrorAsync</c> clears the whole response when an upstream sends headers and
/// then fails before the first body byte, and returns the bare 502 without running the response transforms again.</summary>
internal static class GateResponseHeaders
{
    /// <summary>Puts the host's headers back on a response that hasn't started, exactly as the gate set them: the
    /// request ID included, whose text is materialised once and shared.</summary>
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
    }
}
