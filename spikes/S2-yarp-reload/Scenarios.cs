using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace S2;

public static class Scenarios
{
    // =========================================================================================
    // Q1 + Q5: does ConfigureEndpoints re-run, with which RouteConfig, for which routes?
    // =========================================================================================
    public static async Task MetadataAsync()
    {
        await using var up = await Upstream.StartAsync();
        var (hosts, clusters) = Compiler.Fleet(6, up.Port);
        const int cap = 64;
        long gen = 1;
        PipelineStats.LogCallbacks = true;
        await using var px = await ProxyHost.StartAsync(Compiler.Build(gen, hosts, clusters, false, cap));
        using var http = Net.Client();

        async Task Report(string label)
        {
            var snap = px.Provider.Current;
            var cb = new List<(string, long)>();
            while (PipelineStats.CallbackLog.TryDequeue(out var e)) cb.Add(e);
            Console.WriteLine($"\n--- gen {snap.Generation}: {label}");
            Console.WriteLine($"    GetConfig() calls so far: {px.Provider.GetConfigCalls}; ConfigureEndpoints callbacks this publish: {cb.Count} -> [{string.Join(", ", cb.Select(c => $"{c.Item1}@g{c.Item2}"))}]");
            Console.WriteLine("    endpoint route        meta.gen  RouteModel.Config is current instance?  echo via pipeline (meta-gen/snap-gen)");
            foreach (var ep in px.YarpEndpoints.Endpoints.OrderBy(e => e.Metadata.GetMetadata<RouteModel>()!.Config.RouteId, StringComparer.Ordinal))
            {
                var rm = ep.Metadata.GetMetadata<RouteModel>()!;
                var href = ep.Metadata.GetMetadata<CompiledHostRef>();
                var cur = snap.Routes.FirstOrDefault(r => r.RouteId == rm.Config.RouteId);
                var same = cur is not null && ReferenceEquals(cur, rm.Config);
                var host = rm.Config.Match.Hosts![0];
                using var resp = await Net.Get(http, px.Port, host, echo: true);
                var echo = (int)resp.StatusCode == 299
                    ? $"{resp.Headers.GetValues("x-meta-gen").First()}/{resp.Headers.GetValues("x-snap-gen").First()}"
                    : $"HTTP {(int)resp.StatusCode}";
                Console.WriteLine($"    {rm.Config.RouteId,-18} {href?.Generation,8}  {same,-40} {echo}");
            }
        }

        void Publish(bool genIds)
        {
            gen++;
            var s = Compiler.Build(gen, hosts, clusters, genIds, cap);
            var sw = Stopwatch.StartNew();
            px.Provider.Publish(s);
            // Synchronous? Check endpoints straight after Publish() returns, before any await.
            var maxGen = px.YarpEndpoints.Endpoints.Max(e => e.Metadata.GetMetadata<CompiledHostRef>()!.Generation);
            Console.WriteLine($"\n[publish gen {gen}] Publish() took {sw.Elapsed.TotalMilliseconds:F2} ms on thread {Environment.CurrentManagedThreadId}; newest meta gen visible immediately after return = {maxGen}");
        }

        await Report("initial (startup)");

        Publish(false);
        await Report("identical content, NEW RouteConfig/ClusterConfig instances, stable route IDs");

        hosts[0].Path = "/{**rest}"; hosts[0].RouteVersion++;                // route match changed
        hosts[1].MetaTag = "v3"; hosts[1].RouteVersion++;                    // route string-metadata changed
        clusters[2] = clusters[2].Clone(); clusters[2].Destinations = [.. clusters[2].Destinations, ("c2-dx", $"http://127.0.0.1:{up.Port}/")]; // cluster dests changed
        clusters[3] = clusters[3].Clone(); clusters[3].LbPolicy = "RoundRobin";                                                                 // cluster policy changed
        hosts.RemoveAt(5); clusters.RemoveAt(5);                                                                                                // host removed
        hosts.Add(new HostSpec { Slot = 6, Host = "h6.test", ClusterId = "c6" });
        clusters.Add(new ClusterSpec { Id = "c6", Destinations = [("c6-d0", $"http://127.0.0.1:{up.Port}/")] });                              // host added
        Publish(false);
        await Report("h0 path changed, h1 metadata changed, c2 +destination, c3 LB policy changed, h5 removed, h6 added");

        Publish(true);
        await Report("same content, route IDs encode generation");

        Publish(true);
        await Report("same content, route IDs encode generation (again)");

        Publish(false);
        await Report("back to stable route IDs");

        // ---- Q1b: allocation of GetMetadata<T> in the pipeline after warm-up
        for (var i = 0; i < 2000; i++) { using var r = await Net.Get(http, px.Port, "h2.test", echo: true); }
        PipelineStats.Reset();
        for (var i = 0; i < 5000; i++) { using var r = await Net.Get(http, px.Port, $"h{i % 5}.test", echo: true); }
        Console.WriteLine($"\n[Q1b] GetMetadata<CompiledHostRef>() + GetMetadata<HostSlot>() over {PipelineStats.Requests} warm requests: {PipelineStats.MetaAllocBytes} bytes allocated total; missing metadata = {PipelineStats.MissingMeta}");

        // ---- Q5: back-to-back publishes from several threads; invalid config handling
        Console.WriteLine("\n[Q5] 4 threads x 25 concurrent publishes ...");
        var calls0 = px.Provider.GetConfigCalls;
        PipelineStats.LogCallbacks = false;
        var lockObj = new object();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                // Build AND publish under the lock: the provider swap order must equal generation order
                // (publishing outside the lock could let an older generation be swapped in last).
                lock (lockObj) { gen++; px.Provider.Publish(Compiler.Build(gen, hosts, clusters, true, cap)); }
            }
        })));
        var epGens = px.YarpEndpoints.Endpoints.Select(e => e.Metadata.GetMetadata<CompiledHostRef>()!.Generation).Distinct().ToArray();
        Console.WriteLine($"    after storm: provider gen {px.Provider.Current.Generation}, endpoint meta gens {{{string.Join(",", epGens)}}}, GetConfig() calls during storm {px.Provider.GetConfigCalls - calls0}");

        Console.WriteLine("\n[Q5] publishing an INVALID config (route with empty Match) ...");
        gen++;
        var bad = Compiler.Build(gen, hosts, clusters, false, cap);
        var badRoutes = bad.Routes.ToList();
        badRoutes[0] = badRoutes[0] with { Match = new RouteMatch() };
        Compiler.Bindings.AddOrUpdate(badRoutes[0], new CompiledHostRef(gen, 0, 0));
        var badSnap = new SpikeSnapshot(gen, badRoutes, bad.Clusters, bad.RouteVersionBySlot);
        try { px.Provider.Publish(badSnap); Console.WriteLine("    Publish() returned normally (no exception surfaced to publisher)"); }
        catch (Exception e) { Console.WriteLine($"    Publish() threw {e.GetType().Name}: {e.Message}"); }
        var g2 = px.YarpEndpoints.Endpoints.Select(e => e.Metadata.GetMetadata<CompiledHostRef>()!.Generation).Distinct().ToArray();
        Console.WriteLine($"    endpoint meta gens after invalid publish: {{{string.Join(",", g2)}}} (old config kept?)");
        using (var r = await Net.Get(http, px.Port, "h1.test")) Console.WriteLine($"    h1.test -> HTTP {(int)r.StatusCode}");
        Publish(false);
        var g3 = px.YarpEndpoints.Endpoints.Select(e => e.Metadata.GetMetadata<CompiledHostRef>()!.Generation).Distinct().ToArray();
        Console.WriteLine($"    next valid publish (gen {gen}) applied? endpoint meta gens {{{string.Join(",", g3)}}}");
    }

    // =========================================================================================
    // Q2: reload cost at 100 / 500 / 2000 hosts
    // =========================================================================================
    public static async Task ReloadAsync(int[] sizes, string[]? modes = null)
    {
        modes ??= ["one-route", "10pct-routes", "10pct-clusters", "noop", "gen-ids"];
        await using var up = await Upstream.StartAsync();
        Console.WriteLine($"Server GC = {System.Runtime.GCSettings.IsServerGC}, cores = {Environment.ProcessorCount}");
        foreach (var n in sizes)
        {
            foreach (var mode in modes)
            {
                PipelineStats.LogCallbacks = false;
                Compiler.PathOnlyRoutes = mode == "path-only";
                var (hosts, clusters) = Compiler.Fleet(n, up.Port);
                hosts.Add(new HostSpec { Slot = n, Host = "probe-g1.test", ClusterId = "c0" });
                long gen = 1;
                var cap = n + 1;
                var swStart = Stopwatch.StartNew();
                await using var px = await ProxyHost.StartAsync(Compiler.Build(gen, hosts, clusters, mode == "gen-ids", cap));
                using var http = Net.Client();
                using (var r0 = await Net.Get(http, px.Port, "h0.test", path: Compiler.PathOnlyRoutes ? "/__r/0/x" : "/")) { }
                var startMs = swStart.Elapsed.TotalMilliseconds;

                var build = new List<double>(); var pub = new List<double>(); var live = new List<double>();
                var alloc = new List<double>(); var cpu = new List<double>(); var cbs = new List<double>(); var misses = 0;
                var firstReq = new List<double>(); var steadyReq = new List<double>();
                var toSignal = new List<double>(); var afterSignal = new List<double>();
                var rnd = new Random(42);
                var markBox = new StrongBox<long>();
                var proc = Process.GetCurrentProcess();
                const int warm = 60, measured = 40;
                for (var i = 0; i < warm + measured; i++)
                {
                    gen++;
                    // noop: identical content (new instances) -> YARP rebuilds nothing.
                    // path-only: routes have no Host match (Path "/__r/{slot}/..."), probe route changes via metadata.
                    if (mode == "path-only") { hosts[n].MetaTag = gen.ToString(CultureInfo.InvariantCulture); hosts[n].RouteVersion++; }
                    else if (mode != "noop") { hosts[n].Host = $"probe-g{gen}.test"; hosts[n].RouteVersion++; }
                    var probeHost = hosts[n].Host;
                    var probePath = mode == "path-only" ? $"/__r/{n}/x" : "/";
                    switch (mode)
                    {
                        case "10pct-routes":
                            for (var k = 0; k < n / 10; k++) { var h = hosts[rnd.Next(n)]; h.MetaTag = gen.ToString(CultureInfo.InvariantCulture); h.RouteVersion++; }
                            break;
                        case "10pct-clusters":
                            for (var k = 0; k < n / 10; k++)
                            {
                                var j = rnd.Next(n);
                                var c = clusters[j] = clusters[j].Clone();
                                c.MetaTag = gen.ToString(CultureInfo.InvariantCulture);
                            }
                            break;
                    }
                    var tb = Stopwatch.GetTimestamp();
                    var snap = Compiler.Build(gen, hosts, clusters, mode == "gen-ids", cap);
                    var buildMs = Stopwatch.GetElapsedTime(tb).TotalMilliseconds;

                    var cb0 = PipelineStats.CallbackCount;
                    var a0 = GC.GetTotalAllocatedBytes(true);
                    proc.Refresh();
                    var c0 = proc.TotalProcessorTime;
                    long mark = 0;
                    // CancellationToken callbacks run LIFO, so this (latest) registration runs first when YARP
                    // signals "endpoints changed"; everything after it is ASP.NET routing reacting (matcher rebuild).
                    using var reg = px.YarpEndpoints.GetChangeToken().RegisterChangeCallback(static o => ((StrongBox<long>)o!).Value = Stopwatch.GetTimestamp(), markBox);
                    var t0 = Stopwatch.GetTimestamp();
                    px.Provider.Publish(snap);
                    var t1 = Stopwatch.GetTimestamp();
                    proc.Refresh();
                    var c1 = proc.TotalProcessorTime;
                    var a1 = GC.GetTotalAllocatedBytes(true);
                    var cbN = PipelineStats.CallbackCount - cb0;

                    // time until the new probe host answers 200
                    var tr = Stopwatch.GetTimestamp();
                    var first = true;
                    while (true)
                    {
                        using var r = await Net.Get(http, px.Port, probeHost, path: probePath);
                        if (first) { firstReq.Add(Stopwatch.GetElapsedTime(tr).TotalMilliseconds); first = false; }
                        if (r.IsSuccessStatusCode) break;
                        if (i >= warm) misses++;
                    }
                    var t2 = Stopwatch.GetTimestamp();
                    var ts = Stopwatch.GetTimestamp();
                    using (var r2 = await Net.Get(http, px.Port, probeHost, path: probePath)) { }
                    var steady = Stopwatch.GetElapsedTime(ts).TotalMilliseconds;

                    mark = markBox.Value; markBox.Value = 0;
                    if (i < warm) { firstReq.Clear(); continue; }
                    if (mark != 0) { toSignal.Add(Stopwatch.GetElapsedTime(t0, mark).TotalMilliseconds); afterSignal.Add(Stopwatch.GetElapsedTime(mark, t1).TotalMilliseconds); }
                    build.Add(buildMs);
                    pub.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
                    live.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
                    alloc.Add((a1 - a0) / 1024.0 / 1024.0);
                    cpu.Add((c1 - c0).TotalMilliseconds);
                    cbs.Add(cbN);
                    steadyReq.Add(steady);
                }
                Console.WriteLine($"\n== n={n} hosts, mode={mode}  (proxy start+first request {startMs:F0} ms)");
                Console.WriteLine($"   compile snapshot       : {Stat.Summ(build)}");
                Console.WriteLine($"   Publish() (inline)     : {Stat.Summ(pub)}");
                Console.WriteLine($"     of which YARP diff+endpoint build (until endpoints-changed signal): {Stat.Summ(toSignal)}");
                Console.WriteLine($"     of which after signal (ASP.NET routing matcher rebuild etc.):    {Stat.Summ(afterSignal)}");
                Console.WriteLine($"   publish->probe 200     : {Stat.Summ(live)}   (404 misses: {misses})");
                Console.WriteLine($"   1st req after publish  : {Stat.Summ(firstReq)}   vs 2nd req {Stat.Summ(steadyReq)}");
                Console.WriteLine($"   alloc during Publish   : {Stat.Summ(alloc, "MB")}");
                Console.WriteLine($"   process CPU in Publish : {Stat.Summ(cpu)}");
                Console.WriteLine($"   ConfigureEndpoints cbs : {Stat.Summ(cbs, "", "F0")}");
            }
        }
    }

    // =========================================================================================
    // Q2b + Q3: steady load while publishing every 100 ms
    // =========================================================================================
    public static async Task LoadAsync(int n, int concurrency, int seconds)
    {
        await using var up = await Upstream.StartAsync();
        var (hosts, clusters) = Compiler.Fleet(n, up.Port);
        hosts.Add(new HostSpec { Slot = n, Host = "probe-g1.test", ClusterId = "c0" });
        long gen = 1;
        var cap = n + 1;
        await using var px = await ProxyHost.StartAsync(Compiler.Build(gen, hosts, clusters, false, cap));
        using var http = Net.Client(concurrency * 2);
        var hostNames = Enumerable.Range(0, n).Select(i => $"h{i}.test").ToArray();
        Console.WriteLine($"n={n} hosts, concurrency={concurrency}, {seconds}s per phase, in-process client+proxy+upstream on {Environment.ProcessorCount} cores");

        // warm-up
        await LoadGen.RunAsync(http, px.Port, hostNames, concurrency, TimeSpan.FromSeconds(10));

        string[] phases =
        [
            "baseline (no publishes)",
            "identical-content publish every 100ms (YARP rebuilds nothing)",
            "baseline",
            "1-route publish every 100ms (probe route changes)",
            "baseline",
            "churn publish every 100ms (probe + 5% routes + 5% clusters change)",
            "baseline",
            "gen-ids + churn every 100ms (ALL endpoints rebuilt)",
            "baseline",
        ];
        foreach (var phase in phases)
        {
            var publishing = !phase.StartsWith("baseline", StringComparison.Ordinal);
            var genIds = phase.StartsWith("gen-ids", StringComparison.Ordinal);
            var churn = phase.Contains("churn", StringComparison.Ordinal);
            PipelineStats.Reset();
            using var cts = new CancellationTokenSource();
            var pubTimes = new List<double>();
            var rnd = new Random(7);
            var publisher = !publishing ? Task.CompletedTask : Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                try
                {
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        gen++;
                        if (!phase.StartsWith("identical", StringComparison.Ordinal)) { hosts[n].Host = $"probe-g{gen}.test"; hosts[n].RouteVersion++; }
                        if (churn)
                        {
                            for (var k = 0; k < Math.Max(1, n / 20); k++)
                            {
                                var h = hosts[rnd.Next(n)]; h.MetaTag = gen.ToString(CultureInfo.InvariantCulture); h.RouteVersion++;
                                var j = rnd.Next(n);
                                var c = clusters[j] = clusters[j].Clone();
                                // toggle 1 <-> 2 destinations (destination removed while requests may be in flight)
                                c.Destinations = c.Destinations.Length == 1
                                    ? [c.Destinations[0], ($"c{j}-d1", $"http://localhost:{up.Port}/")]
                                    : [c.Destinations[0]];
                            }
                        }
                        var s = Compiler.Build(gen, hosts, clusters, genIds, cap);
                        var t0 = Stopwatch.GetTimestamp();
                        px.Provider.Publish(s);
                        pubTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var res = await LoadGen.RunAsync(http, px.Port, hostNames, concurrency, TimeSpan.FromSeconds(seconds));
            cts.Cancel();
            await publisher;
            Console.WriteLine($"\n== {phase}");
            Console.WriteLine($"   {res}");
            if (publishing) Console.WriteLine($"   publishes: {pubTimes.Count}, Publish() {Stat.Summ(pubTimes)}");
            Console.WriteLine($"   pipeline: requests {PipelineStats.Requests}, missing meta {PipelineStats.MissingMeta}, meta.Generation != gate snapshot gen {PipelineStats.StaleGeneration}, RouteVersion mismatch (true race) {PipelineStats.RouteVersionMismatch}");
        }
    }

    // =========================================================================================
    // Q4: YARP health continuity across publishes
    // =========================================================================================
    public static async Task HealthAsync()
    {
        await using var up = await Upstream.StartAsync();
        var deadPort = Net.FreePort(); // nothing listens here -> connection refused
        var (hosts, clusters) = Compiler.Fleet(50, up.Port);
        var active = new HealthCheckConfig
        {
            Active = new ActiveHealthCheckConfig
            {
                Enabled = true, Interval = TimeSpan.FromMilliseconds(500), Timeout = TimeSpan.FromSeconds(2),
                Policy = HealthCheckConstants.ActivePolicy.ConsecutiveFailures, Path = "/health",
            },
        };
        var passive = new HealthCheckConfig
        {
            Passive = new PassiveHealthCheckConfig
            {
                Enabled = true, Policy = HealthCheckConstants.PassivePolicy.TransportFailureRate, ReactivationPeriod = TimeSpan.FromMinutes(10),
            },
        };
        var cHealth = new ClusterSpec { Id = "c-health", Health = active, Destinations = [("c-health-good", $"http://127.0.0.1:{up.Port}/good/"), ("c-health-bad", $"http://127.0.0.1:{up.Port}/bad/")] };
        var cPassive = new ClusterSpec { Id = "c-passive", Health = passive, Destinations = [("c-passive-good", $"http://127.0.0.1:{up.Port}/"), ("c-passive-dead", $"http://127.0.0.1:{deadPort}/")] };
        var cOther = new ClusterSpec { Id = "c-other", Destinations = [("c-other-d0", $"http://127.0.0.1:{up.Port}/")] };
        clusters.AddRange([cHealth, cPassive, cOther]);
        int sHealth = 50, sPassive = 51, sOther = 52;
        hosts.Add(new HostSpec { Slot = sHealth, Host = "health.test", ClusterId = "c-health" });
        hosts.Add(new HostSpec { Slot = sPassive, Host = "passive.test", ClusterId = "c-passive" });
        hosts.Add(new HostSpec { Slot = sOther, Host = "other.test", ClusterId = "c-other" });
        const int cap = 64;
        long gen = 1;
        await using var px = await ProxyHost.StartAsync(Compiler.Build(gen, hosts, clusters, false, cap));
        using var http = Net.Client();

        DestinationState Dest(string c, string d) => px.State.TryGetCluster(c, out var cs) && cs.Destinations.TryGetValue(d, out var ds) ? ds : null!;
        string H(DestinationState? d) => d is null ? "missing" : $"A={d.Health.Active},P={d.Health.Passive}";

        // 1. wait for active health to settle
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) && Dest("c-health", "c-health-bad")?.Health.Active != DestinationHealth.Unhealthy) await Task.Delay(50);
        Console.WriteLine($"active: c-health-bad {H(Dest("c-health", "c-health-bad"))} after {sw.ElapsedMilliseconds} ms; c-health-good {H(Dest("c-health", "c-health-good"))}");
        // 2. passive: drive traffic until the dead destination is marked unhealthy
        var n502 = 0;
        for (var i = 0; i < 200 && Dest("c-passive", "c-passive-dead")?.Health.Passive != DestinationHealth.Unhealthy; i++)
        {
            using var r = await Net.Get(http, px.Port, "passive.test");
            if ((int)r.StatusCode == 502) n502++;
        }
        Console.WriteLine($"passive: c-passive-dead {H(Dest("c-passive", "c-passive-dead"))} after {n502} x 502; c-passive-good {H(Dest("c-passive", "c-passive-good"))}");

        string[] watched = ["c-health/c-health-good", "c-health/c-health-bad", "c-passive/c-passive-good", "c-passive/c-passive-dead"];
        var refs = watched.ToDictionary(w => w, w => Dest(w.Split('/')[0], w.Split('/')[1]));
        var last = watched.ToDictionary(w => w, w => H(refs[w]));
        Console.WriteLine("baseline: " + string.Join("; ", watched.Select(w => $"{w} {last[w]}")));

        string phase = "";
        var transitions = new List<string>();
        using var stop = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var w in watched)
                {
                    var p = w.Split('/');
                    var d = Dest(p[0], p[1]);
                    var h = H(d);
                    if (!ReferenceEquals(d, refs[w])) { lock (transitions) transitions.Add($"[{phase}] {w}: DestinationState INSTANCE replaced ({h})"); refs[w] = d; }
                    if (h != last[w]) { lock (transitions) transitions.Add($"[{phase}] {w}: {last[w]} -> {h}"); last[w] = h; }
                }
                await Task.Delay(1);
            }
        });

        async Task Phase(string name, Action<long> mutate, bool genIds = false, int publishes = 30)
        {
            phase = name;
            int t0 = transitions.Count;
            int h5xx = 0, p5xx = 0, total = 0, other = 0;
            using var traffic = new CancellationTokenSource();
            var load = Task.Run(async () =>
            {
                while (!traffic.IsCancellationRequested)
                {
                    using (var r = await Net.Get(http, px.Port, "health.test")) { if ((int)r.StatusCode >= 500) h5xx++; }
                    using (var r = await Net.Get(http, px.Port, "passive.test")) { if ((int)r.StatusCode >= 500) p5xx++; }
                    using (var r = await Net.Get(http, px.Port, "other.test")) { if ((int)r.StatusCode >= 500) other++; }
                    total++;
                }
            });
            for (var i = 0; i < publishes; i++)
            {
                gen++;
                mutate(gen);
                px.Provider.Publish(Compiler.Build(gen, hosts, clusters, genIds, cap));
                await Task.Delay(100);
            }
            await Task.Delay(1500); // let a few more health probes run after the last publish
            traffic.Cancel();
            await load;
            List<string> mine;
            lock (transitions) mine = transitions.Skip(t0).ToList();
            Console.WriteLine($"\n== phase: {name}  ({publishes} publishes @100ms)");
            Console.WriteLine($"   requests x{total}: health.test 5xx={h5xx}, passive.test 5xx={p5xx}, other.test 5xx={other}");
            Console.WriteLine(mine.Count == 0 ? "   transitions: none" : "   transitions:");
            foreach (var t in mine) Console.WriteLine("     " + t);
            Console.WriteLine("   now: " + string.Join("; ", watched.Select(w => $"{w} {last[w]}")));
        }

        await Phase("a) unrelated publish (identical content, new instances)", _ => { });
        await Phase("b) change unrelated cluster c-other (dest address toggles)", g =>
        {
            var c = clusters.First(c => c.Id == "c-other");
            c.Destinations = [("c-other-d0", g % 2 == 0 ? $"http://localhost:{up.Port}/" : $"http://127.0.0.1:{up.Port}/")];
        });
        await Phase("c) change only route metadata of health.test + passive.test routes", g =>
        {
            hosts[sHealth].MetaTag = g.ToString(CultureInfo.InvariantCulture);
            hosts[sPassive].MetaTag = g.ToString(CultureInfo.InvariantCulture);
        });
        await Phase("d) route IDs encode generation (all routes replaced each publish)", _ => { }, genIds: true);
        await Phase("e) [extra] change the health clusters themselves (cluster Metadata + LB policy toggled, same destinations)", g =>
        {
            foreach (var c in new[] { cHealth, cPassive })
            {
                c.MetaTag = g.ToString(CultureInfo.InvariantCulture);
                c.LbPolicy = g % 2 == 0 ? "RoundRobin" : "PowerOfTwoChoices";
            }
        });
        await Phase("f) [extra] change active health-check config of c-health (interval toggles 500/600 ms)", g =>
        {
            cHealth.Health = new HealthCheckConfig { Active = active.Active! with { Interval = TimeSpan.FromMilliseconds(g % 2 == 0 ? 600 : 500) } };
        });
        await Phase("g) [extra] change destination ADDRESS of c-health-bad (/bad/ <-> /bad/x/)", g =>
        {
            cHealth.Health = active;
            cHealth.Destinations = [cHealth.Destinations[0], ("c-health-bad", $"http://127.0.0.1:{up.Port}/bad/{(g % 2 == 0 ? "x/" : "")}")];
        }, publishes: 10);
        var keep = cHealth.Destinations;
        await Phase("h) [extra] remove c-health-bad for one generation, then re-add", g =>
        {
            cHealth.Destinations = g % 2 == 0 ? [keep[0]] : keep;
        }, publishes: 2);

        await Phase("i) [extra] point c-passive-dead (same ID) at a WORKING address", _ =>
        {
            cPassive.Destinations = [cPassive.Destinations[0], ("c-passive-dead", $"http://localhost:{up.Port}/")];
        }, publishes: 5);
        for (var i = 0; i < 200; i++) { using var r = await Net.Get(http, px.Port, "passive.test"); }
        Console.WriteLine($"   after phase i: c-passive-dead {H(Dest("c-passive", "c-passive-dead"))}, AvailableDestinations = [{string.Join(",", px.State.TryGetCluster("c-passive", out var cps) ? cps.DestinationsState.AvailableDestinations.Select(d => d.DestinationId) : [])}]");

        stop.Cancel();
        await monitor;
    }
}
