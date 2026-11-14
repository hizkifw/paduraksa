using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Metrics;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The body bytes a request really moved, on a real Kestrel and YARP over HTTP/1.1 and h2c: a chunked body, a declared one, an
/// upstream's gzip (counted compressed), an upload, a page the gate writes itself, a client that leaves mid-body and a WebSocket, each as the
/// access log's line and the host's and route's counters see it; and the counter at 0 B a request with bodies both ways.
/// </summary>
public sealed class BodyBytesTests(BodyUpstream upstream) : IClassFixture<BodyUpstream>
{
    private const int Size = 100_000;
    private const int LongBody = 64 * 1024 * 1024;
    private const int ReadBeforeLeaving = 256 * 1024;
    private const int Connections = 4;
    private const int Warmup = 500;
    private const int Measured = 1_500;
    private const int SmallBody = 4_096;
    private const int Message = 1_000;

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", upstream.Address)],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "web"))],
    };

    [Theory]
    [InlineData(false, "/chunked")]
    [InlineData(true, "/chunked")]
    [InlineData(false, "/length")]
    [InlineData(true, "/length")]
    public async Task AResponseBodyIsCountedExactly(bool h2c, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await StartAsync(h2c, registry);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using var response = await client.SendAsync(server.Request("app.test", $"{path}?n={Size}"), ct);
        // Before the body is buffered, which would compute a length.
        Assert.Equal(path == "/length", response.Content.Headers.Contains("Content-Length"));
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Size, body.Length);

        var entry = await SingleEntryAsync(server);
        Assert.Equal(Size, entry.ResponseBytes);
        Assert.Equal(0, entry.RequestBytes);
        await AssertCountedAsync(server, registry, bytesOut: Size, bytesIn: 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUpstreamsGzipCountsTheCompressedBytes(bool h2c)
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await StartAsync(h2c, registry);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using var response = await client.SendAsync(server.Request("app.test", $"/gzip?n={Size}"), ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(upstream.Gzipped(Size).Length, body.Length);
        Assert.True(body.Length < Size / 10, $"the test body should compress well, was {body.Length} B");

        var entry = await SingleEntryAsync(server);
        Assert.Equal(body.Length, entry.ResponseBytes);
        await AssertCountedAsync(server, registry, bytesOut: body.Length, bytesIn: 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnUploadIsCountedAsBytesIn(bool h2c, bool chunked)
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await StartAsync(h2c, registry);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        var request = server.Request("app.test", "/upload", HttpMethod.Post);
        request.Content = chunked ? new StreamContent(new UnknownLengthStream(new byte[Size])) : new ByteArrayContent(new byte[Size]);
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Size.ToString(CultureInfo.InvariantCulture), text);

        var entry = await SingleEntryAsync(server);
        Assert.Equal(Size, entry.RequestBytes);
        Assert.Equal(text.Length, entry.ResponseBytes);
        await AssertCountedAsync(server, registry, bytesOut: text.Length, bytesIn: Size);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnAnswerTheGateWritesItselfIsCounted(bool h2c, bool html)
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await StartAsync(h2c, registry);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        var request = server.Request("unknown.test", "/");
        if (html)
        {
            // The page path: no Content-Length, streamed from its template.
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        }

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEmpty(body);

        var entry = await SingleEntryAsync(server);
        Assert.Equal(body.Length, entry.ResponseBytes);
        await WaitForAsync(() => Counters(registry, MetricsRegistry.UnmatchedHost.Index)[RequestMetric.BytesOut] >= body.Length, "the unmatched host's bytes are counted");
        Assert.Equal(body.Length, Counters(registry, MetricsRegistry.UnmatchedHost.Index)[RequestMetric.BytesOut]);
    }

    [Fact]
    public async Task AHeadAnswerCountsNoBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(h2c: false, new MetricsRegistry(processors: 2));
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using var response = await client.SendAsync(server.Request("unknown.test", "/", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0, "a HEAD answer declares the length it would have sent");

        Assert.Equal(0, (await SingleEntryAsync(server)).ResponseBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AClientThatLeavesMidBodyCountsWhatWasWritten(bool h2c)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var server = await StartAsync(h2c, new MetricsRegistry(processors: 2));
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        using (var response = await client.SendAsync(server.Request("app.test", $"/chunked?n={LongBody}"), HttpCompletionOption.ResponseHeadersRead, ct))
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            var got = 0;
            while (got < ReadBeforeLeaving)
            {
                var n = await body.ReadAsync(buffer, ct);
                Assert.NotEqual(0, n);
                got += n;
            }
        }

        // Disposing the response mid-body resets the stream (h2c) or closes the connection (H1).
        var entry = await SingleEntryAsync(server);
        Assert.True(entry.ResponseBytes >= ReadBeforeLeaving, $"counted {entry.ResponseBytes} B, the client read {ReadBeforeLeaving} B");
        // What went out before the proxy noticed: the client's and the server's buffers' worth, never the whole body.
        Assert.True(entry.ResponseBytes < LongBody / 2, $"counted {entry.ResponseBytes} B of a {LongBody} B body the client left early");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWebSocketsFramesAreCountedBothWays(bool h2c)
    {
        var ct = TestContext.Current.CancellationToken;
        MetricsRegistry registry = new(processors: 2);
        await using var server = await StartAsync(h2c, registry);
        using HttpMessageInvoker invoker = new(new SocketsHttpHandler { UseProxy = false });
        using ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Host", "app.test");
        if (h2c)
        {
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.BaseAddress.Port}/ws"), invoker, ct);
        await socket.SendAsync(new byte[Message], WebSocketMessageType.Binary, endOfMessage: true, ct);
        var echo = new byte[Message * 2];
        var got = 0;
        while (got < Message)
        {
            var received = await socket.ReceiveAsync(echo.AsMemory(got), ct);
            got += received.Count;
        }

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);

        // The frames as they went over the wire: the message, a few bytes of framing (the client's masked), and the closing handshake.
        var entry = await SingleEntryAsync(server);
        Assert.InRange(entry.RequestBytes, Message, Message + 64);
        Assert.InRange(entry.ResponseBytes, Message, Message + 64);
        var app = server.Store.Current.Hosts.Find("app.test", out _)!;
        await WaitForAsync(() => Counters(registry, app.MetricSlot.Index)[RequestMetric.BytesIn] == entry.RequestBytes, "the tunnel's bytes are counted");
        Assert.Equal(entry.ResponseBytes, Counters(registry, app.MetricSlot.Index)[RequestMetric.BytesOut]);
    }

    /// <summary>
    /// The counter is re-pointed every request because Kestrel resets the body and upgrade features between requests on a
    /// connection. Pinned on one HTTP/1.1 connection: an upgradable request answered normally, a plain one, an upgradable one again.
    /// </summary>
    [Fact]
    public async Task EachRequestOnAConnectionIsCountedOnItsOwnFeatures()
    {
        var ct = TestContext.Current.CancellationToken;
        List<(string? Connection, bool StandsIn, bool Upgradable)> seen = [];
        await using var server = await ProxyTestServer.StartAsync(Model(), new()
        {
            Metrics = new MetricsRegistry(processors: 2),
            AfterGate = (context, next) =>
            {
                var upgrade = context.Features.Get<IHttpUpgradeFeature>();
                lock (seen)
                {
                    seen.Add((context.Connection.Id, upgrade is BodyCounter, upgrade?.IsUpgradableRequest ?? false));
                }

                return next(context);
            },
        });
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, server.BaseAddress.Port, ct);
        var stream = tcp.GetStream();
        const string WebSocketHeaders = "Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n";

        var first = await ExchangeAsync(stream, "/length?n=100", WebSocketHeaders, ct);
        var second = await ExchangeAsync(stream, "/length?n=200", string.Empty, ct);
        var third = await ExchangeAsync(stream, "/length?n=300", WebSocketHeaders, ct);

        Assert.Equal(("yes", 100), first);
        Assert.Equal(("no", 200), second);
        Assert.Equal(("yes", 300), third);
        Assert.Equal(3, seen.Count);
        Assert.Single(seen.Select(static s => s.Connection).Distinct());
        Assert.Equal([(true, true), (false, false), (true, true)], seen.Select(static s => (s.StandsIn, s.Upgradable)));
        await WaitForAsync(() => server.AccessLog.EntriesWritten >= 3, "the three lines are queued");
        foreach (var expected in new long[] { 100, 200, 300 })
        {
            Assert.True(server.AccessLog.TryRead(out var entry));
            Assert.Equal(expected, entry.ResponseBytes);
            Assert.Equal(0, entry.RequestBytes);
        }
    }

    // One request and its answer, by hand: HttpClient won't send an upgrade's headers on a request it then reuses the connection for.
    private static async Task<(string? WebSocketRequest, int BodyLength)> ExchangeAsync(NetworkStream stream, string target, string headers, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: app.test\r\n{headers}\r\n"), ct);
        List<byte> head = [];
        var one = new byte[1];
        while (head.Count < 4 || head[^4] != '\r' || head[^3] != '\n' || head[^2] != '\r' || head[^1] != '\n')
        {
            Assert.Equal(1, await stream.ReadAsync(one, ct));
            head.Add(one[0]);
        }

        var lines = Encoding.ASCII.GetString([.. head]).Split("\r\n");
        Assert.StartsWith("HTTP/1.1 200", lines[0], StringComparison.Ordinal);
        string? Header(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();
        var length = int.Parse(Header("Content-Length")!, CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (Header("x-websocket-request"), length);
    }

    /// <summary>
    /// The counter on Kestrel's pooled state: with bodies both ways (a chunked and a declared response, an upload), the access
    /// log's start (the counter's set-up and the connection's timings included) and the gate's decision stay at 0 B a request, and
    /// every byte is counted. An upload times its body reads too.
    /// </summary>
    [Theory]
    [InlineData("h1", "/chunked")]
    [InlineData("h2c", "/chunked")]
    [InlineData("h1", "/length")]
    [InlineData("h2c", "/length")]
    [InlineData("h1", "/upload")]
    [InlineData("h2c", "/upload")]
    [InlineData("h1-tls", "/upload")]
    [InlineData("h2-tls", "/upload")]
    [InlineData("h2-tls", "/chunked")]
    public async Task CountingAllocatesNothingOnKestrel(string transport, string path)
    {
        MetricsRegistry registry = new(processors: 2);
        using var certificate = transport.EndsWith("-tls", StringComparison.Ordinal) ? TestCertificates.SelfSigned() : null;
        await using var server = await ProxyTestServer.StartAsync(
            Model(),
            new()
            {
                MeasureGate = true,
                Metrics = registry,
                Protocols = transport.StartsWith("h2", StringComparison.Ordinal) ? HttpProtocols.Http2 : HttpProtocols.Http1,
                Certificate = certificate,
            });
        using var client = ProxyTestServer.CreateClient(maxConnections: Connections);
        var upload = path == "/upload";

        await SendAsync(server, client, path, SmallBody, Warmup, Connections);
        server.GateStats!.Reset();
        await SendAsync(server, client, path, SmallBody, Measured, Connections);

        Assert.True(server.GateStats.Requests >= Measured, $"only {server.GateStats.Requests} requests measured");
        // One request in the run may carry a one-off of the runtime's own on that thread (see AccessLogAllocationTests).
        Assert.True(server.GateStats.NonZero <= 1, $"the counted request allocated on Kestrel: {server.GateStats}");
        var expected = (long)(Warmup + Measured) * SmallBody;
        var app = server.Store.Current.Hosts.Find("app.test", out _)!;
        await WaitForAsync(() => Counters(registry, app.MetricSlot.Index)[upload ? RequestMetric.BytesIn : RequestMetric.BytesOut] >= expected, "every request's bytes are counted");
        Assert.Equal(expected, Counters(registry, app.MetricSlot.Index)[upload ? RequestMetric.BytesIn : RequestMetric.BytesOut]);
    }

    private Task<ProxyTestServer> StartAsync(bool h2c, MetricsRegistry registry) =>
        ProxyTestServer.StartAsync(Model(), new() { Metrics = registry, Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1 });

    private static async Task SendAsync(ProxyTestServer server, HttpClient client, string path, int size, int count, int connections)
    {
        var ct = TestContext.Current.CancellationToken;
        var payload = new byte[size];
        var next = 0;
        var workers = new Task[connections];
        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= count)
                    {
                        var upload = path == "/upload";
                        using var request = server.Request("app.test", upload ? path : $"{path}?n={size}", upload ? HttpMethod.Post : null);
                        if (upload)
                        {
                            request.Content = new ByteArrayContent(payload);
                        }

                        using var response = await client.SendAsync(request, ct);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        await response.Content.LoadIntoBufferAsync(ct);
                    }
                },
                ct);
        }

        await Task.WhenAll(workers);
    }

    private static async Task<AccessLogEntry> SingleEntryAsync(ProxyTestServer server)
    {
        await WaitForAsync(() => server.AccessLog.EntriesWritten >= 1, "the request's line is queued");
        Assert.True(server.AccessLog.TryRead(out var entry));
        Assert.Equal(1, server.AccessLog.EntriesWritten);
        return entry;
    }

    private static async Task AssertCountedAsync(ProxyTestServer server, MetricsRegistry registry, long bytesOut, long bytesIn)
    {
        var app = server.Store.Current.Hosts.Find("app.test", out _)!;
        await WaitForAsync(() => Counters(registry, app.MetricSlot.Index)[RequestMetric.Status + 1] >= 1, "the request is counted");
        var host = Counters(registry, app.MetricSlot.Index);
        Assert.Equal(bytesOut, host[RequestMetric.BytesOut]);
        Assert.Equal(bytesIn, host[RequestMetric.BytesIn]);
        var route = new long[RequestMetric.Stride];
        registry.ReadCounters(MetricSubject.Route, app.Routes[0].MetricSlot.Index, route);
        Assert.Equal(bytesOut, route[RequestMetric.BytesOut]);
        Assert.Equal(bytesIn, route[RequestMetric.BytesIn]);
        var web = new long[UpstreamMetric.Stride];
        registry.ReadCounters(MetricSubject.Upstream, app.Routes[0].Upstream!.Slot.Index, web);
        Assert.Equal(bytesOut, web[UpstreamMetric.BytesOut]);
        Assert.Equal(bytesIn, web[UpstreamMetric.BytesIn]);
    }

    private static long[] Counters(MetricsRegistry registry, int hostSlot)
    {
        var totals = new long[RequestMetric.Stride];
        registry.ReadCounters(MetricSubject.Host, hostSlot, totals);
        return totals;
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            // Poll step of a wait bounded to ten seconds.
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), what);
    }

    /// <summary>A body whose length the client can't tell, so HTTP/1.1 sends it chunked and HTTP/2 without a length.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

