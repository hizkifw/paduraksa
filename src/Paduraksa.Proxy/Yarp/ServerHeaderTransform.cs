using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Makes the host's <c>Server</c> mode hold for a forwarded response (PLAN §14 M6). YARP copies an upstream's header
/// after the gate has set ours, so without this the client would get both; <see cref="ServerHeaderMode.Upstream"/> leaves
/// that copy alone, and <see cref="ServerHeaderMode.Remove"/> drops it. It also runs when a forward fails (502/504), where
/// there is no upstream response at all, so the mode holds there too. A WebSocket upgrade goes through it before the
/// upgrade is accepted.
/// </summary>
internal static class ServerHeaderTransform
{
    public static ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GateRequestState? state = context.HttpContext.Features.Get<IGateFeature>()?.State;
        if (state?.Host is not { } host)
        {
            return ValueTask.CompletedTask;
        }

        IHeaderDictionary responseHeaders = context.HttpContext.Response.Headers;
        switch (host.ServerMode)
        {
            case ServerHeaderMode.Paduraksa:
            case ServerHeaderMode.Custom:
                // Replace whatever YARP appended from the upstream with the host's one shared value.
                responseHeaders[ServerHeader.HeaderName] = host.Server;
                break;
            case ServerHeaderMode.Remove:
                responseHeaders.Remove(ServerHeader.HeaderName);
                break;
            default:
                // Upstream: the copied header is what the client gets; gate answers never reach this transform.
                return ValueTask.CompletedTask;
        }

        // With header copying turned off a transform owns the response, and a later copy (if one is ever added) must not
        // resurrect the upstream's value. The default pipeline copies first, so this is normally not reached.
        if (!context.HeadersCopied && context.ProxyResponse is { } proxyResponse)
        {
            proxyResponse.Headers.Remove(ServerHeader.HeaderName);
        }

        return ValueTask.CompletedTask;
    }
}
