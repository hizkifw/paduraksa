using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace S2;

// ---------------------------------------------------------------------------------------------
// Metadata types attached to endpoints
// ---------------------------------------------------------------------------------------------

/// <summary>Naive design: a per-generation object (stand-in for CompiledHost) attached as endpoint metadata.</summary>
public sealed class CompiledHostRef(long generation, int slot, long routeVersion)
{
    public long Generation { get; } = generation;
    public int Slot { get; } = slot;
    /// <summary>Bumped by the compiler only when the YARP-relevant content of the route changes.</summary>
    public long RouteVersion { get; } = routeVersion;
}

/// <summary>Indirection design: one process-lifetime object per stable host identity (like MetricSlot).</summary>
public sealed class HostSlot(int index)
{
    public int Index { get; } = index;
    private static readonly ConcurrentDictionary<int, HostSlot> s_slots = new();
    public static HostSlot For(int index) => s_slots.GetOrAdd(index, static i => new HostSlot(i));
}

// ---------------------------------------------------------------------------------------------
// Snapshot == IProxyConfig (+ the bits of RuntimeSnapshot the spike needs)
// ---------------------------------------------------------------------------------------------

public sealed class SpikeSnapshot : IProxyConfig
{
    private readonly CancellationTokenSource _cts = new();

    public SpikeSnapshot(long generation, IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters, long[] routeVersionBySlot)
    {
        Generation = generation;
        Routes = routes;
        Clusters = clusters;
        RouteVersionBySlot = routeVersionBySlot;
        RevisionId = generation.ToString(CultureInfo.InvariantCulture);
        ChangeToken = new CancellationChangeToken(_cts.Token);
    }

    public long Generation { get; }
    public long[] RouteVersionBySlot { get; }
    public string RevisionId { get; }
    public IReadOnlyList<RouteConfig> Routes { get; }
    public IReadOnlyList<ClusterConfig> Clusters { get; }
    public IChangeToken ChangeToken { get; }

    internal void Expire() => _cts.Cancel();
}

public sealed class SpikeProvider(SpikeSnapshot initial) : IProxyConfigProvider
{
    private SpikeSnapshot _current = initial;
    public long GetConfigCalls;

    public SpikeSnapshot Current => Volatile.Read(ref _current);

    public IProxyConfig GetConfig()
    {
        Interlocked.Increment(ref GetConfigCalls);
        return Volatile.Read(ref _current);
    }

    /// <summary>Atomic swap, then fire the OLD snapshot's token. YARP's reload runs inline inside Cancel().</summary>
    public void Publish(SpikeSnapshot next)
    {
        var old = Interlocked.Exchange(ref _current, next);
        old.Expire();
    }
}

// ---------------------------------------------------------------------------------------------
// Config "compiler"
// ---------------------------------------------------------------------------------------------

public sealed class HostSpec
{
    public required int Slot;
    public required string Host;
    public required string ClusterId;
    public string Path = "/{**catch-all}";
    public string? MetaTag;
    public long RouteVersion = 1;
    public HostSpec Clone() => (HostSpec)MemberwiseClone();
}

public sealed class ClusterSpec
{
    public required string Id;
    public required (string Id, string Address)[] Destinations;
    public HealthCheckConfig? Health;
    public string? LbPolicy;
    public string? MetaTag;
    public ClusterSpec Clone() => (ClusterSpec)MemberwiseClone();
}

public static class Compiler
{
    /// <summary>RouteConfig instance → the metadata objects the compiler produced alongside it.</summary>
    public static readonly ConditionalWeakTable<RouteConfig, CompiledHostRef> Bindings = new();

    /// <summary>Experiment: no Host matching in YARP routes (a gate could pick the endpoint itself).</summary>
    public static bool PathOnlyRoutes;

