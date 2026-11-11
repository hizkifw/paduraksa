using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// Which operator page, if any, an answer with a built-in page is written as. The answers are shared by every host (one 404, one 429), so
/// the choice is made when one is written, from the request's host: the host's own page for that status, else the global defaults' for a
/// request that matched no host. The simulator asks the same question, so what it reports is what is served.
/// </summary>
internal static class ErrorPageSelection
{
    /// <summary>The page <paramref name="response"/> is written as for this request, or null for its built-in page.</summary>
    public static CustomErrorPage? Find(HttpContext context, PrecomputedResponse response)
    {
        if (response.CustomPage is { } own)
        {
            return own;
        }

        return response.HtmlPage is { CustomizableStatus: > 0 } page && page.CustomizableStatus == response.StatusCode
            ? Find(context.Features.Get<IGateFeature>()?.State, response.StatusCode)
            : null;
    }

    public static CustomErrorPage? Find(GateRequestState? state, int status)
    {
        var pages = state?.Host is { } host ? host.ErrorPages : state?.Snapshot?.DefaultErrorPages;
        return pages?.Find(status);
    }
}
