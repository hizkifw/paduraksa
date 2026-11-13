# Spike S3: Zero-allocation gate on Kestrel

**Status:** done. **Runtime:** .NET 10.0.13 (SDK 10.0.113), ASP.NET Core / Kestrel 10.0.13, **YARP 2.3.0**, BenchmarkDotNet 0.15.8. Release build, Server GC for the server runs.
**Box:** 20 cores / 62 GB, Linux (CachyOS), shared with other agents. Byte counts are exact and reproducible. Nanosecond and rps numbers are indicative only (ShortRun, shared machine).

Code: `spikes/S3-zero-alloc-gate/` (standalone, not in `Paduraksa.slnx`). Raw output is in `spikes/S3-zero-alloc-gate/results/` (`selftest.txt`, `probe.txt`, `bench.txt`, `e2e.txt`).

```
spikes/S3-zero-alloc-gate/run.sh [selftest|probe|bench|e2e|all]
# or individually:
dotnet run -c Release --project spikes/S3-zero-alloc-gate -- selftest        # correctness + AllocAssert (item 8)
dotnet run -c Release --project spikes/S3-zero-alloc-gate -- probe 20000     # per-operation bytes on real Kestrel, H1 + H2
DOTNET_gcServer=0 dotnet run -c Release --project spikes/S3-zero-alloc-gate -- bench [HostLookup|HostSource|IpParse|Policy|Dispatch|Gate]
dotnet run -c Release --project spikes/S3-zero-alloc-gate -- e2e gate h1 /x 4 16 3 5 [browser] [generichost] [cf] [xfoff] [noauthz] [vary] [ticks]
```

## Answer

**Yes, for our code.** The gate (stages A, C and D up to the YARP hand-off) runs at **0 B/request** on Kestrel in steady state over keep-alive connections, on both HTTP/1.1 and HTTP/2.
- **What the gate covers:** the snapshot read, client IP (connection-level and `CF-Connecting-IP`), host lookup with port, case and wildcard handling, route match, a 3-policy chain, per-request state published as a feature, `SetEndpoint`, and choosing a precomputed 200/301/403.
- **Where the 0 B was measured:** with minimal test traffic and with Chrome-like traffic.
- **Excluded:** the body write, which is Kestrel's work.

**Kestrel and YARP do allocate.** With realistic browser traffic **Kestrel alone allocates ~1.0-1.1 KB/request**. That traffic is 13 Chrome headers including `sec-ch-ua*`, `sec-fetch-*`, `priority` and `Cookie`, a varying `Accept`/`Referer`, and 64 rotating paths with query strings. Kestrel decodes header values and paths that differ from the previous request into new strings, and allocates name and value strings for unknown headers. A **realistic proxied request costs ~4.1-4.6 KB** in the whole proxy process: Kestrel ~1 KB, routing and YARP init 152 B, and YARP's forwarder plus `SocketsHttpHandler` ~3 KB. The "Kestrel 0 B" result in the first version of this document holds **only for synthetic traffic** that repeats the same few headers and path. It must not be read as Kestrel's real cost.

| Server-side bytes/request (whole proxy process, steady state) | HTTP/1.1 | HTTP/2 |
|---|---|---|
| **Gate** (our synchronous code, per-thread counter), every scenario | **0.00** | **0.00** |
| **Browser-like traffic** (`browser`) | | |
| Kestrel alone (bare app writing a precomputed 200) | **1,057** | **1,011** |
| Kestrel + gate answering 200 / 403 itself (generic host) | 1,093 / 1,090 | 975 / 973 |
| Kestrel + gate + **YARP forward**, `WebApplication` (implicit `UseAuthorization`) | 4,303 | 4,606 |
| Kestrel + gate + **YARP forward**, generic host (no implicit auth middleware) | **4,143** | **4,446** |
| ... plus `CF-Connecting-IP` sent and honoured | 4,782 | 5,087 |
| **Minimal synthetic traffic** (2 headers, 1 or 8 paths) | | |
| Kestrel alone, same path / 8 rotating paths | 0.0 / 32 | 0.0 / 28 |
| Kestrel + gate answering 200 / 301 / 403 | 0.0 | 0.0 |
| Kestrel + gate + YARP forward, `WebApplication` | 2,816-2,857 | 3,176-3,223 |
| ... `noauthz` (implicit auth middleware suppressed) | 2,656 | 3,017 |
| ... `noauthz` + `X-Forwarded` off | 2,504 | 2,864 |
| ... with `CF-Connecting-IP` | 2,920 | 3,280 |
| Hand-off → first proxy-pipeline middleware (routing + YARP init), all proxied runs | 152 | 152 |

Run-to-run noise on the proxied H2 rows is about ±150 B. The first run of this spike measured 3,024-3,067 B for the same H2 configuration.

The gate takes a few hundred ns: the full synchronous gate is 145-225 ns on a `DefaultHttpContext`. Within that, host lookup is 11-22 ns, the 3-policy chain 66 ns, and `CF-Connecting-IP` parsing 13-58 ns.

---

## 1. Host resolution without string allocation

**Cheapest source:** `HttpContext.Request.Headers.Host`. Kestrel stores known request headers in fields, so this is a `StringValues` over the string Kestrel already has. Kestrel also reuses that string across requests on a connection when the value repeats. `[0]` returns it, at 0 B on H1 and H2 (`probe.txt`; on H2 this is the `:authority`). `IHttpRequestFeature` has no separate Host, it's the same header dictionary.

