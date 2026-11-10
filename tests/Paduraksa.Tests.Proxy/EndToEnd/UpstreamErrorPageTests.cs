using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Paduraksa.Core.Model;
using Paduraksa.Proxy;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Paduraksa.Tests.Proxy.Testing;

using Yarp.ReverseProxy.Forwarder;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// YARP's empty error answers through the real host: a refused connection (502), a request that times out (504) and a
/// cluster with no healthy destination (503). The page must be there, the gate's headers with it, and a non-browser
/// client keeps the plain text.
/// </summary>
public sealed class UpstreamErrorPageTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private ProxyConfigModel DeadDestination => new()
    {
        Upstreams = [Upstream("dead", $"http://127.0.0.1:{upstream.DeadPort}/")],
        Hosts = [ProxyHost("dead", "dead.test", Route("dead-root", "dead"))],
    };

    [Fact]
    public async Task ARefusedConnectionGetsTheBadGatewayPageForABrowserAndPlainTextForAClient()
    {
        await using var server = await ProxyTestServer.StartAsync(DeadDestination);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var browser = await client.SendAsync(Request(server, "dead.test", "/x", "text/html"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, browser.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, browser.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", browser.Headers.CacheControl!.ToString());
        Assert.True(browser.Headers.TryGetValues("X-Request-Id", out var ids));
        var page = await browser.Content.ReadAsStringAsync(ct);
        Assert.Contains("<p class=\"status\">502 - Bad gateway</p>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>Bad gateway.</h1>", page, StringComparison.Ordinal);
        Assert.Contains($"Request ID: <code>{Assert.Single(ids)}</code>", page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", browser.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        using var api = await client.SendAsync(Request(server, "dead.test", "/x", "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, api.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", api.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
        Assert.Equal("bad gateway\n", await api.Content.ReadAsStringAsync(ct));

        using var head = await client.SendAsync(Request(server, "dead.test", "/x", "text/html", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.BadGateway, head.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, head.Content.Headers.ContentType!.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task AnUpstreamThatTimesOutGetsTheGatewayTimeoutPage()
    {
        ProxyTestServerOptions options = new()
        {
            Services = static services =>
            {
                services.RemoveAll<IForwarderHttpClientFactory>();
                services.AddSingleton<IForwarderHttpClientFactory>(new TimingOutForwarderFactory());
            },
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("slow", upstream.Address("slow"))],
            Hosts = [ProxyHost("slow", "slow.test", Route("slow-root", "slow"))],
        };

        await using var server = await ProxyTestServer.StartAsync(model, options);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var browser = await client.SendAsync(Request(server, "slow.test", "/x", "text/html"), ct);
        Assert.Equal(HttpStatusCode.GatewayTimeout, browser.StatusCode);
        var page = await browser.Content.ReadAsStringAsync(ct);
        Assert.Contains("<p class=\"status\">504 - Gateway timeout</p>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>Gateway timeout.</h1>", page, StringComparison.Ordinal);
        Assert.Contains("Request ID: <code>", page, StringComparison.Ordinal);

        using var api = await client.SendAsync(Request(server, "slow.test", "/x", "text/plain"), ct);
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
        Assert.Equal("gateway timeout\n", await api.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task AClusterWithNoAvailableDestinationGetsTheServiceUnavailablePage()
    {
        // YARP answers 503 before it calls the forwarder, so a substituted forwarder is the only way to produce the
        // feature and status a real empty cluster produces without an empty cluster config (which the compiler refuses).
        ProxyTestServerOptions options = new()
        {
            Services = static services =>
            {
                services.RemoveAll<IHttpForwarder>();
                services.AddSingleton<IHttpForwarder>(new NoDestinationsForwarder());
            },
        };
        await using var server = await ProxyTestServer.StartAsync(DeadDestination, options);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var browser = await client.SendAsync(Request(server, "dead.test", "/x", "text/html"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, browser.StatusCode);
        var page = await browser.Content.ReadAsStringAsync(ct);
        Assert.Contains("<p class=\"status\">503 - Service unavailable</p>", page, StringComparison.Ordinal);
        Assert.Contains("<h1>Service unavailable.</h1>", page, StringComparison.Ordinal);
        Assert.Contains("Request ID: <code>", page, StringComparison.Ordinal);

        using var api = await client.SendAsync(Request(server, "dead.test", "/x", "application/json"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
        Assert.Equal("service unavailable\n", await api.Content.ReadAsStringAsync(ct));

        using var head = await client.SendAsync(Request(server, "dead.test", "/x", "text/html", HttpMethod.Head), ct);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task AResponseThatAlreadyStartedIsNotRewritten()
    {
        var context = TestContexts.Create("dead.test");
        var responseFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!;
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponseFeature { Headers = responseFeature.Headers });
        context.Response.StatusCode = 502;
        var body = new MemoryStream();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new Microsoft.AspNetCore.Http.StreamResponseBodyFeature(body));

        await UpstreamErrors.WriteAsync(context);

        Assert.Empty(body.ToArray());
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    [Fact]
    public async Task TwoRequestsOnOneConnectionSurviveTheErrorPage()
    {
        await using var server = await ProxyTestServer.StartAsync(DeadDestination);

        var raw = await SendRawAsync(
            server,
            "GET /x HTTP/1.1\r\nHost: dead.test\r\nAccept: text/html\r\n\r\n" +
            "GET /y HTTP/1.1\r\nHost: dead.test\r\nAccept: text/html\r\nConnection: close\r\n\r\n");

        var first = raw.IndexOf("HTTP/1.1 502", StringComparison.Ordinal);
        var second = first < 0 ? -1 : raw.IndexOf("HTTP/1.1 502", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0, raw);
        Assert.True(second >= 0, "the second request on the keep-alive connection went unanswered:" + raw);
        Assert.Equal(2, raw.Split("<h1>Bad gateway.</h1>", StringSplitOptions.None).Length - 1);
        Assert.EndsWith("0\r\n\r\n", raw, StringComparison.Ordinal);
        Assert.Contains("connection: close", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HeadThenGetOnOneConnectionAreBothAnswered()
    {
        await using var server = await ProxyTestServer.StartAsync(DeadDestination);

        var raw = await SendRawAsync(
            server,
            "HEAD /x HTTP/1.1\r\nHost: dead.test\r\nAccept: text/html\r\n\r\n" +
            "GET /y HTTP/1.1\r\nHost: dead.test\r\nAccept: text/html\r\nConnection: close\r\n\r\n");

        var first = raw.IndexOf("HTTP/1.1 502", StringComparison.Ordinal);
        var second = first < 0 ? -1 : raw.IndexOf("HTTP/1.1 502", first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0, raw);
        Assert.True(second >= 0, "the GET after the HEAD error went unanswered:" + raw);

        var firstHeadersEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(firstHeadersEnd >= 0 && firstHeadersEnd < second, raw);
        Assert.DoesNotContain("<h1>", raw[(firstHeadersEnd + 4)..second], StringComparison.Ordinal);
        Assert.Contains("<h1>Bad gateway.</h1>", raw[second..], StringComparison.Ordinal);
        Assert.EndsWith("0\r\n\r\n", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http2GetsTheBadGatewayPage()
    {
        await using var server = await ProxyTestServer.StartAsync(DeadDestination, new() { Protocols = HttpProtocols.Http2 });
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "dead.test", "/x", "text/html"), ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Contains("<h1>Bad gateway.</h1>", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http2HeadGetsNoBody()
    {
        await using var server = await ProxyTestServer.StartAsync(DeadDestination, new() { Protocols = HttpProtocols.Http2 });
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var response = await client.SendAsync(Request(server, "dead.test", "/x", "text/html", HttpMethod.Head), ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(ct));
    }

    [Fact]
    public async Task ALyingUpstreamGetsThePageAndItsCopiedHeadersAreGone()
    {
        await using var lying = LyingUpstream.Start();
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("lying", $"http://127.0.0.1:{lying.Port}/")],
            Hosts = [ProxyHost("lying", "lying.test", Route("lying-root", "lying"))],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var browser = await client.SendAsync(Request(server, "lying.test", "/x", "text/html"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, browser.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, browser.Content.Headers.ContentType!.ToString());
        Assert.Equal("no-store", browser.Headers.CacheControl!.ToString());
        Assert.NotEqual(5000L, browser.Content.Headers.ContentLength);
        Assert.False(browser.Headers.Contains("X-Upstream"));
        Assert.Contains("<h1>Bad gateway.</h1>", await browser.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        using var api = await client.SendAsync(Request(server, "lying.test", "/x", "application/json"), ct);
        Assert.Equal(HttpStatusCode.BadGateway, api.StatusCode);
        Assert.Equal("no-store", api.Headers.CacheControl!.ToString());
        Assert.Equal("bad gateway\n", await api.Content.ReadAsStringAsync(ct));
        Assert.False(api.Headers.Contains("X-Upstream"));
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status408RequestTimeout)]
    public async Task RequestBodyErrorStatusesKeepYarpsPlainAnswer(int status)
    {
        var context = TestContexts.Create("dead.test");
        context.Response.StatusCode = status;
        var body = new MemoryStream();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>(
            new Microsoft.AspNetCore.Http.StreamResponseBodyFeature(body));

        await UpstreamErrors.WriteAsync(context);

        Assert.Empty(body.ToArray());
        Assert.Equal(status, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Content-Type"));
        Assert.False(context.Response.Headers.ContainsKey("Content-Security-Policy"));
    }

    /// <summary>
    /// A raw H1 exchange: pipelined requests on one connection, read until the proxy closes it. HttpClient can't show
    /// the framing the page's chunked body depends on.
    /// </summary>
    private static async Task<string> SendRawAsync(ProxyTestServer server, string requests)
    {
        var ct = TestContext.Current.CancellationToken;
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, server.BaseAddress.Port, ct);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(requests), ct);

        using MemoryStream received = new();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await stream.CopyToAsync(received, timeout.Token);
        return Encoding.UTF8.GetString(received.ToArray());
    }

    /// <summary>An upstream that sends a 5000-byte length and headers, then drops the connection before any body.</summary>
    private sealed class LyingUpstream : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private Task _serving = Task.CompletedTask;

        public int Port { get; private set; }

        public static LyingUpstream Start()
        {
            var upstream = new LyingUpstream();
            upstream._listener.Start();
            upstream.Port = ((IPEndPoint)upstream._listener.LocalEndpoint).Port;
            upstream._serving = upstream.ServeAsync(upstream._cts.Token);
            return upstream;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _serving;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }

                _ = HandleAsync(client, ct);
            }
        }

        private static async Task HandleAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                await using var stream = client.GetStream();
                var headers = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 502 Lying\r\nContent-Length: 5000\r\nX-Upstream: yes\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, ct);
            }
            catch (IOException)
            {
            }
            finally
            {
                client.Dispose();
            }
        }
    }

    /// <summary>
    /// A connect attempt that reports cancellation, which is what a timeout looks like to the forwarder. The real
    /// handler would wait out the proxy's activity timeout; only the transport is substituted, the forwarder and its
    /// error feature are YARP's own.
    /// </summary>
    private sealed class TimingOutForwarderFactory : ForwarderHttpClientFactory
    {
        protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
        {
            base.ConfigureHandler(context, handler);
            // The connect never completes; the handler's own connect timeout cancels it, which is the cancellation a
            // real timeout surfaces to the forwarder.
            handler.ConnectTimeout = TimeSpan.FromMilliseconds(100);
            handler.ConnectCallback = static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Stream.Null;
            };
        }
    }

    /// <summary>Produces what <c>ForwarderMiddleware</c> does when its available-destination list is empty.</summary>
    private sealed class NoDestinationsForwarder : IHttpForwarder
    {
        public ValueTask<ForwarderError> SendAsync(
            HttpContext context, string destinationPrefix, HttpMessageInvoker httpClient, ForwarderRequestConfig requestConfig, HttpTransformer transformer)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Features.Set<IForwarderErrorFeature>(new NoDestinationsError());
            return new ValueTask<ForwarderError>(ForwarderError.NoAvailableDestinations);
        }
    }

    private sealed class NoDestinationsError : IForwarderErrorFeature
    {
        public ForwarderError Error => ForwarderError.NoAvailableDestinations;

        public Exception? Exception => null;
    }

    private static HttpRequestMessage Request(ProxyTestServer server, string host, string path, string accept, HttpMethod? method = null)
    {
        var request = server.Request(host, path, method);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        return request;
    }

    private sealed class StartedResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        public override bool HasStarted => true;
    }
}
