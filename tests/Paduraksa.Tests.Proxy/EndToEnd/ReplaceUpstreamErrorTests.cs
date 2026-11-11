using System.Net;
using System.Net.Http;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// A host that asked for it answers with its own error page in place of an upstream's error, decided from the status and content type when
/// the upstream's response arrives; the upstream's body is never read. Through the real host, so YARP's own path is what is exercised.
/// </summary>
public sealed class ReplaceUpstreamErrorTests(ErrorUpstream upstream) : IClassFixture<ErrorUpstream>
{
    private static readonly ErrorPageSpec Custom404 = new() { Heading = "Lost: {status}", Text = "{host} has no such page.", Source = ErrorPageSource.Host };

    private ProxyConfigModel Config => new()
    {
        Upstreams = [Upstream("err", upstream.Address)],
        Hosts =
        [
            ProxyHost("html", "html.test", Route("html-root", "err")) with { ErrorPages = Replace(UpstreamErrorReplacement.Html) },
            ProxyHost("any", "any.test", Route("any-root", "err")) with { ErrorPages = Replace(UpstreamErrorReplacement.Any) },
            ProxyHost("off", "off.test", Route("off-root", "err")) with { ErrorPages = Replace(UpstreamErrorReplacement.Off) },
        ],
    };

    private static HostErrorPagesSpec Replace(UpstreamErrorReplacement mode) => new()
    {
        ReplaceUpstream = mode,
        Pages = new Dictionary<int, ErrorPageSpec> { [404] = Custom404 },
    };

