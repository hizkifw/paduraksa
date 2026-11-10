using System.Net.Http.Headers;

using Paduraksa.Core.Hosting;
using Paduraksa.Proxy.Gate;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Stamps the admin app's internal hop secret on requests forwarded to an admin host, and removes any client-supplied copy from
/// every request. The admin app trusts <c>X-Forwarded-*</c> only when that header has this process's secret, so a
/// local process or an SSRF request to the TCP fallback can't forge its client IP, scheme or host.
/// </summary>
/// <remarks>
/// Cost per request: one header removal. The secret is a shared string, added only on the admin route; nothing is formatted or
/// allocated. The header is removed on every route (not just the admin one), so an upstream never receives it.
/// </remarks>
internal sealed class AdminInternalSecretTransform(AdminInternalListener? listener)
{
    public ValueTask ApplyAsync(RequestTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var upstream = context.ProxyRequest.Headers;
        upstream.Remove(AdminInternalListener.HeaderName);

        var state = context.HttpContext.Features.Get<IGateFeature>()?.State;
        if (listener is not null && state?.Route is { Admin: true })
        {
            upstream.TryAddWithoutValidation(AdminInternalListener.HeaderName, listener.Secret);
        }

        return ValueTask.CompletedTask;
    }
}
