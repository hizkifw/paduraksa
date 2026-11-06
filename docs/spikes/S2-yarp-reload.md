# Spike S2: YARP config churn + endpoint metadata binding

**Status:** done. **YARP:** 2.3.0. **Runtime:** .NET 10 (SDK 10.0.113), Release, Server GC.
**Box:** 20 cores / 62 GB, Linux. Client, proxy and upstream all ran in one process, and other agents' builds were running on the same machine at the same time. Treat the numbers as indicative, not as benchmarks. Latency and cost numbers are more reliable than the throughput deltas.

Code: `spikes/S2-yarp-reload/` (standalone, not in `Paduraksa.slnx`). Raw output from the run quoted here is in `spikes/S2-yarp-reload/results/`.

```
spikes/S2-yarp-reload/run.sh [metadata|reload|load|health|all]
# or individually:
dotnet run -c Release --project spikes/S2-yarp-reload -- metadata
dotnet run -c Release --project spikes/S2-yarp-reload -- reload 500 one-route   # sizes: 100,500,2000; modes below
dotnet run -c Release --project spikes/S2-yarp-reload -- load 500 64 10          # hosts, concurrency, seconds/phase
dotnet run -c Release --project spikes/S2-yarp-reload -- health
```

Setup used in every scenario:
- A custom `IProxyConfigProvider`. Each snapshot (an `IProxyConfig`) owns one `CancellationTokenSource` and exposes a `CancellationChangeToken`. `Publish` does `Interlocked.Exchange` and then cancels the *old* snapshot's token.
- `app.MapReverseProxy(pipeline => …)` (the custom-pipeline overload), followed by `.ConfigureEndpoints((IEndpointConventionBuilder ep, RouteConfig route) => ep.WithMetadata(compiledHostRef, hostSlot))`.
- The compiler records which metadata object belongs to which `RouteConfig` **instance** in a `ConditionalWeakTable`. That lets the callback, and the spike, prove which generation's `RouteConfig` was passed in.
- A "gate" middleware placed before `UseRouting()` captures the current snapshot once, as in PLAN §7.2 C. A proxy-pipeline middleware then compares the endpoint metadata against that snapshot.
- Fleet: N hosts. Each host has 1 route (`Hosts=[hN.test]`, `Path=/{**catch-all}`) pointing at its own cluster, which has 1 or 2 destinations. The upstream is a tiny Kestrel app on another port in the same process.

## Exit criteria (PLAN §13, S2)

| Criterion | Result |
|---|---|
| Publish < 50 ms at 500 hosts | **Met.** A route-changing publish takes 15–23 ms median and ≤ 27 ms max. A policy-only publish (no YARP change) takes 3 ms median. At 2000 hosts a route-changing publish takes 64–68 ms median idle, and 100–250 ms under heavy load. |
| No request errors during swaps under load | **Met.** 0 connection errors and 0 non-2xx across ~42 M requests, with publishes every 100 ms, at 500 and 2000 hosts, including full endpoint rebuilds and destination add/remove churn. |
| Typed metadata on every endpoint matches its generation (or the fallback is in place) | **Not as the plan assumes.** Unchanged routes keep their old endpoint and old metadata. The fix is a design change (below): stable metadata plus a version check. Generation-encoded route IDs don't solve it either. |

---

## Q1. `ConfigureEndpoints` across reloads

### 1a. Does the callback re-run, and with which `RouteConfig`?

It runs **only for routes YARP considers new or changed**, and in that case it receives the **new** `RouteConfig` instance. **Unchanged routes keep their existing endpoint object**, so the callback does not run for them and their metadata stays at whatever generation last rebuilt them. Excerpt from `results/metadata.txt`:

