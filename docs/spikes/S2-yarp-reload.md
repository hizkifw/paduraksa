# Spike S2: YARP config churn + endpoint metadata binding

**Status:** done, review findings addressed. **YARP:** 2.3.0. **Runtime:** .NET 10 (SDK 10.0.113), Release, Server GC.
**Box:** 20 cores / 62 GB, Linux. Client, proxy and upstream all ran in one process, and other agents' builds were running on the same machine at the same time. Treat the numbers as indicative, not as benchmarks. Latency and cost numbers are more reliable than the throughput deltas.

Code: `spikes/S2-yarp-reload/` (standalone, not in `Paduraksa.slnx`). Raw output from the runs quoted here is in `spikes/S2-yarp-reload/results/` (`metadata`, `reload`, `load`, `health`, `gate`).

```
spikes/S2-yarp-reload/run.sh [metadata|reload|load|health|gate|all]
# or individually:
dotnet run -c Release --project spikes/S2-yarp-reload -- metadata
dotnet run -c Release --project spikes/S2-yarp-reload -- reload 500 one-route        # sizes: 100,500,2000; modes in Q2
dotnet run -c Release --project spikes/S2-yarp-reload -- load 500 64 10               # hosts, concurrency, seconds/phase
dotnet run -c Release --project spikes/S2-yarp-reload -- health
dotnet run -c Release --project spikes/S2-yarp-reload -- gate                         # gate-selected endpoint probe (i)-(v)
dotnet run -c Release --project spikes/S2-yarp-reload -- gate-cost 500,2000
dotnet run -c Release --project spikes/S2-yarp-reload -- gate-load GateSelect 500 64 5            # or RoutingFirst [... pending]
```

Base setup (Q1–Q5):
- A custom `IProxyConfigProvider`. Each snapshot (an `IProxyConfig`) owns one `CancellationTokenSource` and exposes a `CancellationChangeToken`. `Publish` does `Interlocked.Exchange` and then cancels the *old* snapshot's token.
- `app.MapReverseProxy(pipeline => …)` (the custom-pipeline overload), followed by `.ConfigureEndpoints((IEndpointConventionBuilder ep, RouteConfig route) => ep.WithMetadata(…))`.
- The compiler records which metadata object belongs to which `RouteConfig` **instance** in a `ConditionalWeakTable`, so the spike can prove which generation's `RouteConfig` was passed in.
- A "gate" middleware placed before `UseRouting()` captures the current snapshot once.
- Fleet: N hosts. Each host has 1 route (`Hosts=[hN.test]`, `Path=/{**catch-all}`) pointing at its own cluster, which has 1 or 2 destinations. The upstream is a tiny Kestrel app in the same process.

## Exit criteria (PLAN §13, S2)

| Criterion | Result |
|---|---|
| Publish < 50 ms at 500 hosts | **Met idle, all the way to the maximum.** In the host-matched layout a route-changing publish takes 15–23 ms median and ≤ 27 ms max. **Under load it is met only at the median:** 9–11 ms median, but up to 99–124 ms max (`results/load.txt`). With the recommended gate design: 1.9 ms median and ≤ 9 ms max idle; 4.6 ms median and ≤ 35 ms max under churn load. |
| No request errors during swaps under load | **Met.** 0 connection errors and 0 non-2xx across ~42 M requests (host-matched) plus ~5 M (gate design), with publishes every 100 ms at 500 and 2000 hosts. |
| Typed metadata on every endpoint matches its generation | **Not as PLAN §6.2 assumes.** Unchanged routes keep their old endpoint and old metadata. Solved exactly by the recommended gate design (below), which needs no generation metadata at all. |

---

## Q1. `ConfigureEndpoints` across reloads

### 1a. Does the callback re-run, and with which `RouteConfig`?

It runs **only for routes YARP considers new or changed**, and in that case it receives the **new** `RouteConfig` instance. **Unchanged routes keep their existing endpoint object**, so the callback does not run for them and their metadata stays at whatever generation last rebuilt them. Excerpt from `results/metadata.txt`:

