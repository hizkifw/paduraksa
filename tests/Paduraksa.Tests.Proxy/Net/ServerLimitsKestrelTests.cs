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

using Paduraksa.Proxy.Net;

namespace Paduraksa.Tests.Proxy.Net;

/// <summary>
/// The limits <see cref="ServerLimits"/> sets, on a real Kestrel: a client that dribbles its headers or its body is closed, and a
/// WebSocket or an event stream that sits idle between messages is not, even with data-rate limits far stricter than any default.
/// </summary>
public sealed class ServerLimitsKestrelTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private IHost? _host;
    private Uri _address = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Strict on purpose: a stream that survives these survives the defaults. The rates are far above what a quiet
        // WebSocket or an event every few hundred milliseconds ever sends.
        ServerLimits limits = new()
        {
            RequestHeadersTimeout = TimeSpan.FromSeconds(1),
            MinRequestBodyRate = 100_000,
            MinRequestBodyGrace = TimeSpan.FromSeconds(1.5),
            MinResponseRate = 100_000,
            MinResponseGrace = TimeSpan.FromSeconds(1.5),
        };
        _host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    limits.Apply(kestrel.Limits);
                    kestrel.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.Run(HandleAsync);
                }))
            .Build();
        await _host.StartAsync(Ct);
        _address = new Uri(_host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }
    }

    private static async Task HandleAsync(HttpContext context)
    {
        switch (context.Request.Path.Value)
        {
            case "/ws" when context.WebSockets.IsWebSocketRequest:
                using (var socket = await context.WebSockets.AcceptWebSocketAsync())
                {
                    var buffer = new byte[256];
                    while (true)
                    {
                        var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            return;
                        }

                        await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, endOfMessage: true, context.RequestAborted);
                    }
                }

            case "/sse":
                context.Response.ContentType = "text/event-stream";
                for (var i = 0; i < 6; i++)
                {
                    await context.Response.WriteAsync($"data: {i}\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(700, context.RequestAborted);
                }

                break;
            case "/body":
                var read = 0;
                var scratch = new byte[64];
                int n;
                while ((n = await context.Request.Body.ReadAsync(scratch, context.RequestAborted)) > 0)
                {
                    read += n;
                }

                await context.Response.WriteAsync($"read {read}", context.RequestAborted);
                break;
            default:
                await context.Response.WriteAsync("ok", context.RequestAborted);
                break;
        }
    }

    private async Task<TcpClient> ConnectAsync()
    {
        TcpClient tcp = new();
        await tcp.ConnectAsync(_address.Host, _address.Port, Ct);
        return tcp;
    }

    /// <summary>Everything the server sends until it closes the connection, or until <see cref="Patience"/> runs out.</summary>
    private static async Task<(string Text, TimeSpan Elapsed, bool Closed)> ReadUntilClosedAsync(TcpClient tcp)
    {
        var watch = Stopwatch.StartNew();
        var stream = tcp.GetStream();
        var buffer = new byte[2048];
        StringBuilder text = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            return (text.ToString(), watch.Elapsed, true);
        }
        catch (IOException)
        {
            return (text.ToString(), watch.Elapsed, true);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return (text.ToString(), watch.Elapsed, false);
        }
    }

    [Fact]
    public async Task AClientThatNeverFinishesItsHeadersIsClosedAfterTheHeadersTimeout()
    {
        using var tcp = await ConnectAsync();
        // One header line and then silence: no blank line ends the headers (the slowloris).
        await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nX-Slow: 1\r\n"), Ct);

        var (text, elapsed, closed) = await ReadUntilClosedAsync(tcp);

        Assert.True(closed, "the server never closed the connection");
        Assert.StartsWith("HTTP/1.1 408", text, StringComparison.Ordinal);
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task AClientThatDribblesItsHeadersByteByByteIsStillCutAtTheTimeout()
    {
        using var tcp = await ConnectAsync();
        var stream = tcp.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\nX-Pad: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n\r\n");
        var watch = Stopwatch.StartNew();
        var sent = 0;
        try
        {
            // 150 ms a byte: well inside any per-read timeout, far too slow for the whole header block.
            for (; sent < request.Length && watch.Elapsed < TimeSpan.FromSeconds(8); sent++)
            {
                await stream.WriteAsync(request.AsMemory(sent, 1), Ct);
                await Task.Delay(150, Ct);
            }
        }
        catch (IOException)
        {
            // The server closed the connection mid-send: the point of the test.
        }

        Assert.True(sent < request.Length, "the whole request got through");
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task AClientThatSendsABodyBelowTheMinimumRateIsClosed()
    {
        using var tcp = await ConnectAsync();
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /body HTTP/1.1\r\nHost: x\r\nContent-Length: 100000\r\n\r\n"), Ct);

        var watch = Stopwatch.StartNew();
        var closed = false;
        try
        {
            // 10 bytes a second against 100 KB/s once the grace period is over.
            while (watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                await stream.WriteAsync(new byte[10], Ct);
                await Task.Delay(1000, Ct);
            }
        }
        catch (IOException)
        {
            closed = true;
        }

        Assert.True(closed, "the slow upload was never cut");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task AnIdleWebSocketIsNotCutByTheDataRateLimits()
    {
        using ClientWebSocket socket = new();
        await socket.ConnectAsync(new UriBuilder(_address) { Scheme = "ws", Path = "/ws" }.Uri, Ct);

        for (var round = 0; round < 3; round++)
        {
            // Longer than the grace period, with nothing on the wire between the messages.
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            await socket.SendAsync(Encoding.ASCII.GetBytes($"hello {round}"), WebSocketMessageType.Text, true, Ct);
            var buffer = new byte[64];
            var result = await socket.ReceiveAsync(buffer, Ct);
            Assert.Equal($"hello {round}", Encoding.ASCII.GetString(buffer, 0, result.Count));
        }

        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
    }

    [Fact]
    public async Task AnEventStreamThatIsQuietBetweenEventsIsNotCut()
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await client.GetAsync(new Uri(_address, "/sse"), HttpCompletionOption.ResponseHeadersRead, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);

        // Six events over four seconds, with each pause well below the response rate the server was told to enforce.
        Assert.Equal(string.Concat(Enumerable.Range(0, 6).Select(i => $"data: {i}\n\n")), body);
    }

    [Fact]
    public async Task AnOrdinaryRequestIsUnaffected()
    {
        using HttpClient client = new();
        Assert.Equal("ok", await client.GetStringAsync(new Uri(_address, "/"), Ct));
    }
}
