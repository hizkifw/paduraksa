using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Analytics;
using Paduraksa.Proxy.Config;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Proxy;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>
/// The stop's budgets (docs/design/hosting.md "Shutdown"), found by the soak's restart: a proxied WebSocket is closed with 1001 as the drain
/// starts instead of holding the stop for the whole <c>ShutdownTimeout</c>, and core's last flush keeps a budget of its own however long the
/// drain takes, so the traffic analytics in memory reach the database.
/// </summary>
public sealed class ShutdownBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopWithAProxiedWebSocketOpenClosesItAndIsQuick(bool http2)
    {
        await using var upstream = await WebSocketUpstream.StartAsync();
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:ShutdownTimeout"] = "00:00:30" });
        await host.StartAsync(Ct);
        await PublishAsync(host, upstream.Address);

        using var client = TestHost.CreateClient(host);
        const int Requests = 5;
        for (var i = 0; i < Requests; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, "http"), "/plain"));
            request.Headers.Host = "ws.test";
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var socket = await ConnectAsync(host, http2);
        await EchoAsync(socket, "before the stop");
        var received = ReceiveCloseAsync(socket);

        var clock = Stopwatch.StartNew();
        await host.StopAsync(Ct);
        clock.Stop();

        // The drain would otherwise have waited the 30 s out; the close handshake takes a round trip.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the stop took {clock.Elapsed}");
        var (status, description) = await received.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, status); // 1001 Going Away
        Assert.Null(description);
        await upstream.SawClose.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var (stored, unwritten) = await StoredRequestsAsync(host);
        Assert.Equal(0, unwritten);
        Assert.True(stored >= Requests, $"{stored} requests stored of at least {Requests}");
    }

    [Fact]
    public async Task CoreStillFlushesWhenThePublicDrainUsesItsWholeBudget()
    {
        SlowEndpoint slow = new();
        TaskCompletionSource<bool> coreToken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = TestHost.Create(
            settings: new Dictionary<string, string?> { ["Paduraksa:ShutdownTimeout"] = "00:00:03" },
            configureServices: (role, services) =>
            {
                if (role == HostRole.Public)
                {
                    services.AddSingleton<IStartupFilter>(slow);
                }
                else if (role == HostRole.Core)
                {
                    services.AddSingleton<IHostedService>(new LifecycleProbe(new EventLog(), role)
                    {
                        OnStop = async stopToken =>
                        {
                            coreToken.TrySetResult(stopToken.IsCancellationRequested);
                            // A flush that takes a while still finishes inside core's reserve.
                            await Task.Delay(TimeSpan.FromMilliseconds(300), stopToken);
                        },
                    });
                }
            });
        await host.StartAsync(Ct);
        using var client = TestHost.CreateClient(host);
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, "http"), "/unknown-host"));
            request.Headers.Host = "nobody.test";
            using var response = await client.SendAsync(request, Ct);
        }

        // A request that never finishes: the drain runs to its budget.
        var hung = client.GetAsync(new Uri(TestHost.Endpoint(host, HostRole.Public, "https"), SlowEndpoint.Path), Ct);
        await slow.Entered.Task.WaitAsync(Ct);

        var clock = Stopwatch.StartNew();
        await host.StopAsync(Ct);
        clock.Stop();

        Assert.False(await coreToken.Task.WaitAsync(Ct), "core was stopped with a cancelled token");
        // The drain's 2 s, Kestrel's wait for the connections it aborts, and core's flush.
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6));
        var (stored, unwritten) = await StoredRequestsAsync(host);
        Assert.Equal(0, unwritten);
        Assert.True(stored >= 3, $"{stored} requests stored of at least 3");
        await Assert.ThrowsAnyAsync<Exception>(() => hung);
        slow.Release.SetResult();
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(30, 10)]
    [InlineData(300, 10)]
    public void CoresReserveIsAThirdOfTheBudgetAtMostTenSeconds(int shutdownSeconds, double reserveSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(reserveSeconds), PaduraksaHost.CoreFlushReserve(TimeSpan.FromSeconds(shutdownSeconds)));

    private static async Task PublishAsync(PaduraksaHost host, string upstream)
    {
        var published = await host.Shared.SnapshotPublisher.PublishAsync(PublicProxyTests.Compile(host, new ProxyConfigModel
        {
            Revision = TestIds.Revision("shutdown"),
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("ws"), Destinations = [new(TestIds.Destination("ws-1"), upstream)] }],
            Hosts = [new HostSpec { Id = TestIds.Host("ws"), Names = ["ws.test"], Routes = [new RouteSpec { Id = TestIds.Route("ws-root"), Upstream = TestIds.Upstream("ws") }] }],
        }), Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
    }

    private static async Task<ClientWebSocket> ConnectAsync(PaduraksaHost host, bool http2)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Host", "ws.test");
        var endpoint = TestHost.Endpoint(host, HostRole.Public, http2 ? "https" : "http");
        var pin = host.Shared.ServerCertificate.Certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            SslOptions =
            {
                TargetHost = "ws.test",
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == pin,
            },
        };
        if (http2)
        {
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        using HttpMessageInvoker invoker = new(handler);
        await socket.ConnectAsync(new Uri($"{(http2 ? "wss" : "ws")}://127.0.0.1:{endpoint.Port}/ws"), invoker, Ct);
        return socket;
    }

    private static async Task EchoAsync(ClientWebSocket socket, string text)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, endOfMessage: true, Ct);
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer, Ct);
        Assert.Equal(text, Encoding.UTF8.GetString(buffer, 0, result.Count));
    }

    /// <summary>Reads until the close frame, answers it as a client does, and returns its status.</summary>
    private static async Task<(WebSocketCloseStatus? Status, string? Description)> ReceiveCloseAsync(ClientWebSocket socket)
    {
        var buffer = new byte[256];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, Ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, Ct);
                }
                catch (WebSocketException)
                {
                    // The proxy may already have ended the connection; the status is what is checked.
                }

                return (result.CloseStatus, string.IsNullOrEmpty(result.CloseStatusDescription) ? null : result.CloseStatusDescription);
            }
        }
    }

    private static async Task<(long Stored, int Unwritten)> StoredRequestsAsync(PaduraksaHost host)
    {
        var store = host.GetServices(HostRole.Core).GetRequiredService<AnalyticsRollupStore>();
        var now = DateTimeOffset.UtcNow;
        var read = await store.ReadAsync(AnalyticsScope.All, now.AddHours(-1), now.AddHours(1), Ct);
        return (read.Rows.Where(static row => row.Bucket is not null).Sum(static row => row.Requests), read.Unwritten.Count);
    }

    /// <summary>A loopback upstream: <c>/ws</c> echoes WebSocket messages and notes the client's close; anything else answers 200.</summary>
    private sealed class WebSocketUpstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private WebSocketUpstream(IHost host)
        {
            _host = host;
            Address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
        }

        public string Address { get; }

        public TaskCompletionSource SawClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<WebSocketUpstream> StartAsync()
        {
            WebSocketUpstream? created = null;
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(static kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                    .Configure(app =>
                    {
                        app.UseWebSockets();
                        app.Run(context => created!.HandleAsync(context));
                    }))
                .Build();
            await host.StartAsync(Ct);
            created = new WebSocketUpstream(host);
            return created;
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(CancellationToken.None);
            _host.Dispose();
        }

        private async Task HandleAsync(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await context.Response.WriteAsync("plain");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[256];
            try
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        SawClose.TrySetResult();
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
                        return;
                    }

                    await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, context.RequestAborted);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
        }
    }
}
