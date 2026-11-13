using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Transforms;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>The rewrite's rules on their own, the compiler's backstop, and what it costs: nothing in the gate, and nothing more than the
/// URI it already built in the request transform.</summary>
public sealed class PathRewriteTests
{
    [Theory]
    [InlineData("/app", RouteMatchKind.Prefix, "/app/x", 4)]
    [InlineData("/app", RouteMatchKind.Prefix, "/APP", 4)]
    [InlineData("/app", RouteMatchKind.Prefix, "/app//x", 4)]
    [InlineData("/app/", RouteMatchKind.Prefix, "/app/x", 4)]
    [InlineData("/app", RouteMatchKind.Prefix, "/apps", -1)]
    [InlineData("/app", RouteMatchKind.Prefix, "//app/x", -1)]
    [InlineData("/app", RouteMatchKind.Prefix, "/%61pp/x", -1)]
    [InlineData("/app", RouteMatchKind.Prefix, "/app;v=1/x", -1)]
    [InlineData("/app", RouteMatchKind.Exact, "/app", 4)]
    [InlineData("/app", RouteMatchKind.Exact, "/app/", 4)]
    [InlineData("/app", RouteMatchKind.Exact, "/app/x", -1)]
    public void TheStripTakesOffTheRoutesOwnSpellingOnly(string route, RouteMatchKind kind, string raw, int end) =>
        Assert.Equal(end, new PathRewrite(route, kind, stripPrefix: true, addPrefix: null).StripEnd(raw));

