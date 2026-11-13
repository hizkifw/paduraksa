using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Sets the <c>Host</c> the upstream receives from the route's compiled <see cref="UpstreamHost"/> (docs/design/config.md "Upstream Host header modes"): the client's own
/// (<c>Original</c>), none, so the HTTP client sends the destination's address (<c>Destination</c>), or a fixed name (<c>Custom</c>).
/// The forwarder's HTTP client takes the TLS server name from the same header, so SNI follows it with nothing more to do.
/// </summary>
/// <remarks>
/// Cost per request: reading a precomputed string off the compiled route (<see cref="HostFor"/>, 0 B), then the one header write the old
/// <c>AddOriginalHost(true)</c> also made (that write allocates inside <c>HttpRequestHeaders</c>, whichever name it carries). It is registered after
/// <c>AddOriginalHost(false)</c>, which is what keeps YARP from adding its own transform at the end that drops a <c>Host</c> equal to
/// the client's.
/// </remarks>
internal static class UpstreamHostTransform
{
    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ProxyRequest.Headers.Host = HostFor(context.HttpContext);
        return ValueTask.CompletedTask;
    }

    /// <summary>The <c>Host</c> to send; null: none, so the destination's own address goes out.</summary>
    internal static string? HostFor(HttpContext context)
    {
        var host = context.Features.Get<IGateFeature>()?.State.Route?.UpstreamHost;
        return host?.Mode switch
        {
            UpstreamHostHeaderMode.Destination => null,
            UpstreamHostHeaderMode.Custom => host.Name,
            _ => context.Request.Host.Value,
        };
    }
}
