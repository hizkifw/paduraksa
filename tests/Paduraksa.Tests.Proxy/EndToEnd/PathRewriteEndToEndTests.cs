using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>Strip-prefix and add-prefix through a real proxy, on the raw path: what the upstream receives, the query, an encoded
/// slash, a spelling that can't be stripped, <c>X-Forwarded-Prefix</c>, and a redirect that gets the client's prefix back.</summary>
public sealed class PathRewriteEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    [Theory]
    // Strip: the route's path comes off, the rest goes as received (an encoded slash, case of the prefix ignored).
    [InlineData("/app/x", "/a/x")]
    [InlineData("/app", "/a/")]
    [InlineData("/app/", "/a/")]
    [InlineData("/APP/group%2Fproject/-/tree", "/a/group%2Fproject/-/tree")]
    [InlineData("/app/caf%C3%A9", "/a/caf%C3%A9")]
    // Dots inside a segment are not dot segments, and an encoded slash in one stays allowed.
    [InlineData("/both/a.b/...x/v1.2", "/a/api/a.b/...x/v1.2")]
    [InlineData("/both/group%2Fproject", "/a/api/group%2Fproject")]
    // Strip and add.
    [InlineData("/both/x", "/a/api/x")]
    [InlineData("/both", "/a/api")]
    // Add only: the prefix goes in front of the whole path.
    [InlineData("/added/x", "/a/v2/added/x")]
    // A route without a rewrite is untouched.
    [InlineData("/other/x", "/a/other/x")]
    public async Task TheUpstreamReceivesTheRewrittenRawPath(string target, string expected)
    {
        await using var server = await StartAsync();

        var (status, body, _) = await RawHttp.GetAsync(server.BaseAddress, "app.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task TheQueryIsUntouched()
    {
        await using var server = await StartAsync();

        var (status, body, head) = await RawHttp.GetAsync(server.BaseAddress, "app.test", "/both/p%2Fq?x=%2F&y=a+b", TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
        Assert.Equal("/a/api/p%2Fq", body);
        Assert.Contains("x-query: ?x=%2F&y=a+b", head, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Matched leniently, as every route is, but the prefix can't be taken off a spelling that isn't the route's own.
    [InlineData("//app/x")]
    [InlineData("/app;v=1/x")]
    [InlineData("/%61pp/x")]
    // A dot segment in any spelling, in a path that still resolves into the route (Kestrel resolves them before matching, so
    // /both/%2e%2e/admin is the root route's /admin): /both/%2e%2e/both/x would reach the upstream as /api/%2e%2e/both/x, which it
    // resolves to /both/x, outside the /api the route exposes.
    [InlineData("/both/%2e%2e/both/x")]
    [InlineData("/both/%2E%2E/both/x")]
    [InlineData("/both/.%2e/both/x")]
    [InlineData("/both/%2e./both/x")]
    [InlineData("/both/%2E/x")]
    [InlineData("/both/x/%2e%2e%2f%2e%2e%2fboth/x")]
    [InlineData("/both/%2e%2e%5cboth/x")]
    [InlineData("/both/..;/both/x")]
    [InlineData("/both/%2e%2e%20/both/x")]
    [InlineData("/app/%2e%2e/app/x")]
    // Literal dot segments too: Kestrel resolves them for matching, but the raw path is what is forwarded.
    [InlineData("/both/a/../../both/x")]
    // Without a strip as well: /added/%2e%2e/%2e%2e/added/x is the route's /added/x, and /v2/added/%2e%2e/%2e%2e/added/x steps out of /v2.
    [InlineData("/added/%2E%2e/%2e%2E/added/x")]
    [InlineData("/added/../../added/x")]
    public async Task APathThatCantBeRewrittenWithoutAGuessIsNotFound(string target)
    {
        await using var server = await StartAsync();

        var (status, body, _) = await RawHttp.GetAsync(server.BaseAddress, "app.test", target, TestContext.Current.CancellationToken);

        Assert.True(404 == status, $"{status}: {body}");
    }

    [Fact]
    public async Task AStrippingRouteSendsTheStrippedPrefixAndTheClientsCopyIsReplaced()
    {
        var (echo, address) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken);
        try
        {
            await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
            {
                Upstreams = [Upstream("up", address)],
                Hosts = [ProxyHost("app", "app.test", Route("app-root", "up"), Route("app-app", "up", "/app") with { StripPrefix = true })],
            });
            using var client = ProxyTestServer.CreateClient();
            using var stripped = server.Request("app.test", "/app/x");
            stripped.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/forged");
            using var plain = server.Request("app.test", "/x");
            plain.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", "/forged");

            var seen = HeaderEchoUpstreamHost.Parse(await (await client.SendAsync(stripped, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var seenPlain = HeaderEchoUpstreamHost.Parse(await (await client.SendAsync(plain, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Equal("/app", seen["X-Forwarded-Prefix"]);
            Assert.Equal("/x", seen["target"]);
            Assert.DoesNotContain("X-Forwarded-Prefix", seenPlain.Keys);
        }
        finally
        {
            await echo.StopAsync(TestContext.Current.CancellationToken);
            echo.Dispose();
        }
    }

    [Theory]
    [InlineData("/api/login?next=%2F", "/app/login?next=%2F")]
    [InlineData("/api", "/app")]
    [InlineData("http://app.test/api/login", "http://app.test/app/login")]
    // Pointing elsewhere: left as the upstream sent it.
    [InlineData("/apiary", "/apiary")]
    [InlineData("/elsewhere", "/elsewhere")]
    [InlineData("https://other.test/api/login", "https://other.test/api/login")]
    [InlineData("//cdn.test/api/x", "//cdn.test/api/x")]
    // A browser reads /\ as protocol-relative too; a scheme and host in another case are the same origin; another port isn't.
    [InlineData("/\\evil.test/api", "/\\evil.test/api")]
    [InlineData("HTTPS://APP.TEST/api/login", "HTTPS://APP.TEST/app/login")]
    [InlineData("http://app.test:443/api/x", "http://app.test:443/api/x")]
    public async Task ARedirectIntoTheUpstreamsPathsGetsTheClientsPrefixBack(string sent, string expected)
    {
        var (echo, address) = await HeaderEchoUpstreamHost.StartAsync(
            response =>
            {
                response.StatusCode = 302;
                response.Headers.Location = sent;
            },
            TestContext.Current.CancellationToken);
        try
        {
            await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
            {
                Upstreams = [Upstream("up", address)],
                Hosts = [ProxyHost("app", "app.test", Route("app-app", "up", "/app") with { StripPrefix = true, AddPrefix = "/api" })],
            });
            using var client = ProxyTestServer.CreateClient();
            using var request = server.Request("app.test", "/app/start");

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal(expected, response.Headers.Location!.OriginalString);
        }
        finally
        {
            await echo.StopAsync(TestContext.Current.CancellationToken);
            echo.Dispose();
        }
    }

    [Theory]
    // Strip only, on /grafana: the root of the upstream's paths gets the prefix; a prefix-aware app already sent it, and keeps it.
    [InlineData(null, "/login", "/grafana/login")]
    [InlineData(null, "/grafana/login", "/grafana/login")]
    [InlineData(null, "/GRAFANA", "/GRAFANA")]
    [InlineData(null, "/grafanax", "/grafana/grafanax")]
    // A sibling route of the host is the mapping's ambiguity: on, it is put under this route; the switch leaves it as sent.
    [InlineData(null, "/oauth2/sign_in", "/grafana/oauth2/sign_in")]
    [InlineData(false, "/oauth2/sign_in", "/oauth2/sign_in")]
    [InlineData(false, "/login", "/login")]
    public async Task AStripOnlyRouteMapsLocationUnlessItIsAlreadyTheClientsOrTheSwitchIsOff(bool? rewriteLocation, string sent, string expected)
    {
        var route = Route("app-grafana", "up", "/grafana") with { StripPrefix = true, RewriteLocation = rewriteLocation ?? true };
        Assert.Equal(expected, await LocationAsync(route, sent));
    }

    [Theory]
    // The upstream answers on the name it was sent (a custom Host header): an internal address never reaches the client.
    [InlineData("http://app.internal/login", "http://app.test/grafana/login")]
    [InlineData("http://APP.internal/grafana/x", "http://app.test/grafana/x")]
    [InlineData("http://app.internal/elsewhere?q=1", "http://app.test/grafana/elsewhere?q=1")]
    [InlineData("http://app.internal", "http://app.test/grafana")]
    [InlineData("http://other.internal/login", "http://other.internal/login")]
    public async Task ALocationOnTheUpstreamsOwnNameGetsTheClientsOrigin(string sent, string expected)
    {
        var route = Route("app-grafana", "up", "/grafana") with { StripPrefix = true };
        Assert.Equal(expected, await LocationAsync(route, sent, static upstream => upstream with { HostHeader = UpstreamHostHeaderMode.Custom, HostHeaderValue = "app.internal" }));
    }

    [Theory]
    // A route path that needs percent-encoding: the raw target spells it encoded, in either case of hex.
    [InlineData("/caf%C3%A9/x", "/a/x")]
    [InlineData("/caf%c3%a9/x", "/a/x")]
    [InlineData("/CAF%C3%A9", "/a/")]
    [InlineData("/my%20app/x%20y", "/a/x%20y")]
    public async Task AnEncodedRoutePathIsStrippedInTheSpellingAClientSends(string target, string expected)
    {
        await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
        {
            Upstreams = [Upstream("ua", upstream.Address("a"))],
            Hosts = [ProxyHost("app", "app.test", Route("app-cafe", "ua", "/café") with { StripPrefix = true }, Route("app-space", "ua", "/my app") with { StripPrefix = true })],
        });

        var (status, body, _) = await RawHttp.GetAsync(server.BaseAddress, "app.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
        Assert.Equal(expected, body);
    }

    [Fact]
    public async Task AnEncodedPrefixGoesUpstreamEncodedAndComesBackInALocation()
    {
        var (echo, address) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken);
        try
        {
            await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
            {
                Upstreams = [Upstream("up", address)],
                Hosts = [ProxyHost("app", "app.test", Route("app-cafe", "up", "/café") with { StripPrefix = true })],
            });
            using var client = ProxyTestServer.CreateClient();
            using var request = server.Request("app.test", "/caf%C3%A9/x");
            var seen = HeaderEchoUpstreamHost.Parse(await (await client.SendAsync(request, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            Assert.Equal("/caf%C3%A9", seen["X-Forwarded-Prefix"]);
            Assert.Equal("/x", seen["target"]);
        }
        finally
        {
            await echo.StopAsync(TestContext.Current.CancellationToken);
            echo.Dispose();
        }

        Assert.Equal("/caf%C3%A9/login", await LocationAsync(Route("app-cafe", "up", "/café") with { StripPrefix = true }, "/login"));
    }

    [Theory]
    // Escapes an upstream that decodes loosely could turn into something the gate never saw: a double encoding, an overlong
    // UTF-8 dot, IIS's %u form, control characters.
    [InlineData("/both/%252e%252e/both/x")]
    [InlineData("/both/%c0%ae%c0%ae/both/x")]
    [InlineData("/both/%C1%9C/x")]
    [InlineData("/both/%u002e%u002e/both/x")]
    [InlineData("/both/a%01b")]
    [InlineData("/both/a%1Fb")]
    [InlineData("/both/a%7Fb")]
    [InlineData("/added/%252e%252e/x")]
    public async Task ALooseEscapeOnARewrittenRouteIsNotForwarded(string target)
    {
        await using var server = await StartAsync();

        var (status, body, _) = await RawHttp.GetAsync(server.BaseAddress, "app.test", target, TestContext.Current.CancellationToken);

        // 404 from the gate, or Kestrel's own 400 for a target it refuses first: never the upstream's 200.
        Assert.True(status is 404 or 400, $"{status}: {body}");
    }

    [Theory]
    // An add-only route forwards the client's whole path under its prefix: taking the prefix off a Location would point
    // outside the route, so it is left as the upstream sent it.
    [InlineData("/v2/login")]
    [InlineData("/v2/added/login")]
    public async Task AnAddOnlyRouteLeavesLocationAlone(string sent)
    {
        var (echo, address) = await HeaderEchoUpstreamHost.StartAsync(
            response =>
            {
                response.StatusCode = 302;
                response.Headers.Location = sent;
            },
            TestContext.Current.CancellationToken);
        try
        {
            await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
            {
                Upstreams = [Upstream("up", address)],
                Hosts = [ProxyHost("app", "app.test", Route("app-added", "up", "/added") with { AddPrefix = "/v2" })],
            });
            using var client = ProxyTestServer.CreateClient();
            using var request = server.Request("app.test", "/added/start");

            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(sent, response.Headers.Location!.OriginalString);
        }
        finally
        {
            await echo.StopAsync(TestContext.Current.CancellationToken);
            echo.Dispose();
        }
    }

    [Theory]
    [InlineData("/both/x", "/api/x")]
    [InlineData("/app", "/")]
    [InlineData("/added/x", "/v2/added/x")]
    public void TheSimulatorShowsTheForwardedPath(string path, string expected)
    {
        var run = GateSimulator.Run(Bound(Model()), new GateProbe { Host = "app.test", Path = path, ClientAddress = IPAddress.Loopback });

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream);
        Assert.Equal(expected, Assert.Single(step.Facts, f => f.Name == "forwardedPath").Value);
    }

    [Theory]
    [InlineData("/caf%C3%A9/x", "/x")]
    [InlineData("/caf%c3%a9", "/")]
    [InlineData("/café/x", "/x")]
    public void TheSimulatorStripsAnEncodedRoutePathAsTheServerDoes(string path, string expected)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("ua", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("app", "app.test", Route("app-cafe", "ua", "/café") with { StripPrefix = true })],
        };

        var run = GateSimulator.Run(Bound(model), new GateProbe { Host = "app.test", Path = path, ClientAddress = IPAddress.Loopback });

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream);
        Assert.Equal(expected, Assert.Single(step.Facts, f => f.Name == "forwardedPath").Value);
        Assert.Equal("/caf%C3%A9", Assert.Single(step.Facts, f => f.Name == "X-Forwarded-Prefix").Value);
    }

    [Fact]
    public void TheSimulatorSaysWhyADotSegmentCantBeRewritten()
    {
        var run = GateSimulator.Run(Bound(Model()), new GateProbe { Host = "app.test", Path = "/both/%2e%2e/both/x", ClientAddress = IPAddress.Loopback });

        Assert.Contains(run.Steps, s => s is { Kind: GateStepKind.RouteMatch, Verdict: GateStepVerdict.Stopped } && s.Summary.Contains("dot segment", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Steps, s => s.Kind == GateStepKind.Upstream);
    }

    [Fact]
    public void TheSimulatorSaysWhyAPrefixCantBeStripped()
    {
        var run = GateSimulator.Run(Bound(Model()), new GateProbe { Host = "app.test", Path = "/app;v=1/x", ClientAddress = IPAddress.Loopback });

        Assert.Contains(run.Steps, s => s is { Kind: GateStepKind.RouteMatch, Verdict: GateStepVerdict.Stopped } && s.Summary.Contains("404", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Steps, s => s.Kind == GateStepKind.Upstream);
    }

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("ua", upstream.Address("a"))],
        Hosts =
        [
            ProxyHost(
                "app",
                "app.test",
                Route("app-root", "ua"),
                Route("app-app", "ua", "/app") with { StripPrefix = true },
                Route("app-both", "ua", "/both") with { StripPrefix = true, AddPrefix = "/api" },
                Route("app-added", "ua", "/added") with { AddPrefix = "/v2" },
                Route("app-other", "ua", "/other")),
        ],
    };

    private Task<ProxyTestServer> StartAsync() => ProxyTestServer.StartAsync(Model());

    /// <summary>The Location the client gets through <paramref name="route"/> when the upstream redirects to <paramref name="sent"/>.</summary>
    private static async Task<string> LocationAsync(RouteSpec route, string sent, Func<UpstreamSpec, UpstreamSpec>? upstreamWith = null)
    {
        var (echo, address) = await HeaderEchoUpstreamHost.StartAsync(
            response =>
            {
                response.StatusCode = 302;
                response.Headers.Location = sent;
            },
            TestContext.Current.CancellationToken);
        try
        {
            var upstreamSpec = Upstream("up", address);
            await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
            {
                Upstreams = [upstreamWith is null ? upstreamSpec : upstreamWith(upstreamSpec)],
                Hosts = [ProxyHost("app", "app.test", route)],
            });
            using var client = ProxyTestServer.CreateClient();
            var path = route.Path == "/" ? "/start" : route.Path + "/start";
            using var request = server.Request("app.test", path);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            return response.Headers.Location!.OriginalString;
        }
        finally
        {
            await echo.StopAsync(TestContext.Current.CancellationToken);
            echo.Dispose();
        }
    }
}
