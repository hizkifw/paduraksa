using System.Text;

using Microsoft.AspNetCore.Http;

using Paduraksa.Proxy.Gate;
using Paduraksa.Tests.Proxy.Testing;

namespace Paduraksa.Tests.Proxy.Gate;

public sealed class PrecomputedResponseTests
{
    [Fact]
    public void TextResponseCarriesStatusBodyAndHeaders()
    {
        var response = PrecomputedResponse.Text(404, "nope\n");
        var context = TestContexts.Create("a.test");

        ResponseWriter.ApplyHeaders(context, response);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(5, context.Response.ContentLength);
        Assert.False(context.Response.Headers.ContainsKey("Location"));
    }

    [Fact]
    public async Task BodyIsWrittenExceptForHead()
    {
        var response = PrecomputedResponse.Create(200, "hello"u8, "text/plain");

        var get = WithBody(TestContexts.Create("a.test"), out var getBody);
        await ResponseWriter.WriteAsync(get, response);
        Assert.Equal("hello", Encoding.UTF8.GetString(getBody.ToArray()));

        var head = WithBody(TestContexts.Create("a.test", method: "HEAD"), out var headBody);
        await ResponseWriter.WriteAsync(head, response);
        Assert.Empty(headBody.ToArray());
        Assert.Equal(5, head.Response.ContentLength);
    }

    [Fact]
    public void FixedRedirectHasAPrecomputedLocation()
    {
        var response = PrecomputedResponse.RedirectTo(302, "https://elsewhere.test/landing");
        var context = TestContexts.Create("a.test", "/x");

        ResponseWriter.ApplyHeaders(context, response);

        Assert.Equal(302, context.Response.StatusCode);
        Assert.Equal("https://elsewhere.test/landing", context.Response.Headers.Location);
        Assert.Null(response.Redirect);
    }

    [Theory]
    [InlineData(443, "a.test", "/p/q?x=1&y=%20", "https://a.test/p/q?x=1&y=%20")]
    [InlineData(8443, "a.test:80", "/", "https://a.test:8443/")]
    [InlineData(443, "[2001:db8::1]:80", "/v6", "https://[2001:db8::1]/v6")]
    public void HttpsRedirectKeepsHostAndTargetAndUsesThePublicPort(int publicPort, string host, string rawTarget, string expected)
    {
        var response = PrecomputedResponse.RedirectTo(301, RedirectTarget.Https(publicPort));
        var context = TestContexts.Create(host, rawTarget.Split('?')[0], rawTarget: rawTarget);

        ResponseWriter.ApplyHeaders(context, response);

        Assert.Equal(expected, context.Response.Headers.Location);
    }

    [Fact]
    public void LocationFallsBackToPathAndQueryWithoutARawTarget()
    {
        RedirectTarget target = new("https://new.test", IncludeRequestHost: false, string.Empty, AppendPathAndQuery: true);
        var context = TestContexts.Create("old.test", "/a b", rawTarget: "*");
        context.Request.QueryString = new QueryString("?q=1");

        Assert.Equal("https://new.test/a%20b?q=1", ResponseWriter.BuildLocation(context, target));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    public void RedirectsNeedARedirectStatus(int status) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PrecomputedResponse.RedirectTo(status, "https://x.test/"));

    [Theory]
    [InlineData(204)]
    [InlineData(205)]
    [InlineData(304)]
    public void BodilessStatusesTakeNoBodyAndNoContentLength(int status)
    {
        Assert.Throws<ArgumentException>(() => PrecomputedResponse.Text(status, "x"));
        Assert.Throws<ArgumentException>(() => PrecomputedResponse.Create(status, "x"u8, "text/plain"));

        var empty = PrecomputedResponse.Text(status, string.Empty);
        var context = TestContexts.Create("a.test");
        ResponseWriter.ApplyHeaders(context, empty);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Null(context.Response.ContentLength);
    }

    [Fact]
    public void HeaderSetupDoesNotAllocate()
    {
        var text = PrecomputedResponse.Text(403, "forbidden\n");
        var redirect = PrecomputedResponse.RedirectTo(308, "https://x.test/");
        var context = TestContexts.Create("a.test");

        AllocAssert.Zero(
            (context, text, redirect),
            static s =>
            {
                ResponseWriter.ApplyHeaders(s.context, s.text);
                ResponseWriter.ApplyHeaders(s.context, s.redirect);
            },
            "ResponseWriter.ApplyHeaders");
    }

    private static DefaultHttpContext WithBody(DefaultHttpContext context, out MemoryStream body)
    {
        body = new MemoryStream();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        return context;
    }
}
