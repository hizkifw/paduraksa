using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace S3;

public static class Bench
{
    public static void Run(string[] filter)
    {
        var config = ManualConfig.CreateEmpty()
            // In-process: no generated project, short runs (the machine is shared). Allocation numbers are exact
            // regardless; ns numbers are indicative.
            .AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance).WithId("short-inproc"))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddLogger(ConsoleLogger.Default)
            .WithSummaryStyle(SummaryStyle.Default.WithRatioStyle(RatioStyle.Trend))
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "bdn"));
        var types = new[] { typeof(HostLookupBench), typeof(HostSourceBench), typeof(IpParseBench), typeof(PolicyBench), typeof(DispatchBench), typeof(GateBench) };
        if (filter.Length > 0) types = types.Where(t => filter.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToArray();
        foreach (var t in types) BenchmarkRunner.Run(t, config);
    }
}

// ---------------------------------------------------------------------------------------------
// 1. Host resolution
// ---------------------------------------------------------------------------------------------

[MemoryDiagnoser]
public class HostLookupBench
{
    private HostTable<CompiledHost> _table = null!;
    private Dictionary<string, CompiledHost> _dict = null!;

    public static IEnumerable<string> Inputs() =>
    [
        "h123.test",                 // exact
        "H123.TEST",                 // uppercase
        "h123.test:8443",            // with port
        "xn--bcher57-n2a.test",      // punycode IDN (as browsers send it)
        "a.w3.test",                 // wildcard *.w3.test
        "nope.example.org",          // unknown (exact miss + wildcard miss)
    ];

    [ParamsSource(nameof(Inputs))]
    public string Host { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _table = Fleet.Build().Hosts;
        _dict = new Dictionary<string, CompiledHost>(_table.Exact, StringComparer.OrdinalIgnoreCase);
    }

    [Benchmark(Baseline = true, Description = "HostTable.Find(span) (Frozen alt-lookup)")]
    public CompiledHost? SpanLookup() => _table.Find(Host);

    [Benchmark(Description = "Substring port + Dictionary<string> (naive)")]
    public CompiledHost? NaiveSubstring()
    {
        var h = Host;
        var colon = h.LastIndexOf(':');
        if (colon >= 0) h = h.Substring(0, colon);
        if (_dict.TryGetValue(h, out var v)) return v;
        var dot = h.IndexOf('.');
        return dot > 0 && _dict.TryGetValue("*" + h.Substring(dot), out v) ? v : null;
    }
}

[MemoryDiagnoser]
public class HostSourceBench
{
    private DefaultHttpContext _ctx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ctx = new DefaultHttpContext();
        _ctx.Request.Headers.Host = "h123.test:8443";
    }

    [Benchmark(Description = "Headers.Host[0] → HostName(span)")]
    public int HeaderSpan() => HostTable<CompiledHost>.HostName(_ctx.Request.Headers.Host[0]).Length;

    [Benchmark(Description = "Request.Host (HostString struct)")]
    public int HostStringValue() => _ctx.Request.Host.Value!.Length;

    [Benchmark(Description = "Request.Host.Host (port present)")]
    public int HostStringHost() => _ctx.Request.Host.Host.Length;

    [Benchmark(Description = "Request.Host.Port (port present)")]
    public int HostStringPort() => _ctx.Request.Host.Port ?? 0;
}

// ---------------------------------------------------------------------------------------------
// 2. Client IP parsing (CF-Connecting-IP)
// ---------------------------------------------------------------------------------------------

[MemoryDiagnoser]
public class IpParseBench
{
    [Params("203.0.113.77", "2001:db8:85a3::8a2e:370:7334", "::ffff:198.51.100.9")]
    public string Value { get; set; } = "";

    [Benchmark(Baseline = true, Description = "Ip.TryParse (custom span parser)")]
    public UInt128 Custom() => Ip.TryParse(Value, out var ip) ? ip : 0;

    [Benchmark(Description = "IPAddress.TryParse(span) → UInt128")]
    public UInt128 Bcl() => Ip.TryParseBcl(Value, out var ip) ? ip : 0;
}

// ---------------------------------------------------------------------------------------------
// 5. Policy chain
// ---------------------------------------------------------------------------------------------

[MemoryDiagnoser]
public class PolicyBench
{
    private DefaultHttpContext _ctx = null!;
    private GateRequestState _st = null!;
    private IRequestPolicy[] _chain = null!;
    private IpRangePolicy _ip = null!;
    private UserAgentPolicy _ua = null!;
    private PathPrefixPolicy _path = null!;