```
--- gen 2: identical content, NEW RouteConfig/ClusterConfig instances, stable route IDs
    ConfigureEndpoints callbacks this publish: 0 -> []
    h0  meta.gen 1   RouteModel.Config is current instance? False   echo (meta-gen/snap-gen) 1/2
    ...
--- gen 3: h0 path changed, h1 metadata changed, c2 +destination, c3 LB policy changed, h5 removed, h6 added
    ConfigureEndpoints callbacks this publish: 4 -> [h6@g3, h3@g3, h1@g3, h0@g3]
    h0 3 True 3/3 | h1 3 True 3/3 | h2 1 False 1/3 | h3 3 True 3/3 | h4 1 False 1/3 | h6 3 True 3/3
--- gen 4: same content, route IDs encode generation
    ConfigureEndpoints callbacks this publish: 6 -> [4:3:0@g4, ...]       (all rebuilt)
```

YARP rebuilds a route's endpoint, and so re-runs the callback, when:
- the `RouteConfig` is not value-equal to the previous one. That covers a match change and a change to the string `Metadata` dictionary, and it also covers a new route ID.
- the route's **cluster** changed in anything **other than its destinations**. Examples are LB policy, cluster `Metadata`, health-check config and HttpClient config: `c3 LB policy` rebuilt `h3`, and the `10pct-clusters` mode below rebuilt about 49 routes per publish.

YARP does **not** rebuild the endpoint for:
- a destination-only change (`c2 +destination` did not rebuild `h2`).
- an identical config delivered as new object instances.

`RouteModel.Config` on a kept endpoint still points at the *old* `RouteConfig` instance.

### 1b. Typed metadata in the pipeline, and allocations

`context.GetEndpoint().Metadata.GetMetadata<CompiledHostRef>()` returns the attached object inside the custom proxy pipeline. Over 5000 warm requests, `GetMetadata<CompiledHostRef>()` plus `GetMetadata<HostSlot>()` allocated **0 bytes**, measured with `GC.GetAllocatedBytesForCurrentThread()` around the two calls. No metadata was missing in any of ~42 M load-test requests.

### 1c. Does metadata guarantee "policy belongs to the same generation as the route"?

**No, for two separate reasons:**

1. **Stale metadata on unchanged routes.** This follows from 1a. With stable route IDs, `meta.Generation != snapshot.Generation` was true for practically every request once any publish had happened (e.g. 2,659,463 of 2,684,189 requests in the identical-content phase). A policy-only edit, such as adding an IP allowlist, leaves the YARP route unchanged. A per-generation `CompiledHost` attached as metadata would therefore keep serving the **old policies indefinitely**, and it would also pin old snapshots in memory.
2. **Gate snapshot vs routed endpoint race.** The gate reads the snapshot before routing. YARP swaps endpoints only after its reload, which runs inline in `Cancel()` and takes about 2–65 ms idle and up to about 250 ms under load at 2000 hosts. During that window, requests for *changed* routes see the new snapshot but the old endpoint. The spike counted requests whose route version (bumped by the compiler when the YARP-relevant route content changes) differed between the endpoint and the gate snapshot:

   | Load phase (publishes every 100 ms) | 500 hosts | 2000 hosts |
   |---|---|---|
   | identical content / 1 route changed | 0 | 0 |
   | churn: 5 % of routes + 5 % of clusters change per publish | 11,148 / 2.5 M (0.44 %) | 117,811 / 2.46 M (4.8 %) |
   | gen-encoded route IDs + same churn | 11,566 / 2.56 M | 112,434 / 2.36 M |

   **Generation-encoded route IDs do not remove the race.** They make each endpoint internally consistent, because every endpoint carries its own generation's objects. But the gate's snapshot still disagrees with the endpoint for every request in the reload window (258,773 / 2.56 M requests at 500 hosts). They also cost more (Q2).

**Options compared:**

