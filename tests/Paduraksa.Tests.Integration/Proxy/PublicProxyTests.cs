using System.Net;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Server.Hosting;
using Paduraksa.Server.Placeholders;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Proxy;

public sealed class PublicProxyTests
{
    [Fact]
    public async Task BootsWithAnEmptyConfigAndServesPublishedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await EchoUpstream.StartAsync(ct);
        await using var host = TestHost.Create();
        await host.StartAsync(ct);
        using var client = TestHost.CreateClient(host);

        var boot = host.Shared.SnapshotStore.Current;
        Assert.Equal(1, boot.Generation);
        Assert.Equal(0, boot.Hosts.Count);
        Assert.True(host.Shared.SnapshotPublisher.IsAttached);

        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, new ProxyConfigModel
        {
            Revision = TestIds.Revision("it-1"),
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("app"), Destinations = [new(TestIds.Destination("app-1"), upstream.Address)] }],
            Hosts =
            [
                new HostSpec { Id = TestIds.Host("app"), Names = ["app.test"], Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("app") }] },
                new HostSpec { Id = TestIds.Host("secure"), Names = ["secure.test"], ForceHttps = true, Routes = [new RouteSpec { Id = TestIds.Route("secure-root"), Upstream = TestIds.Upstream("app") }] },
            ],
        }), ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        Assert.Equal(2, published.Generation);

        // Plain HTTP (H1) and HTTPS (H2) both reach the upstream through YARP.
        using var overHttp = await client.SendAsync(Request(host, "http", "app.test", "/hello"), ct);
        Assert.Equal(HttpStatusCode.OK, overHttp.StatusCode);
        Assert.Equal("upstream:/hello", await overHttp.Content.ReadAsStringAsync(ct));

        using var h2 = Request(host, "https", "app.test", "/h2");
        h2.Version = HttpVersion.Version20;
        h2.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        using var overHttps = await client.SendAsync(h2, ct);
        Assert.Equal(HttpVersion.Version20, overHttps.Version);
        Assert.Equal("upstream:/h2", await overHttps.Content.ReadAsStringAsync(ct));

        // Forced HTTPS: plain HTTP is redirected to the public HTTPS port (443 by default), HTTPS is proxied.
        using HttpClient noRedirects = new(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var redirect = await noRedirects.SendAsync(Request(host, "http", "secure.test", "/x?y=1"), ct);
        Assert.Equal(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.Equal("https://secure.test/x?y=1", redirect.Headers.Location!.OriginalString);
        using var secure = await client.SendAsync(Request(host, "https", "secure.test", "/x"), ct);
        Assert.Equal("upstream:/x", await secure.Content.ReadAsStringAsync(ct));

        // Unknown hosts still get the default.
        using var unknown = await client.SendAsync(Request(host, "http", "nobody.test", "/"), ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task AStartupConfigWithRoutesDoesNotBlockStartupAndGoesLiveWhenYarpAttaches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await EchoUpstream.StartAsync(ct);
        var data = TestDataDirectories.NewPath();
        var startup = await ConfigTestSupport.SeedAsync(
            data,
            new ConfigDocument(Upstreams: [ConfigTestSupport.Upstream("app", upstream.Address)], Hosts: [ConfigTestSupport.ProxyHost("app", "app.test", "app")]),
            ct);

        // The core host alone: its start returns although the publish needs YARP, which doesn't exist yet.
        await using (var coreOnly = TestHost.Create(dataDirectory: data))
        {
            await coreOnly.Core.StartAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False(coreOnly.Shared.SnapshotStore.HasPublished);
            await coreOnly.Core.StopAsync(ct);
        }

        // The whole process: the public app's start attaches YARP, so the config is live before any listener accepts.
        await using var host = TestHost.Create(dataDirectory: data);
        await host.StartAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(host.Shared.SnapshotStore.HasPublished);
        Assert.Equal(startup.Id, host.Shared.SnapshotStore.Current.Revision);
        using var client = TestHost.CreateAdminClient(host);
        using var ready = await client.GetAsync(new Uri(TestHost.Endpoint(host, HostRole.Admin, "https"), "/readyz"), ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var proxied = await client.SendAsync(Request(host, "http", "app.test", "/started"), ct);
        Assert.Equal("upstream:/started", await proxied.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task ThePublicAppSetsNoRequestBodyLimit()
    {
        // Until hosts carry their own limit, an upload of any size goes through to the upstream, which decides.
        var ct = TestContext.Current.CancellationToken;
        await using var host = TestHost.Create();
        await host.StartAsync(ct);

        Assert.Null(host.Public.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
    }

    [Fact]
    public async Task TheAuthorizationMiddlewareNeverRunsOnThePublicApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await EchoUpstream.StartAsync(ct);
        SpyAuthorizationPolicyProvider spy = new();
        await using var host = TestHost.Create(configureServices: (role, services) =>
        {
            if (role == HostRole.Public)
            {
                services.AddSingleton<IAuthorizationPolicyProvider>(spy);
            }
        });
        await host.StartAsync(ct);
        Assert.Same(spy, host.Public.Services.GetRequiredService<IAuthorizationPolicyProvider>());
        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, new ProxyConfigModel
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("app"), Destinations = [new(TestIds.Destination("app-1"), upstream.Address)] }],
            Hosts = [new HostSpec { Id = TestIds.Host("app"), Names = ["app.test"], Routes = [new RouteSpec { Id = TestIds.Route("app-root"), Upstream = TestIds.Upstream("app") }] }],
        }), ct);
        Assert.True(published.Succeeded);
        using var client = TestHost.CreateClient(host);

        for (var i = 0; i < 5; i++)
        {
            using var proxied = await client.SendAsync(Request(host, "https", "app.test", "/p"), ct);
            Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
            using var answered = await client.SendAsync(Request(host, "http", "nobody.test", "/"), ct);
            Assert.Equal(HttpStatusCode.NotFound, answered.StatusCode);
        }

        Assert.Equal(0, spy.FallbackCalls);
    }

    [Fact]
    public async Task SanityTheSpySeesTheImplicitAuthorizationMiddlewareOfAWebApplication()
    {
        // Proves the guard above can fail: a WebApplication with YARP's services auto-inserts UseAuthorization().
        var ct = TestContext.Current.CancellationToken;
        SpyAuthorizationPolicyProvider spy = new();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRoutingCore();
        builder.Services.AddReverseProxy();
        builder.Services.AddSingleton<IAuthorizationPolicyProvider>(spy);
        await using var app = builder.Build();
        app.UseRouting();
        app.Run(static context => context.Response.WriteAsync("ok"));
        await app.StartAsync(ct);
        using HttpClient client = new();

        using var response = await client.GetAsync(new Uri(app.Urls.Single()), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(spy.FallbackCalls > 0);
        await app.StopAsync(ct);
    }

    /// <summary>Compiles like the server does: its compiler options, the placeholder as the default certificate.</summary>
    internal static RuntimeSnapshot Compile(PaduraksaHost host, ProxyConfigModel model, SnapshotCompilerOptions? options = null)
    {
        if (model.Defaults.Certificate is null)
        {
            model = model with { Defaults = model.Defaults with { Certificate = PlaceholderCertificates.Id } };
        }

        var result = SnapshotCompiler.Compile(model, (options ?? host.Shared.PublicListeners.CompilerOptions) with { Certificates = host.Shared.Certificates });
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return result.Snapshot!;
    }

    private static HttpRequestMessage Request(PaduraksaHost host, string scheme, string hostHeader, string pathAndQuery)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, scheme), pathAndQuery));
        request.Headers.Host = hostHeader;
        return request;
    }

    /// <summary>Counts fallback-policy lookups, which only the authorization middleware makes.</summary>
    private sealed class SpyAuthorizationPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _inner = new(Options.Create(new AuthorizationOptions()));
        private int _fallbackCalls;

        public int FallbackCalls => Volatile.Read(ref _fallbackCalls);

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            Interlocked.Increment(ref _fallbackCalls);
            return _inner.GetFallbackPolicyAsync();
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _inner.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => _inner.GetPolicyAsync(policyName);
    }

    /// <summary>A loopback upstream answering <c>upstream:{path}</c>.</summary>
    private sealed class EchoUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private EchoUpstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<EchoUpstream> StartAsync(CancellationToken ct)
        {
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(static web => web
                    .UseKestrel(static kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(static app => app.Run(static context => context.Response.WriteAsync("upstream:" + context.Request.Path))))
                .Build();
            await host.StartAsync(ct);
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new EchoUpstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
