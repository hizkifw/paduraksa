using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// An operator's pages through the real gate and writer: a host's own page replaces the built-in one for its status (a message in the
/// built-in layout, or a document with its own CSP), an unknown host gets the global defaults', and everything else about the answer (the
/// status, <c>Retry-After</c>, <c>no-store</c>, plain text for a client that doesn't ask for HTML, no body for <c>HEAD</c>) is as before.
/// </summary>
public sealed class CustomErrorPagesGateTests : IDisposable
{
    private const string Hostile = "<script>alert(1)</script>\"'&";
    private static readonly IPAddress Lan = IPAddress.Parse("10.0.0.5");
    private static readonly AccessListSpec Locked = TestAccessLists.Allow("locked", "10.0.0.5");
    private static readonly RateLimitPolicyId Tight = TestIds.RateLimitPolicy("tight");

    private static readonly ErrorPageSpec NotFoundMessage = new()
    {
        Heading = "{status} {statusText} - not on {host}",
        Text = "Quote <{requestId}> if you write to us. Ray: [{cfRay}] {{literal}",
        Source = ErrorPageSource.Host,
    };

    private static readonly ErrorPageSpec ForbiddenDocument = new()
    {
        Mode = ErrorPageMode.Html,
        Html = "<!doctype html><html><head><style>body{color:#900}</style></head><body><b>{status}</b> {statusText} for {host} (<i>{requestId}</i>)</body></html>",
        Source = ErrorPageSource.Profile,
    };

