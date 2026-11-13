using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// An upstream's idle and connect timeouts through the real proxy (Kestrel, YARP, the Paduraksa client factory): a quiet WebSocket, event
/// stream or request is cut at the configured idle time and not before, activity resets it, and a connect that never completes fails
/// at the configured connect timeout. Short timeouts stand in for the minutes of a real configuration.
/// </summary>
public sealed class UpstreamTimeoutsTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortIdle = TimeSpan.FromSeconds(2);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IHost _upstream = null!;
    private string _address = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _upstream = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1))
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(HandleAsync);
                }))
            .Build();
        await _upstream.StartAsync(Ct);
        _address = _upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public async ValueTask DisposeAsync()
    {
        await _upstream.StopAsync(CancellationToken.None);
        _upstream.Dispose();
    }

    private static async Task HandleAsync(HttpContext context)
    {
        switch (context.Request.Path.Value)
        {
            case "/ws" when context.WebSockets.IsWebSocketRequest:
                using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                {
                    var buffer = new byte[256];
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        try
                        {
                            result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
                        {
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, endOfMessage: true, context.RequestAborted);
                    }
                }

                break;
            case "/sse":
                // One event, a silence of ?gap= milliseconds, a second event.
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: first\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(int.Parse(context.Request.Query["gap"]!, System.Globalization.CultureInfo.InvariantCulture), context.RequestAborted);
                await context.Response.WriteAsync("data: second\n\n", context.RequestAborted);
                break;
            case "/slow":
                // Nothing, not even headers, for ?gap= milliseconds.
                await Task.Delay(int.Parse(context.Request.Query["gap"]!, System.Globalization.CultureInfo.InvariantCulture), context.RequestAborted);
                await context.Response.WriteAsync("done", context.RequestAborted);
                break;
            default:
                await context.Response.WriteAsync("ok", context.RequestAborted);
                break;
        }
    }

    private ProxyConfigModel Model(TimeSpan? idle = null, TimeSpan? connect = null, string? address = null) => new()
    {
        Upstreams = [Upstream("up", address ?? _address) with { IdleTimeout = idle ?? UpstreamTimeouts.DefaultIdle, ConnectTimeout = connect ?? UpstreamTimeouts.DefaultConnect }],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "up"))],
    };

    private static async Task<ClientWebSocket> ConnectAsync(ProxyTestServer server, bool h2c)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Host", "app.test");
        if (h2c)
        {
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        using HttpMessageInvoker invoker = new(new SocketsHttpHandler { UseProxy = false });
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.BaseAddress.Port}/ws"), invoker, Ct);
        return socket;
    }

    private static async Task EchoAsync(ClientWebSocket socket, string text)
    {
        await socket.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);
        var buffer = new byte[64];
        var result = await socket.ReceiveAsync(buffer, Ct);
        Assert.Equal(text, Encoding.ASCII.GetString(buffer, 0, result.Count));
    }

    /// <summary>How long the socket takes to be told it is over (a close frame or the connection dropping), or the limit.</summary>
    private static async Task<TimeSpan> TimeToCutAsync(ClientWebSocket socket, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(limit);
        try
        {
            var buffer = new byte[64];
            while ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close)
            {
            }
        }
        catch (WebSocketException)
        {
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
        }

        return clock.Elapsed;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnIdleWebSocketIsCutAtTheIdleTimeout(bool h2c)
    {
        await using var server = await ProxyTestServer.StartAsync(Model(idle: ShortIdle), new ProxyTestServerOptions { Protocols = h2c ? HttpProtocols.Http2 : HttpProtocols.Http1 });
        using var socket = await ConnectAsync(server, h2c);
        await EchoAsync(socket, "hello");

        var cutAfter = await TimeToCutAsync(socket, TimeSpan.FromSeconds(20));

        Assert.InRange(cutAfter, ShortIdle - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AWebSocketThatKeepsTalkingOutlivesTheIdleTimeout()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(idle: ShortIdle));
        using var socket = await ConnectAsync(server, h2c: false);

        // Five seconds in all, with a message every second: more than twice the timeout, never idle for it.
        for (var round = 0; round < 5; round++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            await EchoAsync(socket, $"m{round}");
        }

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWebSocketQuietForLongerThanYarpsOwnDefaultSurvivesTheDefault(bool none)
    {
        // YARP's activity timeout is 100 s; the test can't wait that long, so this pins the other half of the fix: the cluster
        // carries ours (here the default, and the explicit "never"), which is long, rather than a short test value.
        await using var server = await ProxyTestServer.StartAsync(Model(idle: none ? UpstreamTimeouts.None : null));
        using var socket = await ConnectAsync(server, h2c: false);

        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        await EchoAsync(socket, "still here");

        Assert.Equal(WebSocketState.Open, socket.State);
        var cluster = Assert.Single(server.Store.Current.Yarp.Clusters);
        Assert.Equal(none ? Timeout.InfiniteTimeSpan : TimeSpan.FromHours(1), cluster.HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public async Task AnEventStreamQuietForLongerThanTheIdleTimeoutIsCut()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(idle: ShortIdle));
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/sse?gap=8000");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        using StreamReader reader = new(body);
        Assert.Equal("data: first", await reader.ReadLineAsync(Ct));

        var clock = Stopwatch.StartNew();
        var rest = await ReadToEndOrCutAsync(reader);

        Assert.DoesNotContain("second", rest, StringComparison.Ordinal);
        Assert.InRange(clock.Elapsed, ShortIdle - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task AnEventStreamWithAGapUnderTheIdleTimeoutIsNotCut()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/sse?gap=3000");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        await using var body = await response.Content.ReadAsStreamAsync(Ct);
        using StreamReader reader = new(body);

        Assert.Equal("data: first", await reader.ReadLineAsync(Ct));
        Assert.Contains("data: second", await reader.ReadToEndAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestTheUpstreamNeverAnswersGetsAGatewayTimeoutAtTheIdleTimeout()
    {
        await using var server = await ProxyTestServer.StartAsync(Model(idle: ShortIdle));
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/slow?gap=8000");

        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(clock.Elapsed, ShortIdle - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task ChangingTheIdleTimeoutOfARunningProxyAppliesToTheNextConnection()
    {
        await using var server = await ProxyTestServer.StartAsync(Model());
        using var before = await ConnectAsync(server, h2c: false);
        await EchoAsync(before, "one");

        var published = await server.PublishAsync(Model(idle: ShortIdle));
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        using var after = await ConnectAsync(server, h2c: false);
        await EchoAsync(after, "two");

        Assert.InRange(await TimeToCutAsync(after, TimeSpan.FromSeconds(20)), ShortIdle - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AConnectThatNeverCompletesFailsAtTheConnectTimeoutNotTheDefault()
    {
        using var blackhole = Blackhole.Start();
        var connect = TimeSpan.FromSeconds(1);
        await using var server = await ProxyTestServer.StartAsync(Model(connect: connect, address: blackhole.Address));
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");

        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(clock.Elapsed, connect - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task ATlsHandshakeThatNeverCompletesFailsAtTheConnectTimeoutToo()
    {
        // The TCP connect succeeds (the listener accepts) and then nothing is said, so only the handshake hangs.
        using TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var accepted = await silent.AcceptTcpClientAsync(Ct);
                await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            }
        }, Ct);
        var connect = TimeSpan.FromSeconds(1);
        await using var server = await ProxyTestServer.StartAsync(Model(connect: connect, address: $"https://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}/"));
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");

        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.InRange(clock.Elapsed, connect - TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task ChangingTheConnectTimeoutOfARunningProxyRebuildsTheClient()
    {
        using var blackhole = Blackhole.Start();
        await using var server = await ProxyTestServer.StartAsync(Model(connect: TimeSpan.FromSeconds(8), address: blackhole.Address));
        using var client = ProxyTestServer.CreateClient();

        var published = await server.PublishAsync(Model(connect: TimeSpan.FromSeconds(1), address: blackhole.Address));
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
        using var request = server.Request("app.test", "/");
        var clock = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"took {clock.Elapsed}, so the 8 s client was kept");
    }

    private static async Task<string> ReadToEndOrCutAsync(StreamReader reader)
    {
        try
        {
            return await reader.ReadToEndAsync(Ct);
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A listener whose accept queue is full, so the next SYN is dropped and a connect to it hangs until something gives up: the
    /// same as a blackholed address, without depending on the network the test runs on.
    /// </summary>
    private sealed class Blackhole : IDisposable
    {
        private readonly Socket _listener;
        private readonly List<Socket> _fillers = [];

        private Blackhole(Socket listener, List<Socket> fillers)
        {
            _listener = listener;
            _fillers = fillers;
        }

        public string Address => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndPoint!).Port}/";

        public static Blackhole Start()
        {
            Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            List<Socket> fillers = [];
            for (var i = 0; i < 16; i++)
            {
                Socket filler = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                fillers.Add(filler);
                if (!filler.ConnectAsync(listener.LocalEndPoint!).Wait(TimeSpan.FromMilliseconds(300)))
                {
                    return new Blackhole(listener, fillers);
                }
            }

            listener.Dispose();
            throw new InvalidOperationException("the accept queue never filled, so a connect would not hang");
        }

        public void Dispose()
        {
            foreach (var filler in _fillers)
            {
                filler.Dispose();
            }

            _listener.Dispose();
        }
    }
}
