using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.Gate;

/// <summary>The fixed answers of the request limits, built once for the process.</summary>
internal static class RequestLimitResponses
{
    /// <summary>A declared <c>Content-Length</c> over the host's or route's body limit.</summary>
    public static PrecomputedResponse ContentTooLarge { get; } =
        PrecomputedResponse.Text(StatusCodes.Status413PayloadTooLarge, "content too large\n", htmlPage: ErrorPages.RequestTooLarge);

    /// <summary>A request target over the host's limit.</summary>
    public static PrecomputedResponse UriTooLong { get; } =
        PrecomputedResponse.Text(StatusCodes.Status414UriTooLong, "uri too long\n", htmlPage: ErrorPages.UriTooLong);

    /// <summary>More header lines, or more header bytes, than the host takes.</summary>
    public static PrecomputedResponse HeadersTooLarge { get; } =
        PrecomputedResponse.Text(StatusCodes.Status431RequestHeaderFieldsTooLarge, "request header fields too large\n", htmlPage: ErrorPages.HeadersTooLarge);

    public static PrecomputedResponse For(RequestLimitVerdict verdict) => verdict switch
    {
        RequestLimitVerdict.UrlTooLong => UriTooLong,
        RequestLimitVerdict.TooManyHeaders or RequestLimitVerdict.HeadersTooLarge => HeadersTooLarge,
        _ => ContentTooLarge,
    };
}
