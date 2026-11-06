using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;

namespace S3;

/// <summary>Correctness checks for the spike's building blocks, plus the AllocAssert pattern applied to them (item 8).</summary>
public static class SelfTest
{
    private static int s_fail;

    private static void Check(bool ok, string what)
    {
        if (!ok) { s_fail++; Console.WriteLine("FAIL " + what); }
    }

    public static int Run()
    {
        IpParsing();
        Hosts();
        Allocations();
        Console.WriteLine(s_fail == 0 ? "selftest: all checks passed" : $"selftest: {s_fail} FAILED");
        return s_fail == 0 ? 0 : 1;
    }

    private static void IpParsing()
    {
        var rnd = new Random(1);
        var n = 0;
        for (var i = 0; i < 200_000; i++)
        {
            var bytes = new byte[i % 3 == 0 ? 4 : 16];
            rnd.NextBytes(bytes);
            if (i % 7 == 0 && bytes.Length == 16) Array.Clear(bytes, 2, 10);        // force "::" compression
            if (i % 11 == 0 && bytes.Length == 16) { Array.Clear(bytes, 0, 10); bytes[10] = bytes[11] = 0xFF; } // mapped
            var a = new IPAddress(bytes);
            foreach (var text in new[] { a.ToString(), bytes.Length == 16 ? Full(bytes) : a.ToString() })
            {
                var okC = Ip.TryParse(text, out var c);
                var okB = Ip.TryParseBcl(text, out var bcl);
                Check(okC && okB && c == bcl, $"parse {text}: custom={okC}/{c:X} bcl={okB}/{bcl:X}");
                n++;
            }
        }
        foreach (var bad in new[] { "", "1.2.3", "1.2.3.4.5", "256.1.1.1", "1.2.3.4 ", " 1.2.3.4", "01.2.3.4", ":::", "1::2::3", "12345::",
                     "1:2:3:4:5:6:7:8:9", "1:2:3:4:5:6:7:8::", "::1:2:3:4:5:6:7:8", "fe80::1%eth0", "[::1]", "1.2.3.4:80", ":1::", "1:", "g::1", "::ffff:1.2.3" })
            Check(!Ip.TryParse(bad, out _), $"should reject '{bad}'");
        Check(Ip.TryParse("::", out var z) && z == 0, "::");
        Check(Ip.TryParse("1::", out var one) && one == ((UInt128)1 << 112), "1::");
        Check(Ip.TryParse("::ffff:1.2.3.4", out var m) && m == Ip.FromV4(0x01020304) && Ip.TryParse("1.2.3.4", out var v4) && v4 == m, "mapped == v4");
        Console.WriteLine($"ip parse: {n} random round-trips compared with IPAddress.TryParse");

        static string Full(byte[] b) => string.Join(':', Enumerable.Range(0, 8).Select(i => ((b[2 * i] << 8) | b[2 * i + 1]).ToString("x4", CultureInfo.InvariantCulture)));
    }

    private static void Hosts()
    {
        var t = Fleet.Build().Hosts;
        Check(t.Count == Fleet.Hosts + Fleet.Wildcards, "count");
        Check(t.Find("h1.test")?.Name == "h1.test", "exact");
        Check(t.Find("H1.TeSt")?.Name == "h1.test", "case");
        Check(t.Find("h1.test:8443")?.Name == "h1.test", "port");
        Check(t.Find("h1.test.")?.Name == "h1.test", "trailing dot");
        Check(t.Find("h1.test.:80")?.Name == "h1.test", "trailing dot + port");
        Check(t.Find("x.w3.test")?.Name == "*.w3.test", "wildcard");
        Check(t.Find("X.W3.TEST:443")?.Name == "*.w3.test", "wildcard case + port");
        Check(t.Find("a.b.w3.test") is null, "wildcard is single-label");
        Check(t.Find("w3.test") is null, "wildcard doesn't match the parent");
        var puny = new IdnMapping().GetAscii("bücher57.test");
        Check(t.Find(puny)?.Name == "bücher57.test", $"IDN via punycode ({puny})");
        Check(t.Find("bücher57.test") is null, "raw Unicode Host is not converted at runtime");
        Check(t.Find("[::1]:8080") is null && HostTable<CompiledHost>.HostName("[::1]:8080").SequenceEqual("[::1]"), "IPv6 literal");
        Check(t.Find("") is null && t.Find(":80") is null, "empty");
        Console.WriteLine($"hosts: table of {t.Count}, punycode of bücher57.test = {puny}");
    }

