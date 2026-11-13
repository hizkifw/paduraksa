// Bare Kestrel + YARP reference: the baseline's floor for a .NET reverse proxy, with none of Paduraksa's gate,
// routing or policies.
//
//   dotnet run -c Release --project bench/Paduraksa.Bench.Yarp -- \
//       --upstream http://127.0.0.1:18080 --cert cert.pem --key key.pem [--http 18087] [--https 18449]
//
// bench.test is a catch-all YARP route to --upstream (YARP's default transforms add X-Forwarded-For/-Proto/-Host).
// The comparison scenarios' hosts (the same ones Paduraksa serves, bench/README.md) are catch-all routes too:
// access-log.test through ASP.NET Core's W3C logger (to --access-log-dir), rate-limit.test and rate-limited.test under
// ASP.NET Core's token bucket limiter (429 on refusal, partitioned by client address), error-page.test to a closed port
// with a 502 page from --error-page, and cf.test, where --cloudflare-tunnel true makes the forwarded-headers middleware
// read the client address from CF-Connecting-IP.
// ws.test is a catch-all route to the WebSocket echo server (YARP forwards HTTP/1.1 upgrades on its own).
// respond.test is a minimal endpoint answering 200 "ok\n". HTTPS uses the script's throwaway ECDSA certificate with
// HTTP/1.1 and HTTP/2 (ALPN). The process runs outside Docker, under taskset, like Paduraksa.
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Yarp.ReverseProxy.Configuration;

var options = ParseArgs(args);
if (!options.TryGetValue("upstream", out var upstream) || !Uri.TryCreate(upstream, UriKind.Absolute, out _))
{
    await Console.Error.WriteLineAsync(
        "usage: Paduraksa.Bench.Yarp --upstream http://host:port --cert cert.pem --key key.pem [--http 18087] [--https 18449] [--websocket-upstream http://127.0.0.1:18090] [--cloudflare-tunnel true] [--error-page page.html] [--access-log-dir dir]");
    return 2;
}

if (!options.TryGetValue("cert", out var certPath) || !options.TryGetValue("key", out var keyPath))
{
    await Console.Error.WriteLineAsync("missing --cert and --key");
    return 2;
}

var cloudflareTunnel = Get("cloudflare-tunnel", "false") == "true";
var errorPage = options.TryGetValue("error-page", out var errorPagePath) ? await File.ReadAllBytesAsync(errorPagePath) : [];
var httpPort = int.Parse(Get("http", "18087"), CultureInfo.InvariantCulture);
var httpsPort = int.Parse(Get("https", "18449"), CultureInfo.InvariantCulture);

// Kept alive until the app stops: Kestrel reads the certificate on each handshake, not once at bind time.
using X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);

var builder = WebApplication.CreateBuilder(args);
// Access logs off, like every other proxy: Kestrel's request diagnostics log one line per request at Information.
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// The error-page scenario fails every forward, and YARP's forwarder logs a stack trace for each failure at Warning,
// which would make the log writer the thing that scenario measures (the Paduraksa runner turns the same logger off).
builder.Logging.AddFilter("Yarp.ReverseProxy.Forwarder", LogLevel.None);
// "allowed": a budget the run never reaches. "refused": one token refilled once an hour, so after the first request
// every one is a 429. Both are token buckets, one per client address.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("allowed", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1_048_575,
            TokensPerPeriod = 1_000_000,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
        }));
    limiter.AddPolicy("refused", context => RateLimitPartition.GetTokenBucketLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));
});
builder.Services.AddW3CLogging(logging =>
{
    logging.LoggingFields = W3CLoggingFields.Date | W3CLoggingFields.Time | W3CLoggingFields.ClientIpAddress
        | W3CLoggingFields.Method | W3CLoggingFields.UriStem | W3CLoggingFields.UriQuery | W3CLoggingFields.ProtocolStatus
        | W3CLoggingFields.UserAgent | W3CLoggingFields.Referer | W3CLoggingFields.Host;
    // The logger drains a 1,024-line queue once per interval and makes requests wait while it is full, so its default
    // second caps a host at about a thousand requests a second (measured: 2.8k req/s at c=64 against 127k with 1 ms).
    logging.FlushInterval = TimeSpan.FromMilliseconds(1);
    logging.LogDirectory = Get("access-log-dir", Path.Combine(Path.GetTempPath(), "yarp-access-log"));
});
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
        new RouteConfig { RouteId = "access-log", ClusterId = "upstream", Match = HostMatch("access-log.test") },
        new RouteConfig { RouteId = "rate-limit", ClusterId = "upstream", Match = HostMatch("rate-limit.test"), RateLimiterPolicy = "allowed" },
        new RouteConfig { RouteId = "rate-limited", ClusterId = "upstream", Match = HostMatch("rate-limited.test"), RateLimiterPolicy = "refused" },
        new RouteConfig { RouteId = "error-page", ClusterId = "closed", Match = HostMatch("error-page.test") },
        new RouteConfig { RouteId = "cf", ClusterId = "upstream", Match = HostMatch("cf.test") },
        new RouteConfig
        {
            RouteId = "websocket",
            ClusterId = "echo",
            Match = new RouteMatch
            {
                Hosts = ["ws.test"],
                Path = "/{**catch-all}",
            },
        },
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
        new ClusterConfig
        {
            ClusterId = "closed",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                // Nothing listens here, and the cluster has no passive health check, so every request tries it.
                ["closed"] = new DestinationConfig { Address = "http://127.0.0.1:18079" },
            },
        },
        new ClusterConfig
        {
            ClusterId = "echo",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal)
            {
                ["echo"] = new DestinationConfig { Address = Get("websocket-upstream", "http://127.0.0.1:18090") },
            },
        },
    ]);

var app = builder.Build();
if (cloudflareTunnel)
{
    // Loopback is the trusted peer (the middleware's default), standing in for cloudflared as in Paduraksa's Tunnel
    // mode; the client address becomes CF-Connecting-IP's, and YARP's X-Forwarded-For transform forwards it.
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        ForwardedForHeaderName = "CF-Connecting-IP",
    });
}

// YARP answers a failed forward with a bare 502; this is the minimal page the other proxies write too. The status is
// an integer compare for every other request.
app.Use(async (context, next) =>
{
    await next(context);
    if (context.Response.StatusCode == StatusCodes.Status502BadGateway && !context.Response.HasStarted && errorPage.Length > 0)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = errorPage.Length;
        await context.Response.Body.WriteAsync(errorPage);
    }
});
app.UseWhen(
    context => context.Request.Host.Value?.StartsWith("access-log.test", StringComparison.Ordinal) == true,
    branch => branch.UseW3CLogging());
app.UseRateLimiter();
app.MapReverseProxy();
app.MapGet("/", () => Results.Text("ok\n")).RequireHost("respond.test");
await app.RunAsync();
return 0;

static RouteMatch HostMatch(string host) => new() { Hosts = [host], Path = "/{**catch-all}" };

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
