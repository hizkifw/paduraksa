using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;

namespace Paduraksa.Bench.WebSocket;

internal static class LoadClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(Uri uri, string connectAddress, int connections, int size, TimeSpan duration, string? outputPath)
    {
        var address = IPAddress.Parse(connectAddress);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, cancellation) =>
            {
                Socket socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        Dictionary<string, int> errors = new(StringComparer.Ordinal);
        var sockets = await Task.WhenAll(Enumerable.Range(0, connections).Select(_ => ConnectAsync(uri, invoker, errors)));
        var open = sockets.Where(socket => socket is not null).Select(socket => socket!).ToArray();
        if (open.Length == 0)
        {
            await Console.Error.WriteLineAsync($"no WebSocket connection could be opened to {uri}: {string.Join("; ", errors.Keys)}");
            return 1;
        }

        // Every connection starts together, once all are up: connection setup isn't what this measures.
        var start = Stopwatch.GetTimestamp();
        var deadline = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var workers = await Task.WhenAll(open.Select(socket => Task.Run(() => PingPongAsync(socket, size, deadline, errors))));
        var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;

        foreach (var socket in open)
        {
            socket.Abort();
            socket.Dispose();
        }

        var echoes = workers.Sum(worker => worker.Count);
        var latencies = workers.SelectMany(worker => worker).ToArray();
        Array.Sort(latencies);
        var failedConnections = connections - open.Length;
        if (failedConnections > 0)
        {
            errors["connect"] = failedConnections;
        }

        Dictionary<string, object> result = new()
        {
            ["kind"] = "websocket",
            ["summary"] = new Dictionary<string, double> { ["total"] = seconds },
            ["echoes"] = echoes,
            ["connections"] = connections,
            ["messageBytes"] = size,
            // A client socket that got as far as being open was upgraded: ClientWebSocket fails the connect on anything but 101.
            ["statusCodeDistribution"] = new Dictionary<string, int> { ["101"] = open.Length },
            ["errorDistribution"] = errors,
            ["latencyPercentiles"] = new Dictionary<string, double?>
            {
                ["p50"] = Percentile(latencies, 0.5),
                ["p90"] = Percentile(latencies, 0.9),
                ["p95"] = Percentile(latencies, 0.95),
                ["p99"] = Percentile(latencies, 0.99),
                ["p99.9"] = Percentile(latencies, 0.999),
            },
        };
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (outputPath is null)
        {
            Console.WriteLine(json);
        }
        else
        {
            await File.WriteAllTextAsync(outputPath, json);
        }

        return echoes > 0 && errors.Count == 0 ? 0 : 1;
    }

    private static async Task<ClientWebSocket?> ConnectAsync(Uri uri, HttpMessageInvoker invoker, Dictionary<string, int> errors)
    {
        ClientWebSocket socket = new();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, invoker, timeout.Token);
            return socket;
        }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            lock (errors)
            {
                errors[ex.Message] = errors.GetValueOrDefault(ex.Message) + 1;
            }

            socket.Dispose();
            return null;
        }
    }

    /// <summary>Round-trip times in seconds of the echoes one connection completed before the deadline.</summary>
    private static async Task<List<double>> PingPongAsync(ClientWebSocket socket, int size, long deadline, Dictionary<string, int> errors)
    {
        var message = new byte[size];
        Random.Shared.NextBytes(message);
        var buffer = new byte[size];
        List<double> latencies = [];
        try
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var sent = Stopwatch.GetTimestamp();
                await socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
                var received = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(received), CancellationToken.None);
                    received += result.Count;
                }
                while (!result.EndOfMessage);

                if (received != size)
                {
                    throw new InvalidDataException($"expected an echo of {size} bytes, got {received}");
                }

                latencies.Add(Stopwatch.GetElapsedTime(sent).TotalSeconds);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidDataException or IOException)
        {
            lock (errors)
            {
                errors[ex.Message] = errors.GetValueOrDefault(ex.Message) + 1;
            }
        }

        return latencies;
    }

    private static double? Percentile(double[] sorted, double quantile)
        => sorted.Length == 0 ? null : sorted[Math.Min(sorted.Length - 1, (int)(quantile * sorted.Length))];
}
