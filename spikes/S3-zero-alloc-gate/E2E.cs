using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Model;

namespace S3;

/// <summary>
/// Item 7: this process is the proxy (Kestrel + gate + YARP). The upstream and the load generator run as separate
/// processes (same binary), so GC.GetTotalAllocatedBytes in this process is the proxy's own allocation.
/// </summary>
public static class E2E
{
    public sealed record Options(string Mode, string Proto, string Path, int Conns, int Depth, int WarmSec, int MeasureSec, bool Cf, bool XfOff, bool Ticks, bool VaryPath, bool NoAuthz, bool Browser, bool GenericHost);

    private static long s_requests;

    public static async Task<int> RunAsync(Options o)
    {
        Gate.StateMode = StateMode.Pooled;
        Gate.ClientInfoMode = ClientInfoMode.ConnectionFeature;
        if (o.Cf) ConnectionGate.IsTrustedProxy = static ip => ip == Ip.FromV4(0x7F00_0001); // 127.0.0.1 is "Cloudflare"

        Process? upstream = null;
        var upPort = 0;
        if (o.Mode == "gate")
        {
            upstream = Spawn("upstream");
            var line = await upstream.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("upstream died");
            upPort = int.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
        }

        var port = Probe.FreePort();
        void Kestrel(KestrelServerOptions k) => k.Listen(IPAddress.Loopback, port, lo =>
        {
            lo.Protocols = o.Proto == "h2" ? HttpProtocols.Http2 : HttpProtocols.Http1;
            lo.UseClientInfo();
        });
        void Services(IServiceCollection services)
        {
            if (o.Mode != "gate") return;
            var routes = Enumerable.Range(0, Fleet.Hosts).Select(i => new RouteConfig
            {
                RouteId = $"r{i}",
                ClusterId = "c0",
                Match = new RouteMatch { Path = $"/.paduraksa/r/{i}/{{**rest}}" },
                Transforms = o.XfOff ? [new Dictionary<string, string> { ["X-Forwarded"] = "Off" }] : null,
            }).ToList();
            var clusters = new[]
            {
                new ClusterConfig
                {
                    ClusterId = "c0",
                    Destinations = new Dictionary<string, DestinationConfig> { ["d0"] = new() { Address = $"http://127.0.0.1:{upPort}/" } },
                },
            };
            services.AddReverseProxy().LoadFromMemory(routes, clusters);   // calls AddAuthorization() unconditionally
            services.AddSingleton<IAuthorizationPolicyProvider, SpyPolicyProvider>(); // guard: counts fallback-policy lookups
        }

        IHost host;
        if (o.GenericHost)
        {
            // Generic host + explicit pipeline: public API only, no WebApplication auto-wiring of auth middleware.
            host = new HostBuilder()
                .ConfigureWebHost(w => w
                    .UseKestrel(Kestrel)
                    .ConfigureServices(sv => { sv.AddRouting(); Services(sv); })
                    .Configure(app => ConfigurePipeline(app, o)))
                .Build();
            await host.StartAsync();
        }
        else
        {
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(Kestrel);
            Services(b.Services);
            var app = b.Build();
            // AddReverseProxy registers IAuthorizationHandlerProvider, so WebApplication inserts UseAuthorization()
            // BEFORE the whole user pipeline (because the app calls UseRouting itself): ahead of the gate, with no
            // endpoint. Marking it as already added suppresses it (ASP.NET Core's internal property key).
            if (o.NoAuthz) ((IApplicationBuilder)app).Properties["__AuthorizationMiddlewareSet"] = true;
            ConfigurePipeline(app, o);
            await app.StartAsync();
            host = app;
        }

        var snap = Fleet.Build();
        if (o.Mode == "gate")
        {
            var eds = s_endpoints!.DataSources.Single(d => d.GetType().FullName!.Contains("ProxyConfigManager", StringComparison.Ordinal));
            var byId = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
            foreach (var e in eds.Endpoints) if (e.Metadata.GetMetadata<RouteModel>() is { } rm) byId[rm.Config.RouteId] = e;
            snap.Endpoints = Enumerable.Range(0, Fleet.Hosts).Select(i => (Endpoint?)byId[$"r{i}"]).ToArray();
        }
        SnapshotStore.Publish(snap);

        // Idle background allocation (Kestrel heartbeat, timers): measured so it can be discounted.
        var idle0 = GC.GetTotalAllocatedBytes(true);
        await Task.Delay(1000);
        var idleBytesPerSec = GC.GetTotalAllocatedBytes(true) - idle0;

        var load = Spawn("load", port.ToString(CultureInfo.InvariantCulture), o.Proto, o.Path, o.Conns.ToString(CultureInfo.InvariantCulture),
            o.Depth.ToString(CultureInfo.InvariantCulture), o.WarmSec.ToString(CultureInfo.InvariantCulture), o.MeasureSec.ToString(CultureInfo.InvariantCulture),
            o.Cf ? "cf" : "nocf", o.VaryPath ? "vary" : "same", o.Browser ? "browser" : "plain");
        string? l;
        while ((l = await load.StandardOutput.ReadLineAsync()) != "WARM")
            if (l is null) throw new InvalidOperationException("load died: " + await load.StandardError.ReadToEndAsync());

        TickListener? ticks = o.Ticks ? new TickListener() : null;
        Gate.ResetStats();
        s_routingBytes = s_routingSamples = 0;
        var r0 = Interlocked.Read(ref s_requests);
        SpyPolicyProvider.FallbackCalls = 0;
        var a0 = GC.GetTotalAllocatedBytes(true);
        var g0 = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        var done = await load.StandardOutput.ReadLineAsync() ?? "";
        var a1 = GC.GetTotalAllocatedBytes(true);
        var fallbackCalls = Interlocked.Read(ref SpyPolicyProvider.FallbackCalls);
        var r1 = Interlocked.Read(ref s_requests);
        var secs = sw.Elapsed.TotalSeconds;
        var g1 = GC.CollectionCount(0);
        ticks?.Dispose();
        await load.WaitForExitAsync();

        var reqs = r1 - r0;
        var total = a1 - a0;
        var idleShare = idleBytesPerSec * secs;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"mode={o.Mode} proto={o.Proto} path={o.Path}{(o.VaryPath ? "*" : "")} conns={o.Conns} depth={o.Depth} cf={o.Cf} xf={(o.XfOff ? "off" : "default")}{(o.NoAuthz ? " noauthz" : "")}{(o.Browser ? " browser" : "")}{(o.GenericHost ? " generichost" : "")} | " +
            $"{reqs} req in {secs:F1}s ({reqs / secs:F0} rps), client: {done} | " +
            $"server total {(total < 0 ? "NON-MONOTONIC GetTotalAllocatedBytes, discard " : "")}{(double)total / reqs:F1} B/req (idle {(double)idleShare / reqs:F2} B/req of that; gen0 GCs {g1 - g0}) | " +
            $"gate {(o.Mode == "bare" ? "n/a" : ((double)Gate.AllocBytes / Math.Max(1, Gate.Requests)).ToString("F2", CultureInfo.InvariantCulture))} B/req " +
            $"(nonzero {Gate.AllocRequests}/{Gate.Requests}, max {Gate.MaxAlloc}, fwd {Gate.Forwarded}, resp {Gate.Responded}, noClientInfo {Gate.NoClientInfo}, cf {Gate.CfParsed}) | " +
            $"handoff→proxy pipeline {(s_routingSamples == 0 ? "n/a" : ((double)s_routingBytes / s_routingSamples).ToString("F1", CultureInfo.InvariantCulture))} B/req | auth fallback-policy lookups {fallbackCalls}"));
        ticks?.Print(reqs);

        await host.StopAsync();
        if (upstream is not null) { upstream.StandardInput.Close(); await upstream.WaitForExitAsync(); }
        return 0;
    }

    private static long s_routingBytes, s_routingSamples;
    private static IEndpointRouteBuilder? s_endpoints;

    private static void ConfigurePipeline(IApplicationBuilder app, Options o)
    {
        app.Use(static (ctx, next) => { Interlocked.Increment(ref s_requests); return next(ctx); });
        if (o.Mode == "bare")
        {
            Gate.Measure = false;
            app.Run(static ctx => ResponseWriter.WriteAsync(ctx, Fleet.Ok));
            return;
        }
        app.Use(Gate.Middleware);
        app.UseRouting();
        app.UseEndpoints(e =>
        {
            s_endpoints = e;
            e.MapReverseProxy(proxy =>
            {
                proxy.Use(static (ctx, next) =>
                {
                    if (Gate.t_armed)
                    {
                        Gate.t_armed = false;
                        Interlocked.Add(ref s_routingBytes, GC.GetAllocatedBytesForCurrentThread() - Gate.t_handoff);
                        Interlocked.Increment(ref s_routingSamples);
                    }
                    return next();
                });
                proxy.UseSessionAffinity();
                proxy.UseLoadBalancing();
                proxy.UsePassiveHealthChecks();
            });
        });
    }

    private static Process Spawn(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(typeof(E2E).Assembly.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    // -----------------------------------------------------------------------------------------
    // Upstream process
    // -----------------------------------------------------------------------------------------

    public static async Task<int> UpstreamAsync()
    {
        var port = Probe.FreePort();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = b.Build();
        app.Run(static ctx => ResponseWriter.WriteAsync(ctx, Fleet.Ok));
        await app.StartAsync();
        Console.WriteLine($"PORT {port}");
        await Console.In.ReadToEndAsync(); // until the parent closes stdin
        await app.StopAsync();
        return 0;
    }

    // -----------------------------------------------------------------------------------------
    // Load process: raw pipelined H1 sockets, or HttpClient H2 (prior knowledge). Prints WARM, then the window result.
    // -----------------------------------------------------------------------------------------

    public static async Task<int> LoadAsync(string[] a)
    {
        var port = int.Parse(a[1], CultureInfo.InvariantCulture);
        var proto = a[2];
        var path = a[3];
        var conns = int.Parse(a[4], CultureInfo.InvariantCulture);
        var depth = int.Parse(a[5], CultureInfo.InvariantCulture);
        var warm = int.Parse(a[6], CultureInfo.InvariantCulture);
        var measure = int.Parse(a[7], CultureInfo.InvariantCulture);
        var cf = a[8] == "cf";
        var vary = a[9] == "vary";
        var browser = a[10] == "browser";

        // Each connection uses one host (like a browser); paths rotate over 8 values if "vary".
        string HostFor(int c) => $"h{(c * 37) % Fleet.Hosts}.test";
        string PathFor(int i) => browser ? path.TrimEnd('/') + BrowserPaths[i % BrowserPaths.Length]
            : vary ? $"{path}{(path.EndsWith('/') ? "" : "/")}p{i % 8}" : path;
        // "browser": 13 Chrome-like headers (sec-ch-ua*, sec-fetch-*, priority, Cookie, varying Accept/Referer) and
        // 64 rotating paths with query strings, under the scenario's prefix (/x -> YARP, /static -> gate).
        (string, string)[] HeadersFor(int i) => browser ? BrowserHeaders(i)
            : [("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0.0.0"), ("Accept", "*/*")];

        long ok = 0, other = 0;
        var stop = new CancellationTokenSource();
        var tasks = new List<Task>();
        if (proto == "h1")
        {
            for (var c = 0; c < conns; c++)
            {
                var host = HostFor(c);
                var reqs = Enumerable.Range(0, browser ? 64 : 8).Select(i =>
                {
                    var sb = new StringBuilder($"GET {PathFor(i)} HTTP/1.1\r\nHost: {host}\r\n");
                    foreach (var (k, v) in HeadersFor(i)) sb.Append(k).Append(": ").Append(v).Append("\r\n");
                    if (cf) sb.Append("CF-Connecting-IP: 203.0.113.77\r\n");
                    return Encoding.ASCII.GetBytes(sb.Append("\r\n").ToString());
                }).ToArray();
                tasks.Add(Task.Run(() => H1Loop(port, reqs, depth, stop.Token, ref ok, ref other)));
            }
        }
        else
        {
            var client = Probe.Client(port, HttpVersion.Version20, maxConns: 1);
            for (var c = 0; c < conns * depth; c++)
            {
                var host = HostFor(c / depth);
                tasks.Add(Task.Run(async () =>
                {
                    var i = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}{PathFor(i++)}") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                        foreach (var (k, v) in HeadersFor(i - 1)) req.Headers.TryAddWithoutValidation(k, v);
                        if (cf) req.Headers.TryAddWithoutValidation("CF-Connecting-IP", "203.0.113.77");
                        using var resp = await client.SendAsync(req);
                        await resp.Content.ReadAsByteArrayAsync();
                        if ((int)resp.StatusCode < 400) Interlocked.Increment(ref ok); else Interlocked.Increment(ref other);
                    }
                }));
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(warm));
        var ok0 = Interlocked.Read(ref ok);
        var o0 = Interlocked.Read(ref other);
        Console.WriteLine("WARM");
        await Task.Delay(TimeSpan.FromSeconds(measure));
        Console.WriteLine($"{Interlocked.Read(ref ok) - ok0} ok, {Interlocked.Read(ref other) - o0} 4xx/5xx");
        await stop.CancelAsync();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        return 0;
    }

    private static readonly string[] BrowserPaths = Enumerable.Range(0, 64).Select(i => (i % 4) switch
    {
        0 => $"/assets/js/chunk-{i:x4}a9f3c2.js",
        1 => $"/api/v1/items/{1000 + i}?include=owner,tags&page={i % 7}",
        2 => $"/css/app.{i:x6}.css",
        _ => $"/images/products/{i}/thumbnail-320x240.webp",
    }).ToArray();

    private static (string, string)[] BrowserHeaders(int i) =>
    [
        ("sec-ch-ua", "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\""),
        ("sec-ch-ua-mobile", "?0"),
        ("sec-ch-ua-platform", "\"Linux\""),
        ("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36"),
        ("Accept", i % 2 == 0 ? "*/*" : "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8"),
        ("Sec-Fetch-Site", "same-origin"),
        ("Sec-Fetch-Mode", i % 2 == 0 ? "cors" : "no-cors"),
        ("Sec-Fetch-Dest", i % 2 == 0 ? "script" : "image"),
        ("Referer", $"https://app.example.test/dashboard/{i % 5}"),
        ("Accept-Encoding", "gzip, deflate, br, zstd"),
        ("Accept-Language", "en-US,en;q=0.9,id;q=0.8"),
        ("Cookie", "session=8f14e45fceea167a5a36dedd4bea2543; theme=dark; _ga=GA1.1.123456789.1700000000"),
        ("priority", "u=1, i"),
    ];

    private static void H1Loop(int port, byte[][] reqs, int depth, CancellationToken ct, ref long ok, ref long other)
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        s.Connect(IPAddress.Loopback, port);
        var buf = new byte[1 << 16];
        var batch = new List<byte>();
        int fill = 0, next = 0;
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            for (var i = 0; i < depth; i++) batch.AddRange(reqs[next++ % reqs.Length]);
            s.Send(batch.ToArray());
            var got = 0;
            while (got < depth)
            {
                // parse as many complete responses as are in buf[0..fill)
                var consumed = 0;
                while (got < depth && TryParse(buf.AsSpan(consumed, fill - consumed), out var len, out var status))
                {
                    consumed += len; got++;
                    if (status < 400) Interlocked.Increment(ref ok); else Interlocked.Increment(ref other);
                }
                if (consumed > 0) { Buffer.BlockCopy(buf, consumed, buf, 0, fill - consumed); fill -= consumed; }
                if (got < depth)
                {
                    var n = s.Receive(buf, fill, buf.Length - fill, SocketFlags.None);
                    if (n == 0) return;
                    fill += n;
                }
            }
        }
    }

    private static bool TryParse(ReadOnlySpan<byte> b, out int len, out int status)
    {
        len = 0; status = 0;
        var end = b.IndexOf("\r\n\r\n"u8);
        if (end < 0) return false;
        var head = b[..end];
        status = int.Parse(Encoding.ASCII.GetString(head.Slice(9, 3)), CultureInfo.InvariantCulture);
        var cl = 0;
        var idx = head.IndexOf("Content-Length: "u8);
        if (idx < 0) idx = head.IndexOf("content-length: "u8);
        if (idx >= 0)
        {
            var rest = head[(idx + 16)..];
            var eol = rest.IndexOf("\r\n"u8);
            cl = int.Parse(Encoding.ASCII.GetString(eol < 0 ? rest : rest[..eol]), CultureInfo.InvariantCulture);
        }
        if (b.Length < end + 4 + cl) return false;
        len = end + 4 + cl;
        return true;
    }
}