    public static SpikeSnapshot Build(long gen, IReadOnlyList<HostSpec> hosts, IReadOnlyList<ClusterSpec> clusters, bool genInRouteId, int slotCapacity)
    {
        var routes = new RouteConfig[hosts.Count];
        var versions = new long[slotCapacity];
        for (var i = 0; i < hosts.Count; i++)
        {
            var h = hosts[i];
            var r = new RouteConfig
            {
                RouteId = genInRouteId ? $"{gen}:{h.Slot}:0" : $"h{h.Slot}",
                ClusterId = h.ClusterId,
                Match = PathOnlyRoutes ? new RouteMatch { Path = $"/__r/{h.Slot}/{{**catch-all}}" } : new RouteMatch { Hosts = [h.Host], Path = h.Path },
                Metadata = h.MetaTag is null ? null : new Dictionary<string, string> { ["tag"] = h.MetaTag },
            };
            routes[i] = r;
            versions[h.Slot] = h.RouteVersion;
            Bindings.Add(r, new CompiledHostRef(gen, h.Slot, h.RouteVersion));
        }

        var cl = new ClusterConfig[clusters.Count];
        for (var i = 0; i < clusters.Count; i++)
        {
            var c = clusters[i];
            var d = new Dictionary<string, DestinationConfig>(c.Destinations.Length);
            foreach (var (id, addr) in c.Destinations) d[id] = new DestinationConfig { Address = addr };
            cl[i] = new ClusterConfig
            {
                ClusterId = c.Id,
                Destinations = d,
                HealthCheck = c.Health,
                LoadBalancingPolicy = c.LbPolicy,
                Metadata = c.MetaTag is null ? null : new Dictionary<string, string> { ["tag"] = c.MetaTag },
            };
        }
        return new SpikeSnapshot(gen, routes, cl, versions);
    }

    /// <summary>N hosts, each 1 route (Host + /{**catch-all}) → own cluster with 1–2 destinations.</summary>
    public static (List<HostSpec> Hosts, List<ClusterSpec> Clusters) Fleet(int n, int upstreamPort)
    {
        var hosts = new List<HostSpec>(n + 1);
        var clusters = new List<ClusterSpec>(n + 1);
        for (var i = 0; i < n; i++)
        {
            var cid = $"c{i}";
            hosts.Add(new HostSpec { Slot = i, Host = $"h{i}.test", ClusterId = cid });
            clusters.Add(new ClusterSpec
            {
                Id = cid,
                Destinations = i % 2 == 0
                    ? [($"{cid}-d0", $"http://127.0.0.1:{upstreamPort}/")]
                    : [($"{cid}-d0", $"http://127.0.0.1:{upstreamPort}/"), ($"{cid}-d1", $"http://localhost:{upstreamPort}/")],
            });
        }
        return (hosts, clusters);
    }
}

// ---------------------------------------------------------------------------------------------
// Proxy host
// ---------------------------------------------------------------------------------------------

public sealed class GateFeature(SpikeSnapshot snapshot)
{
    public SpikeSnapshot Snapshot { get; } = snapshot;
}

public static class PipelineStats
{
    public static long Requests, MetaAllocBytes, MissingMeta, StaleGeneration, RouteVersionMismatch;
    public static long CallbackCount;
    public static readonly ConcurrentQueue<(string RouteId, long Gen)> CallbackLog = new();
    public static bool LogCallbacks;

    public static void Reset()
    {
        Requests = MetaAllocBytes = MissingMeta = StaleGeneration = RouteVersionMismatch = 0;
    }
}

public sealed class ProxyHost : IAsyncDisposable
{
    public required WebApplication App;
    public required int Port;
    public required SpikeProvider Provider;
    public required EndpointDataSource YarpEndpoints;
    public required IProxyStateLookup State;