    [Fact]
    public async Task AnUpstreamsHtmlErrorPageGivesWayToTheHostsOwn()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // 404 has a page of the host's own; 403 falls back to the built-in one.
        using var custom = await client.SendAsync(Request(server, "html.test", "/status/404?type=text/html", "text/html"), ct);
        Assert.Equal(HttpStatusCode.NotFound, custom.StatusCode);
        var page = await custom.Content.ReadAsStringAsync(ct);
        Assert.Contains("<h1>Lost: 404</h1>", page, StringComparison.Ordinal);
        Assert.Contains("<p>html.test has no such page.</p>", page, StringComparison.Ordinal);
        Assert.DoesNotContain("upstream's own", page, StringComparison.Ordinal);
        Assert.Equal("text/html; charset=utf-8", custom.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", custom.Headers.CacheControl!.ToString());
        Assert.True(custom.Headers.Contains("X-Request-Id"));
        Assert.Contains("Request ID: <code>" + custom.Headers.GetValues("X-Request-Id").Single(), page, StringComparison.Ordinal);

        using var builtIn = await client.SendAsync(Request(server, "html.test", "/status/403?type=text/html", "text/html"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, builtIn.StatusCode);
        Assert.Contains("<h1>403 Forbidden</h1>", await builtIn.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // A client that doesn't ask for HTML gets the plain line, as for every other answer of the proxy.
        using var plain = await client.SendAsync(Request(server, "html.test", "/status/404?type=text/html", "application/json"), ct);
        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", plain.Content.Headers.ContentType!.ToString());
        Assert.Equal("not found\n", await plain.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task TheBodyHeadersGoAndTheRestStay()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "html.test", "/status/503?type=text/html&retry=30", "text/html"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // Cookies and Retry-After are the upstream's to send; the body's own headers describe bytes that are not.
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Contains("s=1", response.Headers.GetValues("Set-Cookie").Single(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("ETag"));
        Assert.Null(response.Content.Headers.LastModified);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.DoesNotContain("upstream's own", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadGetsTheHeadersAndNoBody()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var head = await client.SendAsync(Request(server, "html.test", "/status/404?type=text/html", "text/html", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        Assert.Equal("text/html; charset=utf-8", head.Content.Headers.ContentType!.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task OnlyAnHtmlErrorIsReplacedInHtmlModeAndAnyErrorInAnyMode()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        // A JSON error of an API is the client's to read: html mode leaves it, any mode answers with the page.
        using var json = await client.SendAsync(Request(server, "html.test", "/status/403?type=application/json", "text/html"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, json.StatusCode);
        Assert.Equal("application/json", json.Content.Headers.ContentType!.MediaType);
        Assert.Contains("upstream's own", await json.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        using var any = await client.SendAsync(Request(server, "any.test", "/status/403?type=application/json", "text/html"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, any.StatusCode);
        Assert.Equal("text/html; charset=utf-8", any.Content.Headers.ContentType!.ToString());
        Assert.Contains("<h1>403 Forbidden</h1>", await any.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        // No content type at all is "any" too.
        using var bare = await client.SendAsync(Request(server, "any.test", "/status/502?type=", "text/html"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, bare.StatusCode);
        Assert.Contains("<h1>502 Bad Gateway</h1>", await bare.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatDidNotAskNeverHasItsUpstreamsErrorsTouched()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "off.test", "/status/404?type=text/html", "text/html"), ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("upstream's own", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/status/500?type=text/html")]
    [InlineData("/status/401?type=text/html")]
    [InlineData("/status/400?type=text/html")]
    [InlineData("/status/200?type=text/html")]
    [InlineData("/status/301?type=text/html")]
    public async Task OnlyAStatusThatHasAPageIsReplaced(string path)
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "any.test", path, "text/html"), ct);
        Assert.Contains("upstream's own", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/status/503?type=text/event-stream")]
    [InlineData("/status/503?type=application/grpc")]
    [InlineData("/status/503?type=application/grpc%2Bproto")]
    public async Task AnEventStreamAndGrpcAreNeverReplacedWhateverTheMode(string path)
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "any.test", path, "text/html"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("upstream's own", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpgradeRequestKeepsTheUpstreamsAnswer()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var request = Request(server, "any.test", "/status/403?type=text/html", "text/html");
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("upstream's own", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheUpstreamsBodyIsDiscardedUnreadNotBufferedOrDrained()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        upstream.ResetHuge();

        // The upstream would send 2 GiB of error page; the answer comes back at once and the upstream is cut off long before the end.
        using var response = await client.SendAsync(Request(server, "html.test", "/huge", "text/html"), ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var page = await response.Content.ReadAsStringAsync(ct);
        Assert.True(page.Length < 4096, $"the page is {page.Length} chars: the upstream's body leaked into it");
        Assert.Contains("<h1>Lost: 404</h1>", page, StringComparison.Ordinal);

        Assert.True(await upstream.HugeWasCutOffAsync(ct), "the upstream was allowed to send its whole body");
        Assert.True(upstream.HugeBytesWritten < 512L * 1024 * 1024, $"the upstream wrote {upstream.HugeBytesWritten} bytes before it was stopped");
    }

    [Fact]
    public async Task ASuccessfulResponseAndALaterGoodRequestOnTheSameConnectionAreUntouched()
    {
        await using var server = await ProxyTestServer.StartAsync(Config);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            using var error = await client.SendAsync(Request(server, "html.test", "/status/404?type=text/html", "text/html"), ct);
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            await error.Content.LoadIntoBufferAsync(ct);

            using var ok = await client.SendAsync(Request(server, "html.test", "/status/200?type=text/html", "text/html"), ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains("upstream's own", await ok.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    private static HttpRequestMessage Request(ProxyTestServer server, string host, string path, string accept, HttpMethod? method = null)
    {
        var request = server.Request(host, path, method);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        return request;
    }
}

/// <summary>An upstream that answers any status and content type on request, with a body of its own and an ETag, a cookie and a modification time.</summary>
public sealed class ErrorUpstream : IAsyncLifetime
{
    private IHost? _host;
    private long _hugeBytes;
    private int _hugeCutOff;

    public string Address { get; private set; } = string.Empty;

    public long HugeBytesWritten => Interlocked.Read(ref _hugeBytes);

    public void ResetHuge()
    {
        Interlocked.Exchange(ref _hugeBytes, 0);
        Interlocked.Exchange(ref _hugeCutOff, 0);
    }

    public async Task<bool> HugeWasCutOffAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 100 && Volatile.Read(ref _hugeCutOff) == 0; i++)
        {
            await Task.Delay(50, cancellationToken);
        }

        return Volatile.Read(ref _hugeCutOff) == 1;
    }

    public async ValueTask InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(HandleAsync)))
            .Build();
        await _host.StartAsync();
        var address = Assert.Single(_host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Address = address + "/";
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        var parts = context.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var response = context.Response;
        if (parts is ["huge"])
        {
            response.StatusCode = 404;
            response.ContentType = "text/html";
            var chunk = new byte[64 * 1024];
            Array.Fill(chunk, (byte)'x');
            try
            {
                for (var i = 0; i < 32_768; i++)
                {
                    await response.Body.WriteAsync(chunk, context.RequestAborted);
                    await response.Body.FlushAsync(context.RequestAborted);
                    Interlocked.Add(ref _hugeBytes, chunk.Length);
                }
            }
            catch (OperationCanceledException)
            {
                Volatile.Write(ref _hugeCutOff, 1);
            }
            catch (IOException)
            {
                Volatile.Write(ref _hugeCutOff, 1);
            }

            return;
        }

        var status = parts is ["status", var code] ? int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) : 200;
        var query = context.Request.Query;
        response.StatusCode = status;
        if (!query.TryGetValue("type", out var type) || type.Count > 0 && type[0]!.Length > 0)
        {
            response.ContentType = query.TryGetValue("type", out var chosen) ? chosen.ToString() : "text/html";
        }

        response.Headers.ETag = "\"v1\"";
        response.Headers.LastModified = "Wed, 21 Oct 2015 07:28:00 GMT";
        response.Headers.SetCookie = "s=1; Path=/";
        if (query.TryGetValue("retry", out var retry))
        {
            response.Headers.RetryAfter = retry.ToString();
        }

        if (status is 204 or 304)
        {
            return;
        }

        await response.Body.WriteAsync(Encoding.UTF8.GetBytes("<h1>upstream's own page</h1>"), context.RequestAborted);
    }
}