/// <summary>
/// An upstream with bodies of a known size: <c>/chunked?n=</c> streams n bytes in 8 KiB writes with no length, <c>/length?n=</c>
/// declares them, <c>/gzip?n=</c> sends n bytes gzip-compressed with no length, <c>/upload</c> answers with how many request
/// body bytes it read, and a WebSocket is echoed.
/// </summary>
public sealed class BodyUpstream : IAsyncLifetime
{
    private readonly Dictionary<int, byte[]> _gzipped = [];
    private IHost? _host;

    public string Address { get; private set; } = string.Empty;

    /// <summary>What <c>/gzip?n=</c> sends.</summary>
    public byte[] Gzipped(int size)
    {
        lock (_gzipped)
        {
            if (!_gzipped.TryGetValue(size, out var compressed))
            {
                using MemoryStream output = new();
                using (GZipStream gzip = new(output, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(new byte[size]);
                }

                _gzipped[size] = compressed = output.ToArray();
            }

            return compressed;
        }
    }

    public async ValueTask InitializeAsync()
    {
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, 0);
                    kestrel.Limits.MaxRequestBodySize = null;
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(context => HandleAsync(context));
                }))
            .Build();
        await _host.StartAsync();
        var address = Assert.Single(_host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        Address = $"http://127.0.0.1:{new Uri(address).Port}/";
    }

    private async Task HandleAsync(HttpContext context)
    {
        var size = int.TryParse(context.Request.Query["n"], CultureInfo.InvariantCulture, out var n) ? n : 0;
        var path = context.Request.Path.Value ?? string.Empty;
        var ct = context.RequestAborted;
        context.Response.Headers["x-websocket-request"] = context.WebSockets.IsWebSocketRequest ? "yes" : "no";
        if (context.WebSockets.IsWebSocketRequest && path.EndsWith("/ws", StringComparison.Ordinal))
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, ct);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, ct);
            }
        }

        if (path.EndsWith("/upload", StringComparison.Ordinal))
        {
            var buffer = new byte[16 * 1024];
            long read = 0;
            int got;
            while ((got = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                read += got;
            }

            await context.Response.WriteAsync(read.ToString(CultureInfo.InvariantCulture), ct);
            return;
        }

        if (path.EndsWith("/gzip", StringComparison.Ordinal))
        {
            context.Response.Headers.ContentEncoding = "gzip";
            await context.Response.Body.WriteAsync(Gzipped(size), ct);
            return;
        }

        if (path.EndsWith("/length", StringComparison.Ordinal))
        {
            context.Response.ContentLength = size;
        }

        var chunk = new byte[8 * 1024];
        try
        {
            for (var sent = 0; sent < size; sent += chunk.Length)
            {
                await context.Response.Body.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, size - sent)), ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The proxy gave up on the forward (its client left).
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }
}
