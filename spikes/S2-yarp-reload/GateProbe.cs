using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Routing;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace S2;

// =============================================================================================
// Review follow-up probe: the gate selects YARP's endpoint itself (HttpContext.SetEndpoint before
// UseRouting), from a snapshot that is swapped in only after YARP reported ConfigurationApplied.
// Also: the "routing-first + {current, previous} version check" fallback, and the old host-matched
// layout, for comparison.
// =============================================================================================

public enum GateMode
{
    /// <summary>Old layout: gate before routing, YARP matches on Host.</summary>
    HostMatch,
    /// <summary>Proposal: gate resolves host → slot → YARP Endpoint from its snapshot and sets it; routing skips matching.</summary>
    GateSelect,
    /// <summary>Fallback: UseRouting (Host match) first, gate after it checks the binding against {current, previous}.</summary>
    RoutingFirst,
}

/// <summary>Stand-in for RuntimeSnapshot: YARP config + host table + slot → YARP Endpoint table.</summary>
public sealed class GateSnapshot
{
    public required SpikeSnapshot Yarp { get; init; }
    public required Dictionary<string, int> HostToSlot { get; init; }
    public required string?[] RouteIdBySlot { get; init; }
    /// <summary>Filled after ConfigurationApplied, before the snapshot is swapped in. Immutable afterwards.</summary>
    public Endpoint?[] EndpointBySlot { get; set; } = [];
    public long Generation => Yarp.Generation;

    public static GateSnapshot From(SpikeSnapshot y, IReadOnlyList<HostSpec> hosts, int cap)
    {
        var map = new Dictionary<string, int>(hosts.Count, StringComparer.OrdinalIgnoreCase);
        var ids = new string?[cap];
        for (var i = 0; i < hosts.Count; i++)
        {
            map[hosts[i].Host] = hosts[i].Slot;
            ids[hosts[i].Slot] = y.Routes[i].RouteId;
        }
        return new GateSnapshot { Yarp = y, HostToSlot = map, RouteIdBySlot = ids };
    }
}

/// <summary>Completes the publisher's wait when YARP reports the outcome for a given RevisionId.</summary>
public sealed class ApplyListener : IConfigChangeListener
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Exception?>> _waiters = new(StringComparer.Ordinal);
    public Func<IReadOnlyList<IProxyConfig>, bool>? EndpointsReadyCheck;
    public long Applied, ApplyFailed, AppliedInline, EndpointsReadyAtApplied, EndpointsNotReadyAtApplied;
    [ThreadStatic] public static bool t_inPublish;

    public Task<Exception?> Expect(string revisionId)
    {
        var tcs = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[revisionId] = tcs;
        return tcs.Task;
    }

    private void Complete(IReadOnlyList<IProxyConfig> configs, Exception? ex)
    {
        foreach (var c in configs)
            if (_waiters.TryRemove(c.RevisionId, out var tcs)) tcs.TrySetResult(ex);
    }

    public void ConfigurationLoadingFailed(IProxyConfigProvider configProvider, Exception ex) { }
    public void ConfigurationLoaded(IReadOnlyList<IProxyConfig> proxyConfigs) { }

    public void ConfigurationApplyingFailed(IReadOnlyList<IProxyConfig> proxyConfigs, Exception ex)
    {
        Interlocked.Increment(ref ApplyFailed);
        Complete(proxyConfigs, ex);
    }

    public void ConfigurationApplied(IReadOnlyList<IProxyConfig> proxyConfigs)
    {
        Interlocked.Increment(ref Applied);
        if (t_inPublish) Interlocked.Increment(ref AppliedInline);
        if (EndpointsReadyCheck is { } chk)
        {
            if (chk(proxyConfigs)) Interlocked.Increment(ref EndpointsReadyAtApplied);
            else Interlocked.Increment(ref EndpointsNotReadyAtApplied);
        }
        Complete(proxyConfigs, null);
    }
}

public static class GateStats
{
    public static long Requests, GateAllocBytes, SpanAllocBytes, SpanSamples, UnknownHost, MatchCurrent, MatchPrevious, MatchPending, Mismatch503, ProxyRequests;
    [ThreadStatic] public static long t_a0;
    [ThreadStatic] public static bool t_armed;

