// WebSocket echo server and load generator for the baseline's `websocket` scenario (bench/run-baseline.sh). Both run
// natively under taskset, like Paduraksa and the bare YARP reference.
//
//   dotnet run -c Release --project bench/Paduraksa.Bench.WebSocket -- echo --port 18090
//   dotnet run -c Release --project bench/Paduraksa.Bench.WebSocket -- load --url ws://ws.test:18082/ \
//       --connect 127.0.0.1 --connections 64 --size 1024 --duration 10 [--out result.json]
//
// `echo` answers every WebSocket frame with the same frame and answers a plain GET / with 200 "ok" (the harness's
// readiness probe). `load` opens --connections sockets, and once all are up each one sends a --size byte binary message,
// waits for the echo and repeats until --duration seconds have passed. --connect is the address to dial in place of the
// URL's host, which stays the Host header (oha's --connect-to). The result is oha-shaped JSON for summarize.py.
using System.Globalization;

using Paduraksa.Bench.WebSocket;

if (args.Length == 0 || args[0] is not ("echo" or "load"))
{
    await Console.Error.WriteLineAsync("usage: Paduraksa.Bench.WebSocket echo --port N | load --url ws://host:port/ [--connect IP] "
        + "[--connections 64] [--size 1024] [--duration 10] [--out file]");
    return 2;
}

var options = ParseArgs(args.AsSpan(1));
if (args[0] == "echo")
{
    return await EchoServer.RunAsync(int.Parse(Get("port", "18090"), CultureInfo.InvariantCulture));
}

if (!options.TryGetValue("url", out var url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "ws")
{
    await Console.Error.WriteLineAsync("load needs --url ws://host:port/");
    return 2;
}

return await LoadClient.RunAsync(
    uri,
    Get("connect", "127.0.0.1"),
    int.Parse(Get("connections", "64"), CultureInfo.InvariantCulture),
    int.Parse(Get("size", "1024"), CultureInfo.InvariantCulture),
    TimeSpan.FromSeconds(double.Parse(Get("duration", "10"), CultureInfo.InvariantCulture)),
    options.GetValueOrDefault("out"));

string Get(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;

static Dictionary<string, string> ParseArgs(ReadOnlySpan<string> args)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        }

        parsed[args[i][2..]] = args[i + 1];
    }

    return parsed;
}