| Accessor (Host = `h123.test:8443`) | Bytes | ns (BDN, DefaultHttpContext) |
|---|---|---|
| `Request.Headers.Host[0]` + strip port as span | 0 | 9.6 |
| `Request.Host.Value` / `.Port` | 0 | 14 / 37 |
| **`Request.Host.Host` with a port** | **40 B** (Substring) | 39 |
| `IPAddress.TryParse` (for comparison) | 40 B v4, 80 B v6 | 20 / 67 |

**Lookup** (`HostTable<T>` in `HostTable.cs`):
- Two `FrozenDictionary<string, T>` instances built with `StringComparer.OrdinalIgnoreCase`, one for exact names and one for wildcard parents (`*.w3.test` is stored as `w3.test`). Both are queried through `GetAlternateLookup<ReadOnlySpan<char>>()`, and the lookup structs are cached in fields.
- `HostName(span)` handles the raw header: an IPv6 literal (`[...]`) is kept whole up to `]`; otherwise the last `:port` and a trailing dot are stripped.
- The lookup tries exact first. On a miss it drops the first label once and tries the wildcard table, so a wildcard is single-label like a TLS wildcard: `a.b.w3.test` and `w3.test` don't match `*.w3.test`.
- IDN names are converted to punycode **at compile time** (`IdnMapping.GetAscii` works under `InvariantGlobalization`). Browsers send punycode. A raw Unicode Host header is a miss, and there's no runtime conversion.

BenchmarkDotNet, 520-entry table (500 exact including 10 IDN names, plus 20 wildcards), workstation GC:

| Input | `HostTable.Find(span)` | Naive (`Substring` port + `"*"+parent` + `Dictionary<string>`) |
|---|---|---|
| `h123.test` | 16 ns, 0 B | 14 ns, 0 B |
| `H123.TEST` | 17 ns, 0 B | 15 ns, 0 B |
| `h123.test:8443` | 17 ns, 0 B | 22 ns, **40 B** |
| `xn--bcher57-n2a.test` (IDN) | 21 ns, 0 B | 16 ns, 0 B |
| `a.w3.test` (wildcard) | 22 ns, 0 B | 30 ns, **80 B** |
| `nope.example.org` (unknown) | 11 ns, 0 B | 31 ns, **96 B** |

The frozen alternate lookup is within a few ns of a plain `Dictionary` on clean hits, and it never allocates. `AllocAssert` over all six inputs reports 0 B for the span version and 216 B/iteration for the naive one (`selftest.txt`).

## 2. Client IP and per-connection state

**`ClientInfo` per connection** (`ClientIp.cs`): a `ListenOptions.Use` middleware allocates one `ClientInfo` per connection. That's an **immutable** sealed class (`init`-only properties) holding the peer IP as `UInt128`, a country index placeholder and a trusted-proxy flag. The peer IP comes from `IPAddress.TryWriteBytes` into `stackalloc`. IPv4 is stored IPv4-mapped (`::ffff:a.b.c.d`), so the dual-mode TCP form and the bare QUIC form produce the same key. The middleware stores the object in two places, so both could be compared: `ConnectionContext.Features.Set<IClientInfoFeature>(info)` and `ConnectionContext.Items[key]`.

**Are connection features visible from `HttpContext.Features`? Yes, on H1 and H2.** Kestrel's per-request feature collection falls back to the connection's feature collection for types it doesn't know. Results from `probe.txt`, over 20,000 requests per protocol after warm-up:

| Per-request read | H1 | H2 | Misses |
|---|---|---|---|
| `HttpContext.Features.Get<IClientInfoFeature>()` (custom connection feature) | 0 B | 0 B | 0 |
| `Features.Get<IConnectionItemsFeature>().Items.TryGetValue(key)` | 0 B | 0 B | 0 |
| `Connection.RemoteIpAddress` → `UInt128` (per-request recompute) | 0 B | 0 B | - |

All three are allocation-free. **Use the custom connection feature.** It is typed, it needs no dictionary lookup or cast, and it is where the stage-A work (country lookup, trusted-proxy check, PROXY-protocol IP) naturally lands.

**Concurrency: the connection object is shared by every stream.**
- On H2 (and H3), each stream's feature collection falls back to the *same* connection feature collection. `HttpProtocol.Generated.cs` falls back to `ConnectionFeatures`, and `CreateHttp2StreamContext` passes the connection's collection to every stream.
- Concurrent requests on one connection therefore read the same `ClientInfo` instance. It **must be immutable**, and the spike's `ClientInfo` now is.
- Anything per request, such as the `CF-Connecting-IP` result, belongs in the per-request state (§3), never in `ClientInfo`.

`IConnectionItemsFeature.Items` has the same sharing but is worse in two ways. It is untyped, and it's **one plain, non-thread-safe `Dictionary` shared by all concurrent H2 streams**, so a write from any stream races with reads from the others. Don't use it on the request path.