    private sealed class PersistentState : IPersistentStateFeature
    {
        public IDictionary<object, object?> State { get; } = new Dictionary<object, object?>();
    }

    private static void Allocations()
    {
        var snap = Fleet.Build();
        snap.Endpoints = Enumerable.Range(0, Fleet.Hosts).Select(i => (Endpoint?)new Endpoint(null, new EndpointMetadataCollection(), $"r{i}")).ToArray();
        SnapshotStore.Publish(snap);
        Gate.Measure = false;

        void Report(string what, AllocAssert.Result r, bool expectZero)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {(r.TotalBytes == 0 ? "0 B " : $"{r.BytesPerCall,5:F1} B/call")}  {what}"));
            if (expectZero) Check(r.TotalBytes == 0, what);
        }

        Console.WriteLine("AllocAssert (1000 warm-up + 10000 measured calls, current thread):");
        string[] hosts = ["h123.test", "H123.TEST", "h123.test:8443", new IdnMapping().GetAscii("bücher57.test"), "a.w3.test", "nope.example.org"];
        Report("HostTable.Find over 6 inputs", AllocAssert.Measure((snap.Hosts, hosts), static s => { foreach (var h in s.hosts) s.Hosts.Find(h); }), true);
        var naive = new Dictionary<string, CompiledHost>(snap.Hosts.Exact, StringComparer.OrdinalIgnoreCase);
        Report("naive: Substring(port) + \"*\" + parent + Dictionary<string> over the same 6 inputs (expected to allocate)", AllocAssert.Measure((naive, hosts), static s =>
        {
            foreach (var h0 in s.hosts)
            {
                var h = h0;
                var colon = h.LastIndexOf(':');
                if (colon >= 0) h = h.Substring(0, colon);
                if (s.naive.TryGetValue(h, out _)) continue;
                var dot = h.IndexOf('.');
                if (dot > 0) s.naive.TryGetValue("*" + h.Substring(dot), out _);
            }
        }), false);
        Report("Ip.TryParse v4 + v6", AllocAssert.Measure(0, static x => { Ip.TryParse("203.0.113.77", out _); Ip.TryParse("2001:db8::1", out _); }), true);
        Report("IPAddress.TryParse v4 + v6 (BCL, expected to allocate)", AllocAssert.Measure(0, static x => { Ip.TryParseBcl("203.0.113.77", out _); Ip.TryParseBcl("2001:db8::1", out _); }), false);

        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IPersistentStateFeature>(new PersistentState());
        ctx.Features.Set<IClientInfoFeature>(new ClientInfo { PeerIp = Ip.FromV4(0xC633_6409) });
        ctx.Request.Headers.Host = "h123.test:8443";
        ctx.Request.Path = "/assets/app.js";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0.0.0";
        Report("Request.Host.Host with a port (DefaultHttpContext, expected to allocate)", AllocAssert.Measure(ctx, static c => _ = c.Request.Host.Host), false);
        var st = new GateRequestState { ClientIp = Ip.FromV4(0xC633_6409) };
        Report("policy chain (3 policies, all Continue)", AllocAssert.Measure((ctx, st), static s =>
        {
            var v = new RequestView(s.ctx, s.st);
            foreach (var p in SnapshotStore.Current.Hosts.Find("h1.test")!.Policies) p.Evaluate(in v);
        }), true);
        foreach (var mode in new[] { StateMode.Pooled, StateMode.Locals, StateMode.NewFeature })
        {
            Gate.StateMode = mode;
            Report($"Gate.Run forward path, StateMode={mode}", AllocAssert.Measure(ctx, static c =>
            {
                _ = Gate.Run(c, out var fwd);
                if (!fwd) throw new InvalidOperationException("not forwarded");
            }), mode != StateMode.NewFeature);
        }
        Gate.StateMode = StateMode.Pooled;
        Report("accidental allocation demo: LINQ FirstOrDefault over a non-empty IDictionary.Values (boxed enumerator)", AllocAssert.Measure(ctx, static c =>
            _ = c.Features.Get<IPersistentStateFeature>()!.State.Values.FirstOrDefault()), false);

        // Async caveat: a method that really suspends finishes on another thread; the current-thread counter misses
        // the continuation's allocations and sees only the state-machine box.
        Report("async method with await Task.Yield() (measures only the part before the first real suspension)",
            AllocAssert.Measure(0, static _ => AsyncWork().GetAwaiter().GetResult(), warmup: 100, iterations: 1000), false);

        static async Task AsyncWork()
        {
            await Task.Yield();
            _ = new byte[1000]; // allocated on a thread-pool thread: invisible to the caller's counter
        }
    }
}
