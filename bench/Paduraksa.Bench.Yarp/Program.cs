// Bare Kestrel + YARP reference: the baseline's floor for a .NET reverse proxy, with none of Paduraksa's gate,
// routing or policies.
//
//   dotnet run -c Release --project bench/Paduraksa.Bench.Yarp -- \
//       --upstream http://127.0.0.1:18080 --cert cert.pem --key key.pem [--http 18087] [--https 18449]
//
// bench.test is a catch-all YARP route to --upstream (YARP's default transforms add X-Forwarded-For/-Proto/-Host).
// respond.test is a minimal endpoint answering 200 "ok\n". HTTPS uses the script's throwaway ECDSA certificate with
// HTTP/1.1 and HTTP/2 (ALPN). The process runs outside Docker, under taskset, like Paduraksa.
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Server.Kestrel.Core;

using Yarp.ReverseProxy.Configuration;

var options = ParseArgs(args);
if (!options.TryGetValue("upstream", out var upstream) || !Uri.TryCreate(upstream, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync(
        "usage: Paduraksa.Bench.Yarp --upstream http://host:port --cert cert.pem --key key.pem [--http 18087] [--https 18449]");
    return 2;
}

if (!options.TryGetValue("cert", out var certPath) || !options.TryGetValue("key", out var keyPath))
{
    await Console.Error.WriteLineAsync("missing --cert and --key");
    return 2;
}

var httpPort = int.Parse(Get("http", "18087"), CultureInfo.InvariantCulture);
var httpsPort = int.Parse(Get("https", "18449"), CultureInfo.InvariantCulture);

// Kept alive until the app stops: Kestrel reads the certificate on each handshake, not once at bind time.
using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);

var builder = WebApplication.CreateBuilder(args);
// Access logs off, like every other proxy: Kestrel's request diagnostics log one line per request at Information.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(IPAddress.Loopback, httpPort, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.Listen(IPAddress.Loopback, httpsPort, listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
        listen.UseHttps(certificate);
    });
});

builder.Services.AddReverseProxy().LoadFromMemory(
    routes:
    [
        new RouteConfig
        {
            RouteId = "bench",
            ClusterId = "upstream",
            Match = new RouteMatch
            {
                Hosts = ["bench.test"],
                Path = "/{**catch-all}",
            },
        },
    ],
    clusters:
    [
        new ClusterConfig
        {
            ClusterId = "upstream",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["upstream"] = new DestinationConfig { Address = upstream },
            },
        },
    ]);

var app = builder.Build();
app.MapReverseProxy();
app.MapGet("/", () => Results.Text("ok\n")).RequireHost("respond.test");
await app.RunAsync();
return 0;

string Get(string key, string fallback) => options.TryGetValue(key, out var value) ? value : fallback;

static Dictionary<string, string> ParseArgs(string[] args)
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
