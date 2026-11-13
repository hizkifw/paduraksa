using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Snapshot;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Makes the host's response headers hold for a forwarded response. YARP copies an upstream's own headers
/// after the gate has set ours, so this runs after that copy and loops over the same compiled list the gate applied
/// (<see cref="CompiledHost.ResponseHeaders"/>): ours replaces the upstream's for <c>Server</c> and <c>X-Request-Id</c>,
/// an upstream's <c>Server</c> is removed when the host removes it, and <c>Alt-Svc</c> and HSTS are set exactly as the
/// gate set them (their conditions decide, so a plain-HTTP forward carries neither).
/// <para>It also runs when a forward fails (502/504), where there is no upstream response at all, so the modes hold
/// there too, and before a WebSocket upgrade is accepted. With header copying turned off a transform owns the response, so
/// a removed header is taken off the proxy response too, and a later copy (if one is ever added) can't resurrect it.</para>
/// <para>The host's or route's custom response rules (<see cref="CompiledHeaders"/>) follow the built-in list, so they
/// have the last word on the names they may touch.</para>
/// <para>0 B on the ordinary path: the precomputed <see cref="StringValues"/> is stored as it is, and the request ID is
/// the one string the request already materialised.</para>
/// </summary>
internal static class ResponseHeadersTransform
{
    public static ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.HttpContext.Features.Get<IGateFeature>()?.State is not { Host: { } host } state)
        {
            return ValueTask.CompletedTask;
        }

        var responseHeaders = context.HttpContext.Response.Headers;
        foreach (var operation in host.ResponseHeaders)
        {
            if (operation.Kind is ResponseHeaderKind.LeaveToUpstream or ResponseHeaderKind.NotSent
                || !ResponseHeaders.Holds(context.HttpContext, in operation, state, out _))
            {
                continue;
            }

            if (operation.Kind == ResponseHeaderKind.Remove)
            {
                responseHeaders.Remove(operation.Name);
                if (!context.HeadersCopied && context.ProxyResponse is { } removing)
                {
                    removing.Headers.Remove(operation.Name);
                }

                continue;
            }

            if (!context.HeadersCopied)
            {
                // A transform owns the response here: settle it on the proxy response, which is copied afterwards.
                Settle(context, operation, state, responseHeaders);
                continue;
            }

            responseHeaders[operation.Name] = ResponseHeaders.IsRequestId(operation) ? state.RequestIdText() : operation.Value;
        }

        // After the built-in list, so a rule for Server wins over the Server setting and a remove takes it off too.
        if ((state.Route?.Headers ?? host.Headers).Response is { Length: > 0 } rules)
        {
            if (!context.HeadersCopied && context.ProxyResponse is { } uncopied)
            {
                // The copy still to come would put the upstream's value next to a set, or bring a removed header back. Each name lives in
                // one of the two stores, and the other throws for it.
                foreach (var rule in rules)
                {
                    if (rule.Operation != Core.Model.HeaderOperation.Add)
                    {
                        _ = rule.OnContent ? uncopied.Content.Headers.Remove(rule.Name) : uncopied.Headers.Remove(rule.Name);
                    }
                }
            }

            CompiledHeaders.ApplyResponse(responseHeaders, rules);
        }

        return ValueTask.CompletedTask;
    }

    private static void Settle(ResponseTransformContext context, in ResponseHeaderOperation operation, GateRequestState state, IHeaderDictionary responseHeaders)
    {
        if (context.ProxyResponse is not { } proxyResponse)
        {
            return;
        }

        // The upstream's own goes, so a copy can't put it next to ours.
        var headers = proxyResponse.Headers;
        headers.Remove(operation.Name);
        if (responseHeaders.ContainsKey(operation.Name))
        {
            // The gate already put ours on the response, and the copy that follows keeps what is there: adding it to the
            // proxy response as well would send it twice.
            return;
        }

        if (ResponseHeaders.IsRequestId(operation))
        {
            headers.TryAddWithoutValidation(operation.Name, state.RequestIdText());
        }
        else
        {
            headers.TryAddWithoutValidation(operation.Name, operation.Value.ToArray());
        }
    }
}
