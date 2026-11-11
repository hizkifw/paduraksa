using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Integration.Proxy.PublicProxyTests;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// The operator's error pages, maintenance mode and the replacement of an upstream's error through a real <c>PaduraksaHost</c>: its own
/// listeners, TLS and pipeline, over HTTP/1.1 and HTTP/2 (HTTP/3 has the same pipeline, and runs in the Docker QUIC suite).
/// </summary>
public sealed class PublicErrorPagesTests : IAsyncLifetime
{
    private readonly TestCertificates _certificates = new();
    private WebApplication? _upstream;
    private string _upstreamAddress = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        _upstream = builder.Build();
        _upstream.Run(async context =>
        {
            context.Response.StatusCode = context.Request.Path.Value is "/missing" ? 404 : 200;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<h1>the upstream's own page</h1>", Ct);
        });
        await _upstream.StartAsync(Ct);
        _upstreamAddress = Assert.Single(_upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/";
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.DisposeAsync();
        }

        _certificates.Dispose();
    }

    private static HostSpec Proxy(string label, string name, params RouteSpec[] routes) =>
        new() { Id = TestIds.Host(label), Names = [name], Routes = routes.Length > 0 ? routes : [new RouteSpec { Id = TestIds.Route($"{label}-root"), Upstream = TestIds.Upstream("web") }] };

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("web"), Destinations = [new(TestIds.Destination("web-1"), _upstreamAddress)] }],
        Hosts =
        [
            // No route takes anything but /app, so every other path is the gate's own 404.
            Proxy("pages", "pages.test", new RouteSpec { Id = TestIds.Route("pages-app"), Path = "/app", Upstream = TestIds.Upstream("web") }) with
            {
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [404] = new() { Heading = "{status} {statusText} on {host}", Text = "Quote {requestId}.", Source = ErrorPageSource.Host },
                    },
                },
            },
            Proxy("doc", "doc.test", new RouteSpec { Id = TestIds.Route("doc-app"), Path = "/app", Upstream = TestIds.Upstream("web") }) with
            {
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [404] = new() { Mode = ErrorPageMode.Html, Html = "<style>b{color:red}</style><b>{status}</b> {requestId}", Source = ErrorPageSource.Host },
                    },
                },
            },
            Proxy("maint", "maint.test") with
            {
                Maintenance = new HostMaintenanceSpec { Enabled = true, RetryAfterSeconds = 300 },
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [ErrorPageStatuses.MaintenanceKey] = new() { Heading = "Back at {status}", Source = ErrorPageSource.Host },
                    },
                },
            },
            Proxy("replace", "replace.test") with
            {
                ErrorPages = new HostErrorPagesSpec
                {
                    ReplaceUpstream = UpstreamErrorReplacement.Html,
                    Pages = new Dictionary<int, ErrorPageSpec> { [404] = new() { Heading = "Ours: {status}", Source = ErrorPageSource.Host } },
                },
            },
        ],
    };

    private async Task<PaduraksaHost> StartedAsync()
    {
        var host = TestHost.Create();
        await host.StartAsync(Ct);
        foreach (var name in new[] { "pages", "doc", "maint", "replace" })
        {
            host.Shared.Certificates.Set(TestIds.Certificate(name), _certificates.Create($"{name}.test", $"{name}.test"));
        }

        var result = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, Model()), Ct);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        return host;
    }

    public static TheoryData<string, bool> Protocols => new() { { "http", false }, { "https", false }, { "https", true } };

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(PaduraksaHost host, string scheme, bool h2, string name, string path, string accept = "text/html")
    {
        using var client = EdgeClient.Create(out _);
        using var request = EdgeClient.Request(host, scheme, $"{name}.test", path, h2 ? HttpVersion.Version20 : HttpVersion.Version11);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        var response = await client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AHostsMessagePageIsServedForTheGatesOwn404(string scheme, bool h2)
    {
        await using var host = await StartedAsync();

        var (response, body) = await GetAsync(host, scheme, h2, "pages", "/nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        var id = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.Contains("<h1>404 Not Found on pages.test</h1>", body, StringComparison.Ordinal);
        Assert.Contains($"<p>Quote {id}.</p>", body, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'none'; style-src 'sha256-", Assert.Single(response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        if (h2)
        {
            Assert.Equal(HttpVersion.Version20, response.Version);
        }

        var (plain, plainBody) = await GetAsync(host, scheme, h2, "pages", "/nothing", accept: "application/json");
        Assert.Equal("text/plain; charset=utf-8", plain.Content.Headers.ContentType!.ToString());
        Assert.DoesNotContain("pages.test", plainBody, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AHostsDocumentIsServedWithItsOwnPolicy(string scheme, bool h2)
    {
        await using var host = await StartedAsync();

        var (response, body) = await GetAsync(host, scheme, h2, "doc", "/nothing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var id = Assert.Single(response.Headers.GetValues("X-Request-Id"));
        Assert.Equal($"<style>b{{color:red}}</style><b>404</b> {id}", body);
        Assert.Equal(
            "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AHostInMaintenanceAnswersEveryPathWithItsPageStatusAndRetryAfter(string scheme, bool h2)
    {
        await using var host = await StartedAsync();

        foreach (var path in new[] { "/", "/anything", "/deep/path?x=1" })
        {
            var (response, body) = await GetAsync(host, scheme, h2, "maint", path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("300", Assert.Single(response.Headers.GetValues("Retry-After")));
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            Assert.Contains("<h1>Back at 503</h1>", body, StringComparison.Ordinal);
        }

        // HEAD: the headers and no body; a client that doesn't ask for HTML gets the plain line.
        using var client = EdgeClient.Create(out _);
        using var head = EdgeClient.Request(host, scheme, "maint.test", "/", h2 ? HttpVersion.Version20 : HttpVersion.Version11);
        head.Method = HttpMethod.Head;
        head.Headers.TryAddWithoutValidation("Accept", "text/html");
        using var headResponse = await client.SendAsync(head, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, headResponse.StatusCode);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync(Ct));

        var (_, plain) = await GetAsync(host, scheme, h2, "maint", "/", accept: "application/json");
        Assert.Equal("down for maintenance\n", plain);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task AnUpstreamsHtmlErrorIsReplacedAndItsSuccessIsNot(string scheme, bool h2)
    {
        await using var host = await StartedAsync();

        var (error, errorBody) = await GetAsync(host, scheme, h2, "replace", "/missing");
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
        Assert.Contains("<h1>Ours: 404</h1>", errorBody, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream's own", errorBody, StringComparison.Ordinal);

        var (ok, okBody) = await GetAsync(host, scheme, h2, "replace", "/fine");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("the upstream's own page", okBody, StringComparison.Ordinal);

        // The same connection serves the next request after a replaced one: the discarded body left nothing behind.
        using var client = EdgeClient.Create(out _);
        for (var i = 0; i < 3; i++)
        {
            using var again = EdgeClient.Request(host, scheme, "replace.test", i % 2 == 0 ? "/missing" : "/fine", h2 ? HttpVersion.Version20 : HttpVersion.Version11);
            again.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var response = await client.SendAsync(again, Ct);
            Assert.Equal(i % 2 == 0 ? HttpStatusCode.NotFound : HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync(Ct);
        }
    }
}
