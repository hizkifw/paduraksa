using System.Net;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

public sealed class ProxyEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel Config => new()
    {
        Revision = TestIds.Revision("e2e"),
        Upstreams =
        [
            Upstream("ua", upstream.Address("a")),
            Upstream("ub", upstream.Address("b")),
            Upstream("uc", upstream.Address("c")),
        ],
        Hosts =
        [
            ProxyHost("one", "one.test", Route("one-api", "uc", "/api"), Route("one-root", "ua")),
            ProxyHost("two", "two.test", Route("two-root", "ub")) with { Names = ["two.test", "*.two.test"] },
            ProxyHost("secure", "secure.test", Route("secure-root", "ua")) with { ForceHttps = true },
            ProxyHost("guarded", "guarded.test", Route("guarded-deny", "ua", "/deny") with { Policies = [new DenyPolicySpec(451, "unavailable for legal reasons\n")] }, Route("guarded-drop", "ua", "/drop") with { Policies = [new DropPolicySpec()] }, Route("guarded-root", "ua")),
            new HostSpec { Id = TestIds.Host("old"), Names = ["old.test"], Kind = HostKind.Redirect, Redirect = new RedirectSpec { Target = "https://new.test/base", StatusCode = 308 } },
            new HostSpec { Id = TestIds.Host("soon"), Names = ["soon.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 503, Body = "coming soon\n" } },
            new HostSpec { Id = TestIds.Host("empty"), Names = ["empty.test"], Kind = HostKind.Respond, Respond = new RespondSpec { StatusCode = 204 } },
            ProxyHost("quiet", "quiet.test", Route("quiet-root", "ua") with { Policies = [new DenyPolicySpec(304, string.Empty)] }),
        ],
    };

    [Theory]
    [InlineData("one.test", "/x", "/a/x")]
    [InlineData("ONE.test:1234", "/api/v1?q=1", "/c/api/v1")]
    [InlineData("two.test", "/", "/b/")]
    [InlineData("www.two.test", "/y", "/b/y")]
    [InlineData("guarded.test", "/ok", "/a/ok")]
    public async Task HostsRouteToTheirClusters(string host, string path, string upstreamPath)
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request(host, path), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(upstreamPath, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnknownHostsGetTheDefaultBehaviour()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var notFound = await client.SendAsync(server.Request("nobody.test"), ct);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("no host configured\n", await notFound.Content.ReadAsStringAsync(ct));

        Assert.True((await server.PublishAsync(Config with { Defaults = new DefaultsSpec { UnknownHost = UnknownHostBehaviour.Close } })).Succeeded);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(server.Request("nobody.test"), ct));

        Assert.True((await server.PublishAsync(Config with { Defaults = new DefaultsSpec { UnknownHost = UnknownHostBehaviour.MisdirectedRequest } })).Succeeded);
        using var misdirected = await client.SendAsync(server.Request("nobody.test"), ct);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, misdirected.StatusCode);
    }

    [Fact]
    public async Task RedirectAndRespondHostsAreAnsweredByTheGate()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var redirect = await client.SendAsync(server.Request("old.test", "/p/q?x=1"), ct);
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirect.StatusCode);
        Assert.Equal("https://new.test/base/p/q?x=1", redirect.Headers.Location!.OriginalString);

        using var respond = await client.SendAsync(server.Request("soon.test", "/anything"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, respond.StatusCode);
        Assert.Equal("coming soon\n", await respond.Content.ReadAsStringAsync(ct));

        using var head = await client.SendAsync(server.Request("soon.test", "/", HttpMethod.Head), ct);
        Assert.Equal(12, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));
    }

    [Theory]
    [InlineData("empty.test", HttpStatusCode.NoContent)]
    [InlineData("quiet.test", HttpStatusCode.NotModified)]
    public async Task BodilessAnswersAreSentWithoutABody(string host, HttpStatusCode status)
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request(host, "/x"), TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestsThatReachYarpWithoutTheGateAreRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = ProxyTestServer.CreateClient();

        // Without the gate, routing matches the internal route by itself; the proxy pipeline refuses it.
        await using (var ungated = await ProxyTestServer.StartAsync(Config, new() { WithoutGate = true }))
        {
            using var direct = await client.SendAsync(ungated.Request("one.test", "/.paduraksa/r/one-root/x"), ct);
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
            Assert.Empty(await direct.Content.ReadAsStringAsync(ct));
        }

        // With the gate, the internal path is just a path: unknown hosts get the default, known hosts their own route.
        await using var gated = await ProxyTestServer.StartAsync(Config);
        using var unknown = await client.SendAsync(gated.Request("nobody.test", "/.paduraksa/r/one-root/x"), ct);
        Assert.Equal("no host configured\n", await unknown.Content.ReadAsStringAsync(ct));
        using var known = await client.SendAsync(gated.Request("two.test", "/.paduraksa/r/one-root/x"), ct);
        Assert.Equal("/b/.paduraksa/r/one-root/x", await known.Content.ReadAsStringAsync(ct));
    }

    [Theory]
    [InlineData("GET", HttpStatusCode.MovedPermanently)]
    [InlineData("POST", HttpStatusCode.PermanentRedirect)]
    public async Task PlainHttpIsRedirectedToHttpsOnThePublicPort(string method, HttpStatusCode status)
    {
        await using var server = await ProxyTestServer.StartAsync(Config, new() { CompilerOptions = Port8443 });
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request("secure.test:80", "/a%20b/c?d=e", new HttpMethod(method)), TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("https://secure.test:8443/a%20b/c?d=e", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task PoliciesCanRespondOrDrop()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var denied = await client.SendAsync(server.Request("guarded.test", "/deny/x"), ct);
        Assert.Equal((HttpStatusCode)451, denied.StatusCode);
        Assert.Equal("unavailable for legal reasons\n", await denied.Content.ReadAsStringAsync(ct));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(server.Request("guarded.test", "/drop"), ct));

        using var allowed = await client.SendAsync(server.Request("guarded.test", "/fine"), ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task AnEmptyConfigAnswersEveryHostWithTheDefault()
    {
        await using var server = await ProxyTestServer.StartAsync(ProxyConfigModel.Empty);
        using var client = ProxyTestServer.CreateClient();

        using var response = await client.SendAsync(server.Request("one.test"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, server.Store.Current.Generation);
    }
}
