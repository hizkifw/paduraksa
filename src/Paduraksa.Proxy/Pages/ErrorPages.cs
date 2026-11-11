using Paduraksa.Core.Model;
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

    /// <summary>A rule set's deny.</summary>
    public static HtmlPageTemplate RequestBlocked { get; } = Page("403 Forbidden", "This site doesn't accept that request.");

    /// <summary>A host that only takes connections from Cloudflare got one from another address.</summary>
    public static HtmlPageTemplate OriginLockdown { get; } = Page("403 Forbidden", "This site only accepts connections that come through Cloudflare.");

    /// <summary>A host that requires Cloudflare's client certificate got a request without a verified one.</summary>
    public static HtmlPageTemplate ClientCertificateRequired { get; } = Page("403 Forbidden", "This site only accepts requests that Cloudflare has authenticated.");

    /// <summary>An access list wants a password and the request had none, or a wrong one.</summary>
    public static HtmlPageTemplate Unauthorized { get; } = Page("401 Unauthorized", "This site needs a username and password. Reload the page to enter them.");

    /// <summary>A host matched but no route took the path, or the host is unknown.</summary>
    public static HtmlPageTemplate NotFound { get; } = Page("404 Not Found", "Nothing on this site matches that address.");

    /// <summary>The connection is weaker than the host's TLS settings, so an H2 coalesced connection must retry.</summary>
    public static HtmlPageTemplate Misdirected { get; } = Page("421 Misdirected Request", "This connection can't be used for this site. Reload the page to open a new one.");

    public static HtmlPageTemplate TooManyRequests { get; } = Page("429 Too Many Requests", "You've sent too many requests in a short time. Wait a moment, then try again.");

    public static HtmlPageTemplate BadGateway { get; } = Page("502 Bad Gateway", "The server behind this site didn't respond properly. Try again in a moment.");

    /// <summary>No healthy destination, or a proof-of-work key that can't be opened: the route fails closed.</summary>
    public static HtmlPageTemplate ServiceUnavailable { get; } = Page("503 Service Unavailable", "The site is temporarily unavailable. Try again in a moment.");

    /// <summary>The proof-of-work key is missing; the page names the check rather than the whole site.</summary>
    public static HtmlPageTemplate CheckUnavailable { get; } = Page("503 Service Unavailable", "The browser check is temporarily unavailable. Try again in a moment.", customizable: false);

    public static HtmlPageTemplate GatewayTimeout { get; } = Page("504 Gateway Timeout", "The server behind this site took too long to respond. Try again in a moment.");

    /// <summary>Anything that isn't a browser asking for a page never gets the challenge.</summary>
    public static HtmlPageTemplate BrowserRequired { get; } = Page("403 Forbidden", "This site checks your browser first, which needs JavaScript. Turn it on and reload the page.", customizable: false);

    /// <summary>A posted challenge or solution that doesn't check out.</summary>
    public static HtmlPageTemplate CheckFailed { get; } = Page("403 Forbidden", "The browser check didn't pass. Reload the page to try again.", customizable: false);

    /// <summary>The verify endpoint's body is over 2 KiB.</summary>
    public static HtmlPageTemplate PayloadTooLarge { get; } = Page("413 Content Too Large", "The browser check sent more data than expected. Reload the page to try again.", customizable: false);

    /// <summary>A declared body over the host's or route's limit.</summary>
    public static HtmlPageTemplate RequestTooLarge { get; } = Page("413 Content Too Large", "What you sent is larger than this site accepts. Send less and try again.");

    /// <summary>A request target over the host's limit.</summary>
    public static HtmlPageTemplate UriTooLong { get; } = Page("414 URI Too Long", "The address is longer than this site accepts.");

    /// <summary>Too many header lines, or too many header bytes (cookies are the usual cause).</summary>
    public static HtmlPageTemplate HeadersTooLarge { get; } = Page("431 Request Header Fields Too Large", "Your browser sent more headers than this site accepts. Clearing this site's cookies usually fixes it.");

    /// <summary>A forward-auth endpoint answered something it must not, or couldn't be reached: the route fails closed.</summary>
    public static HtmlPageTemplate AuthUnavailable { get; } = Page("502 Bad Gateway", "The sign-in service for this site didn't respond properly. Try again in a moment.");

    /// <summary>A forward-auth endpoint didn't answer in time.</summary>
    public static HtmlPageTemplate AuthTimeout { get; } = Page("504 Gateway Timeout", "The sign-in service for this site took too long to respond. Try again in a moment.");

    /// <summary>Too many calls to a forward-auth endpoint were already running.</summary>
    public static HtmlPageTemplate AuthBusy { get; } = Page("503 Service Unavailable", "The sign-in service for this site is busy. Try again in a moment.");

    private static readonly PrecomputedResponse?[] Replacements = BuildReplacements();

    /// <summary>
    /// The answer that replaces an upstream's own error with this status: its built-in page (an operator's takes its place when the
    /// answer is written) and a plain line for a client that doesn't ask for HTML. Null for a status without a page.
    /// </summary>
    public static PrecomputedResponse? Replacement(int status)
    {
        var index = ErrorPageStatuses.IndexOf(status);
        return index >= 0 && index < Replacements.Length ? Replacements[index] : null;
    }

    private static PrecomputedResponse?[] BuildReplacements()
    {
        var answers = new PrecomputedResponse?[ErrorPageStatuses.Statuses.Count];
        (int Status, HtmlPageTemplate Page)[] pages =
        [
            (403, AccessDenied), (404, NotFound), (413, RequestTooLarge), (414, UriTooLong), (421, Misdirected), (429, TooManyRequests),
            (431, HeadersTooLarge), (502, BadGateway), (503, ServiceUnavailable), (504, GatewayTimeout),
        ];
        foreach (var (status, page) in pages)
        {
            answers[ErrorPageStatuses.IndexOf(status)] = PrecomputedResponse.Text(
                status, string.Concat(ErrorPageStatuses.StatusText(status).ToLowerInvariant(), "\n"), htmlPage: page);
        }

        return answers;
    }

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

    // The heading starts with the status. Only a page whose status an operator can give a page of their own is customizable: a
    // proof-of-work step's refusal says what that step needs, which a generic page would hide.
    private static HtmlPageTemplate Page(string heading, string message, bool customizable = true)
    {
        var status = int.Parse(heading.AsSpan(0, 3), System.Globalization.CultureInfo.InvariantCulture);
        return HtmlPageTemplate.Create(
            heading,
            string.Concat(
                "<h1>", heading, "</h1>",
                "<p>", message, "</p>",
                "<p class=\"request-id\">Request ID: <code>{requestId}</code></p>"),
            customizableStatus: customizable && ErrorPageStatuses.IndexOf(status) >= 0 ? status : 0,
            slots: ["requestId"]);
    }
}
