using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;

namespace S3;

/// <summary>
/// Items 1-4 and 6 on a real Kestrel (H1 keep-alive and H2 cleartext): each request runs a list of micro-operations and
/// measures GC.GetAllocatedBytesForCurrentThread around each one. Client is HttpClient in the same process; its
/// allocations happen on other threads, so they don't pollute the per-thread deltas.
/// </summary>
public static class Probe
{
    private static readonly StringValues LocationSv = new("https://h123.test/new");
    private static readonly StringValues CustomSv = new("blocked");
    private static readonly StringValues CacheSv = new("no-store");
    private static readonly object ItemsKey = new();

    private delegate int Op(HttpContext ctx);

    private static readonly (string Name, Op Run)[] Ops =
    [
        ("Request.Headers.Host[0]", static c => c.Request.Headers.Host[0]!.Length),
        ("Request.Host.Value", static c => c.Request.Host.Value!.Length),
        ("Request.Host.Host (port present)", static c => c.Request.Host.Host.Length),
        ("Request.Host.Port", static c => c.Request.Host.Port ?? 0),
        ("HostTable.Find(Headers.Host[0])", static c => SnapshotStore.Current.Hosts.Find(c.Request.Headers.Host[0])!.Index),
        ("Features.Get<IClientInfoFeature>() [conn feature]", static c => c.Features.Get<IClientInfoFeature>() is null ? Miss(0) : 1),
        ("Features.Get<IConnectionItemsFeature>().Items[k]", static c => c.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ConnectionGate.ItemsKey, out _) == true ? 1 : Miss(1)),
        ("Connection.RemoteIpAddress → UInt128", static c => (int)(ulong)Ip.From(c.Connection.RemoteIpAddress!)),
        ("Headers[\"CF-Connecting-IP\"] + Ip.TryParse", static c => Ip.TryParse(c.Request.Headers["CF-Connecting-IP"][0], out var ip) ? (int)(ulong)ip : -1),
        ("Headers[\"CF-Connecting-IP\"] + IPAddress.TryParse", static c => Ip.TryParseBcl(c.Request.Headers["CF-Connecting-IP"][0], out var ip) ? (int)(ulong)ip : -1),
        ("new GateRequestState + Features.Set", static c => { c.Features.Set<IGateFeature>(new GateRequestState()); return 1; }),
        ("IPersistentStateFeature pooled object", static c => Pooled(c).GetHashCode()),
        ("Features.Set<IGateFeature>(pooled)", static c => { c.Features.Set<IGateFeature>(Pooled(c)); return 1; }),
        ("Features.Get<IGateFeature>()", static c => c.Features.Get<IGateFeature>()!.State.Country),
        ("Request.Path.Value + route match", static c => SnapshotStore.Current.Hosts.Find("h123.test")!.Match(c.Request.Path.Value)!.Prefix.Length),
        ("SnapshotStore.Current + SetEndpoint", static c => { c.SetEndpoint(SnapshotStore.Current.Endpoints[123]); return 1; }),
        ("Response.Headers.Location = sv", static c => { c.Response.Headers.Location = LocationSv; return 1; }),
        ("Response.Headers[\"Location\"] = sv", static c => { c.Response.Headers["Location"] = LocationSv; return 1; }),
        ("Response.Headers.CacheControl = sv", static c => { c.Response.Headers.CacheControl = CacheSv; return 1; }),
        ("Response.Headers[\"X-Paduraksa\"] = sv (unknown)", static c => { c.Response.Headers["X-Paduraksa"] = CustomSv; return 1; }),
        ("Response.ContentType = const string", static c => { c.Response.ContentType = "text/html; charset=utf-8"; return 1; }),
        ("ResponseWriter.WriteAsync (sync part)", static c => { var t = ResponseWriter.WriteAsync(c, Fleet.Ok); return t.IsCompleted ? 1 : Miss(2); }),
    ];

    private static readonly object PooledKey = new();
    private static GateRequestState Pooled(HttpContext c)
    {
        var s = c.Features.Get<IPersistentStateFeature>()!.State;
        if (s.TryGetValue(PooledKey, out var o)) return (GateRequestState)o!;
        var st = new GateRequestState();
        s[PooledKey] = st;
        Interlocked.Increment(ref PooledCreated);
        return st;
    }

    private static long PooledCreated;
    private static readonly long[] Misses = new long[3];
    private static int Miss(int i) { Interlocked.Increment(ref Misses[i]); return 0; }

    private static readonly long[,] Bytes = new long[2, 32];
    private static readonly long[,] NonZero = new long[2, 32];
    private static readonly long[] Reqs = new long[2];
    private static volatile bool s_measuring;

    public static async Task RunAsync(int requestsPerProto)
    {
        var snap = Fleet.Build();
        snap.Endpoints = Enumerable.Range(0, Fleet.Hosts).Select(i => (Endpoint?)new Endpoint(null, new EndpointMetadataCollection(), $"r{i}")).ToArray();
        SnapshotStore.Publish(snap);

        int p1 = FreePort(), p2 = FreePort();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders(); if (Environment.GetEnvironmentVariable("S3_LOG") is not null) b.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Debug);
        b.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, p1, lo => { lo.Protocols = HttpProtocols.Http1; lo.UseClientInfo(); });
            o.Listen(IPAddress.Loopback, p2, lo => { lo.Protocols = HttpProtocols.Http2; lo.UseClientInfo(); });
        });
        var app = b.Build();
        app.Run(static ctx =>
        {
            var proto = ctx.Request.Protocol == "HTTP/2" ? 1 : 0;
            if (!s_measuring)
            {
                foreach (var (_, run) in Ops) run(ctx);
                return Task.CompletedTask;
            }
            for (var i = 0; i < Ops.Length; i++)
            {
                var a0 = GC.GetAllocatedBytesForCurrentThread();
                Ops[i].Run(ctx);
                var d = GC.GetAllocatedBytesForCurrentThread() - a0;
                if (d != 0) { Interlocked.Add(ref Bytes[proto, i], d); Interlocked.Increment(ref NonZero[proto, i]); }
            }
            Interlocked.Increment(ref Reqs[proto]);
            return Task.CompletedTask;
        });
        await app.StartAsync();

        using var h1 = Client(p1, HttpVersion.Version11, maxConns: 4);
        using var h2 = Client(p2, HttpVersion.Version20, maxConns: 1);
        foreach (var (client, conc, name) in new[] { (h1, 4, "HTTP/1.1 (4 keep-alive conns)"), (h2, 16, "HTTP/2 (1 conn, 16 concurrent streams)") })
        {
            s_measuring = false;
            await Drive(client, conc, 2000);            // warm-up
            s_measuring = true;
            await Drive(client, conc, requestsPerProto);
            s_measuring = false;
            Console.WriteLine($"# warm-up done for {name}");
        }

        Console.WriteLine();
        Console.WriteLine($"Kestrel probe: per-operation allocation on the request thread, after warm-up ({Environment.Version}, server GC={System.Runtime.GCSettings.IsServerGC})");
        Console.WriteLine($"Requests measured: HTTP/1.1 {Reqs[0]}, HTTP/2 {Reqs[1]}. Host header 'h123.test:8443'.");
        Console.WriteLine();
        Console.WriteLine($"| {"operation",-52} | {"H1 B/req",9} | {"H1 nonzero",10} | {"H2 B/req",9} | {"H2 nonzero",10} |");
        Console.WriteLine($"|{new string('-', 54)}|{new string('-', 11)}|{new string('-', 12)}|{new string('-', 11)}|{new string('-', 12)}|");
        for (var i = 0; i < Ops.Length; i++)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {Ops[i].Name,-52} | {(double)Bytes[0, i] / Reqs[0],9:F1} | {NonZero[0, i],10} | {(double)Bytes[1, i] / Reqs[1],9:F1} | {NonZero[1, i],10} |"));
        Console.WriteLine();
        Console.WriteLine($"misses: IClientInfoFeature null={Misses[0]}, IConnectionItemsFeature/key missing={Misses[1]}, WriteAsync not completed sync={Misses[2]}");
        Console.WriteLine($"pooled GateRequestState objects created (IPersistentStateFeature): {PooledCreated} for {Reqs[0] + Reqs[1] + 4000} requests; connections opened: {ConnectionGate.Connections}");
        await app.StopAsync();
    }

    private static async Task Drive(HttpClient c, int concurrency, int total)
    {
        var left = total;
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (Interlocked.Decrement(ref left) >= 0)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "http://h123.test:8443/assets/app.js") { Version = c.DefaultRequestVersion, VersionPolicy = c.DefaultVersionPolicy };
                req.Headers.TryAddWithoutValidation("CF-Connecting-IP", "2001:db8:85a3::8a2e:370:7334");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0.0.0");
                using var resp = await c.SendAsync(req);
                if (resp.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException($"status {resp.StatusCode}");
                await resp.Content.ReadAsByteArrayAsync();
            }
        });
        await Task.WhenAll(tasks);
    }

    public static HttpClient Client(int port, Version v, int maxConns) => new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = maxConns,
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        ConnectCallback = async (ctx, ct) =>
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new NetworkStream(s, ownsSocket: true);
        },
    })
    { DefaultRequestVersion = v, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
