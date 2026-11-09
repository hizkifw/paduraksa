using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Makes the response's <c>X-Request-Id</c> say what the host's <see cref="RequestIdPropagation"/> says (PLAN §7.4): ours
/// when the setting sends it back to the client, and nothing at all when it doesn't - an upstream's own
/// <c>X-Request-Id</c> is stripped then, so the host's choice can't be defeated by whatever the app behind it sets.
/// </summary>
/// <remarks>
/// The gate already set ours on the response before forwarding, so an upstream that sets the same header would overwrite
/// it as YARP copies the response headers. This runs after that copy (or, when YARP runs it before the copy, on the proxy
/// response that is about to be copied) and settles the client-facing value. 0 B: one header assignment or removal.
/// </remarks>
internal static class RequestIdTransform
{
    public static ValueTask OnResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.Features.Get<IGateFeature>()?.State is not { } state)
        {
            return ValueTask.CompletedTask;
        }

        RequestIdPropagation propagation = state.Snapshot is { } snapshot
            ? snapshot.RequestIdPropagationFor(state.Host)
            : RequestIdPropagation.UpstreamAndResponse;
        string? id = propagation.SendsResponse() ? state.RequestIdText() : null;
        if (context.HeadersCopied)
        {
            Settle(context.HttpContext.Response.Headers, id);
        }
        else if (context.ProxyResponse is { } proxyResponse)
        {
            Settle(proxyResponse.Headers, id);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Sets our value, or removes whatever is there. Returns nothing: both outcomes are the point.</summary>
    private static void Settle(IHeaderDictionary headers, string? id)
    {
        if (id is null)
        {
            headers.Remove(RequestIds.HeaderName);
        }
        else
        {
            headers[RequestIds.HeaderName] = id;
        }
    }

    private static void Settle(System.Net.Http.Headers.HttpResponseHeaders headers, string? id)
    {
        if (id is null)
        {
            headers.Remove(RequestIds.HeaderName);
        }
        else
        {
            headers.Remove(RequestIds.HeaderName);
            headers.TryAddWithoutValidation(RequestIds.HeaderName, id);
        }
    }
}
