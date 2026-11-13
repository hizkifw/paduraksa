using System.Net;

using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>Custom request and response headers through a real proxy: what the upstream receives, what the client receives, a route's own rules, and a gate answer.</summary>
public sealed class CustomHeadersEndToEndTests : IAsyncLifetime
{
    private static readonly HostHeadersSpec HostRules = new()
    {
        Request =
        [
            new HeaderRuleSpec("X-Custom", HeaderOperation.Set, "from-proxy"),
            new HeaderRuleSpec("X-Other", HeaderOperation.Add, "added"),
            new HeaderRuleSpec("Cookie", HeaderOperation.Remove, null),
            new HeaderRuleSpec("Content-Language", HeaderOperation.Set, "en"),
        ],
        Response =
        [
            new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY"),
            new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
            new HeaderRuleSpec("Server", HeaderOperation.Set, "edge"),
            new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin"),
        ],
    };

    // The route's rules over the host's: X-Custom is replaced by name, the rest are inherited.
    private static readonly HostHeadersSpec ApiRules = HostRules with
    {
        Request = [new HeaderRuleSpec("X-Custom", HeaderOperation.Set, "from-api"), .. HostRules.Request.Skip(1)],
        Response = [new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Remove, null), .. HostRules.Response.Skip(1)],
    };

    private IHost? _upstream;
    private string _address = string.Empty;

    public async ValueTask InitializeAsync() =>
        (_upstream, _address) = await HeaderEchoUpstreamHost.StartAsync(
            static response =>
            {
                response.Headers["X-Powered-By"] = "Express";
                response.Headers["Server"] = "upstream/1.0";
                response.Headers["Vary"] = "Accept-Encoding";
                response.Headers["X-Frame-Options"] = "SAMEORIGIN";
            },
            TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.StopAsync(TestContext.Current.CancellationToken);
            _upstream.Dispose();
        }
    }

    [Fact]
    public async Task TheUpstreamAndTheClientSeeTheHostsRules()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();
        // A body, so the Content-Language rule has the request's content to go on.
        using var request = server.Request("app.test", "/", HttpMethod.Post);
        request.Content = new StringContent("body");
        request.Headers.TryAddWithoutValidation("X-Custom", "from-client");
        request.Headers.TryAddWithoutValidation("X-Other", "client");
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var seen = HeaderEchoUpstreamHost.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("from-proxy", seen["X-Custom"]);
        Assert.Contains("client", seen["X-Other"], StringComparison.Ordinal);
        Assert.Contains("added", seen["X-Other"], StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", seen.Keys);
        Assert.Equal("en", seen["Content-Language"]);

        Assert.Equal(["DENY"], response.Headers.GetValues("X-Frame-Options"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.Equal("edge", Assert.Single(response.Headers.Server).ToString());
        Assert.Equal(["Accept-Encoding", "Origin"], response.Headers.Vary);
    }

    [Fact]
    public async Task ARouteReplacesARuleByNameAndKeepsTheHostsOthers()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/api/items");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var seen = HeaderEchoUpstreamHost.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("from-api", seen["X-Custom"]);

        // A body-less request has no content to carry a Content-* header: the rule does nothing there, and the request still goes.
        Assert.DoesNotContain("Content-Language", seen.Keys);
        Assert.False(response.Headers.Contains("X-Frame-Options"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.Equal("edge", Assert.Single(response.Headers.Server).ToString());
    }

    [Fact]
    public void TheSimulatorRedactsValuesThatLookLikeCredentials()
    {
        HostHeadersSpec rules = new()
        {
            Request =
            [
                new HeaderRuleSpec("Authorization", HeaderOperation.Set, "Bearer s3cret"),
                new HeaderRuleSpec("Cookie", HeaderOperation.Add, "session=s3cret"),
                new HeaderRuleSpec("X-Api-Key", HeaderOperation.Set, "s3cret"),
                new HeaderRuleSpec("X-Upstream-Token", HeaderOperation.Set, "s3cret"),
                new HeaderRuleSpec("x-client-secret", HeaderOperation.Set, "s3cret"),
                new HeaderRuleSpec("X-Tenant", HeaderOperation.Set, "homelab"),
            ],
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("up", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "up")) with { Headers = rules }],
        };

        var run = GateSimulator.Run(Bound(model), new GateProbe { Host = "app.test", ClientAddress = IPAddress.Loopback });

        var facts = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream).Facts;
        Assert.DoesNotContain(facts, f => f.Value.Contains("s3cret", StringComparison.Ordinal));
        Assert.Contains(facts, f => f is { Name: "Authorization", Value: "request: set to (redacted)" });
        Assert.Contains(facts, f => f is { Name: "X-Tenant", Value: "request: set to homelab" });
    }

    [Fact]
    public async Task AFailedForwardsPageCarriesEachAddOnce()
    {
        // YARP runs the response transform on its own 502, and the reapply on the response's start runs the rules again: an add
        // must not land twice.
        HostHeadersSpec adds = new()
        {
            Response = [new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin"), new HeaderRuleSpec("Set-Cookie", HeaderOperation.Add, "seen=1; Path=/")],
        };
        await using var server = await ProxyTestServer.StartAsync(new ProxyConfigModel
        {
            Upstreams = [Upstream("down", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "down")) with { Headers = adds }],
        });
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["Origin"], response.Headers.Vary);
        Assert.Equal(["seen=1; Path=/"], response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task TheProxysOwnAnswerCarriesTheResponseRules()
    {
        // Only /api routes, so / is the gate's 404.
        var model = Model() with { Hosts = [ProxyHost("app", "app.test", Route("app-api", "up", "/api")) with { Headers = HostRules }] };
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(["DENY"], response.Headers.GetValues("X-Frame-Options"));
        Assert.Equal("edge", Assert.Single(response.Headers.Server).ToString());
        Assert.Equal(["Origin"], response.Headers.Vary);
    }

    [Fact]
    public void TheSimulatorReportsTheRulesOfTheRouteThatForwards()
    {
        var run = GateSimulator.Run(Bound(Model()), new GateProbe { Host = "app.test", Path = "/api/x", ClientAddress = IPAddress.Loopback });

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream);
        Assert.Contains(step.Facts, f => f is { Name: "X-Custom", Value: "request: set to from-api" });
        Assert.Contains(step.Facts, f => f is { Name: "Cookie", Value: "request: removed" });
        Assert.Contains(step.Facts, f => f is { Name: "X-Frame-Options", Value: "response: removed" });
        Assert.Contains(step.Facts, f => f is { Name: "Vary", Value: "response: Origin added" });
    }

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("up", _address)],
        Hosts =
        [
            ProxyHost("app", "app.test", Route("app-root", "up"), Route("app-api", "up", "/api") with { Headers = ApiRules }) with { Headers = HostRules },
        ],
    };
}
