using System.Net;
using System.Net.WebSockets;

namespace Paduraksa.Bench.WebSocket;

internal static class EchoServer
{
    public static async Task<int> RunAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        // An abruptly closed socket at the end of a run is how every load run ends; it isn't worth a log line.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        app.UseWebSockets();
        app.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await context.Response.WriteAsync("ok\n");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await EchoAsync(socket, context.RequestAborted);
        });

        await app.RunAsync();
        return 0;
    }

    private static async Task EchoAsync(System.Net.WebSockets.WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, cancellation);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cancellation);
                    return;
                }

                await socket.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, cancellation);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }
}