    public static void Reset() => Requests = GateAllocBytes = SpanAllocBytes = SpanSamples = UnknownHost = MatchCurrent = MatchPrevious = MatchPending = Mismatch503 = ProxyRequests = 0;
}

public sealed class GateHost : IAsyncDisposable
{
    // Static so the middlewares can be static lambdas (one GateHost at a time in this spike).
    private static GateSnapshot? s_current, s_previous, s_pending;
    /// <summary>RoutingFirst only: also accept the snapshot being published (YARP may already route with it).</summary>
    public static bool CheckPending;
    public static GateSnapshot? Pinned;      // probe (ii): requests with "x-pin" use this (old) snapshot
    public static GateMode Mode;
    public static GateSnapshot Current => Volatile.Read(ref s_current)!;

    public required WebApplication App;
    public required int Port;
    public required SpikeProvider Provider;
    public required EndpointDataSource YarpEndpoints;
    public required IProxyStateLookup State;
    public required ApplyListener Listener;
    private readonly SemaphoreSlim _publishLock = new(1, 1);

    public static async Task<GateHost> StartAsync(GateMode mode, SpikeSnapshot initialYarp, IReadOnlyList<HostSpec> hosts, int cap)
    {
        Mode = mode;
        var port = Net.FreePort();
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        b.Logging.ClearProviders();
        b.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        b.Logging.AddFilter("Yarp.ReverseProxy.Forwarder.HttpForwarder", LogLevel.None);
        b.Logging.AddFilter("Yarp.ReverseProxy.Health", LogLevel.Error);
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var provider = new SpikeProvider(initialYarp);
        var listener = new ApplyListener();
        b.Services.AddSingleton<IProxyConfigProvider>(provider);
        b.Services.AddSingleton<IConfigChangeListener>(listener);
        b.Services.AddReverseProxy();
        b.Services.Configure<TransportFailureRateHealthPolicyOptions>(o => { o.MinimalTotalCountThreshold = 5; o.DefaultFailureRateLimit = 0.3; });
        var app = b.Build();

        // Allocation stamp: gate entry → first proxy-pipeline middleware (same thread: nothing awaits in between).
        app.Use(static (ctx, next) =>
        {
            GateStats.t_armed = true;
            GateStats.t_a0 = GC.GetAllocatedBytesForCurrentThread();
            return next(ctx);
        });

        if (mode == GateMode.GateSelect)
        {
            app.Use(static (ctx, next) =>
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                var s = Volatile.Read(ref s_current)!;
                if (Pinned is not null && ctx.Request.Headers.ContainsKey("x-pin")) s = Pinned;
                string host = ctx.Request.Headers.Host.ToString(); // single value: returns the existing string
                if (!s.HostToSlot.TryGetValue(host, out var slot) || s.EndpointBySlot[slot] is not { } ep)
                {
                    Interlocked.Increment(ref GateStats.UnknownHost);
                    ctx.Response.StatusCode = 404;      // never falls through to routing
                    return Task.CompletedTask;
                }
                ctx.SetEndpoint(ep);
                Interlocked.Add(ref GateStats.GateAllocBytes, GC.GetAllocatedBytesForCurrentThread() - a0);
                Interlocked.Increment(ref GateStats.Requests);
                return next(ctx);
            });
            app.UseRouting();   // must skip matching because an endpoint is already set
        }
        else if (mode == GateMode.HostMatch)
        {
            app.UseRouting();
        }
        else // RoutingFirst
        {
            app.UseRouting();
            app.Use(static (ctx, next) =>
            {
                var ep = ctx.GetEndpoint();
                var b = ep?.Metadata.GetMetadata<CompiledHostRef>();
                if (b is null) return next(ctx);
                var cur = Volatile.Read(ref s_current)!;
                if (cur.Yarp.RouteVersionBySlot[b.Slot] == b.RouteVersion) Interlocked.Increment(ref GateStats.MatchCurrent);
                else if (Volatile.Read(ref s_previous) is { } prev && prev.Yarp.RouteVersionBySlot[b.Slot] == b.RouteVersion) Interlocked.Increment(ref GateStats.MatchPrevious);
                else if (CheckPending && Volatile.Read(ref s_pending) is { } pend && pend.Yarp.RouteVersionBySlot[b.Slot] == b.RouteVersion) Interlocked.Increment(ref GateStats.MatchPending);
                else
                {
                    Interlocked.Increment(ref GateStats.Mismatch503);
                    ctx.Response.StatusCode = 503;
                    return Task.CompletedTask;
                }
                return next(ctx);
            });
        }

