using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.Gate;

/// <summary>The answers of the Cloudflare origin checks, built once for the process.</summary>
internal static class OriginLockdownResponses
{
    /// <summary>A host that only takes Cloudflare's connections got one from somewhere else.</summary>
    public static PrecomputedResponse Forbidden { get; } =
        PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n", htmlPage: ErrorPages.OriginLockdown);

    /// <summary>A host with Authenticated Origin Pulls got a request on a connection without a verified client certificate.</summary>
    public static PrecomputedResponse ClientCertificateRequired { get; } =
        PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "forbidden\n", htmlPage: ErrorPages.ClientCertificateRequired);
}
