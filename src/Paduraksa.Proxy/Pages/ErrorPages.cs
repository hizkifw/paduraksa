using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Pages;

/// <summary>
/// The built-in error pages: the shared template with the status code and its standard name as the h1 (and the title),
/// one sentence on what happened and what to do, and the request ID, so a visitor can report it. Every page is ASCII and has no script or external resource, so its CSP is a style
/// hash and nothing else. A text answer keeps its existing plain body for clients that don't ask for HTML.
/// </summary>
internal static class ErrorPages
{
    /// <summary>The IP access policy's built-in refusal; a custom deny body stays the operator's own text.</summary>
    public static HtmlPageTemplate AccessDenied { get; } = Page("403 Forbidden", "Your network address isn't allowed to open this site.");

    /// <summary>A host that only takes connections from Cloudflare got one from another address.</summary>
    public static HtmlPageTemplate OriginLockdown { get; } = Page("403 Forbidden", "This site only accepts connections that come through Cloudflare.");

    /// <summary>A host matched but no route took the path, or the host is unknown.</summary>
    public static HtmlPageTemplate NotFound { get; } = Page("404 Not Found", "Nothing on this site matches that address.");

    /// <summary>The connection is weaker than the host's TLS settings, so an H2 coalesced connection must retry.</summary>
    public static HtmlPageTemplate Misdirected { get; } = Page("421 Misdirected Request", "This connection can't be used for this site. Reload the page to open a new one.");

    public static HtmlPageTemplate TooManyRequests { get; } = Page("429 Too Many Requests", "You've sent too many requests in a short time. Wait a moment, then try again.");

    public static HtmlPageTemplate BadGateway { get; } = Page("502 Bad Gateway", "The server behind this site didn't respond properly. Try again in a moment.");

    /// <summary>No healthy destination, or a proof-of-work key that can't be opened: the route fails closed.</summary>
    public static HtmlPageTemplate ServiceUnavailable { get; } = Page("503 Service Unavailable", "The site is temporarily unavailable. Try again in a moment.");

    /// <summary>The proof-of-work key is missing; the page names the check rather than the whole site.</summary>
    public static HtmlPageTemplate CheckUnavailable { get; } = Page("503 Service Unavailable", "The browser check is temporarily unavailable. Try again in a moment.");

    public static HtmlPageTemplate GatewayTimeout { get; } = Page("504 Gateway Timeout", "The server behind this site took too long to respond. Try again in a moment.");

    /// <summary>Anything that isn't a browser asking for a page never gets the challenge.</summary>
    public static HtmlPageTemplate BrowserRequired { get; } = Page("403 Forbidden", "This site checks your browser first, which needs JavaScript. Turn it on and reload the page.");

    /// <summary>A posted challenge or solution that doesn't check out.</summary>
    public static HtmlPageTemplate CheckFailed { get; } = Page("403 Forbidden", "The browser check didn't pass. Reload the page to try again.");

    /// <summary>The verify endpoint's body is over 2 KiB.</summary>
    public static HtmlPageTemplate PayloadTooLarge { get; } = Page("413 Content Too Large", "The browser check sent more data than expected. Reload the page to try again.");

    private static readonly PrecomputedResponse UpstreamBadGateway =
        PrecomputedResponse.Text(502, "bad gateway\n", htmlPage: BadGateway);

    private static readonly PrecomputedResponse UpstreamServiceUnavailable =
        PrecomputedResponse.Text(503, "service unavailable\n", htmlPage: ServiceUnavailable);

    private static readonly PrecomputedResponse UpstreamGatewayTimeout =
        PrecomputedResponse.Text(504, "gateway timeout\n", htmlPage: GatewayTimeout);

    /// <summary>The answer for a YARP error status that has a built-in page, else null (the response stays as YARP left it).</summary>
    public static PrecomputedResponse? Upstream(int statusCode) => statusCode switch
    {
        502 => UpstreamBadGateway,
        503 => UpstreamServiceUnavailable,
        504 => UpstreamGatewayTimeout,
        _ => null,
    };

    private static HtmlPageTemplate Page(string heading, string message) => HtmlPageTemplate.Create(
        heading,
        string.Concat(
            "<h1>", heading, "</h1>",
            "<p>", message, "</p>",
            "<p class=\"request-id\">Request ID: <code>{requestId}</code></p>"),
        slots: ["requestId"]);
}