        app.MapReverseProxy(proxy =>
        {
            proxy.Use(static (ctx, next) =>
            {
                if (GateStats.t_armed)
                {
                    Interlocked.Add(ref GateStats.SpanAllocBytes, GC.GetAllocatedBytesForCurrentThread() - GateStats.t_a0);
                    Interlocked.Increment(ref GateStats.SpanSamples);
                    GateStats.t_armed = false;
                }
                Interlocked.Increment(ref GateStats.ProxyRequests);
                return next();
            });
            proxy.UseSessionAffinity();
            proxy.UseLoadBalancing();
            proxy.UsePassiveHealthChecks();
        })
        .ConfigureEndpoints(static (IEndpointConventionBuilder ep, RouteConfig route) =>
        {
            if (Compiler.Bindings.TryGetValue(route, out var href)) ep.WithMetadata(href);
        });

        await app.StartAsync();
        var eds = ((IEndpointRouteBuilder)app).DataSources.Single(d => d.GetType().FullName!.Contains("ProxyConfigManager", StringComparison.Ordinal));
        listener.EndpointsReadyCheck = cfgs =>
        {
            // At ConfigurationApplied time, does the data source already expose endpoints for exactly these RouteConfigs?
            var live = new Dictionary<string, RouteConfig>(StringComparer.Ordinal);
            foreach (var e in eds.Endpoints) if (e.Metadata.GetMetadata<RouteModel>() is { } rm) live[rm.Config.RouteId] = rm.Config;
            foreach (var c in cfgs)
                foreach (var r in c.Routes)
                    if (!live.TryGetValue(r.RouteId, out var lc) || !lc.Equals(r)) return false;
            return true;
        };
        var host = new GateHost
        {
            App = app, Port = port, Provider = provider, YarpEndpoints = eds, Listener = listener,
            State = app.Services.GetRequiredService<IProxyStateLookup>(),
        };
        var initial = GateSnapshot.From(initialYarp, hosts, cap);
        initial.EndpointBySlot = host.EndpointTable(initial);
        Volatile.Write(ref s_current, initial);
        return host;
    }

    public Endpoint?[] EndpointTable(GateSnapshot s)
    {
        var byId = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
        foreach (var e in YarpEndpoints.Endpoints)
            if (e.Metadata.GetMetadata<RouteModel>() is { } rm) byId[rm.Config.RouteId] = e;
        var t = new Endpoint?[s.RouteIdBySlot.Length];
        for (var i = 0; i < t.Length; i++)
            if (s.RouteIdBySlot[i] is { } id) t[i] = byId.TryGetValue(id, out var e) ? e : throw new InvalidOperationException($"route {id} not live after ConfigurationApplied");
        return t;
    }

    public sealed record PublishTiming(double CancelMs, double AppliedMs, double TableMs, double TotalMs);

    /// <summary>Single writer: hand config to YARP → await ConfigurationApplied(RevisionId) → build endpoint table → swap.</summary>
    public async Task<PublishTiming> PublishAsync(GateSnapshot next)
    {
        await _publishLock.WaitAsync();
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            var applied = Listener.Expect(next.Yarp.RevisionId);
            Volatile.Write(ref s_pending, next);
            ApplyListener.t_inPublish = true;
            try { Provider.Publish(next.Yarp); }
            finally { ApplyListener.t_inPublish = false; }
            var t1 = Stopwatch.GetTimestamp();
            var ex = await applied.WaitAsync(TimeSpan.FromSeconds(10));
            if (ex is not null) throw new InvalidOperationException("YARP rejected revision " + next.Yarp.RevisionId, ex);
            var t2 = Stopwatch.GetTimestamp();
            next.EndpointBySlot = EndpointTable(next);
            var t3 = Stopwatch.GetTimestamp();
            Volatile.Write(ref s_previous, Volatile.Read(ref s_current));
            Volatile.Write(ref s_current, next);
            Volatile.Write(ref s_pending, null);
            return new PublishTiming(
                Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds,
                Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds,
                Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds,
                Stopwatch.GetElapsedTime(t0, t3).TotalMilliseconds);
        }
        finally { _publishLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Pinned = null;
        Volatile.Write(ref s_current, null);
        Volatile.Write(ref s_previous, null);
        Volatile.Write(ref s_pending, null);
    }
}