```
--- gen 2: identical content, NEW RouteConfig/ClusterConfig instances, stable route IDs
    ConfigureEndpoints callbacks this publish: 0 -> []
    h0  meta.gen 1   RouteModel.Config is current instance? False   echo (meta-gen/snap-gen) 1/2
--- gen 3: h0 path changed, h1 metadata changed, c2 +destination, c3 LB policy changed, h5 removed, h6 added
    ConfigureEndpoints callbacks this publish: 4 -> [h6@g3, h3@g3, h1@g3, h0@g3]
    h0 3 True 3/3 | h1 3 True 3/3 | h2 1 False 1/3 | h3 3 True 3/3 | h4 1 False 1/3 | h6 3 True 3/3
--- gen 4: same content, route IDs encode generation
    ConfigureEndpoints callbacks this publish: 6 -> [4:3:0@g4, ...]       (all rebuilt)
```

YARP rebuilds a route's endpoint, and so re-runs the callback, when:
- the `RouteConfig` is not value-equal to the previous one. That covers a match change, a change to the string `Metadata`, and a new route ID.
- the route's **cluster** changed in anything **other than its destinations**. Examples are LB policy, cluster `Metadata`, health config and HttpClient config: `c3 LB policy` rebuilt `h3`, and `10pct-clusters` rebuilt about 49 routes per publish.

YARP does **not** rebuild the endpoint for:
- a destination-only change (`c2 +destination` did not rebuild `h2`).
- an identical config delivered as new object instances.

### 1b. Typed metadata in the pipeline, and allocations

`GetMetadata<T>()` returns the attached object inside the custom proxy pipeline. Over 5000 warm requests, two lookups per request allocated **0 bytes**. No metadata was missing in any load-test request.

### 1c. Does metadata guarantee "policy belongs to the same generation as the route"?

**No, for two separate reasons:**

1. **Stale metadata on unchanged routes.** This follows from 1a. A policy-only edit, such as adding an IP allowlist, leaves the YARP route unchanged. A per-generation `CompiledHost` attached as metadata would therefore keep serving the **old policies indefinitely**, and it would also pin old snapshots in memory.
2. **The gate-snapshot vs routed-endpoint window.**
   - **Mechanism.** In the base setup, the gate's snapshot is swapped *before* `Cancel()`. That spike ordering makes the window the **whole YARP reload**: requests that read the new snapshot and are then routed against the old endpoints.
   - **Rate.** A request disagrees only if it targets a route that changed in that publish. With publishes every *T* ms, each taking *R* ms, the expected mismatch rate is **≈ (share of traffic on changed routes) × min(1, R/T)**.
   - **PLAN §7.2 makes it longer.** The host policy chain, including forward-auth I/O, runs before routing, so the window grows by the policy-chain time as well.
   - **Measured counts follow the model, not a property of YARP.** At 500 hosts: 0.44 % of requests, with 5 % churn and R/T ≈ 0.1. At 2000 hosts: 4.8 %, because the publisher was saturated (R ≈ 103 ms against T = 100 ms), so the rate is simply the 5 % churn fraction.
   - **"0 mismatches" rows are zero by construction.** In the identical-content and 1-route phases, the only changed route was the probe host, which receives no load.
   - **Generation-encoded route IDs don't remove the window.** The endpoint is internally consistent, but it still disagrees with the gate's snapshot during the window.

Both problems disappear if the gate takes **both** the policies and the YARP endpoint from the same snapshot. That is the probe in Q6.

---

## Q2. Reload cost (host-matched layout)

Each (size, mode) pair ran in its own process with 60 warm-up publishes followed by 40 measured publishes, idle. "Live" is the time from `Publish()` until a request to the newly added host returns 200. There were 0 misses, so the change was live when `Publish()` returned (see Q5 for why that is not guaranteed).

Medians (p90) in ms. Allocation is the median MB per publish.

