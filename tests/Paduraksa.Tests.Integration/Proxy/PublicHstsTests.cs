using System.Net;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// HSTS through the whole process: ours on every HTTPS response for the host, replacing the upstream's own;
/// the upstream's passed through when the host has it off; never over plain HTTP, to an IP literal or to a loopback name.
/// </summary>
public sealed class PublicHstsTests : IAsyncLifetime
{
    private const string Header = "Strict-Transport-Security";
    private const string OneYear = "max-age=31536000";

    private readonly TestCertificates _certificates = new();
    private EdgeUpstream? _upstream;

    private EdgeUpstream Upstream => _upstream!;

    public async ValueTask InitializeAsync() => _upstream = await EdgeUpstream.StartAsync();

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        _certificates.Dispose();
    }

    private static readonly AccessListSpec Private = TestAccessLists.Allow("private", "10.0.0.0/8");

    private ProxyConfigModel App(params HostSpec[] more) => new()
    {
        Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("echo"), Destinations = [new(TestIds.Destination("echo-1"), Upstream.Address)] }],
        AccessLists = [Private],
        Hosts =
        [
            ProxyHost("app", "app.test") with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("plain", "plain.test"),
            ProxyHost("full", "full.test") with { Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 63_072_000, IncludeSubDomains = true, Preload = true } },
            ProxyHost("literal", "127.0.0.1") with { Hsts = new HostHstsSpec { Enabled = true } },
            ProxyHost("loopback", "localhost") with { Hsts = new HostHstsSpec { Enabled = true } },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                Hsts = new HostHstsSpec { Enabled = true, MaxAgeSeconds = 86_400 },
            },
            .. more,
        ],
    };

    private static HostSpec ProxyHost(string label, string name) =>
        new() { Id = TestIds.Host(label), Names = [name], Routes = [new RouteSpec { Id = TestIds.Route($"{label}-root"), Upstream = TestIds.Upstream("echo") }] };

    /// <summary>A started host with a certificate for every name these tests ask for, and <paramref name="model"/> live.</summary>
    private async Task<PaduraksaHost> StartedAsync(ProxyConfigModel model)
    {
        var host = TestHost.Create();
        await host.StartAsync(TestContext.Current.CancellationToken);
        host.Shared.Certificates.Set(TestIds.Certificate("app"), _certificates.Create("app.test", "app.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("api"), _certificates.Create("api.test", "api.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("deny"), _certificates.Create("deny.test", "deny.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("full"), _certificates.Create("full.test", "full.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("soon"), _certificates.Create("soon.test", "soon.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("literal"), _certificates.Create("literal", "127.0.0.1"));
        host.Shared.Certificates.Set(TestIds.Certificate("loopback"), _certificates.Create("loopback", "localhost"));
        host.Shared.Certificates.Set(TestIds.Certificate("plain"), _certificates.Create("plain.test", "plain.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("secure"), _certificates.Create("secure.test", "secure.test"));
        host.Shared.Certificates.Set(TestIds.Certificate("fallback"), _certificates.Create("fallback", "fallback.test"));
        await PublishAsync(host, model);
        return host;
    }

    private static async Task PublishAsync(PaduraksaHost host, ProxyConfigModel model)
    {
        var result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, model), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
    }

    private static async Task<(HttpResponseMessage Response, string[] Hsts)> SendAsync(
        PaduraksaHost host, string scheme, string hostHeader, string path = "/", Action<HttpRequestMessage>? configure = null)
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, scheme, hostHeader, path);
        configure?.Invoke(request);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        string[] hsts = response.Headers.TryGetValues(Header, out var values) ? [.. values] : [];
        return (response, hsts);
    }

    [Fact]
    public async Task HstsGoesOnHttpsResponsesOfEveryKindAndReplacesTheUpstreams()
    {
        await using var host = await StartedAsync(App(
            ProxyHost("deny", "deny.test") with { Hsts = new HostHstsSpec { Enabled = true }, Policies = [Private.Policy()] },
            new HostSpec
            {
                Id = TestIds.Host("api"),
                Names = ["api.test"],
                Routes = [new RouteSpec { Id = TestIds.Route("api-root"), Path = "/api", Upstream = TestIds.Upstream("echo") }],
                Hsts = new HostHstsSpec { Enabled = true },
            }));

        foreach (var (name, path, status, expected) in new (string Name, string Path, HttpStatusCode Status, string Expected)[]
        {
            ("app.test", "/", HttpStatusCode.OK, OneYear), // forwarded
            ("full.test", "/", HttpStatusCode.OK, "max-age=63072000; includeSubDomains; preload"),
            ("soon.test", "/", HttpStatusCode.ServiceUnavailable, "max-age=86400"), // a Respond host
            ("deny.test", "/", HttpStatusCode.Forbidden, OneYear), // a policy's refusal
            ("app.test", "/nothing-takes-this", HttpStatusCode.OK, OneYear), // the catch-all route takes it (forwarded)
            ("api.test", "/nothing-takes-this", HttpStatusCode.NotFound, OneYear), // the gate's own 404: no route takes it
        })
        {
            var (response, hsts) = await SendAsync(host, "https", name, path);
            using (response)
            {
                Assert.Equal(status, response.StatusCode);
                Assert.Equal([expected], hsts); // one value: ours, not the upstream's, not both
            }
        }
    }

    [Fact]
    public async Task WithoutHstsTheUpstreamsOwnPassesThrough()
    {
        await using var host = await StartedAsync(App());

        var (response, hsts) = await SendAsync(host, "https", "plain.test");
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([EdgeUpstream.UpstreamHsts], hsts);
        }
    }

    [Fact]
    public async Task PlainHttpNeverCarriesIt()
    {
        await using var host = await StartedAsync(App(
            ProxyHost("secure", "secure.test") with { ForceHttps = true }));

        // A plain-HTTP request to a host that serves it: the upstream's own passes through, ours is never sent.
        var (forwarded, overHttp) = await SendAsync(host, "http", "app.test");
        using (forwarded)
        {
            Assert.Equal(HttpStatusCode.OK, forwarded.StatusCode);
            Assert.Equal([EdgeUpstream.UpstreamHsts], overHttp);
        }

        // A forced host's redirect is the gate's own answer: no header at all.
        var (redirect, overRedirect) = await SendAsync(host, "http", "secure.test");
        using (redirect)
        {
            Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
            Assert.Empty(overRedirect);
        }
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task IpLiteralsAndLoopbackNamesNeverGetOurs(string name)
    {
        await using var host = await StartedAsync(App());

        var (response, hsts) = await SendAsync(host, "https", name);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The upstream's own is all that is left: Paduraksa never pins an address or a loopback name.
            Assert.Equal([EdgeUpstream.UpstreamHsts], hsts);
        }
    }

    [Fact]
    public async Task ATrustedTerminatorsHttpsCountsAndAnUntrustedOneDoesnt()
    {
        await using var host = await StartedAsync(App() with
        {
            Defaults = new DefaultsSpec { TrustedProxies = new TrustedProxySpec { Mode = TrustedProxyMode.CloudflareTunnel, Proxies = ["127.0.0.0/8", "::1"] } },
        });

        // cloudflared terminates the visitor's TLS and forwards over plain HTTP, saying so in CF-Visitor.
        var (response, hsts) = await SendAsync(
            host,
            "http",
            "app.test",
            configure: static request => request.Headers.TryAddWithoutValidation("CF-Visitor", "{\"scheme\":\"https\"}"));
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal([OneYear], hsts);
        }

        // The same header from nobody trusted is a spoof: the request is plain HTTP, so nothing of ours.
        await PublishAsync(host, App());
        var (spoofed, none) = await SendAsync(
            host,
            "http",
            "app.test",
            configure: static request => request.Headers.TryAddWithoutValidation("CF-Visitor", "{\"scheme\":\"https\"}"));
        using (spoofed)
        {
            Assert.Equal(HttpStatusCode.OK, spoofed.StatusCode);
            Assert.Equal([EdgeUpstream.UpstreamHsts], none);
        }
    }
}