/// <summary>GC AllocationTick sampling (~every 100 KB per heap): which types the proxy allocates in steady state.</summary>
public sealed class TickListener : EventListener
{
    private readonly ConcurrentDictionary<string, (long Count, long Bytes)> _types = new();
    private volatile bool _on;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1); // GC keyword
            _on = true;
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_on || e.EventName is null || !e.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) || e.Payload is null) return;
        var name = e.PayloadNames!.IndexOf("TypeName") is var ti and >= 0 ? e.Payload[ti]?.ToString() ?? "?" : "?";
        var amt = e.PayloadNames!.IndexOf("AllocationAmount64") is var ai and >= 0 ? Convert.ToInt64(e.Payload[ai], CultureInfo.InvariantCulture) : 0;
        _types.AddOrUpdate(name, (1, amt), (_, v) => (v.Count + 1, v.Bytes + amt));
    }

    public void Print(long requests)
    {
        var total = _types.Values.Sum(v => v.Bytes);
        Console.WriteLine($"  AllocationTick samples (sampled ~100 KB each; share = ticks of type / all ticks; listener's own allocations included):");
        foreach (var (k, v) in _types.OrderByDescending(kv => kv.Value.Bytes).Take(25))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {v.Bytes * 100.0 / Math.Max(1, total),5:F1}%  {v.Count,6} ticks  {k}"));
    }
}

/// <summary>Guard for the implicit authorization middleware: counts fallback-policy lookups (expected 0 per request).</summary>
public sealed class SpyPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    public static long FallbackCalls;
    private readonly DefaultAuthorizationPolicyProvider _inner = new(options);

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
    {
        Interlocked.Increment(ref FallbackCalls);
        return _inner.GetFallbackPolicyAsync();
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _inner.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => _inner.GetPolicyAsync(policyName);
}
