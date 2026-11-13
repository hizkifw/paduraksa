using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Applies the route's custom request headers (<see cref="Snapshot.CompiledRoute.Headers"/>) to the request the upstream
/// receives. It is registered after the forwarding, identity and session-cookie transforms, so a rule has the last word on the
/// names it may touch; the names those transforms own are refused by the validator and the compiler, never overwritten here.
/// </summary>
/// <remarks>Cost per request: one length test when the route has no rule. A rule is a removal or one header write of a
/// precomputed string, and the write allocates inside <c>HttpRequestHeaders</c> as every header YARP copies does.</remarks>
internal static class CustomHeadersTransform
{
    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.Features.Get<IGateFeature>()?.State.Route?.Headers.Request is { Length: > 0 } rules)
        {
            CompiledHeaders.ApplyRequest(context.ProxyRequest, rules);
        }

        return ValueTask.CompletedTask;
    }
}