| Hosts | Mode | Publish ms | ≈ YARP part | ≈ matcher part | Alloc MB | Callbacks |
|---|---|---|---|---|---|---|
| 100 | noop (identical content) | 0.37 (0.52) | all | – | 0.17 | 0 |
| 100 | one-route | 1.56 (2.52) | 0.37 | 1.19 | 0.49 | 1 |
| 100 | gen-ids | 2.95 (6.51) | 0.96 | 1.92 | 0.87 | 101 |
| 500 | noop | **2.87 (7.46)** | all | – | 0.85 | 0 |
| 500 | one-route | **15.4 (15.9)** | 2.20 | 13.3 | 2.29 | 1 |
| 500 | 10pct-routes | 18.2 (21.7) | 1.13 | 17.1 | 2.51 | 49 |
| 500 | 10pct-clusters (cluster metadata) | 23.1 (26.3) | 2.15 | 20.6 | 2.56 | 49 |
| 500 | gen-ids | 15.0 (17.9) | 3.57 | 11.4 | 4.20 | 501 |
| 500 | path-only routes | 5.3 (7.8) | 2.35 | 2.85 | 1.80 | 1 |
| 2000 | noop | **6.8 (13.4)** | all | – | 3.30 | 0 |
| 2000 | one-route | **64.0 (84.8)** | 2.11 | 61.4 | 9.26 | 1 |
| 2000 | 10pct-routes | 66.8 (83.7) | 2.76 | 63.2 | 10.1 | 192 |
| 2000 | gen-ids | 59.6 (73.0) | 4.76 | 54.1 | 21.2 | 2001 |
| 2000 | path-only routes | 13.3 (19.6) | 10.1 | 3.1 | 7.19 | 1 |

**How to read the YARP/matcher split.** It is **approximate**. It is the time until YARP signals its endpoint change token, and a probe callback registered last runs first (LIFO), so work YARP does after the signal is counted as "matcher". It is not an exact measurement of YARP's diff. The noop rows are YARP's full reload with no endpoint change: 2.9 ms at 500 hosts and **6.8–7.5 ms at 2000 hosts** across runs. YARP's own part therefore grows with config size, and the "2–5 ms" seen in the split columns understates it at 2000 hosts.

**What drives the cost:**
- Every endpoint change triggers ASP.NET Core's eager DFA matcher rebuild. That rebuild costs the same whether 1 or 2000 endpoints changed.
- It grows super-linearly with route count, and **Host matching dominates it**: at 2000 hosts, publish drops from about 62 ms to about 12–13 ms total with path-only routes.
- Snapshot compile is ≤ 0.6 ms.

**Under load** (`results/load.txt`, 64 concurrent clients on the same cores), the host-matched layout behaves as follows:
- **500 hosts:** publish takes 9–11 ms median, with a max of 99–124 ms.
- **2000 hosts:** publish takes 100–250 ms median. The publisher could not keep up with 100 ms intervals.
- **Request impact:** at 2000 hosts, publishing every 100 ms cost about 5–10 % throughput (baselines 250–267 k rps; 236–246 k while publishing). p99 stayed roughly unchanged (1–2 ms), and p99.9 rose to as much as 12 ms with gen-ids.
- **The 500-host run was too noisy** to quantify, with baselines ranging from 119 k to 298 k rps, because the box was shared.

## Q3. Errors during swaps

**Zero.** Every load phase at 500 and 2000 hosts (≈ 42 M requests) had 0 exceptions and 0 non-2xx. The phases covered identical-content publishes, a single route changing, 5 % of routes plus 5 % of clusters changing (destinations added and removed in flight), and gen-ids rebuilding every endpoint every 100 ms. The gate-design loads in Q6 were also error-free.

## Q4. YARP health continuity

Setup:
- `c-health` has active checks every 500 ms using `ConsecutiveFailures`. `c-health-bad` (`/bad/` returns 500) becomes Active=Unhealthy.
- `c-passive` uses `TransportFailureRate` with `ReactivationPeriod` set to 10 min. `c-passive-dead` points at a closed port.
- A monitor sampled health and `DestinationState` identity about every 1 ms while traffic ran. A flap to Unknown would show up as 5xx.

| Phase (30 publishes @100 ms unless noted) | Transitions | 5xx |
|---|---|---|
| a) unrelated publish (identical content) | none | 0 |
| b) change an unrelated cluster | none | 0 |
| c) change only route metadata of the health clusters' routes | none | 0 |
| d) gen-encoded route IDs (all routes replaced) | none | 0 |
| e) change the health clusters themselves (LB policy, metadata) | none | 0 |
| f) change `c-health`'s active health config (interval) | none | 0 |
| g) change `c-health-bad`'s **address**, same ID | none (state kept) | 0 |
| h) remove `c-health-bad` for one generation, then re-add | **instance replaced: Unhealthy → missing → Unknown → Unhealthy** | **2,734** in the committed run. Earlier runs gave 4,872 and 5,395; the count depends on when the next probe lands. |
| i) point `c-passive-dead` (same ID) at a **working** address | none: **stays Passive=Unhealthy** for the full reactivation period | 0 |

