using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace S3;

// =============================================================================================
// Precomputed responses (item 6)
// =============================================================================================

public sealed class PrecomputedResponse
{
    public required int Status { get; init; }
    public required StringValues ContentType { get; init; }
    public required byte[] Body { get; init; }
    public StringValues Location { get; init; }          // empty = no Location header
    public StringValues CacheControl { get; init; }      // known header, field-backed in Kestrel

    public static PrecomputedResponse Html(int status, string html) => new()
    {
        Status = status,
        ContentType = new StringValues("text/html; charset=utf-8"),
        Body = Encoding.UTF8.GetBytes(html),
        CacheControl = new StringValues("no-store"),
    };

    public static PrecomputedResponse Redirect(int status, string location) => new()
    {
        Status = status,
        ContentType = new StringValues("text/plain"),
        Body = Encoding.UTF8.GetBytes("Redirecting to " + location),
        Location = new StringValues(location),
    };
}

public static class ResponseWriter
{
    /// <summary>
    /// Writes a precomputed response. Header values are StringValues created at compile time and shared by every
    /// request (StringValues is an immutable struct over a string, so reuse is safe). The body goes straight into
    /// Kestrel's output pipe; the ValueTask almost always completes synchronously (small body, no backpressure).
    /// </summary>
    public static Task WriteAsync(HttpContext ctx, PrecomputedResponse r)
    {
        var resp = ctx.Response;
        resp.StatusCode = r.Status;
        var h = resp.Headers;
        h.ContentType = r.ContentType;
        if (r.Location.Count > 0) h.Location = r.Location;
        if (r.CacheControl.Count > 0) h.CacheControl = r.CacheControl;
        resp.ContentLength = r.Body.Length;
        var vt = resp.BodyWriter.WriteAsync(r.Body);
        return vt.IsCompletedSuccessfully ? Task.CompletedTask : Await(vt);

        static async Task Await(ValueTask<FlushResult> vt) => await vt;
    }
}

// =============================================================================================
// Snapshot (stand-in for RuntimeSnapshot, §6.2)
// =============================================================================================

public sealed class RouteEntry
{
    public required string Prefix { get; init; }           // "/" = catch-all
    public int Slot { get; init; } = -1;                   // index into Snapshot.Endpoints, or -1
    public PrecomputedResponse? Respond { get; init; }     // Respond/Redirect route (answered in the gate)
}

public sealed class CompiledHost
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required IRequestPolicy[] Policies { get; init; }
    public required RouteEntry[] Routes { get; init; }    // ordered: longest prefix first

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RouteEntry? Match(ReadOnlySpan<char> path)
    {
        foreach (var r in Routes)
            if (r.Prefix.Length == 1 || PathPrefixPolicy.SegmentPrefix(path, r.Prefix)) return r;
        return null;
    }
}

public sealed class Snapshot
{
    public required long Generation { get; init; }
    public required HostTable<CompiledHost> Hosts { get; init; }
    public Endpoint?[] Endpoints { get; set; } = [];      // filled after YARP applied (S2), immutable afterwards
    public required PrecomputedResponse UnknownHost { get; init; }
}

public static class SnapshotStore
{
    private static Snapshot? s_current;
    public static Snapshot Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref s_current)!;
    }
    public static void Publish(Snapshot s) => Volatile.Write(ref s_current, s);
}

// =============================================================================================
// Per-request state hand-off (item 3)
// =============================================================================================

public interface IGateFeature
{
    GateRequestState State { get; }
}

/// <summary>Everything later stages (policies, YARP transforms, telemetry) need. Reset on every request.</summary>
public sealed class GateRequestState : IGateFeature
{
    public Snapshot? Snapshot;
    public CompiledHost? Host;
    public RouteEntry? Route;
    public UInt128 ClientIp;
    public ushort Country;
    public long StartTimestamp;
    GateRequestState IGateFeature.State => this;

    public void Reset()
    {
        Snapshot = null; Host = null; Route = null; ClientIp = 0; Country = 0; StartTimestamp = 0;
    }

    public static long Created;
    public GateRequestState() => Interlocked.Increment(ref Created);
}

public enum StateMode
{
    /// <summary>One middleware, values in locals; nothing published for later stages.</summary>
    Locals,
    /// <summary>new GateRequestState() + Features.Set per request.</summary>
    NewFeature,
    /// <summary>Object cached in IPersistentStateFeature.State (per connection on H1, per pooled stream on H2/H3) + Features.Set.</summary>
    Pooled,
}

public enum ClientInfoMode
{
    /// <summary>HttpContext.Features.Get&lt;IClientInfoFeature&gt;() → falls through to the connection's features.</summary>
    ConnectionFeature,
    /// <summary>HttpContext.Features.Get&lt;IConnectionItemsFeature&gt;().Items[key].</summary>
    ConnectionItems,
    /// <summary>Per request from HttpContext.Connection.RemoteIpAddress (no connection middleware needed).</summary>
    PerRequest,
}

// =============================================================================================
// Gate middleware (stages C + D up to the YARP hand-off)
// =============================================================================================

public static class Gate
{
    public static StateMode StateMode = StateMode.Pooled;
    public static ClientInfoMode ClientInfoMode = ClientInfoMode.ConnectionFeature;
    public static bool UseBclIpParse;
    public static bool Measure = true;

    // accounting (gate's own synchronous code only)
    public static long Requests, AllocBytes, AllocRequests, MaxAlloc, Forwarded, Responded, NoClientInfo, CfParsed;
    public static void ResetStats() => Requests = AllocBytes = AllocRequests = MaxAlloc = Forwarded = Responded = NoClientInfo = CfParsed = 0;