**Header-based IP (`CF-Connecting-IP`):** `Request.Headers["CF-Connecting-IP"]` is a lookup in Kestrel's unknown-header dictionary, at 0 B. Parsing is not free with the BCL:
- `IPAddress.TryParse(ReadOnlySpan<char>)` **allocates the `IPAddress`**: 40 B for v4, and 80 B for v6 because the IPv6 `IPAddress` carries a `ushort[8]`. That holds on Kestrel and in BDN.
- The spike's `Ip.TryParse(span, out UInt128)` handles dotted quad, IPv6 with one `::` and an embedded IPv4 tail. It rejects zone IDs, brackets, ports, whitespace and leading zeros.
- It is **0 B and faster**: 15 ns against 20 ns for v4, 58 against 67 ns for v6, and 35 against 65 ns for mapped v6.
- It agreed with `IPAddress.TryParse` on 400,000 random v4/v6/compressed/mapped round-trips, and rejected 20 malformed forms (`selftest`).
- **Coverage gap:** the round-trips only cover canonical forms. The deprecated IPv4-compatible form `::a.b.c.d` parses to `::a.b.c.d` and does **not** collapse to the IPv4 key. Only `::ffff:a.b.c.d` does, which matches the BCL. M1 should keep the edge-case list and add a differential fuzz against `IPAddress.TryParse` over non-canonical inputs: mixed case, uncompressed zero runs, `::` at every position, and IPv4 tails in non-final positions.

**Kestrel's own cost for the header is not zero.** An unknown request header makes Kestrel allocate its name and value strings on every request, and YARP then copies it to the upstream request. With `CF-Connecting-IP` present, the proxied request cost **+104 B (H1) / +256 B (H2)**. The gate's part stayed at 0 B (`cf` rows; 593k/593k and 1.0M/1.0M requests parsed). With browser-like traffic the delta was larger: +639 B (H1) and +641 B (H2), which includes run-to-run noise. This is accepted, because it's the price of being behind Cloudflare. Per [cloudflare.md](../design/cloudflare.md), M1 must also *remove* the header before forwarding, which is a YARP transform.

## 3. Per-request state hand-off between gate stages

The question was whether the gate can hand state between stages without `HttpContext.Items`. Kestrel probe (H1 4 keep-alive connections; H2 1 connection with 16 concurrent streams):

| Mechanism | H1 | H2 | Notes |
|---|---|---|---|
| `new GateRequestState()` + `Features.Set<IGateFeature>(st)` | 80 B | 80 B | Exactly the object. **`Features.Set` itself is 0 B**: Kestrel's extra-feature list is cleared and reused per request/stream. |
| Object cached in **`IPersistentStateFeature.State`**, reset per request, then `Features.Set` | **0 B** | **0 B** | 20 objects created for 44,000 requests: 4 H1 connections + 16 pooled H2 streams. |
| `Features.Get<IGateFeature>()` in a later stage | 0 B | 0 B | |
| Locals inside one middleware | 0 B | 0 B | Later stages (YARP transforms, logging) can't see them. |

`IPersistentStateFeature` (`Microsoft.AspNetCore.Connections.Features`) is Kestrel's built-in answer to this. Its `State` dictionary lives on the `Http1Connection` for H1 and on the **pooled `Http2Stream`** (and `Http3Stream`) for H2/H3. It survives requests, but it is never shared by two concurrent requests. A "one object per connection" pool you build yourself would be **wrong on H2**, where many streams on one connection run concurrently. `IPersistentStateFeature` avoids that.

In the e2e H2 runs the gate showed 0-2 non-zero requests per run out of 0.75-2.1 M, each exactly **408 B**. That is a freshly created stream's state dictionary plus the `GateRequestState`, amortised to 0.00 B/req.

**Pooling caveats:**
- A stream returns to the pool only when it **ends gracefully** (`CanReuse`).
- The pool holds at most **100** streams.
- Idle pooled streams are **evicted after ~5 s**.

So RST_STREAM, client cancellations, our own `Abort()`/Drop, bursts above 100 concurrent streams and bursty idle connections all create a fresh stream, and with it a new dictionary and state object (408 B). "1-2 per million" reflects this benchmark's steady, well-behaved load, not a property of the mechanism. Under attack traffic, where Drop is common, the per-request cost of a dropped request is ~408 B. That's still small next to Kestrel's own per-request cost.

Cost: on a `DefaultHttpContext` (BDN) the pooled variant is the slowest: 145 ns locals, 185-192 ns new feature, 219-226 ns pooled. That's because `DefaultHttpContext`'s feature collection is a `Dictionary`. On Kestrel `IPersistentStateFeature` is a field-backed fast path, so the extra cost there should be little more than one `Dictionary<object, object?>` lookup. That wasn't timed separately on Kestrel.

## 4. Snapshot read + route pick + `SetEndpoint`

The forward path is `SnapshotStore.Current` (one `Volatile.Read`), then host lookup, then `CompiledHost.Match(path span)`, an ordered segment-aware prefix list. Then `ctx.SetEndpoint(snapshot.Endpoints[slot])`, then `next` → `UseRouting` → YARP. It costs **0 B**:
- in the Kestrel probe;
- in `AllocAssert` on the whole `Gate.Run`;
- and in every e2e proxy run: 0 non-zero gate requests on H1, and at most 2 per run on H2 (408 B each: new stream state, §3).

Kestrel's feature collection implements `IEndpointFeature` itself, as S2 found. The 152 B from the hand-off to the first proxy-pipeline middleware matches S2. That cost is `EndpointRoutingMiddleware` and YARP's pipeline initialiser (`ReverseProxyFeature`). It's not ours.

## 5. Policy chain shape