| Design | Consistent? | Publish cost | Notes |
|---|---|---|---|
| Per-generation object as metadata, stable IDs (current PLAN text) | **No.** Stale forever on unchanged routes. | cheap | Rejected. |
| Gen-encoded route IDs `{gen}:{host}:{route}` + per-generation metadata | Endpoint is self-consistent, but it is *not* consistent with the gate snapshot during the reload window. | Every publish rebuilds every endpoint **and** the matcher, even for policy-only edits (500 hosts: 15 ms vs 3 ms; 2000 hosts: 60 ms vs 7 ms). Allocations are about 2× (4.2 MB vs 2.3 MB at 500 hosts; 21 MB vs 9 MB at 2000). | Throws away YARP's cheap no-op path. |
| **Stable IDs + stable metadata (`RouteBinding{Slot, YarpVersion}`) resolved against the request's snapshot, with a version check and a recent-snapshot ring fallback** | **Yes.** | Policy-only edits don't touch YARP. Only real route or cluster edits pay the matcher rebuild. | **Recommended.** |

---

## Q2. Reload cost

Each (size, mode) pair ran in its own process with 60 warm-up publishes followed by 40 measured publishes, on an idle proxy. `Publish()` is synchronous: YARP's reload *and* ASP.NET's matcher rebuild both run inline inside `CancellationTokenSource.Cancel()` on the publishing thread. "publish→live" is the time from the start of `Publish()` until a request to the newly added host returns 200. There were zero 404 misses in every case, so the change is live when `Publish()` returns.

Medians (p90) in ms. Allocation is the median MB per publish (`GC.GetTotalAllocatedBytes(true)` delta). Callbacks = `ConfigureEndpoints` invocations per publish.

| Hosts | Mode | Publish ms | YARP diff+endpoints | Matcher rebuild | Alloc MB | Callbacks |
|---|---|---|---|---|---|---|
| 100 | noop (identical content) | 0.37 (0.52) | – | – (no endpoint change) | 0.17 | 0 |
| 100 | one-route | 1.56 (2.52) | 0.37 | 1.19 | 0.49 | 1 |
| 100 | 10pct-routes | 1.54 (2.21) | 0.42 | 1.12 | 0.53 | 11 |
| 100 | gen-ids | 2.95 (6.51) | 0.96 | 1.92 | 0.87 | 101 |
| 500 | noop | **2.87 (7.46)** | – | – | 0.85 | 0 |
| 500 | one-route | **15.4 (15.9)** | 2.20 | 13.3 | 2.29 | 1 |
| 500 | 10pct-routes | 18.2 (21.7) | 1.13 | 17.1 | 2.51 | 49 |
| 500 | 10pct-clusters (cluster metadata) | 23.1 (26.3) | 2.15 | 20.6 | 2.56 | 49 |
| 500 | gen-ids | 15.0 (17.9) | 3.57 | 11.4 | 4.20 | 501 |
| 500 | path-only routes (experiment) | 5.3 (7.8) | 2.35 | 2.85 | 1.80 | 1 |
| 2000 | noop | **6.8 (13.4)** | – | – | 3.30 | 0 |
| 2000 | one-route | **64.0 (84.8)** | 2.11 | 61.4 | 9.26 | 1 |
| 2000 | 10pct-routes | 66.8 (83.7) | 2.76 | 63.2 | 10.1 | 192 |
| 2000 | 10pct-clusters | 67.6 (85.3) | 3.14 | 64.0 | 10.1 | 192 |
| 2000 | gen-ids | 59.6 (73.0) | 4.76 | 54.1 | 21.2 | 2001 |
| 2000 | path-only routes (experiment) | 13.3 (19.6) | 10.1 | 3.1 | 7.19 | 1 |

"Matcher rebuild" is the time from YARP signalling its endpoint change token to `Cancel()` returning. The probe registers last, and `CancellationToken` callbacks run LIFO, so it fires first. Snapshot compile (building the `RouteConfig`/`ClusterConfig` objects) is ≤ 0.6 ms even at 2000 hosts. Process CPU per publish roughly equals wall time, with p90 up to about 2× because of server-GC threads.

**What drives the cost:**
- **About 90 % is ASP.NET Core's DFA matcher rebuild.** It happens on *any* endpoint change and costs the same whether 1 or 2000 endpoints changed. It grows super-linearly with the number of routes: about 1.2 ms at 100, 13 ms at 500 and 61 ms at 2000.
- **Host matching is the expensive part.** With the same number of routes but path-only matching, the matcher rebuild drops from 61 ms to 3 ms at 2000 hosts.
- YARP's own diff plus the creation of changed endpoints is small, about 2–5 ms.
- When nothing YARP-relevant changes, YARP doesn't signal the endpoint change and the matcher isn't rebuilt. The publish is then only YARP's diff: 0.4 ms at 100 hosts, 3 ms at 500, 7 ms at 2000.