    private static readonly object PooledKey = new();

    private static void Account(long delta, bool forwarded)
    {
        Interlocked.Increment(ref Requests);
        if (forwarded) Interlocked.Increment(ref Forwarded); else Interlocked.Increment(ref Responded);
        if (delta != 0)
        {
            Interlocked.Add(ref AllocBytes, delta);
            Interlocked.Increment(ref AllocRequests);
            long m;
            while (delta > (m = Volatile.Read(ref MaxAlloc)) && Interlocked.CompareExchange(ref MaxAlloc, delta, m) != m) { }
        }
    }

    /// <summary>
    /// Synchronous gate: decides only. Returns the precomputed response to write (null when forwarding or dropping);
    /// `forwarded` = endpoint set, caller invokes next. Neither the body write nor YARP is part of this method.
    /// </summary>
    public static PrecomputedResponse? Run(HttpContext ctx, out bool forwarded)
    {
        forwarded = false;
        var s = SnapshotStore.Current;                         // one volatile read

        GateRequestState st;
        switch (StateMode)
        {
            case StateMode.NewFeature:
                st = new GateRequestState();
                ctx.Features.Set<IGateFeature>(st);
                break;
            case StateMode.Pooled:
                st = Pooled(ctx);
                st.Reset();
                ctx.Features.Set<IGateFeature>(st);
                break;
            default:
                st = t_locals ??= new GateRequestState(); // stand-in for "locals" (a struct on the stack in M1)
                st.Reset();
                break;
        }
        st.Snapshot = s;

        // ---- client IP: connection-level ClientInfo, then CF-Connecting-IP if the peer is trusted ----
        ClientInfo? ci = ClientInfoMode switch
        {
            ClientInfoMode.ConnectionFeature => ctx.Features.Get<IClientInfoFeature>()?.Info,
            ClientInfoMode.ConnectionItems => ctx.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ConnectionGate.ItemsKey, out var o) == true ? (ClientInfo?)o : null,
            _ => null,
        };
        bool trusted;
        if (ci is not null)
        {
            st.ClientIp = ci.PeerIp; st.Country = ci.Country; trusted = ci.PeerIsTrustedProxy;
        }
        else
        {
            if (ClientInfoMode != ClientInfoMode.PerRequest) Interlocked.Increment(ref NoClientInfo);
            st.ClientIp = ctx.Connection.RemoteIpAddress is { } ra ? Ip.From(ra) : 0;
            trusted = ConnectionGate.IsTrustedProxy(st.ClientIp);
        }
        if (trusted)
        {
            var cf = ctx.Request.Headers["CF-Connecting-IP"];
            if (cf.Count == 1)
            {
                var span = cf[0].AsSpan().Trim();
                if (UseBclIpParse ? Ip.TryParseBcl(span, out var ip) : Ip.TryParse(span, out ip))
                {
                    st.ClientIp = ip;
                    Interlocked.Increment(ref CfParsed);
                }
            }
        }

        // ---- host ----
        var hh = ctx.Request.Headers.Host;                       // field-backed known header, no allocation
        var host = hh.Count == 1 ? s.Hosts.Find(hh[0]) : null;
        if (host is null) return s.UnknownHost;
        st.Host = host;

        // ---- route (per-host ordered prefix list) ----
        var route = host.Match(ctx.Request.Path.Value.AsSpan());
        if (route is null) return s.UnknownHost;
        st.Route = route;

        // ---- policy chain (stage D) ----
        var view = new RequestView(ctx, st);
        foreach (var p in host.Policies)
        {
            var d = p.Evaluate(in view);
            switch (d.Action)
            {
                case PolicyAction.Continue: continue;
                case PolicyAction.Respond: return d.Response!;
                default: ctx.Abort(); return null;
            }
        }

        if (route.Respond is { } fixedResponse) return fixedResponse;

        // ---- hand off to YARP: endpoint from the same snapshot ----
        ctx.SetEndpoint(s.Endpoints[route.Slot]);
        forwarded = true;
        return null;
    }

    [ThreadStatic] private static GateRequestState? t_locals;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static GateRequestState Pooled(HttpContext ctx)
    {
        // Kestrel: IPersistentStateFeature lives on the Http1Connection (H1) or the pooled Http2Stream/Http3Stream,
        // so the object is reused across requests on a keep-alive connection and across pooled H2/H3 streams, and it
        // is never shared by two concurrent requests.
        var state = ctx.Features.Get<IPersistentStateFeature>()!.State;
        if (state.TryGetValue(PooledKey, out var o)) return (GateRequestState)o!;
        var st = new GateRequestState();
        state[PooledKey] = st;
        return st;
    }

    /// <summary>The middleware: run the synchronous gate, then (outside the measured section) call next.</summary>
    public static Func<HttpContext, RequestDelegate, Task> Middleware => static (ctx, next) =>
    {
        var a0 = Measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        var response = Run(ctx, out var forwarded);
        if (Measure)
        {
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            Account(a1 - a0, forwarded);
            if (forwarded) { t_handoff = a1; t_armed = true; }
        }
        // The body write is Kestrel's work (it may rent a pipe block) and is deliberately outside the measured section.
        return response is not null ? ResponseWriter.WriteAsync(ctx, response) : forwarded ? next(ctx) : Task.CompletedTask;
    };

    /// <summary>Stamp at the YARP hand-off; the first proxy-pipeline middleware reads it (same thread, no await in between).</summary>
    [ThreadStatic] public static long t_handoff;
    [ThreadStatic] public static bool t_armed;
}