public static class GateScenarios
{
    private static async Task<(int Status, string? UpPath, string? Cookie)> Req(HttpClient c, int port, string host, bool pin = false, string? cookie = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/x");
        req.Headers.Host = host;
        req.Headers.Add("x-want-path", "1");
        if (pin) req.Headers.Add("x-pin", "1");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        using var resp = await c.SendAsync(req);
        var up = resp.Headers.TryGetValues("x-up-path", out var v) ? v.First() : null;
        var setCookie = resp.Headers.TryGetValues("Set-Cookie", out var sc) ? sc.First().Split(';')[0] : null;
        return ((int)resp.StatusCode, up, setCookie);
    }

    public static async Task ProbeAsync()
    {
        Compiler.PathOnlyRoutes = true;
        await using var up = await Upstream.StartAsync();
        var deadPort = Net.FreePort();
        var (hosts, clusters) = Compiler.Fleet(20, up.Port);
        var u = $"http://127.0.0.1:{up.Port}";
        clusters.Add(new ClusterSpec { Id = "c-lb", LbPolicy = "RoundRobin", Destinations = [("c-lb-a", $"{u}/a/"), ("c-lb-b", $"{u}/b/")] });
        clusters.Add(new ClusterSpec { Id = "c-health", Destinations = [("c-health-good", $"{u}/good/"), ("c-health-bad", $"{u}/bad/")],
            Health = new HealthCheckConfig { Active = new ActiveHealthCheckConfig { Enabled = true, Interval = TimeSpan.FromMilliseconds(300), Timeout = TimeSpan.FromSeconds(2), Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures, Path = "/health" } } });
        clusters.Add(new ClusterSpec { Id = "c-passive", Destinations = [("c-passive-good", $"{u}/p/"), ("c-passive-dead", $"http://127.0.0.1:{deadPort}/")],
            Health = new HealthCheckConfig { Passive = new PassiveHealthCheckConfig { Enabled = true, Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate, ReactivationPeriod = TimeSpan.FromMinutes(10) } } });
        clusters.Add(new ClusterSpec { Id = "c-gone", Destinations = [("c-gone-d", $"{u}/gone/")] });
        clusters.Add(new ClusterSpec { Id = "c-kept", Destinations = [("c-kept-d", $"{u}/kept/")] });
        clusters.Add(new ClusterSpec { Id = "c-moved", Destinations = [("c-moved-d", $"{u}/moved/")] });
        string[] extra = ["lb", "aff", "health", "passive", "gone", "routegone", "moved"];
        string[] extraCluster = ["c-lb", "c-aff", "c-health", "c-passive", "c-gone", "c-kept", "c-moved"];
        clusters.Add(new ClusterSpec { Id = "c-aff", Destinations = [("c-aff-a", $"{u}/a/"), ("c-aff-b", $"{u}/b/")] });
        for (var i = 0; i < extra.Length; i++) hosts.Add(new HostSpec { Slot = 20 + i, Host = $"{extra[i]}.test", ClusterId = extraCluster[i] });
        const int cap = 64;
        long gen = 1;

        // Session affinity needs ClusterConfig.SessionAffinity; ClusterSpec has no field for it, so patch after Build.
        SpikeSnapshot BuildY(long g)
        {
            var y = Compiler.Build(g, hosts, clusters, false, cap);
            var cl = y.Clusters.Select(c => c.ClusterId == "c-aff"
                ? c with { SessionAffinity = new SessionAffinityConfig { Enabled = true, Policy = "Cookie", AffinityKeyName = ".pd-aff" } }
                : c).ToArray();
            return new SpikeSnapshot(g, y.Routes, cl, y.RouteVersionBySlot);
        }

        await using var gh = await GateHost.StartAsync(GateMode.GateSelect, BuildY(gen), hosts, cap);
        using var http = Net.Client();
        Console.WriteLine($"[probe] mode=GateSelect, YARP routes are path-only '/__r/{{slot}}/{{**catch-all}}'; every request below is GET /x (matches NO route)");

        // ---- (i) routing skips matching; the right route/cluster is proxied
        var okAll = true;
        for (var i = 0; i < 20; i++)
        {
            var (st, path, _) = await Req(http, gh.Port, $"h{i}.test");
            okAll &= st == 200 && path == "/x";
        }
        var (stMoved, pMoved, _) = await Req(http, gh.Port, "moved.test");
        var (stUnknown, _, _) = await Req(http, gh.Port, "nope.test");
        Console.WriteLine($"\n(i) 20 fleet hosts via gate-selected endpoint, path /x: all 200 = {okAll}; moved.test -> {stMoved} upstream path {pMoved}; unknown host -> {stUnknown} (gate answers, never reaches routing)");
        Console.WriteLine($"    proxy-pipeline requests {GateStats.ProxyRequests}, gate-selected {GateStats.Requests}");

        // ---- (iv) LB, affinity, active + passive health with gate-selected endpoints
        var lbCounts = new Dictionary<string, int>();
        for (var i = 0; i < 40; i++)
        {
            var (_, p, _) = await Req(http, gh.Port, "lb.test");
            var k = p?.Split('/')[1] ?? "none";
            lbCounts[k] = lbCounts.GetValueOrDefault(k) + 1;
        }
        Console.WriteLine($"\n(iv) LB RoundRobin over 40 requests: {string.Join(", ", lbCounts.Select(kv => $"/{kv.Key}/={kv.Value}"))}");

        var (_, firstPath, cookie) = await Req(http, gh.Port, "aff.test");
        var sticky = new HashSet<string?>();
        for (var i = 0; i < 30; i++) { var (_, p, _) = await Req(http, gh.Port, "aff.test", cookie: cookie); sticky.Add(p); }
        Console.WriteLine($"     affinity: first -> {firstPath}, cookie '{cookie}'; next 30 with cookie hit {{{string.Join(",", sticky)}}}");

        var sw = Stopwatch.StartNew();
        DestinationState D(string c, string d) => gh.State.TryGetCluster(c, out var cs) ? cs.Destinations[d] : null!;
        while (D("c-health", "c-health-bad").Health.Active != DestinationHealth.Unhealthy && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(50);
        int h5 = 0, hGood = 0;
        for (var i = 0; i < 100; i++) { var (st, p, _) = await Req(http, gh.Port, "health.test"); if (st >= 500) h5++; if (p?.StartsWith("/good/", StringComparison.Ordinal) == true) hGood++; }
        Console.WriteLine($"     active health: c-health-bad={D("c-health", "c-health-bad").Health.Active}; 100 requests -> 5xx {h5}, served by /good/ {hGood}");

        int p502 = 0;
        for (var i = 0; i < 100 && D("c-passive", "c-passive-dead").Health.Passive != DestinationHealth.Unhealthy; i++)
            if ((await Req(http, gh.Port, "passive.test")).Status == 502) p502++;
        int p5after = 0;
        for (var i = 0; i < 100; i++) if ((await Req(http, gh.Port, "passive.test")).Status >= 500) p5after++;
        Console.WriteLine($"     passive health: c-passive-dead={D("c-passive", "c-passive-dead").Health.Passive} after {p502} x 502; next 100 requests -> 5xx {p5after}");

        // ---- (v) endpoint table from the data source after ConfigurationApplied
        Console.WriteLine($"\n(v) listener so far: Applied={gh.Listener.Applied}, endpoints already live at ConfigurationApplied: {gh.Listener.EndpointsReadyAtApplied} yes / {gh.Listener.EndpointsNotReadyAtApplied} no");

        // ---- (ii) old endpoint objects from a previous snapshot after YARP replaced/removed them
        var old = GateHost.Current;
        GateHost.Pinned = old;
        var oldGone = old.EndpointBySlot[24]!; var oldRouteGone = old.EndpointBySlot[25]!; var oldMoved = old.EndpointBySlot[26]!;
        hosts.RemoveAll(h => h.Host is "gone.test" or "routegone.test");             // route+cluster removed / route removed, cluster kept
        clusters.RemoveAll(c => c.Id == "c-gone");
        var moved = hosts.First(h => h.Host == "moved.test"); moved.MetaTag = "v2"; moved.RouteVersion++;   // route replaced (new endpoint)
        clusters[clusters.FindIndex(c => c.Id == "c-moved")] = new ClusterSpec { Id = "c-moved", Destinations = [("c-moved-d2", $"{u}/moved2/")] }; // its destination replaced
        gen++;
        var t = await gh.PublishAsync(GateSnapshot.From(BuildY(gen), hosts, cap));
        var liveEps = gh.YarpEndpoints.Endpoints;
        Console.WriteLine($"\n(ii) published gen {gen}: removed gone.test (route+cluster), routegone.test (route only), replaced moved.test route + its destination. publish {t.TotalMs:F2} ms");
        Console.WriteLine($"     old endpoint objects still in YARP data source? gone={liveEps.Contains(oldGone)}, routegone={liveEps.Contains(oldRouteGone)}, moved={liveEps.Contains(oldMoved)}");
        for (var round = 0; round < 2; round++)
        {
            var g1 = await Req(http, gh.Port, "gone.test", pin: true);
            var g2 = await Req(http, gh.Port, "routegone.test", pin: true);
            var g3 = await Req(http, gh.Port, "moved.test", pin: true);
            var g4 = await Req(http, gh.Port, "moved.test");
            var g5 = await Req(http, gh.Port, "gone.test");
            Console.WriteLine($"     {(round == 0 ? "right after" : "+3 s      ")}: pinned OLD endpoints -> gone {g1.Status} ({g1.UpPath}), routegone {g2.Status} ({g2.UpPath}), moved {g3.Status} ({g3.UpPath}); current snapshot -> moved {g4.Status} ({g4.UpPath}), gone {g5.Status}");
            await Task.Delay(3000);
        }
        GateHost.Pinned = null;

        // ---- (iii) steady-state allocations: gate-select step and gate→proxy-pipeline span
        for (var i = 0; i < 3000; i++) await Req(http, gh.Port, $"h{i % 20}.test");
        GateStats.Reset();
        for (var i = 0; i < 5000; i++) await Req(http, gh.Port, $"h{i % 20}.test");
        Console.WriteLine($"\n(iii) GateSelect, 5000 warm requests: gate lookup+SetEndpoint {GateStats.GateAllocBytes / (double)GateStats.Requests:F1} B/req; gate entry -> proxy pipeline (incl. UseRouting) {GateStats.SpanAllocBytes / (double)Math.Max(1, GateStats.SpanSamples):F1} B/req (n={GateStats.SpanSamples})");
        Console.WriteLine($"     listener total: Applied={gh.Listener.Applied}, inline in Publish()={gh.Listener.AppliedInline}, endpoints live at ConfigurationApplied {gh.Listener.EndpointsReadyAtApplied} yes / {gh.Listener.EndpointsNotReadyAtApplied} no");
    }

    /// <summary>Same allocation span for the host-matched layout, for comparison.</summary>
    public static async Task AllocBaselineAsync()
    {
        Compiler.PathOnlyRoutes = false;
        await using var up = await Upstream.StartAsync();
        var (hosts, clusters) = Compiler.Fleet(20, up.Port);
        await using var gh = await GateHost.StartAsync(GateMode.HostMatch, Compiler.Build(1, hosts, clusters, false, 32), hosts, 32);
        using var http = Net.Client();
        for (var i = 0; i < 3000; i++) await Req(http, gh.Port, $"h{i % 20}.test");
        GateStats.Reset();
        for (var i = 0; i < 5000; i++) await Req(http, gh.Port, $"h{i % 20}.test");
        Console.WriteLine($"(iii) HostMatch baseline, 5000 warm requests: gate entry -> proxy pipeline (UseRouting with Host matching) {GateStats.SpanAllocBytes / (double)Math.Max(1, GateStats.SpanSamples):F1} B/req (n={GateStats.SpanSamples})");
    }

    /// <summary>Publish cost of the gate design (path-only routes, await ConfigurationApplied, endpoint table, swap).</summary>
    public static async Task PublishCostAsync(int n)
    {
        Compiler.PathOnlyRoutes = true;
        await using var up = await Upstream.StartAsync();
        var (hosts, clusters) = Compiler.Fleet(n, up.Port);
        hosts.Add(new HostSpec { Slot = n, Host = "probe.test", ClusterId = "c0" });
        var cap = n + 1;
        long gen = 1;
        await using var gh = await GateHost.StartAsync(GateMode.GateSelect, Compiler.Build(gen, hosts, clusters, false, cap), hosts, cap);
        var cancel = new List<double>(); var applied = new List<double>(); var table = new List<double>(); var total = new List<double>();
        const int warm = 40, measured = 30;
        for (var i = 0; i < warm + measured; i++)
        {
            gen++;
            hosts[n].MetaTag = gen.ToString(CultureInfo.InvariantCulture); hosts[n].RouteVersion++;
            var t = await gh.PublishAsync(GateSnapshot.From(Compiler.Build(gen, hosts, clusters, false, cap), hosts, cap));
            if (i < warm) continue;
            cancel.Add(t.CancelMs); applied.Add(t.AppliedMs); table.Add(t.TableMs); total.Add(t.TotalMs);
        }
        Console.WriteLine($"\n== gate design publish cost, n={n} hosts, 1 route changed per publish (idle)");
        Console.WriteLine($"   Cancel() returns          : {Stat.Summ(cancel)}");
        Console.WriteLine($"   ConfigurationApplied seen : {Stat.Summ(applied)}  (inline in Publish(): {gh.Listener.AppliedInline}/{gh.Listener.Applied}; endpoints already live at the callback: {gh.Listener.EndpointsReadyAtApplied} yes / {gh.Listener.EndpointsNotReadyAtApplied} no)");
        Console.WriteLine($"   endpoint table build      : {Stat.Summ(table)}");
        Console.WriteLine($"   total until swapped       : {Stat.Summ(total)}");
    }

    /// <summary>Short churn load: GateSelect vs RoutingFirst fallback; counts errors and fallback outcomes.</summary>
    public static async Task LoadAsync(GateMode mode, int n, int concurrency, int seconds, bool checkPending = false)
    {
        GateHost.CheckPending = checkPending;
        Compiler.PathOnlyRoutes = mode == GateMode.GateSelect;
        await using var up = await Upstream.StartAsync();
        var (hosts, clusters) = Compiler.Fleet(n, up.Port);
        var cap = n;
        long gen = 1;
        await using var gh = await GateHost.StartAsync(mode, Compiler.Build(gen, hosts, clusters, false, cap), hosts, cap);
        using var http = Net.Client(concurrency * 2);
        var names = hosts.Select(h => h.Host).ToArray();
        await LoadGen.RunAsync(http, gh.Port, names, concurrency, TimeSpan.FromSeconds(3));
        GateStats.Reset();
        using var cts = new CancellationTokenSource();
        var pub = new List<double>();
        var rnd = new Random(7);
        var publisher = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    gen++;
                    for (var k = 0; k < Math.Max(1, n / 20); k++)
                    {
                        var h = hosts[rnd.Next(n)]; h.MetaTag = gen.ToString(CultureInfo.InvariantCulture); h.RouteVersion++;
                        var j = rnd.Next(n);
                        var c = clusters[j] = clusters[j].Clone();
                        c.Destinations = c.Destinations.Length == 1 ? [c.Destinations[0], ($"c{j}-d1", $"http://localhost:{up.Port}/")] : [c.Destinations[0]];
                    }
                    var t = await gh.PublishAsync(GateSnapshot.From(Compiler.Build(gen, hosts, clusters, false, cap), hosts, cap));
                    pub.Add(t.TotalMs);
                }
            }
            catch (OperationCanceledException) { }
        });
        var res = await LoadGen.RunAsync(http, gh.Port, names, concurrency, TimeSpan.FromSeconds(seconds));
        cts.Cancel();
        await publisher;
        Console.WriteLine($"\n== {mode}: n={n}, churn (5% routes + 5% clusters) publish every 100 ms, {seconds}s");
        Console.WriteLine($"   {res}");
        Console.WriteLine($"   publishes {pub.Count}, publish->swapped {Stat.Summ(pub)}");
        if (mode == GateMode.RoutingFirst)
            Console.WriteLine($"   fallback check{(GateHost.CheckPending ? " {current, previous, pending}" : " {current, previous}")}: match current {GateStats.MatchCurrent}, previous {GateStats.MatchPrevious}, pending {GateStats.MatchPending}, mismatch -> 503 {GateStats.Mismatch503}");
        else
            Console.WriteLine($"   gate-selected {GateStats.Requests}, unknown host {GateStats.UnknownHost}");
    }
}
