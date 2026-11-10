using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Proxy;

/// <summary>
/// Writes the built-in page for an upstream failure, from the response-start hook that already puts the gate's headers
/// back. YARP sets <see cref="IForwarderErrorFeature"/> and a bare status (502, 503, 504) and returns; the hook runs
/// when Kestrel starts the response, before any header is sent, and can therefore replace the empty body with the page.
/// </summary>
/// <remarks>
/// <para><b>Why this costs nothing on the successful path.</b> The hook is registered for every request already, and the
/// callback is static with the context as its state. A successful forward has started the response by the time it runs,
/// so the error feature is unset and the callback returns a completed task; no wrapper around <c>next</c>, no
/// continuation and no per-request closure is added on that path.</para>
/// <para><b>Why the body can be written there.</b> The callback runs inside Kestrel's <c>OnStarting</c> before
/// <c>ProduceStart</c>. Starting the response explicitly writes the status line and headers first, and the page's
/// segments then go into the body pipe without a flush; Kestrel flushes when the request ends. Advancing bytes into an
/// unstarted response would put them ahead of the headers.</para>
/// </remarks>
internal static class UpstreamErrors
{
    /// <summary>
    /// Writes the page for YARP's status when the response hasn't started. A status without a built-in page, or a
    /// response that already started (a body error), keeps YARP's own answer.
    /// </summary>
    public static Task WriteAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Response.HasStarted || ErrorPages.Upstream(context.Response.StatusCode) is not { } response)
        {
            return Task.CompletedTask;
        }

        return ResponseWriter.WriteAsync(context, response);
    }
}
