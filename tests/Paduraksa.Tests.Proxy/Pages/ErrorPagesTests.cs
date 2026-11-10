using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Pages;

/// <summary>The built-in error pages: copy, status line, request ID, CSP, and the plain text a non-browser still gets.</summary>
public sealed class ErrorPagesTests
{
    private const string Injected = "<script>alert(1)</script>";

    public static TheoryData<int, HtmlPageTemplate, string, string> Pages => new()
    {
        { 403, ErrorPages.AccessDenied, "403 - Forbidden", "Access denied." },
        { 404, ErrorPages.NotFound, "404 - Not found", "Page not found." },
        { 421, ErrorPages.Misdirected, "421 - Misdirected request", "Wrong connection." },
        { 429, ErrorPages.TooManyRequests, "429 - Too many requests", "Too many requests." },
        { 502, ErrorPages.BadGateway, "502 - Bad gateway", "Bad gateway." },
        { 503, ErrorPages.ServiceUnavailable, "503 - Service unavailable", "Service unavailable." },
        { 504, ErrorPages.GatewayTimeout, "504 - Gateway timeout", "Gateway timeout." },
        { 403, ErrorPages.BrowserRequired, "403 - Forbidden", "Browser required." },
        { 403, ErrorPages.CheckFailed, "403 - Forbidden", "The check failed." },
        { 503, ErrorPages.CheckUnavailable, "503 - Service unavailable", "The check is unavailable." },
        { 413, ErrorPages.PayloadTooLarge, "413 - Content too large", "Too large." },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ABrowserGetsThePageWithItsStatusLineRequestIdAndCsp(int status, HtmlPageTemplate page, string statusLine, string heading)
    {
        var (context, body) = Context("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        await ResponseWriter.WriteAsync(context, PrecomputedResponse.Text(status, "plain only\n", htmlPage: page));

        var pageText = await WrittenAsync(context, body);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Contains($"<p class=\"status\">{statusLine}</p>", pageText, StringComparison.Ordinal);
        Assert.Contains($"<h1>{heading}</h1>", pageText, StringComparison.Ordinal);
        Assert.Contains("Request ID: <code>&lt;script&gt;alert(1)&lt;/script&gt;</code>", pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(Injected, pageText, StringComparison.Ordinal);
        var csp = context.Response.Headers["Content-Security-Policy"].ToString();
        Assert.StartsWith("default-src 'none'; style-src '", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("form-action 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src", csp, StringComparison.Ordinal);
        Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"].ToString());
        Assert.All(pageText, c => Assert.InRange(c, '\0', '\x7F'));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AClientThatDoesNotAskForHtmlGetsThePlainAnswer(int status, HtmlPageTemplate page, string statusLine, string heading)
    {
        var (context, body) = Context("application/json");
        await ResponseWriter.WriteAsync(context, PrecomputedResponse.Text(status, "plain only\n", htmlPage: page));

        var text = await WrittenAsync(context, body);
        Assert.Equal("plain only\n", text);
        Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.DoesNotContain(statusLine, text, StringComparison.Ordinal);
        Assert.DoesNotContain(heading, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task HeadGetsTheHeadersAndNoBody(int status, HtmlPageTemplate page, string statusLine, string heading)
    {
        var html = Context("text/html", method: "HEAD");
        await ResponseWriter.WriteAsync(html.Context, PrecomputedResponse.Text(status, "plain only\n", htmlPage: page));
        var htmlBody = await WrittenAsync(html.Context, html.Body);
        Assert.Empty(htmlBody);
        Assert.DoesNotContain(statusLine, htmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(heading, htmlBody, StringComparison.Ordinal);
        Assert.Equal(HtmlPageTemplate.ContentType, html.Context.Response.ContentType);

        var text = Context(null, method: "HEAD");
        await ResponseWriter.WriteAsync(text.Context, PrecomputedResponse.Text(status, "plain only\n", htmlPage: page));
        Assert.Empty(await WrittenAsync(text.Context, text.Body));
        Assert.Equal("text/plain; charset=utf-8", text.Context.Response.ContentType);
    }

    [Fact]
    public async Task TheHtmlRateLimitPageKeepsRetryAfter()
    {
        var (context, body) = Context("text/html");
        var response = PrecomputedResponse.Text(429, "Too many requests. Please slow down.\n", htmlPage: ErrorPages.TooManyRequests);

        await ResponseWriter.WriteAsync(context, response, retryAfter: "7");

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("7", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Contains("<h1>Too many requests.</h1>", await WrittenAsync(context, body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUnknownHostDefaultsUseTheirPagesAndConfiguredTextDoesNot()
    {
        var notFound = DefaultHostBehaviour.NotFound().Response!;
        Assert.NotNull(notFound.HtmlPage);
        var (context, body) = Context("text/html");
        await ResponseWriter.WriteAsync(context, notFound);
        Assert.Contains("<h1>Page not found.</h1>", await WrittenAsync(context, body), StringComparison.Ordinal);

        var (configuredContext, configuredBody) = Context("text/html");
        var configured = DefaultHostBehaviour.NotFound("maintenance\n").Response!;
        Assert.Null(configured.HtmlPage);
        await ResponseWriter.WriteAsync(configuredContext, configured);
        Assert.Equal("maintenance\n", await WrittenAsync(configuredContext, configuredBody));

        var misdirected = DefaultHostBehaviour.MisdirectedRequest().Response!;
        Assert.NotNull(misdirected.HtmlPage);
        Assert.Equal(421, misdirected.StatusCode);

        var snapshot = TestSnapshots.Compile(new ProxyConfigModel());
        Assert.NotNull(snapshot.NoRoute.HtmlPage);
        Assert.NotNull(snapshot.Unavailable.HtmlPage);
        Assert.NotNull(snapshot.Misdirected.HtmlPage);
    }

    [Fact]
    public async Task EveryProofOfWorkFailureHasAPageAndKeepsItsPlainText()
    {
        (PrecomputedResponse Response, string Heading)[] answers =
        [
            (ProofOfWorkResponses.NonBrowser, "Browser required."),
            (ProofOfWorkResponses.Unavailable, "The check is unavailable."),
            (ProofOfWorkResponses.BodyTooLarge, "Too large."),
            (ProofOfWorkResponses.VerificationFailed, "The check failed."),
        ];

        foreach (var (response, heading) in answers)
        {
            Assert.NotNull(response.HtmlPage);
            var (html, htmlBody) = Context("text/html");
            await ResponseWriter.WriteAsync(html, response);
            Assert.Contains($"<h1>{heading}</h1>", await WrittenAsync(html, htmlBody), StringComparison.Ordinal);

            var (plain, plainBody) = Context("application/json");
            await ResponseWriter.WriteAsync(plain, response);
            Assert.Equal(Encoding.UTF8.GetString(response.Body), await WrittenAsync(plain, plainBody));
        }

        Assert.Null(ProofOfWorkResponses.MethodNotAllowed.HtmlPage);
        Assert.Equal("text/plain; charset=utf-8", ProofOfWorkResponses.NonBrowser.ContentType.ToString());
        Assert.Equal("This page needs a browser that runs JavaScript.\n", Encoding.UTF8.GetString(ProofOfWorkResponses.NonBrowser.Body));
    }

    private static (DefaultHttpContext Context, MemoryStream Body) Context(string? accept, string method = "GET")
    {
        var context = TestContexts.Create("app.test", method: method);
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        context.Request.Headers[Paduraksa.Proxy.Logging.RequestIds.HeaderName] = Injected;
        var state = new GateRequestState();
        state.InitRequestId(context.Request, peerTrusted: true);
        context.Features.Set<IGateFeature>(state);

        var body = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return (context, body);
    }

    /// <summary>Completes what Kestrel would at the end of the request: flush the body pipe into the test's stream.</summary>
    private static async Task<string> WrittenAsync(DefaultHttpContext context, MemoryStream body)
    {
        await context.Response.BodyWriter.FlushAsync();
        return Encoding.UTF8.GetString(body.ToArray());
    }
}