`IRequestPolicy.Evaluate(in RequestView)` returns a 16-byte `PolicyDecision` struct holding an action enum (`Continue`/`Respond`/`Challenge`/`Drop`) and a `PrecomputedResponse?`. `RequestView` is a `readonly ref struct` over `(HttpContext, GateRequestState)`. Measured with BDN on `DefaultHttpContext`, all at 0 B:

| Policy | ns |
|---|---|
| `IpRangePolicy`: 2,000 CIDRs (v4-mapped /24 + v6 /48) merged into sorted `UInt128` start/end arrays, binary search | 7.7 |
| `UserAgentPolicy`: 30 needles in `SearchValues<string>` with `OrdinalIgnoreCase`, Chrome UA, no match | 39 |
| `PathPrefixPolicy`: 4 segment-aware prefixes, `OrdinalIgnoreCase` | 15 |
| Chain of all three through `IRequestPolicy[]` | 66 |

**Dispatch shape** (8 mixed policies, megamorphic call site, trivial bodies): interface 15.5 ns, abstract class 11.2 ns, enum switch 6.8 ns. That's at most **~1 ns per policy** of difference, and it disappears next to real policy bodies, where the UA check alone is 39 ns. **Keep `IRequestPolicy` over sealed classes.** It is open for new policies (PoW, bot heuristics) and testable one at a time. An enum switch is only worth it if profiling ever shows dispatch matters, and it won't at this scale.

## 6. Responding without allocation

`ResponseWriter.WriteAsync(ctx, PrecomputedResponse)` sets the status, `Headers.ContentType`, `Headers.Location`, `Headers.CacheControl` and `ContentLength`. It then calls `BodyWriter.WriteAsync(byte[])` and returns `Task.CompletedTask` when the `ValueTask<FlushResult>` completed synchronously, which it did for 100 % of the 40,000 probe requests. Probe results:

| Header write (precomputed `StringValues`, shared by every request) | H1 | H2 |
|---|---|---|
| `Response.Headers.Location = sv` | 0 B | 0 B |
| `Response.Headers["Location"] = sv` | 0 B | 0 B |
| `Response.Headers.CacheControl = sv` | 0 B | 0 B |
| `Response.Headers["X-Paduraksa"] = sv` (unknown header) | 0 B | 0 B |
| `Response.ContentType = "const"` | 0 B | 0 B |
| `ResponseWriter.WriteAsync` (sync part) | 0 B | 0 B |

End to end, with minimal traffic, gate-answered requests cost **0.0 B/req for the whole process** on H1 and H2: 200 (`/static`), 301 with `Location` (`/old`), and 403 from the path policy (`/admin`). That's 1.8-1.9 M rps on H1 pipelined and 0.4-0.67 M rps on H2. With browser-like traffic the process total is ~975-1,093 B/req, which is Kestrel's request parsing. The gate and the response writing still add nothing measurable over bare Kestrel.

**A precomputed `StringValues` is safely reusable across requests and connections.** It's an immutable struct over an immutable string, and Kestrel encodes it into the output pipe without keeping the instance. The typed property (`Headers.Location`) and the string indexer both cost 0 B on Kestrel. The typed property skips the name lookup, so prefer it. Unknown response headers are also 0 B to set, since Kestrel reuses the dictionary per connection/stream. The per-thread probe doesn't cover H2's HPACK encoding of an unknown header at write time, and the e2e responses only use known headers, so that case is unmeasured.

## 7. End-to-end, in-process server

**Setup** (`E2E.cs`):
- The proxy process runs either `WebApplication.CreateSlimBuilder` (default) or, with `generichost`, `new HostBuilder().ConfigureWebHost(... .Configure(app => ...))` with an explicit pipeline. Kestrel has one listener, H1 *or* H2 cleartext prior-knowledge, plus the `ClientInfo` connection middleware.
- The pipeline is: request counter → gate → `UseRouting` → `UseEndpoints(e => e.MapReverseProxy(...))` with a custom pipeline (affinity, LB, passive health), holding 500 path-only YARP routes (`/.paduraksa/r/{slot}/{**rest}`).
- The snapshot has 520 hosts. Each host has 3 policies and 3 routes: `/static` → 200, `/old` → 301, `/` → YARP slot.
- The **upstream** (a Kestrel app returning 16 bytes) and the **load generator** run as **separate processes**, so `GC.GetTotalAllocatedBytes(precise: true)` deltas in the proxy count only the proxy's own allocations.
- The H1 load is raw sockets: 4 connections, 16 requests pipelined per batch, one host per connection. The H2 load is HttpClient on one connection with 64 concurrent streams.
- **Traffic shapes:**
  - *minimal* sends `User-Agent` and `Accept`, with 1 path, or 8 with `vary`.
  - *`browser`* sends 13 Chrome-like headers (`sec-ch-ua`, `-mobile`, `-platform`, UA, `Accept` alternating, `Sec-Fetch-Site/Mode/Dest` alternating, `Referer` over 5 values, `Accept-Encoding`, `Accept-Language`, `Cookie`, `priority`) and 64 rotating paths with query strings under the scenario's prefix.
