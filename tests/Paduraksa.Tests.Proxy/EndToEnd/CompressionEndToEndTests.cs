using System.IO.Compression;
using System.Net;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// Response compression through a real proxy: what is compressed and with which encoding, what never is (the upstream's own
/// encoding, a range, an event stream, an image, a small body, a HEAD, a client that asks for none, a host with it off), the headers
/// that come with it, and the access log's byte count.
/// </summary>
public sealed class CompressionEndToEndTests : IAsyncLifetime
{
    // Repetitive text, so compression always wins and the sizes say so.
    private static readonly string Page = string.Concat(Enumerable.Repeat("<p>Paduraksa compresses this line of text.</p>\n", 200));

    private IHost? _upstream;
    private string _address = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _upstream = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(static app =>
                {
                    app.UseWebSockets();
                    app.Run(Answer);
                }))
            .Build();
        await _upstream.StartAsync(TestContext.Current.CancellationToken);
        _address = Assert.Single(_upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses) + "/";
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.StopAsync(TestContext.Current.CancellationToken);
            _upstream.Dispose();
        }
    }

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("gzip, deflate, br", "br")]
    [InlineData("gzip;q=1, br;q=0.5", "gzip")]
    public async Task TextIsCompressedWithTheEncodingTheClientPrefers(string accept, string expected)
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        using var response = await SendAsync(server, client, "/page", accept);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Contains("Accept-Encoding", response.Headers.Vary);
        Assert.NotEqual(Encoding.UTF8.GetByteCount(Page), response.Content.Headers.ContentLength);
        Assert.True(body.Length < Page.Length / 4, $"{body.Length} bytes compressed from {Page.Length}");
        Assert.Equal(Page, Decode(body, expected));
    }

    [Theory]
    [InlineData("/small")] // declared under the minimum (one without a declared length is compressed)
    [InlineData("/encoded")] // the upstream compressed it itself: passed through
    [InlineData("/range")] // a 206
    [InlineData("/events")] // an event stream must reach the client as written
    [InlineData("/image")] // already compressed
    [InlineData("/untyped")]
    public async Task WhatIsNeverCompressed(string path)
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        using var response = await SendAsync(server, client, path, "gzip, br");

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal(path == "/encoded" ? ["gzip"] : [], response.Content.Headers.ContentEncoding);
        Assert.DoesNotContain("Accept-Encoding", response.Headers.Vary);
    }

    [Fact]
    public async Task AHostWithItOffAClientWithoutAcceptEncodingAndAHeadGetThePlainBody()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        using var off = await SendAsync(server, client, "/page", "gzip, br", host: "plain.test");
        using var none = await SendAsync(server, client, "/page", null);
        using var head = await SendAsync(server, client, "/page", "gzip, br", HttpMethod.Head);

        Assert.Empty(off.Content.Headers.ContentEncoding);
        Assert.Equal(Page, await off.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Empty(none.Content.Headers.ContentEncoding);
        Assert.Equal(Page.Length, none.Content.Headers.ContentLength);
        Assert.Empty(head.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task TheAccessLogCountsTheCompressedBytes()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        using var response = await SendAsync(server, client, "/page", "gzip");
        var sent = (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length;

        AccessLogEntry? entry = null;
        for (var i = 0; i < 400 && entry is null; i++)
        {
            while (server.AccessLog.TryRead(out var read))
            {
                // Only the proxy's own request: a local port prober's GET / is logged too.
                if (read is { Host: "app.test", Path: "/page" })
                {
                    entry = read;
                }
            }

            if (entry is null)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken); // a short poll step inside a bounded wait for the line
            }
        }

        Assert.NotNull(entry);
        Assert.Equal(sent, entry.Value.ResponseBytes);
    }

    [Fact]
    public void TheSimulatorSaysWhatTheHostCompresses()
    {
        var run = GateSimulator.Run(
            Bound(Model()),
            new GateProbe { Host = "app.test", Path = "/page", ClientAddress = IPAddress.Loopback, Headers = [new ProbeHeader("Accept-Encoding", "gzip")] });

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.Upstream);
        Assert.Contains("gzip", Assert.Single(step.Facts, f => f.Name == "compression").Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStageCostsNothingWhenItDoesNothing()
    {
        // Off for the host, and on without Accept-Encoding: straight to the next middleware.
        var snapshot = Bound(Model());
        ResponseCompressionStage stage = new(static _ => Task.CompletedTask, new ServiceCollection().AddLogging().BuildServiceProvider());
        foreach (var (host, accept) in new[] { ("plain.test", "gzip"), ("app.test", (string?)null) })
        {
            var context = TestContexts.Create(host, "/page");
            if (accept is not null)
            {
                context.Request.Headers.AcceptEncoding = accept;
            }

            context.Features.Set<IGateFeature>(new GateRequestState { Host = snapshot.Hosts.Find(host) });
            AllocAssert.Zero((stage, context), static s => s.stage.InvokeAsync(s.context).GetAwaiter().GetResult(), $"the stage for {host}");
        }
    }

    [Fact]
    public async Task AReplacedUpstreamErrorPageIsTheProxysOwnAndIsNotCompressed()
    {
        var model = Model() with
        {
            Hosts =
            [
                ProxyHost("app", "app.test", Route("app-root", "up")) with
                {
                    Compression = new HostCompressionSpec { Enabled = true, MinimumSize = 0 },
                    ErrorPages = new HostErrorPagesSpec { ReplaceUpstream = Paduraksa.Core.Model.UpstreamErrorReplacement.Any },
                },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/broken");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");
        request.Headers.TryAddWithoutValidation("Accept", "text/html");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Contains("<html", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACompressedResponsesETagIsWeakAndStillRevalidates()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();

        using var compressed = await SendAsync(server, client, "/etag", "gzip");
        using var plain = await SendAsync(server, client, "/etag", null);
        var revalidate = server.Request("app.test", "/etag");
        revalidate.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", compressed.Headers.ETag!.ToString());
        using var notModified = await client.SendAsync(revalidate, TestContext.Current.CancellationToken);

        Assert.Equal(["gzip"], compressed.Content.Headers.ContentEncoding);
        Assert.Equal("W/\"v1\"", compressed.Headers.ETag!.ToString());
        Assert.Equal("\"v1\"", plain.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(notModified.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task AWebSocketOverH2cThroughACompressingHostIsLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await ProxyTestServer.StartAsync(Model(), new() { Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2 });
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler { UseProxy = false });
        using System.Net.WebSockets.ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Host", "app.test");
        socket.Options.SetRequestHeader("Accept-Encoding", "gzip, br");
        socket.Options.HttpVersion = HttpVersion.Version20;
        socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.BaseAddress.Port}/ws"), invoker, ct);
        var message = Encoding.UTF8.GetBytes(Page[..2000]);
        await socket.SendAsync(message, System.Net.WebSockets.WebSocketMessageType.Text, endOfMessage: true, ct);
        var echo = new byte[message.Length];
        var got = 0;
        while (got < message.Length)
        {
            got += (await socket.ReceiveAsync(echo.AsMemory(got), ct)).Count;
        }

        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, ct);
        Assert.Equal(message, echo);
    }

    public static TheoryData<string> Bypassed => ["CONNECT", "extended-connect", "upgrade", "HEAD"];

    [Theory]
    [MemberData(nameof(Bypassed))]
    public async Task TheStageNeverWrapsATunnelOrAHead(string kind)
    {
        // An extended CONNECT is how a WebSocket rides HTTP/2 and HTTP/3: the request has no Upgrade header to go by.
        var context = Context("app.test");
        switch (kind)
        {
            case "CONNECT":
                context.Request.Method = HttpMethods.Connect;
                break;
            case "extended-connect":
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpExtendedConnectFeature>(new ExtendedConnect());
                break;
            case "upgrade":
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature>(new Upgradable());
                break;
            default:
                context.Request.Method = HttpMethods.Head;
                break;
        }

        var wrapped = await WrappedAsync(context);

        Assert.False(wrapped, kind);
        Assert.True(await WrappedAsync(Context("app.test")), "a plain GET goes through the middleware");
    }

    [Theory]
    [InlineData(null, null, "gzip", true)]
    [InlineData("no-transform", null, "gzip", false)]
    [InlineData("public, No-Transform, max-age=60", null, "gzip", false)]
    [InlineData("public, max-age=60", null, "gzip", true)]
    [InlineData(null, "DoNotCompress", "gzip", false)]
    [InlineData(null, "Compress", "gzip", true)]
    // The host would compress, but the client takes no encoding the proxy has: the representation is the upstream's, and so are its
    // ETag, Accept-Ranges and digest.
    [InlineData(null, null, "identity", false)]
    [InlineData(null, null, "deflate", false)]
    [InlineData(null, null, "gzip;q=0, br;q=0", false)]
    public async Task NoTransformAndDoNotCompressAreHonoured(string? cacheControl, string? mode, string accept, bool compressed)
    {
        async Task Next(HttpContext http)
        {
            if (mode is not null)
            {
                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpsCompressionFeature>()!.Mode = Enum.Parse<Microsoft.AspNetCore.Http.Features.HttpsCompressionMode>(mode);
            }

            http.Response.ContentType = "text/html";
            http.Response.Headers.ETag = "\"v1\"";
            http.Response.Headers.AcceptRanges = "bytes";
            http.Response.Headers["Content-Digest"] = "sha-256=:abc=:";
            http.Response.Headers["Digest"] = "sha-256=abc=";
            if (cacheControl is not null)
            {
                http.Response.Headers.CacheControl = cacheControl;
            }

            await http.Response.WriteAsync(Page);
        }

        var run = Context("app.test");
        run.Request.Headers.AcceptEncoding = accept;
        await Stage(Next).InvokeAsync(run);
        Assert.Equal(compressed ? "gzip" : string.Empty, run.Response.Headers.ContentEncoding.ToString());
        Assert.Equal(compressed ? "W/\"v1\"" : "\"v1\"", run.Response.Headers.ETag.ToString());
        Assert.Equal(compressed ? string.Empty : "bytes", run.Response.Headers.AcceptRanges.ToString());
        Assert.Equal(!compressed, run.Response.Headers.ContainsKey("Content-Digest"));
        Assert.Equal(!compressed, run.Response.Headers.ContainsKey("Digest"));
    }

    private static ResponseCompressionStage Stage(RequestDelegate? next = null) =>
        new(next ?? (static _ => Task.CompletedTask), new ServiceCollection().AddLogging().BuildServiceProvider());

    private DefaultHttpContext Context(string host)
    {
        DefaultHttpContext context = new();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Host = host;
        context.Request.Headers.AcceptEncoding = "gzip";
        context.Response.Body = new MemoryStream();
        context.Features.Set<IGateFeature>(new GateRequestState { Host = Bound(Model()).Hosts.Find(host) });
        return context;
    }

    private static async Task<bool> WrappedAsync(HttpContext context)
    {
        var wrapped = false;
        await Stage(http =>
        {
            wrapped = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpsCompressionFeature>() is not null;
            return Task.CompletedTask;
        }).InvokeAsync(context);
        return wrapped;
    }

    private sealed class ExtendedConnect : Microsoft.AspNetCore.Http.Features.IHttpExtendedConnectFeature
    {
        public bool IsExtendedConnect => true;

        public string? Protocol => "websocket";

        public ValueTask<Stream> AcceptAsync() => throw new NotSupportedException();
    }

    private sealed class Upgradable : Microsoft.AspNetCore.Http.Features.IHttpUpgradeFeature
    {
        public bool IsUpgradableRequest => true;

        public Task<Stream> UpgradeAsync() => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("text/html; charset=utf-8", true)]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("image/svg+xml", true)]
    [InlineData("application/javascript", true)]
    [InlineData("application/wasm", true)]
    [InlineData("text/event-stream", false)]
    [InlineData("image/png", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("application/grpc", false)]
    [InlineData("", false)]
    public void TheContentTypesThatAreCompressed(string type, bool compressed) => Assert.Equal(compressed, CompressionPolicy.IsCompressible(type));

    private static Task<HttpResponseMessage> SendAsync(ProxyTestServer server, HttpClient client, string path, string? accept, HttpMethod? method = null, string host = "app.test")
    {
        var request = server.Request(host, path, method);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);
        }

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Decode(byte[] body, string encoding)
    {
        using MemoryStream compressed = new(body);
        using Stream decoder = encoding == "br" ? new BrotliStream(compressed, CompressionMode.Decompress) : new GZipStream(compressed, CompressionMode.Decompress);
        using StreamReader reader = new(decoder, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task Answer(HttpContext context)
    {
        var response = context.Response;
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8192];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, CancellationToken.None);
            }
        }

        switch (context.Request.Path.Value)
        {
            case "/broken":
                response.StatusCode = 502;
                response.ContentType = "text/html";
                await response.WriteAsync(Page);
                return;
            case "/etag":
                response.Headers.ETag = "\"v1\"";
                if (context.Request.Headers.IfNoneMatch.ToString() is { Length: > 0 } match && match.Contains("\"v1\"", StringComparison.Ordinal))
                {
                    // A weak comparison, as RFC 9110 has If-None-Match use.
                    response.StatusCode = 304;
                    return;
                }

                response.ContentType = "text/html";
                await response.WriteAsync(Page);
                return;
            case "/small":
                response.ContentType = "text/plain";
                response.ContentLength = 5;
                await response.WriteAsync("short");
                return;
            case "/encoded":
                response.ContentType = "text/html";
                response.Headers.ContentEncoding = "gzip";
                using (MemoryStream buffer = new())
                {
                    await using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        await gzip.WriteAsync(Encoding.UTF8.GetBytes(Page));
                    }

                    await response.Body.WriteAsync(buffer.ToArray());
                }

                return;
            case "/range":
                response.StatusCode = 206;
                response.ContentType = "text/html";
                response.Headers.ContentRange = $"bytes 0-99/{Page.Length}";
                await response.WriteAsync(Page[..100]);
                return;
            case "/events":
                response.ContentType = "text/event-stream";
                await response.WriteAsync(string.Concat(Enumerable.Repeat("data: tick\n\n", 200)));
                return;
            case "/image":
                response.ContentType = "image/png";
                await response.Body.WriteAsync(new byte[4096]);
                return;
            case "/untyped":
                await response.Body.WriteAsync(new byte[4096]);
                return;
            default:
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength = Encoding.UTF8.GetByteCount(Page);
                await response.WriteAsync(Page);
                return;
        }
    }

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("up", _address)],
        Hosts =
        [
            ProxyHost("app", "app.test", Route("app-root", "up")) with { Compression = new HostCompressionSpec { Enabled = true, MinimumSize = 1024 } },
            ProxyHost("plain", "plain.test", Route("plain-root", "up")),
        ],
    };
}
