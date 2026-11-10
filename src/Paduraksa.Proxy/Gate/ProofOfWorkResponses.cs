using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Pages;

namespace Paduraksa.Proxy.Gate;

/// <summary>The fixed answers of the proof-of-work gate. The verify endpoint's success and its per-request 413/403 are
/// written there; these are the ones a route policy or the endpoint can reuse unchanged, each with its built-in page
/// for a browser.</summary>
internal static class ProofOfWorkResponses
{
    /// <summary>A request that isn't a browser (not <c>GET</c>/<c>HEAD</c> accepting <c>text/html</c>).</summary>
    public static PrecomputedResponse NonBrowser { get; } =
        PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "This page needs a browser that runs JavaScript.\n", htmlPage: ErrorPages.BrowserRequired);

    /// <summary>The key isn't open: the protected route fails closed rather than let the request through unchallenged.</summary>
    public static PrecomputedResponse Unavailable { get; } =
        PrecomputedResponse.Text(StatusCodes.Status503ServiceUnavailable, "The browser check is temporarily unavailable.\n", htmlPage: ErrorPages.CheckUnavailable);

    /// <summary>The verify endpoint's body is over 2 KiB.</summary>
    public static PrecomputedResponse BodyTooLarge { get; } =
        PrecomputedResponse.Text(StatusCodes.Status413PayloadTooLarge, "The verification request is too large.\n", htmlPage: ErrorPages.PayloadTooLarge);

    /// <summary>A posted challenge or solution that doesn't check out.</summary>
    public static PrecomputedResponse VerificationFailed { get; } =
        PrecomputedResponse.Text(StatusCodes.Status403Forbidden, "The browser check failed. Reload the page to try again.\n", htmlPage: ErrorPages.CheckFailed);

    /// <summary>The verify path with a method other than <c>POST</c> on a host where the gate is on.</summary>
    public static PrecomputedResponse MethodNotAllowed { get; } = PrecomputedResponse.Create(
        StatusCodes.Status405MethodNotAllowed,
        "Use POST for the browser check.\n"u8,
        "text/plain; charset=utf-8",
        "no-store",
        [new PrecomputedHeader("Allow", new StringValues("POST"))]);
}