**Health state is keyed purely by ClusterId + DestinationId.** It survives every reload that keeps the destination, including changes to the cluster's own policies and health config. Healthy destinations never flapped. Two pitfalls follow:

1. **Remove + re-add, or renamed IDs, resets health to Unknown, which YARP treats as routable.** Traffic reaches the bad destination until the next probe, or under passive-only health until it fails enough again.
2. **An address change under the same ID keeps the old verdict**, in both directions. A fixed address can stay out of rotation for the whole `ReactivationPeriod`. A healthy verdict can also carry over to a new, unverified address.

The gate design (Q6) does not change any of this: health, LB and affinity behave the same with gate-selected endpoints.

## Q5. Change-token mechanics

- **One `CancellationTokenSource` / `CancellationChangeToken` per snapshot is enough.** Publish swaps first and cancels the old token second. YARP calls `GetConfig()` and subscribes to the returned config's token.
- **Completion is not synchronous by contract.** YARP's change-token callback starts `ReloadConfigAsync` fire-and-forget. In this spike the reload *happened* to finish before `Cancel()` returned: `ConfigurationApplied` fired inline in 70 of 71 publishes, and the remaining one was the startup apply. That only holds because every awaited step completes synchronously: the default destination resolver, no async `IProxyConfigFilter`, and the validators' policy lookups. An async resolver or filter, now or after a YARP upgrade, makes the reload finish later on another thread. **Never infer "applied" from `Publish()` returning.**
- **Use `IConfigChangeListener` to learn the outcome.** Register a listener and wait for `ConfigurationApplied` or `ConfigurationApplyingFailed` for the config whose `RevisionId` matches the publish.
  - YARP itself never reads `RevisionId`. It just hands the `IProxyConfig` objects to listeners, so `RevisionId` is a provider-owned correlation key and carries no ordering semantics.
  - The spike's `ApplyListener` does exactly this: a `TaskCompletionSource` per RevisionId, completed by `ConfigurationApplied` or `ConfigurationApplyingFailed`.
  - At `ConfigurationApplied`, YARP's `EndpointDataSource` already exposed the new endpoints in 140 of 140 checks, comparing every route of the applied config by value.
  - A single writer must wait for each outcome before the next publish. If YARP coalesces two configs, the skipped one never gets a callback.
- **Concurrent publishes.** The first version of this test built and published outside the lock, so its ordering assertion only passed by luck. The fixed test builds and publishes under one lock: 100 publishes produced 100 `GetConfig()` calls, and the final endpoints were exactly the last generation. Ordering is the provider's job, through a single writer.
- **YARP swallows invalid configs.** A route with an empty `Match` made `Publish()` return normally. YARP logged `Failed to apply the new config`, kept serving the old config, and stayed subscribed, so the next valid publish applied. With a listener, that surfaces as `ConfigurationApplyingFailed`. Still run **YARP's `IConfigValidator`** (`ValidateRouteAsync` / `ValidateClusterAsync`) while compiling, **before the DB commit**, so a bad revision is never committed.

## Q6. Follow-up probe: gate-selected YARP endpoints

**The proposal:**
- The gate resolves host **and** route from its snapshot `S`.
- `S` carries a slot → YARP `Endpoint` table, captured from YARP's `EndpointDataSource` after `ConfigurationApplied`. Only then is `S` swapped into the store.
- The gate calls `HttpContext.SetEndpoint(ep)` **before** `UseRouting`.
- YARP routes are path-only (`/__r/{slot}/{**catch-all}`), so Host matching disappears from the matcher.

Results, from `results/gate.txt`. Every probe request was `GET /x`, which matches **no** YARP route.