**Under load** (the `load` scenario, 64 concurrent clients running on the same 20 cores), publish times stretch:
- At 500 hosts: 9–11 ms median, p90 14–28 ms.
- At 2000 hosts: 100–250 ms median. At 100 ms intervals the publisher could not keep up (53–101 publishes in 10 s instead of 100), which is acceptable because the reload simply takes the latest config.

**Request impact while publishing every 100 ms.** At 2000 hosts, where the baselines were stable at 250–267 k rps:

| Phase | Throughput | p99 | p99.9 |
|---|---|---|---|
| baselines | 250–267 k rps | 1.0–2.2 ms | 3.6–6.2 ms |
| identical-content publishes | 212 k | 1.7 ms | 9.7 ms |
| 1-route publishes | 243 k | 1.5 ms | 4.5 ms |
| churn | 246 k | 0.9 ms | 5.8 ms |
| gen-ids + churn | 236 k | 1.0 ms | 12.4 ms |

So the impact is about 5–10 % throughput, roughly unchanged p99, and a somewhat higher p99.9. The 500-host run was too noisy to quantify, because other processes were running on the box and baselines ranged from 119 k to 298 k rps. It showed the same pattern and no errors.

---

## Q3. Errors during swaps

**Zero errors.** In every phase at 500 and 2000 hosts (≈ 42 M requests in total), there were 0 exceptions (connection reset or refused, timeouts) and 0 non-2xx responses (`results/load.txt`). The phases covered:
- identical-content publishes;
- a single route changing;
- 5 % of routes plus 5 % of clusters changing per publish, with destinations added and removed while requests were in flight;
- generation-encoded IDs rebuilding all 500 or 2000 endpoints every 100 ms.

The health scenario also saw 0 5xx through 30 publishes per phase on the health-checked clusters. The one exception was the destination-removal phase described in Q4.

---

## Q4. YARP health continuity

Setup:
- `c-health` has active checks every 500 ms using `ConsecutiveFailures` and `/health`. `c-health-bad` points at `/bad/`, which returns 500, so it became Active=Unhealthy after about 560 ms.
- `c-passive` uses `TransportFailureRate` with `ReactivationPeriod` set to 10 min. `c-passive-dead` points at a closed port and became Passive=Unhealthy after 5 × 502.
- A monitor sampled all four destinations about every 1 ms, recording both health values and `DestinationState` object identity.
- Traffic ran continuously to `health.test`, `passive.test` and `other.test`. A flap to Unknown would send traffic to `/bad` (500) or the dead port (502), so it would show up as 5xx.

| Phase (30 publishes @100 ms unless noted) | Transitions | 5xx |
|---|---|---|
| a) unrelated publish (identical content) | none | 0 |
| b) change an unrelated cluster | none | 0 |
| c) change only route metadata of the health clusters' routes | none | 0 |
| d) gen-encoded route IDs (all routes replaced) | none | 0 |
| e) change the health clusters themselves (LB policy, metadata) | none | 0 |
| f) change `c-health`'s active health config (interval) | none | 0 |
| g) change `c-health-bad`'s **address**, same ID | none (state kept) | 0 |
| h) remove `c-health-bad` for one generation, then re-add | **instance replaced: Unhealthy → missing → Unknown → Unhealthy** | **5,395** on health.test |
| i) point `c-passive-dead` (same ID) at a **working** address | none: **stays Passive=Unhealthy** (out of rotation) for the full reactivation period | 0 |

**Health state is keyed purely by ClusterId + DestinationId** and lives in a `DestinationState` that YARP keeps across reloads, *including* changes to the cluster's own policies and health config. It survives everything except removal of the destination. Healthy destinations never flapped. Two pitfalls follow:

1. **Remove + re-add, or renamed IDs, resets health to Unknown, which YARP treats as routable.** Traffic went to the known-bad destination until the next active probe (about one interval). For passive-only health it would keep doing so until it fails enough again.
2. **An address change under the same ID keeps the old verdict.** A destination that was marked passive-unhealthy and is then repointed to a working address stays out of rotation for up to `ReactivationPeriod`. The reverse is also possible: a known-healthy verdict carries over to a new, unverified address.

---

## Q5. Change-token mechanics

- **One `CancellationTokenSource` / `CancellationChangeToken` per snapshot is enough.** Publish swaps first and cancels the old token second. YARP then calls `GetConfig()` and subscribes to the *returned* config's token. Nothing else is needed.
- **The reload is synchronous.** Endpoints and matcher are rebuilt before `Cancel()` returns: "newest meta gen visible immediately after return" matched the published generation every time. This holds because there is no async `IDestinationResolver`. It means the publishing thread pays the full cost, so never publish from a request thread or while holding a lock that the hot path needs.
- **Concurrent publishes coalesce safely.** In the test, 100 publishes from 4 threads with no serialisation produced only 27 `GetConfig()` calls, and the final endpoints were exactly the last generation. YARP just takes whatever `GetConfig()` returns when it reloads.
- **`IProxyConfig.RevisionId`** exists in 2.3.0 as a default interface member, and the spike set it to the generation. YARP uses it only for diagnostics. It has **no ordering semantics**, and YARP never rejects an "older" revision. Ordering is entirely the provider's responsibility (`Interlocked.Exchange` of the latest snapshot).
- **YARP swallows invalid configs.** A route with an empty `Match` made `Publish()` return normally. YARP logged `Failed to apply the new config` and kept serving the old config. It *did* stay subscribed to the bad config's token, so the next valid publish applied normally. Without extra checks, the DB and admin UI would say revision N is live while YARP serves N-1.

---

## Recommendation for Paduraksa

### Snapshot ↔ YARP config ↔ endpoint metadata

1. **Use stable YARP IDs taken from typed IDs, and never put the generation in them.**
   - YARP route ID = `RouteId` typed string. Each Paduraksa route becomes one YARP route; a host with N routes produces N YARP routes.
   - YARP cluster ID = `UpstreamId` string.
   - Destination key: see item 6.
   - Drop the `{generation}:{hostIndex}:{routeIndex}` scheme from §6.2.
2. **Make the YARP config a pure function of the YARP-relevant config.** Never put the generation, revision, timestamps or other per-publish values into `RouteConfig.Metadata`, `ClusterConfig.Metadata`, transforms and so on. Value-equal configs are then no-ops for YARP, and a policy-only edit costs about 3 ms at 500 hosts instead of about 15 ms. Keep in mind that a non-destination cluster change rebuilds every route that uses that cluster.
3. **Endpoint metadata is a stable binding, never a per-generation object:**
   ```csharp
   public sealed class RouteBinding            // attached via ConfigureEndpoints((ep, route) => ep.WithMetadata(binding))
   {
       public required int RouteSlot { get; init; }     // stable, from an append-only RouteId → slot registry (like MetricSlot)
       public required long YarpVersion { get; init; }  // hash of the YARP-relevant RouteConfig (+ its cluster's non-destination config)
   }
   ```
   The compiler creates a `RouteBinding` per `RouteConfig` and hands it to the callback through a `ConditionalWeakTable<RouteConfig, RouteBinding>`, or by looking up `route.RouteId` in the candidate snapshot. The spike used the weak-table approach. When YARP keeps an endpoint, the old binding is still correct, because both fields are stable for unchanged content. It references no snapshot, so it pins nothing.
