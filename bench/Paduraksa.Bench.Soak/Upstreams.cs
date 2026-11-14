using System.Net;
using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Bench.Soak;

/// <summary>
/// One upstream destination: a Kestrel app on a loopback port that answers what the soak's traffic asks for. It can be stopped and started again on the same port,
/// which is the flap. Responses are deterministic, so the clients can check what came back, not only the status.
/// </summary>
internal sealed class UpstreamNode(string name, int port) : IAsyncDisposable
{
    public const int BigBytes = 65_536;

    private static readonly byte[] BigBody = MakeBigBody();
    private static readonly byte[] TextBody = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog, again and again. ", 280)));

    private WebApplication? _app;
    private const int SeenSize = 1 << 18;

    // The ids of the last requests that carried X-Soak-Id, direct-mapped: when a client's request hangs, this says whether the upstream ever got it.
    private readonly long[] _seen = new long[SeenSize];
    private readonly long[] _done = new long[SeenSize];
    private readonly long[] _seenAt = new long[SeenSize];
    private long _requests;
    private long _webSockets;

    public string Name { get; } = name;

    public int Port { get; } = port;

    public long Requests => Interlocked.Read(ref _requests);

    public long WebSocketsAccepted => Interlocked.Read(ref _webSockets);

    public bool Running => _app is not null;

    /// <summary>When it arrived (a <see cref="System.Diagnostics.Stopwatch"/> timestamp), if <see cref="Saw"/>.</summary>
    public long SeenAt(long id) => Volatile.Read(ref _seenAt[id & (SeenSize - 1)]);

    public bool Saw(long id) => Volatile.Read(ref _seen[id & (SeenSize - 1)]) == id;

    /// <summary>The upstream wrote the whole response for it.</summary>
    public bool Finished(long id) => Volatile.Read(ref _done[id & (SeenSize - 1)]) == id;

    public static byte[] Big => BigBody;

    private static byte[] MakeBigBody()
    {
        var body = new byte[BigBytes];
        for (var i = 0; i < body.Length; i++)
        {
            body[i] = (byte)(i % 251);
        }

        return body;
    }

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, Port);
            kestrel.AddServerHeader = false;
        });
        var app = builder.Build();
        app.UseWebSockets();
        app.Run(HandleAsync);
        await app.StartAsync();
        _app = app;
    }

    public async Task StopAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null)
        {
            return;
        }

        // A short grace: a flap is a destination that goes away, not one that drains politely.
        using CancellationTokenSource grace = new(TimeSpan.FromMilliseconds(500));
        try
        {
            await app.StopAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await app.DisposeAsync();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task HandleAsync(HttpContext http)
    {
        Interlocked.Increment(ref _requests);
        var response = http.Response;
        long traceId = 0;
        if (http.Request.Headers.TryGetValue("X-Soak-Id", out var traced) && long.TryParse(traced.ToString(), out traceId))
        {
            Volatile.Write(ref _seenAt[traceId & (SeenSize - 1)], System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _seen[traceId & (SeenSize - 1)], traceId);
        }

        response.Headers["X-Soak-Node"] = Name;
        var path = http.Request.Path.Value ?? "/";
        switch (path)
        {
            case "/health":
                response.ContentType = "text/plain";
                await response.WriteAsync("ok");
                return;
            case "/ws" when http.WebSockets.IsWebSocketRequest:
                Interlocked.Increment(ref _webSockets);
                await EchoAsync(await http.WebSockets.AcceptWebSocketAsync(), http.RequestAborted);
                return;
            case "/big":
                response.ContentType = "application/octet-stream";
                response.ContentLength = BigBody.Length;
                await response.Body.WriteAsync(BigBody, http.RequestAborted);
                return;
            case "/text":
                response.ContentType = "text/plain; charset=utf-8";
                await response.Body.WriteAsync(TextBody, http.RequestAborted);
                return;
            case "/upload":
                var buffer = new byte[16_384];
                long received = 0;
                int read;
                while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
                {
                    received += read;
                }

                response.ContentType = "text/plain";
                await response.WriteAsync("received=" + received);
                return;
        }

        // The path the upstream saw is the answer: the strip-prefix route's check, and a cheap body for everything else.
        response.ContentType = "text/plain";
        response.Headers["X-Soak-Seen-Path"] = path;
        // What the header rules did to the request (docs: the headers host), echoed for the client to check.
        if (http.Request.Headers.TryGetValue("X-Soak-Tenant", out var tenant))
        {
            response.Headers["X-Soak-Seen-Tenant"] = tenant.ToString();
        }

        response.Headers["X-Soak-Seen-Drop"] = http.Request.Headers.TryGetValue("X-Soak-Drop", out var drop) ? drop.ToString() : "none";
        await response.WriteAsync("path=" + path + "\n" + new string('x', 400));
        if (traceId != 0)
        {
            Volatile.Write(ref _done[traceId & (SeenSize - 1)], traceId);
        }
    }

    private static async Task EchoAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }
}

/// <summary>The forward-auth provider: <c>200</c> with identity headers, <c>401</c> when the original URI asks for it.</summary>
internal sealed class AuthProvider(int port) : IAsyncDisposable
{
    private WebApplication? _app;
    private long _calls;

    public long Calls => Interlocked.Read(ref _calls);

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.Run(async http =>
        {
            // Only a real forward-auth call counts: a stray GET / from a port prober carries no X-Forwarded-Method.
            if (!http.Request.Headers.ContainsKey("X-Forwarded-Proto"))
            {
                http.Response.StatusCode = 400;
                return;
            }

            Interlocked.Increment(ref _calls);
            if ((http.Request.Headers["X-Forwarded-Uri"].ToString()).Contains("deny", StringComparison.Ordinal))
            {
                http.Response.StatusCode = 401;
                return;
            }

            http.Response.Headers["X-Auth-User"] = "soak";
            http.Response.StatusCode = 200;
            await Task.CompletedTask;
        });
        await app.StartAsync();
        _app = app;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