    public static async Task<ProxyHost> StartAsync(SpikeSnapshot initial)
    {
        var port = Net.FreePort();
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        b.Logging.ClearProviders();
        b.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        b.Logging.AddFilter("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.None); // expected 502s in passive test
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var provider = new SpikeProvider(initial);
        b.Services.AddSingleton<IProxyConfigProvider>(provider);
        b.Services.AddReverseProxy();
        // Faster passive-health decisions for the test.
        b.Services.Configure<TransportFailureRateHealthPolicyOptions>(o => { o.MinimalTotalCountThreshold = 5; o.DefaultFailureRateLimit = 0.3; });
        var app = b.Build();

        // "Gate": captures the snapshot ONCE before routing (PLAN §7.2 C).
        app.Use((ctx, next) =>
        {
            ctx.Features.Set(new GateFeature(provider.Current));
            return next(ctx);
        });
        app.UseRouting();

        app.MapReverseProxy(proxy =>
        {
            proxy.Use(static (ctx, next) =>
            {
                var ep = ctx.GetEndpoint()!;
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var href = ep.Metadata.GetMetadata<CompiledHostRef>();
                var slot = ep.Metadata.GetMetadata<HostSlot>();
                var a1 = GC.GetAllocatedBytesForCurrentThread();
                Interlocked.Add(ref PipelineStats.MetaAllocBytes, a1 - a0);
                Interlocked.Increment(ref PipelineStats.Requests);

                var snap = ctx.Features.Get<GateFeature>()!.Snapshot;
                if (href is null || slot is null) Interlocked.Increment(ref PipelineStats.MissingMeta);
                else
                {
                    if (href.Generation != snap.Generation) Interlocked.Increment(ref PipelineStats.StaleGeneration);
                    if (href.RouteVersion != snap.RouteVersionBySlot[slot.Index]) Interlocked.Increment(ref PipelineStats.RouteVersionMismatch);
                }

                if (ctx.Request.Headers.ContainsKey("x-spike-echo"))
                {
                    ctx.Response.Headers["x-meta-gen"] = href?.Generation.ToString(CultureInfo.InvariantCulture) ?? "none";
                    ctx.Response.Headers["x-snap-gen"] = snap.Generation.ToString(CultureInfo.InvariantCulture);
                    ctx.Response.Headers["x-route-id"] = ctx.GetReverseProxyFeature().Route.Config.RouteId;
                    ctx.Response.StatusCode = 299;
                    return Task.CompletedTask;
                }
                return next();
            });
            // A custom pipeline REPLACES the default one: re-add the standard steps explicitly.
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        })
        .ConfigureEndpoints(static (IEndpointConventionBuilder ep, RouteConfig route) =>
        {
            Interlocked.Increment(ref PipelineStats.CallbackCount);
            if (!Compiler.Bindings.TryGetValue(route, out var href))
                throw new InvalidOperationException($"No binding for RouteConfig instance {route.RouteId}");
            if (PipelineStats.LogCallbacks) PipelineStats.CallbackLog.Enqueue((route.RouteId, href.Generation));
            ep.WithMetadata(href, HostSlot.For(href.Slot));
        });

        await app.StartAsync();
        var eds = ((IEndpointRouteBuilder)app).DataSources.Single(d => d.GetType().FullName!.Contains("ProxyConfigManager", StringComparison.Ordinal));
        return new ProxyHost
        {
            App = app, Port = port, Provider = provider, YarpEndpoints = eds,
            State = app.Services.GetRequiredService<IProxyStateLookup>(),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------------------------
// Upstream: tiny Kestrel app. Anything under /bad → 500, else 200 "ok".
// ---------------------------------------------------------------------------------------------

public sealed class Upstream : IAsyncDisposable
{
    public required WebApplication App;
    public required int Port;

    public static async Task<Upstream> StartAsync()
    {
        var port = Net.FreePort();
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var app = b.Build();
        var ok = "ok"u8.ToArray();
        app.Run(ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/bad")) { ctx.Response.StatusCode = 500; return Task.CompletedTask; }
            if (ctx.Request.Headers.ContainsKey("x-want-path")) ctx.Response.Headers["x-up-path"] = ctx.Request.Path.Value; // gate probe only
            ctx.Response.ContentLength = ok.Length;
            return ctx.Response.Body.WriteAsync(ok).AsTask();
        });
        await app.StartAsync();
        return new Upstream { App = app, Port = port };
    }

    public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
}

public static class Net
{
    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static HttpClient Client(int maxConns = 256) => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = maxConns,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<HttpResponseMessage> Get(HttpClient c, int port, string host, bool echo = false, string path = "/")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        req.Headers.Host = host;
        if (echo) req.Headers.Add("x-spike-echo", "1");
        return await c.SendAsync(req);
    }
}

// ---------------------------------------------------------------------------------------------
// Load generator
// ---------------------------------------------------------------------------------------------

public sealed class LoadResult
{
    public long Ok, Non2xx, Status5xx, Status404, Errors;
    public ConcurrentDictionary<string, long> ErrorKinds = new();
    public ConcurrentDictionary<int, long> StatusCounts = new();
    public double Seconds;
    public double[] LatMs = [];

    public double Pct(double p) => LatMs.Length == 0 ? double.NaN : LatMs[Math.Min(LatMs.Length - 1, (int)(p / 100.0 * LatMs.Length))];

    public override string ToString()
    {
        var total = Ok + Non2xx + Errors;
        var s = $"{total,8} req in {Seconds:F1}s = {total / Seconds,8:F0} rps | p50 {Pct(50):F2} ms  p99 {Pct(99):F2} ms  p99.9 {Pct(99.9):F2} ms  max {Pct(100):F1} ms | non-2xx {Non2xx} (5xx {Status5xx}, 404 {Status404})  errors {Errors}";
        if (!ErrorKinds.IsEmpty) s += "  [" + string.Join(", ", ErrorKinds.Select(kv => $"{kv.Key}={kv.Value}")) + "]";
        if (Non2xx > 0) s += "  status{" + string.Join(", ", StatusCounts.Where(kv => kv.Key >= 300).Select(kv => $"{kv.Key}={kv.Value}")) + "}";
        return s;
    }
}

public static class LoadGen
{
    public static async Task<LoadResult> RunAsync(HttpClient client, int port, string[] hosts, int concurrency, TimeSpan duration)
    {
        var res = new LoadResult();
        var lats = new List<double>[concurrency];
        var sw = Stopwatch.StartNew();
        var deadline = sw.Elapsed + duration;
        var tasks = new Task[concurrency];
        var uri = new Uri($"http://127.0.0.1:{port}/x");
        for (var w = 0; w < concurrency; w++)
        {
            var mine = lats[w] = new List<double>(200_000);
            var seed = w;
            tasks[w] = Task.Run(async () =>
            {
                var rnd = new Random(seed);
                while (sw.Elapsed < deadline)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                        req.Headers.Host = hosts[rnd.Next(hosts.Length)];
                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead);
                        var code = (int)resp.StatusCode;
                        res.StatusCounts.AddOrUpdate(code, 1, static (_, v) => v + 1);
                        if (code is >= 200 and < 300) Interlocked.Increment(ref res.Ok);
                        else
                        {
                            Interlocked.Increment(ref res.Non2xx);
                            if (code >= 500) Interlocked.Increment(ref res.Status5xx);
                            if (code == 404) Interlocked.Increment(ref res.Status404);
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref res.Errors);
                        var key = e.GetType().Name + (e.InnerException is null ? "" : "/" + e.InnerException.GetType().Name);
                        res.ErrorKinds.AddOrUpdate(key, 1, static (_, v) => v + 1);
                    }
                    mine.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                }
            });
        }
        await Task.WhenAll(tasks);
        res.Seconds = sw.Elapsed.TotalSeconds;
        res.LatMs = lats.SelectMany(l => l).Order().ToArray();
        return res;
    }
}

public static class Stat
{
    public static string Summ(IEnumerable<double> xs, string unit = "ms", string fmt = "F2")
    {
        var a = xs.Order().ToArray();
        if (a.Length == 0) return "n/a";
        double P(double p) => a[Math.Min(a.Length - 1, (int)(p / 100.0 * a.Length))];
        return $"median {P(50).ToString(fmt, CultureInfo.InvariantCulture)} {unit}, p90 {P(90).ToString(fmt, CultureInfo.InvariantCulture)}, max {a[^1].ToString(fmt, CultureInfo.InvariantCulture)} (n={a.Length})";
    }
}