    [GlobalSetup]
    public void Setup()
    {
        var h = Fleet.Build().Hosts.Find("h1.test")!;
        _chain = h.Policies;
        _ip = (IpRangePolicy)_chain[0];
        _ua = (UserAgentPolicy)_chain[1];
        _path = (PathPrefixPolicy)_chain[2];
        _ctx = new DefaultHttpContext();
        _ctx.Request.Path = "/assets/app.js";
        _ctx.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
        _st = new GateRequestState { ClientIp = Ip.FromV4(0xC633_6409) }; // 198.51.100.9
    }

    [Benchmark(Baseline = true, Description = "chain of 3 via IRequestPolicy[] (all Continue)")]
    public int Chain()
    {
        var v = new RequestView(_ctx, _st);
        var n = 0;
        foreach (var p in _chain) n += (int)p.Evaluate(in v).Action;
        return n;
    }

    [Benchmark(Description = "IpRange (2000 CIDRs, binary search)")]
    public PolicyAction IpOnly() => _ip.Evaluate(new RequestView(_ctx, _st)).Action;

    [Benchmark(Description = "UserAgent (30 needles, SearchValues, OIC)")]
    public PolicyAction UaOnly() => _ua.Evaluate(new RequestView(_ctx, _st)).Action;

    [Benchmark(Description = "PathPrefix (4 prefixes)")]
    public PolicyAction PathOnly() => _path.Evaluate(new RequestView(_ctx, _st)).Action;
}

[MemoryDiagnoser]
public class DispatchBench
{
    private IDispatchProbe[] _iface = null!;
    private ProbeBase[] _abstract = null!;
    private ProbeRecord[] _enum = null!;
    private int _x = 12345;

    [GlobalSetup]
    public void Setup()
    {
        // 8 policies per host, mixed types (megamorphic call site, like a real chain)
        _iface = [new ProbeA(), new ProbeB(), new ProbeC(), new ProbeA(), new ProbeC(), new ProbeB(), new ProbeA(), new ProbeC()];
        _abstract = [new ProbeA2(), new ProbeB2(), new ProbeC2(), new ProbeA2(), new ProbeC2(), new ProbeB2(), new ProbeA2(), new ProbeC2()];
        _enum = _iface.Select(p => p switch
        {
            ProbeA a => new ProbeRecord { Kind = ProbeKind.A, K = a.K },
            ProbeB b => new ProbeRecord { Kind = ProbeKind.B, K = b.K },
            ProbeC c => new ProbeRecord { Kind = ProbeKind.C, K = c.K },
            _ => throw new InvalidOperationException(),
        }).ToArray();
    }

    [Benchmark(Baseline = true, Description = "interface over sealed classes (8)")]
    public int Interface() { var n = 0; foreach (var p in _iface) n += p.Eval(_x); return n; }

    [Benchmark(Description = "abstract base class (8)")]
    public int Abstract() { var n = 0; foreach (var p in _abstract) n += p.Eval(_x); return n; }

    [Benchmark(Description = "enum switch on a record (8)")]
    public int EnumSwitch() { var n = 0; foreach (var p in _enum) n += p.Eval(_x); return n; }
}

// ---------------------------------------------------------------------------------------------
// 4. Whole synchronous gate on a DefaultHttpContext (snapshot read → host → route → 3 policies → SetEndpoint)
// ---------------------------------------------------------------------------------------------

[MemoryDiagnoser]
public class GateBench
{
    private DefaultHttpContext _ctx = null!;

    private sealed class PersistentState : IPersistentStateFeature
    {
        public IDictionary<object, object?> State { get; } = new Dictionary<object, object?>();
    }

    [Params(StateMode.Locals, StateMode.Pooled, StateMode.NewFeature)]
    public StateMode Mode { get; set; }

    [Params("h123.test", "a.w3.test:8443")]
    public string Host { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var s = Fleet.Build();
        s.Endpoints = Enumerable.Range(0, Fleet.Hosts).Select(i => (Endpoint?)new Endpoint(null, new EndpointMetadataCollection(), $"r{i}")).ToArray();
        SnapshotStore.Publish(s);
        Gate.Measure = false;
        Gate.StateMode = Mode;
        Gate.ClientInfoMode = ClientInfoMode.ConnectionFeature;
        _ctx = new DefaultHttpContext();
        _ctx.Features.Set<IPersistentStateFeature>(new PersistentState());
        _ctx.Features.Set<IClientInfoFeature>(new ClientInfo { PeerIp = Ip.FromV4(0xC633_6409) });
        _ctx.Request.Headers.Host = Host;
        _ctx.Request.Path = "/assets/app.js";
        _ctx.Request.Headers.UserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
    }

    [Benchmark(Description = "Gate.Run (forward path)")]
    public bool Run()
    {
        _ = Gate.Run(_ctx, out var fwd);
        if (!fwd) throw new InvalidOperationException("expected forward: " + _ctx.Response.StatusCode.ToString(CultureInfo.InvariantCulture));
        return fwd;
    }
}