- Each run has 3 s of warm-up, then a 5 s window. An idle second is measured first; background allocation was 0.02-0.5 B/req.
- Gate bytes come from `GC.GetAllocatedBytesForCurrentThread()` around the gate's synchronous **decision** section (`Gate.Run`) on every request. **The body write is outside the measured section.** The first version also measured `ResponseWriter.WriteAsync`, and its rare 4,160/4,256 B outliers were Kestrel renting output-pipe memory blocks, not gate state. Pool eviction would have made an M1 `gateBytes == 0` assertion flaky. After the change, the only outliers left are the 408 B new-H2-stream cases (§3).
- A spy `IAuthorizationPolicyProvider` counts `GetFallbackPolicyAsync` calls, to show whether an authorization middleware runs.

Results from `results/e2e.txt` (bytes/request; "auth" = fallback-policy lookups per request):

| Run | H1 total | H1 gate | H2 total | H2 gate | auth |
|---|---|---|---|---|---|
| bare (no gate), same path | 0.0 | - | 0.0 | - | 0 |
| bare, 8 rotating paths | 32.0 | - | 28.1 | - | 0 |
| gate → 200 `/static` | 0.0 | 0.00 | 0.0 | 0.00 | 1 |
| gate → 403 `/admin` (policy) | 0.0 | 0.00 | 0.0 | 0.00 | 1 |
| gate → 301 `/old` | 0.0 | 0.00 | 0.0 | 0.00 | 1 |
| gate → YARP `/x` | 2,816 / 2,857 | 0.00 | 3,176 / 3,223 | 0.00 | 1 |
| gate → YARP, rotating paths | 2,856 | 0.00 | 3,213 | 0.00 | 1 |
| gate → YARP + `CF-Connecting-IP` | 2,920 | 0.00 | 3,280 | 0.00 | 1 |
| gate → YARP, `noauthz` | 2,656 | 0.00 | 3,017 | 0.00 | 0 |
| gate → YARP, `noauthz` + `X-Forwarded` off | 2,504 | 0.00 | 2,864 | 0.00 | 0 |
| **browser:** bare | 1,057 | - | 1,011 | - | 0 |
| **browser:** gate → 200 `/static`, generic host | 1,093 | 0.00 | 975 | 0.00 | 0 |
| **browser:** gate → 403 `/admin`, generic host | 1,090 | 0.00 | 973 | 0.00 | 0 |
| **browser:** gate → YARP, `WebApplication` | 4,303 | 0.00 | 4,606 | 0.00 | 1 |
| **browser:** gate → YARP, generic host | 4,143 | 0.00 | 4,446 | 0.00 | 0 |
| **browser:** gate → YARP, generic host + `CF-Connecting-IP` | 4,782 | 0.00 | 5,087 | 0.00 | 0 |

Across all 34 runs the gate allocated on 0-2 requests per run, always H2 and always exactly 408 B. That rounds to 0.00 B/req.

**`GC.GetTotalAllocatedBytes(precise: true)` is not always monotonic under Server GC.** The reviewer saw it go backwards in 2 of 12 runs. `E2E` now flags a negative delta as `NON-MONOTONIC ... discard`; none occurred in the committed runs. M1's process-level benchmark must discard non-monotonic samples, or repeat the run and take the median.

### What allocates, and whose it is
The Answer table and this list use the same attribution: everything outside the gate belongs to Kestrel, routing or YARP.

- **Kestrel: ~1.0-1.1 KB/req with browser-like traffic; 0 B only for synthetic, repetitive traffic.**
  - Kestrel avoids allocating a header-value or path string only when it equals the previous request's value on the same connection.
  - Real browsers vary `Accept`, `Sec-Fetch-*`, `Referer` and the path, so those become new strings.
  - Every **unknown request header** (`sec-ch-ua*`, `priority`, `CF-Connecting-IP`, ...) costs its name and value strings on every request.
  - A changing path costs ~28-32 B for the decoded path string, plus the query string.
  - On H2, when the app goes async (any proxied request), Kestrel's `HttpProtocol.ProcessRequests` state-machine box appears in the allocation ticks. That's inherent to async request handling.
  - **All accepted.** It is Kestrel's request parsing and outside our control.
- **ASP.NET routing + YARP init: 152 B/req** from the hand-off to the first proxy middleware. Accepted, as in S2.
- **Implicit `AuthorizationMiddleware`: ~160 B/req, and a correctness hazard.** This was mis-described in the first version. Verified at ASP.NET Core 10.0.13 / YARP 2.3.0:
  - YARP's `AddReverseProxy` calls `AddAuthorization()` **unconditionally** (`ReverseProxyServiceCollectionExtensions.cs:75`). That registers `IAuthorizationHandlerProvider`, so `WebApplication` auto-adds `UseAuthorization()`.
  - Because the app calls `UseRouting()` itself, `WebApplication` inserts that middleware **before the whole user pipeline**, ahead of the gate. It runs with **no endpoint**, so it never sees YARP route metadata.
  - It is an async wrapper around everything after it. It allocates a state-machine box whenever downstream goes async (every proxied request), and it looks up the fallback policy on **every** request, gate-answered ones included. The spy counted 1 `GetFallbackPolicyAsync` call per request.
  - **Hazard:** if anyone ever configures an authorization `FallbackPolicy` (a common "require authenticated users" snippet), it runs **before the gate** on every request of the public proxy.
  - The component that would throw if a YARP route ever carried `AuthorizationPolicy` without the middleware is ASP.NET's `EndpointMiddleware`, not YARP.
  - **Generic host: verified at 0 extra bytes.** With an explicit pipeline (`new HostBuilder().ConfigureWebHost(w => w.UseKestrel(...).ConfigureServices(...).Configure(app => { app.Use(gate); app.UseRouting(); app.UseEndpoints(e => e.MapReverseProxy(...)); }))`), nothing is auto-inserted. The spy counted 0 calls. Allocation matches the internal-key suppression exactly: 2,656 B (H1, minimal traffic) both ways, and browser 4,143 against 4,303 B (H1) and 4,446 against 4,606 B (H2).
