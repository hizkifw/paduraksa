# Risks, spikes and milestones

This page keeps the spikes that de-risked the design and the scope, exit criteria and outcome of each milestone. The overview of status is in [../PLAN.md](../PLAN.md).

Contents:
- [Risks and spikes](#risks-and-spikes)
- [Milestones](#milestones)

## Risks and spikes

These were done first, before the features that depend on them. Each has an exit criterion; the result is what was recorded.

### S1: HTTP/3 and per-SNI certificate selection (done, partial: browser upgrade pending)

- **Why:** unverified whether Kestrel's QUIC listener invokes per-SNI selection, and whether libmsquic is available in the container.
- **Exit criterion:** either per-SNI works on QUIC with hot-swap, or the single-H3-cert fallback (see [hot-path.md](hot-path.md)) is implemented. Plus: the browser actually upgrades to H3 in the Docker deployment (`Alt-Svc` carries the public port, UDP 443 reachable).
- **Status:** per-SNI works on QUIC. The browser upgrade is still pending.

### S2: YARP config churn and metadata binding (done, design changed: the gate selects the endpoint)

- **Why:** many hosts (500 and more) and frequent publishes, so endpoint rebuild cost matters. `ConfigureEndpoints(Action<IEndpointConventionBuilder, RouteConfig>)` exists in YARP 2.3.0, but it had to be confirmed that it re-runs on every dynamic reload with the new `RouteConfig`.
- **Exit criterion:** publish under 50 ms at 500 hosts; no request errors during swaps under load; typed metadata on every endpoint matches its generation (or the route-ID fallback is in place).
- **Result:** the design changed. The gate picks the YARP endpoint itself, and routes carry no Host match (see [config.md](config.md)).

### S3: Zero-alloc gate on Kestrel (done, 0 B gate)

- **Why:** request-scoped state without `Items`, span-based host lookup, feature reuse per connection.
- **Exit criterion:** BenchmarkDotNet plus an in-process test show 0 B per request from Paduraksa code.
- **Result:** see [hot-path.md](hot-path.md).

### S4: Typed IDs x EF Core 10 x SQLite (done, partial: the `FrozenDictionary` lookup is not covered)

- **Why:** convention converters, BLOB big-endian GUIDs (`UuidV7Generator` plus `TryWriteBytes(span, bigEndian: true, out _)`), JSON complex types containing typed IDs, LINQ translation of comparisons, and the `FrozenDictionary` `OrdinalIgnoreCase` alternate span lookup.
- **Exit criterion:** round-trip, query and index-ordering tests pass.
- **Result:** done as real code in `Paduraksa.Data` (the generic parts moved to `Paduraksa.Storage` in M3), tested in `tests/Paduraksa.Tests.Data` on real SQLite. Everything worked as planned:
  - BLOB, INTEGER and JSON-string storage;
  - `ORDER BY` equals `CompareTo` equals string order (same millisecond too);
  - comparisons translate directly;
  - an Int64Ordered key is the rowid alias without `AUTOINCREMENT`.
- **Changes:** discovery replaces EF's `PropertyDiscoveryConvention` instead of scanning (see [config.md](config.md)), and JSON stores strings.

### S5: Two WebApplications, one process (done, implemented as the real host)

- **Why:** shared singletons, graceful shutdown order, logging, health.
- **Exit criterion:** both apps start and stop cleanly, and integration tests can host both.

### S6: State continuity across publishes (done, partial: YARP health and rate-limit continuity tested)

- **Why:** rate limits and metrics must not reset on config edits (see "State that survives a publish" in [config.md](config.md)).
- **Exit criterion:** a load test with publishes every 100 ms shows:
  1. continuous 429 behaviour;
  2. monotonic counters;
  3. **YARP destination health preserved**: a destination marked unhealthy (active or passive) stays unhealthy across reloads that do not touch its cluster, and no healthy destination flaps.
- **Results:**
  - M1 (`StateContinuityTests`): no request errors, and (3), over real YARP reloads, policy-only edits and unchanged configs.
  - M7 (`RateLimitContinuityTests`): (1), buckets carried over publishes under load (the 2xx count stays within burst + rate x elapsed and 429s keep coming), plus a lowered burst taking effect on the next touch.
  - (2) is tested with the metric slots (M9).

### S7: Baseline performance vs nginx and Caddy (done: baseline recorded; the tail stalls are under investigation)

- **Why:** know where we stand early.
- **Exit criterion:** `oha` or `wrk2` numbers on the same box recorded in `bench/README.md`.
- **Result** (`bench/results/2026-11-07-baseline.md`, raw data alongside; current tables in `bench/RESULTS.md`):
  - Paduraksa reaches about 0.54 to 0.71x nginx's throughput and 2.1 to 3.6x Caddy's.
  - At fixed rates its median latency is within 0.15 ms of nginx's, but its p99 is 1.4 to 2.3x nginx's. The cause is process-wide stalls that are not GC-driven and are still under investigation.
- The harness now also runs HAProxy, Traefik, Envoy and a bare Kestrel + YARP reference, adds `body-64k` and `handshake` scenarios, and records run-to-run history with `RECORD=1`.

## Milestones

Each milestone ends with a working, testable increment. M0 to M9 are done. M10 is done except where noted. M11 is the v0.1 release.

### M0 Foundations (done)

- **Scope:** repo, CI (build, test, format, arch tests), the `[TypedId]` generator plus base62 plus EF, JSON and Yaml converters, Result types, clock abstraction, spikes S1 to S5.
- **Done when:** generator tests green and arch rules enforced in CI.
- **Outcome:** typed IDs, CI, arch tests, host, data layer, spikes S1 to S5.

### M1 Proxy core (done)

- **Scope:**
  - snapshot model and store, gate middleware, host table;
  - client-IP resolution including Cloudflare `Proxy` and `Tunnel` modes with baked-in ranges;
  - TLS SNI selection, YARP provider, H1, H2 and H3;
  - HTTP-to-HTTPS redirect, default host, a hard-coded test config.
- **Done when:**
  - benchmarks and the S7 baseline exist;
  - the S6 continuity test passes (YARP health, no request errors under publishes);
  - zero-alloc tests pass (the gate including client-IP resolution in every trusted-proxy mode);
  - end-to-end tests through `PaduraksaHost` pass: per-SNI certs and hot swap, redirect port, PROXY protocol, Cloudflare and generic client IP, upstream headers, `Alt-Svc`, H3 in Docker.
- **Outcome:** proxy core, end-to-end tests, benchmarks, S6 in scope, S7 baseline.

### M2 Config store (done)

- **Scope:** EF model, migrations, the Application command pipeline, validation, compiler, revisions, diff, rollback, audit (hash chain), last-known-good.
- **Done when:** change to revision to live in under 100 ms; rollback works.
- **Outcome:** config store wired into the process, startup recompile, last-known-good with degraded starts. Change to live median 11 to 15 ms end to end.

### M3 Vault and certificates (done)

- **Scope:** KEK/DEK store, capabilities, upload, self-signed, local CA, ACME client (HTTP-01, then DNS-01 with Cloudflare and deSEC), renewal scheduler, ARI.
- **Done when:** Pebble-based integration tests issue and renew; banned-API rules hold.
- **Decisions:**
  - Shared EF/SQLite infrastructure lives in `Paduraksa.Storage`.
  - The compiler tolerates a missing or unusable certificate (a warning, never a failed start), and the placeholder has a stable ID and is the fallback default (see [vault-and-certificates.md](vault-and-certificates.md)).
- **Outcome:** a vault with envelope encryption and KEK rotation; certificate upload, self-signed and local CA; ACME issuance and renewal with ARI; HTTP-01 and DNS-01 (Cloudflare and deSEC) with the authoritative-NS propagation check; Pebble issue and renew with the real stack.
- **Closed on `host-header-acme`:** DNS-01 against Pebble end to end, `_acme-challenge` CNAME delegation, local CA name constraints and root rotation.

### M4 Admin backend (done)

- **Scope:** the admin app, local users, cookies, TOTP, roles, API tokens, the REST API plus OpenAPI, the first-run setup-token gate, and the **request simulator core** (the gate tracer, `SimulateAsync` over live, revision and draft, `POST /api/v1/simulate`, parity tests).
- **Done when:** an API-only end-to-end setup is possible, and the simulator explains every gate outcome M1 to M3 can produce and matches the real gate in parity tests.
- **Outcome:** identity core, admin auth and setup gate, resource REST API with OpenAPI and offline docs, request simulator with parity tests, admin certificate from the local CA, admin through the proxy. `ApiOnlySetupEndToEndTests` walks the API-only first run.
- **Deferred:** passkeys (to M5, then dropped for now), and local CA root rotation (later built, see M3).

### M5 Dashboard and container (done)

- **Scope:**
  - **Container** (from M11): Dockerfile (Noble plus libmsquic, non-root, 80, 443 tcp+udp, 9443, `/data`, `HEALTHCHECK`), a `compose.yaml` example, an install and first-run README.
  - **Dashboard foundation:** Blazor Server plus MudBlazor on the admin listener over the Application services, sign-in with password plus TOTP, session revocation reaching open circuits, the Data Protection key ring in the vault, CSP.
  - **Wizard (trimmed):** setup token, Owner (plus TOTP), ACME account, DNS provider with live test (skippable), certificate with progress, first host with an upstream check, done.
  - **Pages:** hosts, upstreams, certificates, review-before-apply (dry-run diff plus warnings, Apply, Undo), history (diff, rollback), audit, simulator, settings (defaults, ACME accounts, DNS providers, users and roles, tokens, my account).
- **Deferred:** overview and live logs (M9), security page (M7), profiles UI, inheritance badges, command palette, ApexCharts, mobile polish. Passkeys dropped for now. Backups stay in M10.
- **Done when:** a new user goes from `docker run` to a proxied host via the UI only (a browser-driven exit test).
- **Outcome:** a Noble container with compose, a systemd unit and install docs; the admin Data Protection ring in the vault; the Blazor dashboard with sign-in and TOTP, hosts, upstreams, review-before-apply with Undo, history, simulator, certificates with ACME progress, ACME accounts, DNS providers, settings, users, API tokens, My account and audit; the first-run wizard (later removed, see [dashboard.md](dashboard.md)). The exit path is covered by the wizard's browser test and the container smoke test, run separately.
- **As built:** `Dockerfile` at the root (Noble, pinned libmsquic 2.5.10, non-root, 80, 443 tcp+udp, 9443), `deploy/compose.yaml`, `deploy/paduraksa.service`, `docs/install.md`, `doctor --liveness`. The Data Protection key ring lives in the vault and both web apps use it.

### M6 HSTS and logs (done)

- **Scope:**
  - **HSTS** as a host setting (global default, profile, host): `max-age`, `includeSubDomains`, `preload`. Sent only on HTTPS responses, never for IP-literal or loopback names, precomputed per host (0 B).
  - **Access log:** one plaintext line per request (a documented combined-style format: time, client IP, host, method, path, status, bytes, durations, upstream, request ID, `CF-Ray`, user agent), per-host on or off, written to `<data dir>/logs/access.log`. The hot path only fills a fixed-size struct ring, and a background writer formats and writes.
  - **Error log:** the process's own logs (Warning and above by default, level configurable) to `<data dir>/logs/error.log` in plaintext alongside the console.
  - **Rotation and compression:** by size and daily, rotated files gzip-compressed in the background, retention by count, age and total size. A full ring drops lines and counts the drops, never blocks a request.
  - `X-Request-Id` (honoured from trusted proxies, else generated).
  - **Dashboard:** HSTS fields, and a log page to browse and download the files.
- **Done when:** the gate stays at 0 B with logging on; rotation and compression run under load without blocking requests or losing ordering; no secrets in either log (cookies, `Authorization` and query strings marked sensitive are redacted).
- **Outcome:** HSTS, the `Server` header, the access-log switch and where the request ID is sent as host settings; `X-Request-Id`; the access log through a lock-free ring into `access.log`, the error log into `error.log`, both rotated, gzip-compressed and trimmed; the Logs page and API behind `logs:read`.

### M7 Access lists, auth and WAF (done)

- **Scope:**
  - **Access lists** (IP allow/deny plus HTTP basic auth).
  - **Forward-auth** (authentik, Authelia, oauth2-proxy presets).
  - **PoW gate** (HMAC-signed, IP-bound, expiring tokens; difficulty per host; JS worker solver; vault `IMacKey`; moved forward from Later).
  - IP, geo, rate-limit, size, path and UA policies.
  - Cloudflare range refresh job, `CF-IPCountry` geo, origin lockdown and Authenticated Origin Pulls.
  - Presets, the dashboard's security page, and a simulator trace step for every new policy.
- **Done when:** policy unit tests and simulator parity tests pass; S6 rate-limit continuity holds (429 behaviour continuous across publishes); a PoW challenge is solved in a real browser.
- **Outcome:**
  - named access lists with IP and country rules and basic auth whose passwords live in the vault;
  - forward-auth with authentik, Authelia and oauth2-proxy presets;
  - the PoW gate, solved in a real browser;
  - rate-limit policies with S6 continuity;
  - request limits;
  - a per-IP connection cap, a connection deny list and explicit slow-client and connection limits before TLS;
  - rule sets with presets;
  - Cloudflare edge ranges refreshed daily, origin lockdown and Authenticated Origin Pulls;
  - the managed robots.txt, the built-in error pages, the security overview;
  - a simulator step for every new policy with parity tests.
- **Open:**
  - a local country database (see [waf.md](waf.md));
  - bcrypt `htpasswd` import;
  - native OIDC (see [identity.md](identity.md));
  - a forward-auth trace that makes the real call;
  - a warning for a rule no request can reach.
- Upstream Host header modes were built on `host-header-acme` (see [config.md](config.md)).
- Property tests and fuzzing followed at once (see [testing.md](testing.md)).

### M8 Custom error pages (done)

- **Done when:** every gate and upstream error answers with the configured page, and previews match what is served.
- **Outcome:**
  - custom pages per status at every layer (a message in the built-in layout, or the operator's own document with a style-only CSP) with `{status}`, `{statusText}`, `{requestId}`, `{host}` and `{cfRay}`, written from byte segments at 0 B;
  - maintenance mode per host with a status, `Retry-After`, a page and a bypass named by an access list;
  - opt-in replacement of an upstream's error bodies at response-header time, never buffered;
  - the dashboard's rows with a sandboxed preview that is the proxy's own rendering, the maintenance wrench and badge;
  - the simulator's step and page.
- **Open:** per-route pages, localisation, statuses beyond the ten, the security overview's maintenance column. See "Error pages" in [waf.md](waf.md).

### M9 Observability (done)

- **Scope:** counters, histograms, `/metrics`, health checks, notifications, the live tail (over the M6 access-log ring), rollups, the dashboard's live overview.
- **Done when:** the dashboard overview is fully live, and S6 metric continuity holds (counters monotonic across publishes).
- **Outcome**, in the order it was built (details in [observability.md](observability.md)):
  1. Counters and latency histograms by host, route, upstream and destination at 0 B on the request path, `System.Diagnostics.Metrics`, Prometheus `/metrics` behind `metrics:read`. The one-second sampler (ticks on whole seconds, every window recording the seconds it observed) with an hour of rings seeded from the persisted one-minute rollups, a flush at shutdown, and downtime drawn as gaps. The live overview on the home page.
  2. Upstream health state and notifications (ntfy, Gotify, Discord, webhook, email).
  3. The live tail: the drain fans out to a bounded in-memory hub, a Live tail page under Insight and `GET /api/v1/logs/tail` as SSE, with the request path untouched at 0 B.
  4. Traffic analytics: a third drain consumer with sketches, a user-agent classifier and rollups, `GET /api/v1/analytics` and an Analytics page.
  5. Body bytes counted as they pass, both ways, by host, route and upstream at 0 B a request, into the counters, the access log, the analytics and Prometheus.
  6. Durable notification delivery with a history: a database outbox fed by a buffered flusher, per-channel workers, at-least-once with an event ID for webhooks, retention, `GET /api/v1/notifications/history` with a retry, and a Notification history page.

### M10 Interop (partly done: drafts done, NPM importer not built)

- **Scope and status:**
  - YAML import and export: **done**.
  - The CLI's `config export` and `config import`: **done**.
  - MCP server: **done** (code mode).
  - Backup and restore: **done**. An Owner-only, passphrase-encrypted, online archive of the config and the vault that restores on a fresh machine: `paduraksa backup|restore`, `POST /api/v1/backup`, Settings > Backup.
  - **Drafts:** **done**. Change sets across pages applied as one revision.
  - NPM importer: nice to have.
- **Done when:** a round-trip export then import yields an identical revision hash.
- Details: [admin-surfaces.md](admin-surfaces.md), [vault-and-certificates.md](vault-and-certificates.md), [dashboard.md](dashboard.md).

### M11 Release v0.1 (open)

- **Scope:**
  - multi-arch image publishing;
  - the docs site (**built** on `docs-site`: MkDocs Material over `docs/`, a user guide split from the old install guide, the API reference rendered from the shipped OpenAPI document, built `--strict` on every push; publishing to GitHub Pages waits for the owner, see "The documentation site" in [deployment.md](deployment.md#the-documentation-site));
  - the threat model doc (**done**: [../threat-model.md](../threat-model.md), with its gaps list). The small gaps G2, G3, G4, G5, G7, G9 and G13 are closed on `security-gaps`; G12 waits for the fuzz run;
  - the PROXY protocol parser as two NuGet packages (`Paduraksa.ProxyProtocol`, `Paduraksa.ProxyProtocol.AspNetCore`; **built** on `proxyprotocol-package`: packed on every CI run, pushed to nuget.org by a `proxyprotocol-v*` tag through trusted publishing once a `NUGET_USER` variable exists, see [../development.md](../development.md#publishing-the-nuget-packages));
  - per-upstream connect and idle timeouts (**done** on `upstream-timeouts`: YARP's 100 s activity timeout was cutting quiet WebSockets and event streams; see "Upstream timeouts" in [hot-path.md](hot-path.md));
  - the REST API made consistent (**done** on `api-consistency`: kebab-case paths, one status per verb, `{ items, nextCursor }` on every collection, `GET /api/v1/version`, every query parameter and response in the OpenAPI document, kept by a test; see "Conventions for a new endpoint" in [admin-surfaces.md](admin-surfaces.md#conventions-for-a-new-endpoint));
  - custom request and response headers per host and route, strip-prefix and add-prefix per route, and response compression per host (**built** on `route-transforms`; see "Custom headers", "Path rewrite" and "Response compression" in [hot-path.md](hot-path.md)). Left out: regex rewrites, header placeholders, zstd (not in .NET 10) and a per-route compression switch;
  - a time-boxed run of every fuzz harness (`scripts/fuzz.sh --time 3600 --merge`; the harnesses and property tests are built, YAML and ACME harnesses with M10);
  - load tests.
- **Done when:** a tagged release.

### Later

- Forward-auth to native OIDC (admin and per-host).
- Bot heuristics and header rules.
- Per-tag RBAC.
- L4 streams.
- Declarative mode (watched YAML or ConfigMap files, managed objects read-only, secrets by file reference). See [admin-surfaces.md](admin-surfaces.md).
- **Fleet mode** (control plane and data plane, local bundle sync, vault-to-vault key transfer, staged rollouts, pushed fleet statistics). See [deployment.md](deployment.md).
- Geo and a country map, protection-over-time series and top client IPs for the analytics.
- A plugin model for policies.
- An out-of-process vault.
- A `pass` operation for custom header rules, to un-inherit a rule a lower layer sets without replacing it.
- Cookie `Path` mapping for rewritten routes (an upstream's `Set-Cookie; Path=/` under a stripped prefix).