| # | Question | Result |
|---|---|---|
| (i) | Does routing skip matching, and does the right route/cluster get proxied? | **Yes.** 20/20 hosts returned 200 with the upstream seeing the right destination path. If `EndpointRoutingMiddleware` had matched, `/x` would have produced no endpoint and a 404. An unknown host is answered by the gate (404) and never reaches routing. |
| (ii) | Does an old endpoint object from a previous snapshot still proxy after YARP removed or replaced it? | **Yes.** The publish removed `gone.test` (route + cluster), removed `routegone.test` (route only, cluster kept) and replaced the `moved.test` route plus its destination. None of the three old endpoint objects remained in the data source. Requests on a pinned old snapshot still returned 200, both right away and 3 s later. **Caveat:** an old endpoint shares the live `ClusterState`, so it uses the *current* destinations (`moved` went to `/moved2/`), and a removed cluster is still proxied through its last state. That's fine for in-flight requests: snapshots are held per request only, and destinations aren't policies. |
| (iii) | What are the steady-state allocations? | **Gate lookup + `SetEndpoint`: 0 B/request.** Kestrel's feature collection already implements `IEndpointFeature`. From gate entry to the first proxy-pipeline middleware, including `UseRouting` and YARP's pipeline initialiser: **152 B/request**, against **256 B/request** with Host-matched routing. Matching the catch-all allocates route values. |
| (iv) | Do health, affinity and LB still work? | **Yes.** RoundRobin gave 20/20. Cookie affinity sent 30 of 30 follow-ups to the same destination. Active health: the bad destination got 0 of 100 requests, with 0 5xx. Passive: after 5 × 502 the dead destination was marked unhealthy, then 0 5xx in 100 requests. |
| (v) | How does the gate get YARP's `Endpoint` per route ID? | After `ConfigurationApplied`, iterate `EndpointDataSource.Endpoints` and key by `GetMetadata<RouteModel>().Config.RouteId`. The endpoints were always already live at the callback (140/140). Building the table took 0.05 ms at 500 hosts and 0.28 ms at 2000. |

**Publish cost, gate design** (idle, 1 route changed, median (p90) until `S` is swapped in):

| Hosts | Gate design | Host-matched layout |
|---|---|---|
| 500 | **1.9 ms (1.9)**, max 9 ms | 15.4 ms |
| 2000 | **11.2 ms (16.0)** | 64 ms |

**Under 5 % route + 5 % cluster churn every 100 ms (5 s each):**
- **500 hosts:** publish-to-swap 4.6 ms median (p90 16, max 35). 230 k rps, 0 errors.
- **2000 hosts:** 24.8 ms median (p90 40, max 68). 169 k rps, 0 errors. The publisher kept up with 50 of 50 ticks, compared with only 13 for the Host-matched layout.

**Minimal fallback evaluated: `RoutingFirst`.** `UseRouting` with Host matching runs first, the gate then captures `S`, `S` is swapped after `ConfigurationApplied`, and the route version is checked:
- **Checking only {current, previous} is wrong.** It produced **503s: 625 at 500 hosts and 446 at 2000** in 5 s. The swap happens after an async hop following `ConfigurationApplied`, so YARP's endpoints run *ahead* of the store, and a "previous" match never occurred.
- **Checking {current, previous, *pending*}** (pending = the snapshot being published) gave 0 503s; 546 and 398 requests matched pending. So the fallback works, but it keeps the Host-matched matcher cost: publishes took 427–438 ms under load at 2000 hosts.
- It also still lets a request mix the snapshot used by pre-routing gate work with the route's snapshot.

**Rejected alternatives:**
- **Direct `IHttpForwarder`:** it bypasses YARP's routes and clusters, so you lose LB, health checks and session affinity.
- **`ReassignProxyRequest`:** it needs a matched YARP endpoint to start from, and it allocates per request.
- **Gen-encoded route IDs:** every publish pays the full rebuild, and they don't fix the window (Q1c).

---

## Recommendation for Paduraksa

### Snapshot ↔ YARP config ↔ endpoint (gate-selected endpoints)

1. **YARP IDs are stable typed-ID strings; never put the generation in them.**
   - YARP route ID = `RouteId`. Each Paduraksa route becomes one YARP route.
   - YARP cluster ID = `UpstreamId`.
   - Destination key: see item 7.
   - Drop `{generation}:{hostIndex}:{routeIndex}` and the route-ID parsing fallback from §6.2.