    [Theory]
    [InlineData("/x/%2e%2e/y", true)]
    [InlineData("/x/%2E%2e", true)]
    [InlineData("/.%2E/y", true)]
    [InlineData("/%2e./y", true)]
    [InlineData("/%2e/y", true)]
    [InlineData("/x%2f%2e%2e%2fy", true)]
    [InlineData("/x%5C..%5Cy", true)]
    [InlineData("/x\\..\\y", true)]
    [InlineData("/..;jsessionid=1/y", true)]
    [InlineData("/x/.. /y", true)]
    [InlineData("/x/%2e%2e%20/y", true)]
    [InlineData("/x/...", true)]
    [InlineData("/x/a.b/...c/v1.2", false)]
    [InlineData("/group%2Fproject", false)]
    [InlineData("/x/%2e%2ea", false)]
    [InlineData("/x/%252e%252e/y", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void ADotSegmentIsFoundInAnySpelling(string path, bool found) => Assert.Equal(found, PathRewrite.HasDotSegment(path));

    [Theory]
    [InlineData("/x/%252e%252e/y", true)]
    [InlineData("/x/%25", true)]
    [InlineData("/x/%c0%ae", true)]
    [InlineData("/x/%C1%9c", true)]
    [InlineData("/x/%u002e", true)]
    [InlineData("/x/%U002E", true)]
    [InlineData("/x/a%00", true)]
    [InlineData("/x/a%1f", true)]
    [InlineData("/x/a%7F", true)]
    [InlineData("/x/caf%C3%A9/%E2%82%AC", false)]
    [InlineData("/group%2Fproject/a%20b", false)]
    [InlineData("/x/%2e", false)]
    public void ALooseEscapeIsFound(string path, bool found) => Assert.Equal(found, PathRewrite.HasLooseEscape(path));

    [Theory]
    [InlineData("/café", "/caf%C3%A9")]
    [InlineData("/my app", "/my%20app")]
    [InlineData("/a:b@c!$&'()*+,;=", "/a:b@c!$&'()*+,;=")]
    [InlineData("/𝄞", "/%F0%9D%84%9E")]
    public void ARoutePathIsEncodedAsAClientSendsIt(string route, string encoded)
    {
        var rewrite = new PathRewrite(route, RouteMatchKind.Prefix, stripPrefix: true, addPrefix: null);
        Assert.Equal(encoded, rewrite.EncodedPrefix);
        Assert.Equal(encoded, rewrite.ForwardedPrefix);
        Assert.Equal(encoded.Length, rewrite.StripEnd(encoded.ToLowerInvariant() + "/x"));
    }

    [Theory]
    // strip only on /app
    [InlineData("/app", null, "/x", null, "/app/x")]
    [InlineData("/app", null, "/app/x", null, null)]
    [InlineData("/app", null, "/APP?x", null, null)]
    [InlineData("/app", null, "http://app.test/x", null, "http://app.test/app/x")]
    [InlineData("/app", null, "http://app.test/app", null, null)]
    [InlineData("/app", null, "http://up:8080/x", "up:8080", "https://app.test/app/x")]
    [InlineData("/app", null, "http://up:8080/app/x", "up:8080", "https://app.test/app/x")]
    [InlineData("/app", null, "http://up:8080", "up:8080", "https://app.test/app")]
    [InlineData("/app", null, "http://up:8081/x", "up:8080", null)]
    // strip and add /app -> /api
    [InlineData("/app", "/api", "/api/x", null, "/app/x")]
    [InlineData("/app", "/api", "/other", null, null)]
    [InlineData("/app", "/api", "http://up/other", "up", "https://app.test/other")]
    public void ALocationIsMappedOnlyWhereItPointsUnderTheRoute(string route, string? add, string sent, string? upstream, string? expected) =>
        Assert.Equal(expected, new PathRewrite(route, RouteMatchKind.Prefix, stripPrefix: true, add).ClientLocation(sent, "app.test", upstream));

    [Theory]
    [InlineData("/api", null)]
    [InlineData("/api/v2", null)]
    [InlineData("/a%2Fb", null)]
    [InlineData("/x:y@z", null)]
    [InlineData("api", "starts")]
    [InlineData("/", "end")]
    [InlineData("/api/", "end")]
    [InlineData("/a b", "letters")]
    [InlineData("/a?b", "letters")]
    [InlineData("/a\\b", "letters")]
    [InlineData("/a%zz", "escape")]
    [InlineData("/a%2", "escape")]
    [InlineData("/a//b", "segments")]
    [InlineData("/a/../b", "segment")]
    [InlineData("/a/./b", "segment")]
    [InlineData("/a/%2e%2e", "segment")]
    [InlineData("/a/.%2E/b", "segment")]
    [InlineData("/a/%252e", "%25")]
    [InlineData("/a/%c0%ae", "%25")]
    public void AnAddedPrefixIsAPlainUrlPath(string prefix, string? problem)
    {
        var found = PathRewrite.AddPrefixProblem(prefix);
        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(problem, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCompilerRefusesAStripOnTheRootAndABadPrefix()
    {
        foreach (var route in new[] { Route("r", "web") with { StripPrefix = true }, Route("r", "web", "/x") with { AddPrefix = "/a b" } })
        {
            var result = SnapshotCompiler.Compile(new ProxyConfigModel { Upstreams = [Upstream("web", "http://127.0.0.1:1/")], Hosts = [ProxyHost("h", "h.test", route)] });
            Assert.False(result.Succeeded);
        }
    }

    [Fact]
    public void TheGateDecidesAStrippingRouteWithoutAllocating()
    {
        using SnapshotStore store = new();
        store.Publish(Bound(new ProxyConfigModel
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("h", "h.test", Route("r", "web", "/app") with { StripPrefix = true, AddPrefix = "/api" })],
        }));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        foreach (var target in new[] { "/app/items?q=1", "/APP/x", "//app/x", "/app/%2e%2e/admin" })
        {
            var path = target.Split('?')[0];
            var context = TestContexts.Create("h.test", path, rawTarget: target);
            AllocAssert.Zero((gate, context), static s => _ = s.gate.Decide(s.context), $"the gate for {target}");
        }
    }

    [Fact]
    public void TheRequestTransformBuildsTheSameOneUriWithARewrite()
    {
        var plain = AllocAssert.Measure(ContextFor(null), static c => Transform(c));
        var rewritten = AllocAssert.Measure(ContextFor(new PathRewrite("/app", RouteMatchKind.Prefix, stripPrefix: true, addPrefix: "/api")), static c => Transform(c));

        Assert.True(rewritten.BytesPerCall <= plain.BytesPerCall, $"{rewritten.BytesPerCall:F1} B with the rewrite, {plain.BytesPerCall:F1} B without");
    }

    private static void Transform(DefaultHttpContext context)
    {
        RequestTransformContext transform = new()
        {
            HttpContext = context,
            ProxyRequest = new HttpRequestMessage(),
            DestinationPrefix = "http://127.0.0.1:1/",
            Path = context.Request.Path,
            Query = new QueryTransformContext(context.Request),
        };
        Synchronously(RawPathTransform.ApplyAsync(transform));
    }

    private static void Synchronously(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("the transform should complete synchronously");
        }
    }

    private static DefaultHttpContext ContextFor(PathRewrite? rewrite)
    {
        var context = TestContexts.Create("h.test", "/app/items", rawTarget: "/app/items?q=1");
        context.Request.QueryString = new QueryString("?q=1");
        context.Features.Set<IGateFeature>(new GateRequestState
        {
            Route = new CompiledRoute { Id = TestIds.Route("r"), MatchKind = RouteMatchKind.Prefix, Path = "/app", Slot = 0, Policies = [], Rewrite = rewrite },
        });
        return context;
    }
}