- **YARP forwarder + `SocketsHttpHandler`: ~2.3-2.7 KB/req with minimal traffic, ~3 KB with browser headers.** It grows because every forwarded header is copied into `HttpRequestMessage`. AllocationTick sampling (`ticks` runs) shows:
  - `HttpRequestMessage`/`HttpResponseMessage` and their header collections (`HeaderEntry[]`, `HttpResponseHeaders`, `HttpContentHeaders`), plus `Uri`/`UriInfo`.
  - Async state-machine boxes: `HttpForwarder.SendAsync`, `HttpConnectionPool.SendWithVersionDetectionAndRetryAsync`, `HttpConnection.SendAsync`, `ForwarderMiddleware`, `PassiveHealthCheckMiddleware`.
  - `ReverseProxyFeature`, `RequestTransformContext`, and header-value strings.
  - Default `X-Forwarded-*` transforms cost ~150 B. Paduraksa replaces them with its own ([cloudflare.md](../design/cloudflare.md)), so that's roughly the budget for our transforms.
  - **Accepted, and out of scope per [hot-path.md, Budget and rules](../design/hot-path.md#budget-and-rules).** Reducing it would mean replacing `HttpClient` underneath YARP.

## 8. Zero-alloc test helper for M1

`AllocAssert` (`AllocAssert.cs`):

```csharp
public static Result Measure<TState>(TState state, Action<TState> action, int warmup = 1_000, int iterations = 10_000);
public static void Zero<TState>(TState state, Action<TState> action, string what, int warmup = 1_000, int iterations = 10_000);
// Result: TotalBytes, Iterations, FirstNonZeroIteration, MaxSingle
```

**How it works:**
- It warms up, then measures every iteration separately with `GC.GetAllocatedBytesForCurrentThread()`, which is exact and not sampled. The failure message reports the total, B/call, the largest single call and the first offending iteration, so a one-off lazy is easy to tell apart from a per-call allocation.
- It takes **a static lambda plus explicit state** rather than a closure, so the test can't accidentally measure, or hide, a closure allocation.

`selftest.txt` shows it catching what it should:

| What | Result |
|---|---|
| Correctly 0 B | `HostTable.Find` (6 inputs), `Ip.TryParse`, the 3-policy chain, `Gate.Run` (Pooled and Locals) |
| Correctly non-zero | naive host lookup 216 B, `IPAddress.TryParse` 120 B, `Request.Host.Host` 40 B, `Gate.Run` with `NewFeature` 80 B, a LINQ `FirstOrDefault` over a non-empty dictionary 40 B |

**What it can assert:**
- Synchronous code that runs start to finish on the calling thread: lookups, parsers, policy `Evaluate`, `Gate.Run`, response-header setup, and `ResponseWriter` when the `ValueTask` completes synchronously.
- For gate code, drive it with a **`DefaultHttpContext` set up like Kestrel's**:
  - set `IPersistentStateFeature` and `IClientInfoFeature` on it;
  - set the headers;
  - reuse the context across iterations.

  `DefaultHttpContext` allocates where Kestrel doesn't, on its first `Features.Set` of a new type and on `SetEndpoint` creating an `EndpointFeature`. Warm-up absorbs both, but **never construct a new context per iteration**.

**What it can't assert:**
- **Anything that really awaits.** After a suspension the continuation runs on a pool thread and the caller's counter misses it. The demo `await Task.Yield(); new byte[1000]` reports 96 B: just the box, and none of the 1000 B. For async policies (forward-auth), test the synchronous fast path (cache hit) with `AllocAssert`, and cover the rest with the process-level test below.
- **Thread hops in general.** `Task.Run`, timers, `ThreadPool` work, and Kestrel's own I/O threads aren't visible.
- **Kestrel behaviour.** `DefaultHttpContext` differs from Kestrel's feature collection: Kestrel is field-backed, with header/path reuse and stream pooling.

**For Kestrel behaviour, M1 should add one in-process integration test in the `e2e`/`probe` shape:**
- Kestrel on loopback, H1 + H2 listeners, the real gate.
- A raw socket client for H1 and an `HttpClient` for H2. Client allocations don't matter because measurement is per-thread on the server side.
- A test-only hook that sums `GetAllocatedBytesForCurrentThread()` deltas around the gate's synchronous section.
- Measure only `Gate.Run`, the decision, and **not** the body write. Writing goes through Kestrel's output pipe, which rents memory-pool blocks: those were the 4 KB outliers in the first version.
- After ~2,000 warm-up requests, assert `gateBytes == 0` over ~10,000 requests **on H1**. On H2 a new stream costs 408 B (§3), so either assert "every non-zero sample is exactly the new-stream size and there are at most N of them", or give the test a well-behaved client with ≤ 100 concurrent streams and no cancellations.
- Keep `GC.GetTotalAllocatedBytes` process deltas (client out of process) as a **benchmark**, not a test: Kestrel and YARP numbers change with their versions and shouldn't be assertions. Track them in `bench/`, use browser-like traffic, and discard non-monotonic samples (§7).

**Pitfall:** BenchmarkDotNet's `Allocated` column was **wrong under Server GC** with the in-process toolchain. It showed 675 B and 1,545 B for `IPAddress.TryParse` where the truth is 80 B, and `-` for rows that allocate 40-96 B. Under workstation GC (`DOTNET_gcServer=0`) it matches `AllocAssert` exactly. M1's `bench/` project uses Server GC (`ServerGarbageCollection=true`). Either run BDN in its default out-of-process toolchain, or force workstation GC for allocation benchmarks.

---

## Recommendation for M1

### Gate structure
One middleware, first in the pipeline. It does synchronous work in a non-async method and calls `next` only on the forward path:

```csharp
app.Use(static (ctx, next) =>
{
    var t = Gate.Run(ctx, out var forward);   // sync: 0 B
    return forward ? next(ctx) : t;           // t = Task.CompletedTask or a rare real await
});
app.UseRouting();                             // skips matching: endpoint already set
app.UseEndpoints(e => e.MapReverseProxy(p => { p.UseSessionAffinity(); p.UseLoadBalancing(); p.UsePassiveHealthChecks(); }));
```
In the spike, `Gate.Run` returns the `PrecomputedResponse` to write (or null), and the middleware writes it *after* the measured section: `return response is not null ? ResponseWriter.WriteAsync(ctx, response) : forward ? next(ctx) : Task.CompletedTask;`.

**Build the public app on the generic host with this explicit pipeline**: `new HostBuilder().ConfigureWebHost(w => w.UseKestrel(...).ConfigureServices(...).Configure(app => ...))`. It uses public API only, with no `WebApplication` auto-wiring, so no implicit `UseAuthorization()` runs ahead of the gate (§7). It was verified at 0 extra bytes against the suppressed variant.
- **Fallback** if M1 stays on `WebApplication`: set `((IApplicationBuilder)app).Properties["__AuthorizationMiddlewareSet"] = true` (internal key, with a comment).
- **Guard test, needed with either option:** register a spy `IAuthorizationPolicyProvider` and assert it sees **zero** `GetFallbackPolicyAsync` calls across requests to the public app.

`Gate.Run` does the following, in order:
1. `var s = SnapshotStore.Current` (one volatile read).
2. Acquire the pooled state, then reset it.
3. Client IP: `ClientInfo` from the connection feature, then `CF-Connecting-IP` through `Ip.TryParse` if the peer is trusted.
4. `Hosts.Find(Headers.Host[0])`, with unknown hosts answered from precomputed bytes.
5. `CompiledHost.Match(Path span)`.
6. The `IRequestPolicy[]` chain.
7. A route-level Respond/Redirect, answered from precomputed bytes.
8. `ctx.SetEndpoint(s.Endpoints[slot])`.

An async policy (forward-auth miss) is the only legitimate await. It should return a dedicated "needs async" action (separate from `Challenge`), and the middleware continues in a separate `async` slow-path method, so the common path stays synchronous and box-free.

### Per-connection and per-request state
- **Per connection:** one **immutable** `ClientInfo` sealed class (shared by all concurrent H2/H3 streams on the connection), created in `ListenOptions.Use` (stage A, after PROXY protocol). It holds the `UInt128` IP (IPv4-mapped), the country index and the trusted-proxy flag, and is published with `ConnectionContext.Features.Set<IClientInfoFeature>(info)`. It's read per request through `HttpContext.Features.Get<IClientInfoFeature>()`, which works on H1 and H2 at 0 B. Don't use `IConnectionItemsFeature`: it's untyped, and it's one non-thread-safe dictionary shared by all streams.
  - **QUIC:** `ListenOptions.Use` has no QUIC equivalent (S1). Setting the feature from `TlsHandshakeCallbackOptions` on the QUIC connection context, and its visibility from H3 requests, is untested and should be an M1 acceptance test. Until then, fall back to computing from `Connection.RemoteIpAddress`, which is 0 B but repeats the country lookup per request.
- **Per request:** a `GateRequestState` sealed class (snapshot, host, route, client IP, country, timestamps). It's cached in **`IPersistentStateFeature.State`** under a static key object, `Reset()` at gate entry, and published with `Features.Set<IGateFeature>(st)` so YARP transforms, telemetry and error pages can read it with `Features.Get<IGateFeature>()`.
  - **Never use `HttpContext.Items`, and never build a hand-rolled per-connection pool**, which is unsafe under H2 multiplexing.
  - **Lifetime rule:** a later stage must not keep the state object after the request ends, because the next request on that connection or stream reuses it. Clearing references in `Reset()` also stops an old snapshot from being pinned for long.
  - **Locals only** is fine inside `Gate.Run` for anything later stages don't need.

### Host lookup
`HostTable<CompiledHost>` as in the spike:
- two `FrozenDictionary<string, T>` instances (exact + wildcard parent), `OrdinalIgnoreCase`, with `GetAlternateLookup<ReadOnlySpan<char>>()` cached;
- the port, IPv6-literal brackets and trailing dot handled on the span;
- exact match first, then a single-label wildcard;
- IDN converted to punycode at compile time.

This is 11-22 ns and 0 B for every input shape. Read the host from `Request.Headers.Host[0]`, **never `Request.Host.Host`**, which allocates 40 B when a port is present. Decide in M1 whether `*.example.com` should also match deeper names. If so, walk the remaining dots most-specific first; that's still 0 B.

**Gaps in the spike's table that M1's compile step must close:**
- **Duplicate names are silently last-wins.** The spike builds through a `Dictionary` indexer. M1's compiler must **reject** duplicates, including names that collide only after case folding, trailing-dot removal or punycode conversion, and duplicate wildcard parents.
- **IPv6-literal keys (`[2001:db8::1]`) aren't canonicalised.** `[2001:DB8:0::1]` and `[2001:db8::1]` are different keys. Either canonicalise literal host keys at compile time, and the incoming literal with the zero-alloc IP parser, or don't support IP-literal hosts and leave them to Default behaviour.

### Policy interface
```csharp
public interface IRequestPolicy { PolicyDecision Evaluate(in RequestView r); }
public readonly struct PolicyDecision { PolicyAction Action; PrecomputedResponse? Response; }   // 16 B, by value
public readonly ref struct RequestView { HttpContext Http; GateRequestState State; }
```
Sealed classes with all data compiled into the policy instance:
- sorted `UInt128` ranges (or the trie from [waf.md, The blocks at a glance](../design/waf.md#the-blocks-at-a-glance)) for IP rules;
- `SearchValues<string>` (`OrdinalIgnoreCase`) for user-agent rules;
- segment-aware prefix lists for path rules.

Interface dispatch costs ≤ 1 ns per policy compared with an enum switch, which isn't worth giving up the open design.

### Response-writing helpers
`PrecomputedResponse` is built at snapshot compile time. It holds the status, the body `byte[]`, and `StringValues` for `Content-Type`, `Location` and `Cache-Control` (plus any others). `ResponseWriter.WriteAsync` sets the headers through the **typed properties** (`Headers.Location`, `Headers.ContentType`, ...), sets `ContentLength`, writes with `BodyWriter.WriteAsync(byte[])`, and returns `Task.CompletedTask` on synchronous completion. It costs 0 B on H1 and H2, including redirects. Templated error pages ([waf.md, The blocks at a glance](../design/waf.md#the-blocks-at-a-glance)) write their segments with `BodyWriter.Write(span)` and format variables straight into `GetSpan()`. For example, a request ID uses `Utf8Formatter`/`IUtf8SpanFormattable`. Never build a string.

### Accepted Kestrel / YARP allocations
- **Kestrel: ~1.0-1.1 KB/req with real browser traffic.** That covers header values and paths that differ from the previous request on the connection, unknown headers (name and value), query strings, and the H2 request-loop box when the app goes async. It is 0 B only for repetitive synthetic traffic.
- **ASP.NET routing / YARP init:** 152 B.
- **YARP forwarder + `HttpClient`:** ~2.3-3 KB/req, growing with the number of forwarded headers.
- **Realistic proxied total: ~4.1-4.6 KB/req** (generic host, browser traffic).
- **Remove:** the implicit `AuthorizationMiddleware`, by using the generic host (~160 B, a box on every proxied request, and a fallback-policy hazard ahead of the gate).
- **Budget** for Paduraksa's own YARP transforms (X-Forwarded, X-Real-IP, request ID, security headers): about what YARP's default X-Forwarded costs, ~150 B. Transforms are YARP-side and may allocate header strings, but the request ID and IP strings should come from cached or precomputed values where possible.

### Zero-alloc tests
- `AllocAssert.Zero(state, static s => ...)` unit tests for every synchronous hot-path piece, driven with a reused `DefaultHttpContext` that carries Kestrel-like features.
- One in-process Kestrel test (H1 + H2) that asserts the gate's per-thread delta is 0 after warm-up.
- Process-level Kestrel/YARP bytes as a tracked benchmark, not an assertion.

## Surprises
- **Kestrel's 0 B/request in the first version was an artefact of synthetic traffic.** With Chrome-like headers and varying paths Kestrel allocates ~1 KB/request. Its string reuse only helps when a value repeats on the connection.
- **`IPAddress.TryParse(span)` allocates** (40/80 B), so client-IP parsing needs its own parser.
- **`Request.Host.Host` allocates** when a port is present.
- **`Features.Set<T>` of an unknown type is free on Kestrel**: the backing list is reused. Only the object you set costs anything.
- **Connection features are shared by all H2 streams**, so per-connection objects must be immutable, and `IConnectionItemsFeature.Items` is a shared non-thread-safe dictionary.
- **`IPersistentStateFeature`** is the right per-request pool: per connection on H1, per pooled stream on H2/H3. A per-connection pool would be wrong on H2. Its pooling depends on graceful stream ends, with a pool of at most 100 and 5 s idle eviction.
- **YARP's `AddAuthorization()` makes `WebApplication` insert `UseAuthorization()` ahead of the whole pipeline**, gate included, with no endpoint. It's ~160 B per proxied request, a fallback-policy lookup on every request, and a hazard if a `FallbackPolicy` is ever configured. The generic host with an explicit pipeline avoids it at 0 extra bytes.
- **Measuring the body write inside the gate section** produced rare 4 KB outliers (Kestrel's pipe memory blocks), which would make a strict 0 B test flaky.
- **BDN's `Allocated` column is unreliable** under Server GC with the in-process toolchain, and `GC.GetTotalAllocatedBytes(true)` can go backwards under Server GC.