4. **Resolve policies from the request's snapshot, and guard them with the version.**
   - The gate captures the snapshot `S` once, as in §7.2.
   - In the proxy pipeline, read `b = endpoint.Metadata.GetMetadata<RouteBinding>()`, which allocates 0 B.
   - Look up `r = S.RoutesBySlot[b.RouteSlot]`.
   - **Fast path:** if `r?.YarpVersion == b.YarpVersion`, use `r`'s policies. In the spike this held for every request whenever no route changed, and for 95–99.6 % of requests under extreme churn.
   - **Slow path (publish race):** scan a tiny immutable ring of the last 4–8 published snapshots, newest first, for the one whose `RoutesBySlot[slot].YarpVersion == b.YarpVersion`, and use that snapshot's route and policies. If none matches, which should never happen, return 503 with `Retry-After: 1` and increment a counter.

   This is the §6.2 fallback, keyed by slot and version instead of by parsing route IDs. It guarantees that a route's policies come from a snapshot in which that exact route content exists. Host-level gate decisions keep using `S`.
5. **Publish sequence.** Use a single writer:
   1. compile;
   2. run **YARP's `IConfigValidator`** (`ValidateRouteAsync` / `ValidateClusterAsync`) on the candidate during step 4 of §6.1, because YARP itself fails silently;
   3. commit;
   4. `SnapshotStore` swap, appending to the recent ring;
   5. provider swap and `Cancel()`, which applies inline;
   6. as a cheap post-check, confirm through `IProxyStateLookup.TryGetRoute` that one changed route's `Config` is the new instance. If it isn't, raise the §6.1 "failed publish after commit" alert.

   If edits arrive faster than reloads finish, coalesce them so the latest wins.
6. **Health continuity:** use stable typed IDs for clusters and destinations, which gives continuity across all reloads, as measured. Two rules on top of that:
   - **Rollback and YAML import must preserve `DestinationId`s.** A remove-and-re-add resets the destination to Unknown, which is routable.
   - **Make the YARP destination key `{DestinationId}~{hash8(address)}`** so that editing a destination's address deliberately starts a fresh health state, and every other edit keeps it. Metrics stay keyed by `DestinationId` through `MetricSlot`, so they are unaffected.

   With this, §6.3's "YARP health" row holds, and S6 can assert it with the same monitor approach.
7. **Custom pipeline:** `MapReverseProxy(pipeline => …)` **replaces** YARP's default pipeline. Add `UseSessionAffinity()`, `UseLoadBalancing()` and `UsePassiveHealthChecks()` explicitly, otherwise passive health silently does nothing.

### Publish cost budget
- **Policy-only edit (no YARP change):** ≤ 5 ms at 500 hosts and ≤ 15 ms at 2000, mostly YARP's diff.
- **Edit that changes any route or a cluster's non-destination config:** about 15–25 ms at 500 hosts, which meets the < 50 ms criterion, and about 65–85 ms at 2000 hosts idle. Allocations are about 2.3 MB per publish at 500 hosts and about 9–10 MB at 2000. Cost is dominated by the matcher rebuild with Host matching, regardless of how many routes changed.
- **Under heavy concurrent load**, budget 2–4× those numbers.
- **If 2000+ hosts become a target:** the experiment shows that removing Host matching from YARP routes cuts the 2000-host rebuild from 61 ms to 3 ms. One option is to give YARP path-only routes and let the gate, which already resolves the host from the snapshot, select the endpoint itself, for example with `HttpContext.SetEndpoint` before `UseRouting`. This spike did **not** verify that approach end to end. Treat it as a later optimisation, not M1 scope.

## Surprises
- `ConfigureEndpoints` is effectively "on endpoint (re)build", not "on reload". The PLAN's assumption that metadata always matches the current generation is false for unchanged routes.
- The cost is in ASP.NET routing, not in YARP. The DFA matcher rebuild is O(all routes) with a super-linear Host-matching component, and it runs synchronously inside `Cancel()`.
- Gen-encoded route IDs are *not* much slower per publish, because the matcher rebuild dominates either way. Their real cost is that they make every publish pay the rebuild, and they still don't close the gate-vs-endpoint window.
- YARP accepts an invalid config without telling the publisher, and keeps serving the old one.
- Health state keys on the ID only, and survives a destination **address** change.