2. **YARP routes carry no Host match.** Use path-only, never-requested internal paths, e.g. `/.paduraksa/r/{routeSlot}/{**rest}`, or one catch-all route per cluster. They exist only so YARP builds endpoints and pipeline state, and the gate never lets a request reach the matcher. Path-based routing inside a host, such as `/api` going to upstream A and `/` to upstream B, moves into the gate: `CompiledHost` holds an ordered route list matched in the gate from precompiled prefixes.
3. **`RuntimeSnapshot` gets `Endpoint[] YarpEndpointBySlot`.** Policies (the flattened global → profile → host → route chain) and the endpoint come from the **same** snapshot that the gate read once. This is exact by construction, with no version checks or snapshot ring. It holds even when the policy chain runs long (forward-auth I/O), because nothing is looked up again after the gate.
4. **Gate:** host lookup, then route match, then `ctx.SetEndpoint(S.YarpEndpointBySlot[slot])`. All gate decisions happen before `UseRouting`, and an unknown host is answered in the gate. Endpoint metadata isn't needed for policy resolution. Optionally attach a stable `RouteSlot` object for diagnostics; never attach per-generation objects.
5. **Publish (single writer):**
   1. compile and run YARP's `IConfigValidator` on the candidate;
   2. commit;
   3. provider swap + `Cancel()`;
   4. **await `ConfigurationApplied` or `ConfigurationApplyingFailed` for the candidate's `RevisionId`** through an `IConfigChangeListener`, with a timeout that triggers the §6.1 alert;
   5. build the slot → `Endpoint` table from `EndpointDataSource` by `RouteModel.Config.RouteId`;
   6. `SnapshotStore` swap.

   In-flight requests keep using the old snapshot's old endpoint objects, which still proxy correctly (Q6 ii). Coalescing: if edits arrive faster than publishes complete, the latest wins.
6. **Keep YARP config a pure function of its YARP-relevant inputs.** Then policy-only edits leave YARP untouched.
   - No generation, revision or timestamp in route or cluster `Metadata`, transforms and so on.
   - A non-destination cluster change rebuilds every route that uses the cluster.
   - **If Paduraksa adds a custom `IForwarderHttpClientFactory`**, it must return the existing client when the cluster's HttpClient config is unchanged. A factory that hands out a new `HttpMessageInvoker` makes YARP treat the cluster as changed, and rebuilds its routes on every publish.
7. **Health continuity:**
   - Stable typed IDs keep health across every reload that keeps the destination (Q4).
   - **Rollback and YAML import must preserve `DestinationId`s.** A remove-and-re-add resets the destination to Unknown, which is routable.
   - **Optionally make the YARP destination key `{DestinationId}~{hash8(Address, Host, Health address)}`**, hashing over `DestinationConfig.Address`, `.Host` and `.Health`. A changed target then starts fresh health state. Be clear about what this does and doesn't do:
     - The fresh state is **Unknown, which is routable**, so this only fixes the *stuck passive-unhealthy* case. It does not protect against an unverified new address getting traffic before the first probe.
     - It also **resets session affinity** for that destination, because affinity cookies name the destination key.
     - Metrics stay keyed by `DestinationId` through `MetricSlot`, so they are unaffected.
8. **Custom pipeline:** `MapReverseProxy(pipeline => …)` **replaces** YARP's default pipeline. Add `UseSessionAffinity()`, `UseLoadBalancing()` and `UsePassiveHealthChecks()` explicitly, otherwise passive health silently does nothing.

### Publish cost budget (gate design)
- **Policy-only edit (no YARP change):** YARP diff only, ≤ 5 ms at 500 hosts and about 7–14 ms at 2000.
- **Edit that changes a route or a cluster's non-destination config:** about 2 ms median and ≤ 10 ms at 500 hosts; about 11 ms median and ≤ 20 ms at 2000, idle.
- **Under heavy churn load:** 5 ms / 25 ms median, with maxima of 35 / 68 ms.
- **Allocations:** roughly 2 MB per publish at 500 hosts and 7 MB at 2000 (path-only figures from Q2).

## Surprises
- `ConfigureEndpoints` is effectively "on endpoint (re)build", not "on reload".
- The cost is in ASP.NET routing, not in YARP, and Host matching is most of it. Letting the gate pick the endpoint removes the cost and the consistency problem in one move.
- Old YARP `Endpoint` objects keep working after YARP drops them, but they follow the *live* `ClusterState` (current destinations).
- YARP accepts an invalid config without telling the publisher; only `IConfigChangeListener` reports it.
- Health state keys on the ID only, and survives a destination **address** change.
