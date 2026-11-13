using Paduraksa.Proxy.Gate;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Gives a stripping route's <c>Location</c> its client-facing prefix back: an upstream that redirects to a path of its own
/// (or to an absolute URL on the request's <c>Host</c> or its own address) under what the route sends it would otherwise send the
/// client to a path the proxy doesn't route that way, or to an address only the proxy can reach
/// (<see cref="Snapshot.PathRewrite.ClientLocation"/>).
/// </summary>
/// <remarks>Cost: nothing for a route without a rewrite or a response without <c>Location</c>; one string for a redirect that is
/// rewritten.</remarks>
internal static class LocationRewriteTransform
{
    public static ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var http = context.HttpContext;
        if (http.Features.Get<IGateFeature>()?.State.Route?.Rewrite is not { } rewrite)
        {
            return ValueTask.CompletedTask;
        }

        var headers = http.Response.Headers;
        if (!rewrite.RewriteLocation || headers.Location is not { Count: 1 } location || location[0] is not { Length: > 0 } value)
        {
            return ValueTask.CompletedTask;
        }

        // What the upstream was asked for: the Host sent (a custom Host header mode), else the destination's own authority.
        var sent = context.ProxyResponse?.RequestMessage is { } forwarded ? forwarded.Headers.Host ?? forwarded.RequestUri?.Authority : null;
        var host = http.Request.Headers.Host.Count == 1 ? http.Request.Headers.Host[0] : null;
        var scheme = http.Features.Get<IGateFeature>()?.State is { IsHttps: true } || http.Request.IsHttps ? "https" : "http";
        if (rewrite.ClientLocation(value, host, sent, scheme) is { } rewritten)
        {
            headers.Location = rewritten;
        }

        return ValueTask.CompletedTask;
    }
}