    private static readonly ProxyConfigModel Config = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        AccessLists = [Locked],
        RateLimitPolicies = [new RateLimitPolicySpec { Id = Tight, Name = "tight", Parameters = new RateLimitParameters(1.0 / 60, 1) }],
        Defaults = new DefaultsSpec
        {
            // Both peers the tests use are trusted proxies, so the request ID they send is the one the page shows.
            TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["10.0.0.0/8", "192.0.2.0/24"] },
            ErrorPages = new HostErrorPagesSpec
            {
                Pages = new Dictionary<int, ErrorPageSpec>
                {
                    [404] = new() { Heading = "Nothing here", Text = "No site is configured for {host}.", Source = ErrorPageSource.GlobalDefaults },
                },
            },
        },
        Hosts =
        [
            ProxyHost("pages", "pages.test", Route("pages-app", "web", "/app")) with
            {
                Policies = [Locked.Policy()],
                RequestLimits = new HostRequestLimitsSpec { MaxUrlLength = 256 },
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [403] = ForbiddenDocument,
                        [404] = NotFoundMessage,
                        [414] = new() { Heading = "Too long", Text = "Shorter, please.", Source = ErrorPageSource.Host },
                    },
                },
            },
            ProxyHost("limited", "limited.test", Route("limited-root", "web") with { RateLimit = Tight }) with
            {
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec> { [429] = new() { Heading = "Slow down", Text = "Try later.", Source = ErrorPageSource.Host } },
                },
            },
            ProxyHost("plain", "plain.test", Route("plain-app", "web", "/app")),
            ProxyHost("check", "check.test", Route("check-root", "web")) with
            {
                ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 10 },
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [403] = new() { Heading = "Custom 403", Source = ErrorPageSource.Host },
                        [503] = new() { Heading = "Custom 503", Source = ErrorPageSource.Host },
                    },
                },
            },
        ],
    };

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public CustomErrorPagesGateTests()
    {
        _store.Publish(Bound(Config));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task AHostsMessagePageReplacesTheBuiltInPageForItsStatus()
    {
        var served = await ServeAsync("pages.test", "/missing", accept: "text/html", requestId: Hostile);

        Assert.Equal(404, served.Status);
        Assert.Equal(HtmlPageTemplate.ContentType, served.Headers.ContentType);
        Assert.Equal("no-store", served.Headers.CacheControl);
        Assert.Contains("<title>404 Not Found - not on pages.test</title>", served.Body, StringComparison.Ordinal);
        Assert.Contains("<h1>404 Not Found - not on pages.test</h1>", served.Body, StringComparison.Ordinal);

        // A literal brace is written with {{; the variable names inside it are not read.
        Assert.Contains("Quote &lt;&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#x27;&amp;&gt; if you write to us. Ray: [] {literal}", served.Body, StringComparison.Ordinal);
        Assert.Contains("Request ID: <code>&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#x27;&amp;</code>", served.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", served.Body, StringComparison.Ordinal);

        // The message page keeps the built-in layout's strict CSP: the brand style's hash and nothing else.
        var builtIn = await ServeAsync("plain.test", "/missing", accept: "text/html");
        Assert.Equal(builtIn.Headers.Csp, served.Headers.Csp);
        Assert.StartsWith("default-src 'none'; style-src 'sha256-", served.Headers.Csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", served.Headers.Csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostsDocumentIsServedWithAPolicyThatAllowsItsOwnStyleAndNoScript()
    {
        var served = await ServeAsync("pages.test", "/app", accept: "text/html", remote: IPAddress.Parse("192.0.2.9"), requestId: "req-1");

        Assert.Equal(403, served.Status);
        Assert.Equal(
            "<!doctype html><html><head><style>body{color:#900}</style></head><body><b>403</b> Forbidden for pages.test (<i>req-1</i>)</body></html>",
            served.Body);
        Assert.Equal(
            "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            served.Headers.Csp);
        Assert.Equal("no-referrer", served.Headers.ReferrerPolicy);
        Assert.Equal("nosniff", served.Headers.ContentTypeOptions);
        Assert.Equal("no-store", served.Headers.CacheControl);
        Assert.Equal(HtmlPageTemplate.ContentType, served.Headers.ContentType);
    }

    [Fact]
    public async Task AHostileHostHeaderAndRequestIdAreEncodedAndTheLongOnesCut()
    {
        var longHost = new string('h', 400) + ".test";
        var served = await ServeAsync("pages.test", "/app", accept: "text/html", remote: IPAddress.Parse("192.0.2.9"), requestId: Hostile, hostHeader: "pages.test");
        Assert.Contains("(<i>&lt;script&gt;alert(1)&lt;/script&gt;&quot;&#x27;&amp;</i>)", served.Body, StringComparison.Ordinal);

        // An unknown host's answer writes whatever the client sent as the Host, bounded and encoded.
        var unknown = await ServeAsync(longHost, "/", accept: "text/html");
        Assert.Contains("No site is configured for " + new string('h', CustomErrorPage.MaxHostLength) + ".", unknown.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('h', CustomErrorPage.MaxHostLength + 1), unknown.Body, StringComparison.Ordinal);

        var markup = await ServeAsync("<b>x</b>.test", "/", accept: "text/html");
        Assert.Contains("No site is configured for &lt;b&gt;x&lt;/b&gt;.test.", markup.Body, StringComparison.Ordinal);

        // Everything written is ASCII: anything else became an entity.
        var unicode = await ServeAsync("café.test", "/", accept: "text/html");
        Assert.All(unicode.Body, c => Assert.InRange(c, '\0', '\x7F'));
        Assert.Contains("caf&#xE9;.test", unicode.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRayIsWrittenOnlyWhenCloudflaresHeadersAreTrusted()
    {
        var untrusted = await ServeAsync("pages.test", "/missing", accept: "text/html", ray: "8a1b2c3d4e5f6a7b-AMS");
        Assert.Contains("Ray: [] ", untrusted.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("8a1b2c3d4e5f6a7b", untrusted.Body, StringComparison.Ordinal);

        var trusted = await ServeAsync("pages.test", "/missing", accept: "text/html", ray: "8a1b2c3d4e5f6a7b-AMS", cloudflareTrusted: true);
        Assert.Contains("Ray: [8a1b2c3d4e5f6a7b-AMS] ", trusted.Body, StringComparison.Ordinal);

        var hostile = await ServeAsync("pages.test", "/missing", accept: "text/html", ray: "<i>" + new string('r', 200), cloudflareTrusted: true);
        Assert.Contains("Ray: [&lt;i&gt;" + new string('r', CustomErrorPage.MaxRayLength - 3) + "]", hostile.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientThatDoesNotAskForHtmlStillGetsPlainText()
    {
        var served = await ServeAsync("pages.test", "/missing", accept: "application/json");
        Assert.Equal(404, served.Status);
        Assert.Equal("text/plain; charset=utf-8", served.Headers.ContentType);
        Assert.Equal("no-store", served.Headers.CacheControl);
        Assert.Null(served.Headers.Csp);
        Assert.DoesNotContain("not on", served.Body, StringComparison.Ordinal);

        var none = await ServeAsync("pages.test", "/missing", accept: null);
        Assert.Equal(served.Body, none.Body);
    }

    [Fact]
    public async Task HeadGetsTheHeadersOfTheCustomPageAndNoBody()
    {
        var served = await ServeAsync("pages.test", "/missing", accept: "text/html", method: "HEAD");
        Assert.Equal(404, served.Status);
        Assert.Equal(HtmlPageTemplate.ContentType, served.Headers.ContentType);
        Assert.NotNull(served.Headers.Csp);
        Assert.Empty(served.Body);
    }

    [Fact]
    public async Task RetryAfterAndStatusSurviveTheReplacement()
    {
        // The route's rate limit answers 429 once the one token is gone; the host's own page replaces the built-in one.
        Assert.Equal(GateAction.Forward, _gate.Decide(TestContexts.Create("limited.test", "/", remote: Lan)).Action);
        var served = await ServeAsync("limited.test", "/", accept: "text/html");
        Assert.Equal(429, served.Status);
        Assert.NotNull(served.RetryAfter);
        Assert.Contains("<h1>Slow down</h1>", served.Body, StringComparison.Ordinal);
        Assert.Contains("<p>Try later.</p>", served.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachGateAnswerOfAStatusUsesItsHostsPage()
    {
        // 414 from the request limits, and 404 from route matching, are two answers of two gate steps: both look the page up.
        var tooLong = await ServeAsync("pages.test", "/" + new string('a', 400), accept: "text/html");
        Assert.Equal(414, tooLong.Status);
        Assert.Contains("<h1>Too long</h1>", tooLong.Body, StringComparison.Ordinal);

        // A host with no page of its own for a status is served the built-in one.
        var builtIn = await ServeAsync("plain.test", "/missing", accept: "text/html");
        Assert.Equal(404, builtIn.Status);
        Assert.Contains("<h1>404 Not Found</h1>", builtIn.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownHostGetsTheGlobalDefaultsPageAndAHostThatMatchedDoesNot()
    {
        var unknown = await ServeAsync("nobody.test", "/", accept: "text/html");
        Assert.Equal(404, unknown.Status);
        Assert.Contains("<h1>Nothing here</h1>", unknown.Body, StringComparison.Ordinal);
        Assert.Contains("No site is configured for nobody.test.", unknown.Body, StringComparison.Ordinal);

        // A host's own entry is not the defaults' (the compiler merged the layers, so the host's list is what it resolved).
        var matched = await ServeAsync("plain.test", "/missing", accept: "text/html");
        Assert.Contains("<h1>404 Not Found</h1>", matched.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlowsOwnPagesAreNotReplaced()
    {
        // The proof-of-work step's 503 (no key to sign with) names the check; the host's generic 503 would hide what is wrong.
        var unavailable = await ServeAsync("check.test", "/", accept: "text/html");
        Assert.Equal(503, unavailable.Status);
        Assert.Contains("The browser check is temporarily unavailable.", unavailable.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Custom 503", unavailable.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageIsKnownAtCompileTimeFromItsLayer()
    {
        var snapshot = _store.Current;
        var host = snapshot.Hosts.Find("pages.test", out _)!;
        Assert.Equal(ErrorPageSource.Host, host.ErrorPages.Find(404)!.Source);
        Assert.Equal(ErrorPageSource.Profile, host.ErrorPages.Find(403)!.Source);
        Assert.Equal(ErrorPageMode.Html, host.ErrorPages.Find(403)!.Mode);
        Assert.Null(host.ErrorPages.Find(502));
        Assert.Null(snapshot.Hosts.Find("plain.test", out _)!.ErrorPages.Find(404));
        Assert.Equal(ErrorPageSource.GlobalDefaults, snapshot.DefaultErrorPages.Find(404)!.Source);
    }

    [Fact]
    public void ABrokenPageFailsTheCompileInsteadOfServingSomethingElse()
    {
        foreach (var broken in (ErrorPageSpec[])
        [
            new() { Heading = "{nope}" },
            new() { Heading = string.Empty },
            new() { Mode = ErrorPageMode.Html, Html = string.Empty },
            new() { Mode = ErrorPageMode.Html, Html = new string('a', ErrorPageText.MaxHtmlBytes + 1) },
            new() { Mode = (ErrorPageMode)9, Heading = "x" },
            new() { Mode = ErrorPageMode.Html, Html = "<p>x</p>", Heading = "also a heading" },
            new() { Heading = "a\nb" },
        ])
        {
            var model = Config with
            {
                Hosts = [ProxyHost("broken", "broken.test", Route("broken-root", "web")) with { ErrorPages = new HostErrorPagesSpec { Pages = new Dictionary<int, ErrorPageSpec> { [404] = broken } } }],
            };
            Assert.False(SnapshotCompiler.Compile(model).Succeeded, $"{broken} compiled");
        }
    }

    private async Task<Served> ServeAsync(
        string host,
        string path,
        string? accept,
        string method = "GET",
        IPAddress? remote = null,
        string? requestId = null,
        string? hostHeader = null,
        string? ray = null,
        bool cloudflareTrusted = false)
    {
        var context = TestContexts.Create(hostHeader ?? host, path, method, remote: remote ?? Lan);
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        if (requestId is not null)
        {
            context.Request.Headers[RequestIds.HeaderName] = requestId;
        }

        if (ray is not null)
        {
            context.Request.Headers["CF-Ray"] = ray;
        }

        var body = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));

        var decision = _gate.Decide(context);
        Assert.Equal(GateAction.Respond, decision.Action);
        if (cloudflareTrusted)
        {
            // The resolver decides this from the trusted-proxy mode and the peer; here the verdict is what the page reads.
            var state = context.Features.Get<IGateFeature>()!.State;
            state.ClientIp = state.ClientIp with { CloudflareHeadersTrusted = true };
        }

        await ResponseWriter.WriteAsync(context, decision.Response!, decision.RetryAfter);
        await context.Response.BodyWriter.FlushAsync();
        var headers = context.Response.Headers;
        return new Served(
            context.Response.StatusCode,
            Encoding.UTF8.GetString(body.ToArray()),
            new ServedHeaders(
                context.Response.ContentType,
                headers.CacheControl.ToString(),
                headers.TryGetValue("Content-Security-Policy", out var csp) ? csp.ToString() : null,
                headers["Referrer-Policy"].ToString(),
                headers["X-Content-Type-Options"].ToString()),
            headers.RetryAfter.Count > 0 ? headers.RetryAfter.ToString() : null);
    }

    private sealed record Served(int Status, string Body, ServedHeaders Headers, string? RetryAfter = null);

    private sealed record ServedHeaders(string? ContentType, string CacheControl, string? Csp, string ReferrerPolicy, string ContentTypeOptions);
}
