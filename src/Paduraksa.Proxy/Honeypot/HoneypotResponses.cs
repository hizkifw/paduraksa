using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>The trap's fixed answers.</summary>
internal static class HoneypotResponses
{
    /// <summary>A method other than <c>GET</c> and <c>HEAD</c> (the verify endpoint's <c>POST</c> aside) under the prefix.</summary>
    public static PrecomputedResponse MethodNotAllowed { get; } = PrecomputedResponse.Create(
        StatusCodes.Status405MethodNotAllowed,
        "Method not allowed.\n"u8,
        "text/plain; charset=utf-8",
        "no-store",
        [new PrecomputedHeader("Allow", new StringValues("GET, HEAD"))]);

    /// <summary>The trap is already generating as many answers as it may.</summary>
    public static PrecomputedResponse Busy { get; } =
        PrecomputedResponse.Text(StatusCodes.Status503ServiceUnavailable, "Try again shortly.\n");

    /// <summary>The <c>Retry-After</c> a shed request carries, in seconds.</summary>
    public const string RetryAfter = "5";
}
