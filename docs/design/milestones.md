# Risks, spikes and milestones

For contributors: what each spike proved, and what each milestone built. The overview of status is in [the plan](../PLAN.md).

## In short

- Seven spikes de-risked the design before the features that needed them. All are done. Two changed the design: S2 (the gate selects the YARP endpoint) and S4 (EF discovery is replaced, JSON stores strings).
- Two spikes are partial: S1 (a real browser upgrading to H3 is untested) and S6 (the metric-slot test came with M9).
- Milestones M0 to M11 are done. The NPM importer (M10) is not built, and v0.1.0 (M11) was released on 2026-11-15.
- This page is the history. The current state of each area is on its own page, linked from every entry.
- Items still open inside done milestones are listed at the end of each milestone, and the roadmap is under [Later](#later).

## Contents

- [Risks and spikes](#risks-and-spikes)
- [Milestones](#milestones)
- [Later](#later)

## Risks and spikes

These were done first. Each has an exit criterion, and the result is what was recorded.

### S1: HTTP/3 and per-SNI certificate selection (partial)

- **Question:** does Kestrel's QUIC listener invoke per-SNI selection, and is libmsquic available in the container?
- **Exit criterion:** per-SNI works on QUIC with hot-swap, or the single-H3-certificate fallback ([hot-path.md](hot-path.md)) is implemented. A browser must also upgrade to H3 in the Docker deployment (`Alt-Svc` carries the public port, UDP 443 is reachable).
- **Result:** per-SNI works on QUIC. The browser upgrade is still pending. Detail: [S1](../spikes/S1-http3-sni.md).

### S2: YARP config churn and metadata binding (design changed)

- **Question:** 500 or more hosts and frequent publishes make endpoint rebuild cost matter. `ConfigureEndpoints` exists in YARP 2.3.0, but does it re-run on every dynamic reload with the new `RouteConfig`?
- **Exit criterion:** publish under 50 ms at 500 hosts, no request errors during swaps under load, and typed metadata on every endpoint matching its generation (or the route-ID fallback in place).
- **Result:** the gate picks the YARP endpoint itself, and routes carry no Host match ([config.md](config.md)). Detail: [S2](../spikes/S2-yarp-reload.md).

### S3: Zero-allocation gate on Kestrel (0 B)

- **Question:** can request-scoped state live without `Items`, with span-based host lookup and feature reuse per connection?
- **Exit criterion:** BenchmarkDotNet and an in-process test show 0 B per request from Paduraksa code.
- **Result:** met. See [hot-path.md](hot-path.md) and [S3](../spikes/S3-zero-alloc-gate.md).

### S4: Typed IDs, EF Core 10 and SQLite (partial)

- **Question:** do convention converters, big-endian BLOB GUIDs (`UuidV7Generator` plus `TryWriteBytes(span, bigEndian: true, out _)`), JSON complex types holding typed IDs and LINQ comparisons work? Does the `FrozenDictionary` `OrdinalIgnoreCase` alternate span lookup work?
- **Exit criterion:** round-trip, query and index-ordering tests pass.
- **Result:** built as real code in `Paduraksa.Data` (the generic parts live in `Paduraksa.Storage`), tested in `tests/Paduraksa.Tests.Data` on real SQLite.
  - BLOB, INTEGER and JSON-string storage all work.
  - `ORDER BY` equals `CompareTo` equals string order, including within one millisecond.
  - An Int64Ordered key is the rowid alias without `AUTOINCREMENT`.
  - The `FrozenDictionary` lookup is not covered by that test.
- **Changes:** discovery replaces EF's `PropertyDiscoveryConvention` instead of scanning ([config.md](config.md)), and JSON stores strings.

### S5: Two web apps, one process

- **Question:** shared singletons, graceful shutdown order, logging and health.
- **Exit criterion:** both apps start and stop cleanly, and integration tests can host both.
- **Result:** implemented as the real host ([hosting.md](hosting.md)).

### S6: State continuity across publishes (partial)

- **Question:** rate limits and metrics must not reset on config edits ("State that survives a publish" in [config.md](config.md)).
- **Exit criterion:** under a load test with a publish every 100 ms:
  1. 429 behaviour is continuous;
  2. counters are monotonic;
  3. YARP destination health is preserved. A destination marked unhealthy (active or passive) stays unhealthy across reloads that don't touch its cluster, and no healthy destination flaps.
- **Result:**
  - `StateContinuityTests` covers no request errors and (3) over real YARP reloads, policy-only edits and unchanged configs.
  - `RateLimitContinuityTests` covers (1): buckets carry over publishes (the 2xx count stays within burst + rate x elapsed and 429s keep coming), and a lowered burst takes effect on the next touch.
  - (2) is tested with the metric slots.
  - The soak (`bench/soak/run-soak.sh`) publishes every 100 ms for 60 s under mixed traffic. See [Soak test](testing.md#soak-test).

### S7: Baseline against nginx and Caddy (recorded)

- **Exit criterion:** `oha` or `wrk2` numbers on the same box recorded in `bench/README.md`.
- **Result** (`bench/results/2026-11-07-baseline.md`, current tables in `bench/RESULTS.md`):
  - Throughput is about 0.54 to 0.71x nginx's and 2.1 to 3.6x Caddy's.
  - At fixed rates the median latency is within 0.15 ms of nginx's, but p99 is 1.4 to 2.3x nginx's. The cause is process-wide stalls that are not GC-driven, and it is still under investigation.
  - The final bench on a quiet machine (`bench/results/2026-11-15-0406-baseline`) has Paduraksa at 0.93 to 1.01 of bare YARP.
- The harness also runs HAProxy, Traefik, Envoy and a bare Kestrel + YARP reference, has `body-64k` and `handshake` scenarios, and records history with `RECORD=1`.
- Comparison scenarios for response compression, header rules, path rewrite and a `Location` mapped back through a rewrite exist for every proxy that has an equivalent (`bench/README.md` lists the skips). Their numbers are not recorded yet.

## Milestones

Each milestone ended with a working, testable increment.

### M0: Foundations

- **Scope:** repo, CI (build, test, format, architecture tests), the `[TypedId]` generator with base62 and EF, JSON and YAML converters, Result types, the clock abstraction, spikes S1 to S5.
- **Done when:** generator tests are green and architecture rules are enforced in CI.

### M1: Proxy core

- **Scope:** snapshot model and store, gate middleware, host table, client-IP resolution (including Cloudflare `Proxy` and `Tunnel` with baked-in ranges), TLS SNI selection, the YARP provider, H1, H2 and H3, the HTTP-to-HTTPS redirect, a default host.
- **Done when:**
  - the benchmarks and the S7 baseline exist;
  - the S6 continuity test passes;
  - zero-allocation tests pass, including client-IP resolution in every trusted-proxy mode;
  - end-to-end tests through `PaduraksaHost` pass: per-SNI certificates and hot swap, redirect port, PROXY protocol, Cloudflare and generic client IP, upstream headers, `Alt-Svc`, H3 in Docker.

### M2: Config store

- **Scope:** the EF model, migrations, the Application command pipeline, validation, the compiler, revisions, diff, rollback, audit (hash chain), last-known-good.
- **Done when:** change to revision to live takes under 100 ms, and rollback works.
- **Result:** the median from change to live is 11 to 15 ms end to end. Startup recompiles, and last-known-good gives degraded starts ([config.md](config.md)).

### M3: Vault and certificates

- **Scope:** the KEK/DEK store, capabilities, upload, self-signed, the local CA, the ACME client (HTTP-01, then DNS-01 with Cloudflare and deSEC), the renewal scheduler, ARI.
- **Done when:** Pebble-based integration tests issue and renew, and the banned-API rules hold.
- **Decisions:**
  - Shared EF/SQLite infrastructure lives in `Paduraksa.Storage`.
  - The compiler tolerates a missing or unusable certificate (a warning, never a failed start). The placeholder has a stable ID and is the fallback default ([vault-and-certificates.md](vault-and-certificates.md)).
- **Closed later:** DNS-01 against Pebble end to end, `_acme-challenge` CNAME delegation, local CA name constraints and root rotation.

### M4: Admin backend

- **Scope:** the admin app, local users, cookies, TOTP, roles, API tokens, the REST API with OpenAPI, the first-run setup-token gate, and the request simulator core (the gate tracer, `SimulateAsync` over live, revision and draft, `POST /api/v1/simulate`, parity tests).
- **Done when:** an API-only end-to-end setup works (`ApiOnlySetupEndToEndTests`), and the simulator explains every gate outcome and matches the real gate in parity tests.
- **Deferred:** passkeys (dropped for now).

### M5: Dashboard and container

- **Scope:**
  - Container: `Dockerfile` (Noble, pinned libmsquic, non-root, 80, 443 tcp+udp, 9443, `/data`, `HEALTHCHECK`), `deploy/compose.yaml`, `deploy/paduraksa.service`, install docs, `doctor --liveness`.
  - Dashboard foundation: Blazor Server and MudBlazor over the Application services, sign-in with password and TOTP, session revocation reaching open circuits, the Data Protection key ring in the vault (both web apps use it), CSP.
  - Pages: hosts, upstreams, certificates with ACME progress, review-before-apply (dry-run diff, warnings, Apply, Undo), history, audit, simulator, settings.
  - A first-run wizard, later removed ([dashboard.md](dashboard.md)).
- **Done when:** a new user goes from `docker run` to a proxied host through the UI alone. The wizard's browser test and the container smoke test cover it, run separately.

### M6: HSTS and logs

- **Scope:**
  - HSTS as a host setting (`max-age`, `includeSubDomains`, `preload`). It is sent only on HTTPS responses, never for IP-literal or loopback names, and precomputed per host.
  - The access log: one plaintext line per request in a documented combined-style format, per-host on or off, in `<data dir>/logs/access.log`. The hot path only fills a fixed-size struct ring and a background writer formats it.
  - The error log: the process's own logs (Warning and above by default) in `<data dir>/logs/error.log`.
  - Rotation by size and daily, gzip in the background, retention by count, age and total size. A full ring drops and counts lines and never blocks.
  - `X-Request-Id`: honoured from trusted proxies, otherwise generated.
  - The `Server` header setting and the Logs page.
- **Done when:** the gate stays at 0 B with logging on, rotation runs under load without blocking or losing ordering, and no secret reaches a log (cookies, `Authorization` and query strings marked sensitive are redacted).

### M7: Access lists, auth and WAF

- **Scope:**
  - Named access lists with IP and country rules and basic auth, whose passwords live in the vault.
  - Forward-auth with authentik, Authelia and oauth2-proxy presets.
  - The proof-of-work gate: HMAC-signed, IP-bound, expiring tokens, difficulty per host, a JS worker solver, the vault's `IMacKey`.
  - Rate-limit policies, request limits, rule sets with presets, a per-IP connection cap, a connection deny list and slow-client limits before TLS.
  - Cloudflare edge ranges refreshed daily, `CF-IPCountry` for country rules, origin lockdown, Authenticated Origin Pulls.
  - The managed robots.txt, the built-in error pages, the security overview, and a simulator step for every new policy.
- **Done when:** policy unit tests and simulator parity tests pass, S6 rate-limit continuity holds, and a PoW challenge is solved in a real browser.
- **Open:** a local country database ([waf.md](waf.md)), bcrypt `htpasswd` import, native OIDC ([identity.md](identity.md)), a forward-auth trace that makes the real call, a warning for a rule no request can reach.
- Built after: upstream Host header modes ([config.md](config.md)), property tests and fuzzing ([testing.md](testing.md)).

### M8: Custom error pages

- **Scope:**
  - Custom pages per status at every layer: a message in the built-in layout, or the operator's own document with a style-only CSP. Placeholders are `{status}`, `{statusText}`, `{requestId}`, `{host}` and `{cfRay}`, written from byte segments at 0 B.
  - Maintenance mode per host with a status, `Retry-After`, a page and a bypass named by an access list.
  - Opt-in replacement of an upstream's error bodies at response-header time, never buffered.
  - A sandboxed preview in the dashboard that is the proxy's own rendering, and the simulator's step.
- **Done when:** every gate and upstream error answers with the configured page, and previews match what is served.
- **Open:** per-route pages, localisation, statuses beyond the ten, the security overview's maintenance column ("Error pages" in [waf.md](waf.md)).

### M9: Observability

- **Scope**, in build order ([observability.md](observability.md)):
  1. Counters and latency histograms by host, route, upstream and destination at 0 B, `System.Diagnostics.Metrics`, Prometheus `/metrics` behind `metrics:read`, the one-second sampler seeded from one-minute rollups, and the live overview.
  2. Upstream health state and notifications (ntfy, Gotify, Discord, webhook, email).
  3. The live tail: the drain fans out to a bounded in-memory hub, with a Live tail page and `GET /api/v1/logs/tail` as SSE.
  4. Traffic analytics: a third drain consumer with sketches, a user-agent classifier and rollups, `GET /api/v1/analytics` and a page.
  5. Body bytes counted as they pass, both ways, at 0 B a request.
  6. Durable notification delivery: a database outbox, per-channel workers, at-least-once with an event ID for webhooks, a history API with retry.
- **Done when:** the overview is fully live, and S6 metric continuity holds (counters monotonic across publishes).

### M10: Interop

- **Scope:** YAML import and export, the CLI's `config export` and `config import`, the MCP server (code mode), backup and restore (an Owner-only, passphrase-encrypted, online archive that restores on a fresh machine), and drafts (change sets across pages applied as one revision). The NPM importer is a nice-to-have and is not built.
- **Done when:** a round-trip export then import yields an identical revision hash.
- Detail: [admin-surfaces.md](admin-surfaces.md), [vault-and-certificates.md](vault-and-certificates.md), [dashboard.md](dashboard.md).

### M11: Release v0.1

Everything below is done. v0.1.0 was tagged on 2026-11-15.

- **Images and docs:**
  - Multi-arch image publishing.
  - The docs site: MkDocs Material over `docs/`, the API reference rendered from the shipped OpenAPI document, built `--strict` on every push, published at https://www.paduraksa.org/ ([deployment.md](deployment.md#the-documentation-site)).
  - The threat model: the operator page [threat-model.md](../threat-model.md) and the full analysis [threat-model.md](threat-model.md). Gaps G2, G3, G4, G5, G7, G9 and G13 are closed, and G12 waits for the fuzz run.
- **Packages:** the PROXY protocol parser as two NuGet packages, pushed to nuget.org by a `proxyprotocol-v*` tag through trusted publishing once a `NUGET_USER` variable exists ([publishing](../development.md#publishing-the-nuget-packages)).
- **Proxy features:**
  - Per-upstream connect and idle timeouts, because YARP's 100 s activity timeout cut quiet WebSockets and event streams ("Upstream timeouts" in [hot-path.md](hot-path.md)).
  - Custom request and response headers per host and route, strip-prefix and add-prefix per route, response compression per host ("Custom headers", "Path rewrite" and "Response compression" in [hot-path.md](hot-path.md)). Left out: regex rewrites, header placeholders, zstd (not in .NET 10) and a per-route compression switch.
  - Request timing by stage in the access log, the live tail's waterfall and two histograms on `/metrics`. Request headers are left out, because Kestrel has no first-byte time ("Request timing" in [observability.md](observability.md#request-timing)).
- **API and settings:**
  - The REST API made consistent: kebab-case paths, one status per verb, `{ items, nextCursor }` on every collection, `GET /api/v1/version`, and every query parameter and response in the OpenAPI document, kept by a test ("Conventions for a new endpoint" in [admin-surfaces.md](admin-surfaces.md#conventions-for-a-new-endpoint)).
  - Server settings in the dashboard, stored in `settings.server`, with an explicit environment value winning ("Server settings" in [config.md](config.md#server-settings)). Retention defaults to 90 days for metrics and analytics. Each setting has an address such as `/settings/server#mcp-enabled`, and hints link to a setting instead of naming its variable ("Linking to a field" in [dashboard.md](dashboard.md)).
  - The full `paduraksa doctor`: a read-only check with `--json` and an opt-in `--check-clock`, which makes no outward request unless asked ("`doctor`" in [admin-surfaces.md](admin-surfaces.md#doctor)). The CLI's `reset-password` and `backup` follow the server's sign-in policy precedence, and `config export` and `import` exit 3 on invalid local settings.
- **Verification:**
  - Fuzzing: two parallel hours on each of the sixteen targets, about 1.6 billion inputs, no crash, with the new coverage merged into `fuzz/corpus`.
  - The final bench and review: the bench result is above. The closed-upstream 502 page's connect timing moved off a connect callback, which cost about 10 us per failed connect. A review of everything since r30 found no security or hot-path issue.
  - Load tests: `bench/soak/run-soak.sh` runs an hour of mixed traffic with publishes, a flap, a burst, MCP, a dashboard and a restart, judged on errors, leaks, latency drift and S6 ([Soak test](testing.md#soak-test)). The 2-hour runs before the tag show memory levelling off after a 30-minute climb ([bench/soak/README.md](../../bench/soak/README.md)).
- **Done when:** a tagged release.

## Later

- Native OIDC for the admin and per host.
- Bot heuristics and header rules.
- Per-tag RBAC.
- L4 streams.
- Declarative mode: watched YAML or ConfigMap files, managed objects read-only, secrets by file reference ([admin-surfaces.md](admin-surfaces.md)).
- Fleet mode: control plane and data plane, local bundle sync, vault-to-vault key transfer, staged rollouts, pushed fleet statistics ([deployment.md](deployment.md)).
- Geo, a country map, protection-over-time series and top client IPs for the analytics.
- A plugin model for policies.
- An out-of-process vault.
- A `pass` operation for custom header rules, to un-inherit a rule a lower layer sets without replacing it.
- Cookie `Path` mapping for rewritten routes (an upstream's `Set-Cookie; Path=/` under a stripped prefix).
