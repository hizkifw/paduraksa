# Paduraksa - Design & Implementation Plan

> **Paduraksa**: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.
>
> Open-source, homelab-grade reverse proxy and lightweight WAF on **Kestrel + YARP**. You deploy it once and manage it from the dashboard, the REST API, MCP or the CLI. It covers what nginx-proxy-manager does, adds versioned config, an audit trail, real multi-user support and a hot path designed for speed from day one.

Status: **v0.30**: M0-M10 done (the NPM importer stays a nice-to-have); M11 under way: threat model done, its small gaps closed; the fuzz run, load tests, docs site and the v0.1 tag left (see CHANGELOG.md) · Target runtime: **.NET 10 (LTS)** · Single node, single process.

---

## How to read this

This page is the overview: what Paduraksa is, how it is built, how a request flows through it, the rules the code follows, and where the project stands. Read it top to bottom in about twenty minutes.

The detail lives in `docs/design/`, one page per area. Each section below ends with a link to its page. Every design page opens with a short summary and a contents list.

| Page | Covers |
|---|---|
| [hosting.md](design/hosting.md) | Process and hosts, listeners, ports, the admin certificate, admin through the proxy |
| [config.md](design/config.md) | Typed IDs, data model, settings layers, the config lifecycle and the snapshot |
| [hot-path.md](design/hot-path.md) | The request budget, the pipeline, response headers, connection limits, HTTP/3, the simulator |
| [waf.md](design/waf.md) | Rate limits, access lists, rule sets, forward-auth, proof of work, error pages, request limits |
| [observability.md](design/observability.md) | Logs, metrics, live tail, analytics, health and notifications |
| [cloudflare.md](design/cloudflare.md) | Client IP behind Cloudflare, origin lockdown, Authenticated Origin Pulls |
| [vault-and-certificates.md](design/vault-and-certificates.md) | The vault, backup, the certificate inventory, local CA, ACME, DNS-01 |
| [identity.md](design/identity.md) | Users, sessions, roles, the admin pipeline |
| [admin-surfaces.md](design/admin-surfaces.md) | REST API, MCP, CLI, YAML, declarative mode |
| [dashboard.md](design/dashboard.md) | The Blazor dashboard, drafts, conventions, first-run setup |
| [testing.md](design/testing.md) | Test levels, property tests, fuzzing |
| [deployment.md](design/deployment.md) | Container, compose, bare metal, fleet mode |
| [milestones.md](design/milestones.md) | Spikes and the scope and outcome of each milestone |

Other documents: [the user guide](guide/getting-started.md) and [operating it](operating/docker.md) (running it; [install.md](install.md) is the map to them), [development.md](development.md) (working on it), [threat-model.md](threat-model.md), and [../CHANGELOG.md](../CHANGELOG.md) (what changed, newest first).

---

## 0. Goals / non-goals

**Goals**
1. **Performance.** The request hot path never touches the DB, takes no locks and allocates nothing in Paduraksa code that can be avoided. Config changes are published as immutable snapshots swapped atomically.
2. **UX.** From zero to a wildcard cert and your first host in under 5 minutes: setup claims the instance in one form, and the overview's checklist points at the rest. Every change is previewed as a diff, versioned and reversible.
3. **Manage everything without a shell.** Dashboard, REST API, MCP and CLI are thin skins over **one** application layer, so features never drift between surfaces.
4. **Security by construction.** Typed IDs everywhere, DTO/entity separation, and a key-material trust boundary enforced by the compiler.
5. **Batteries included, kept small.** ACME (HTTP-01/DNS-01), uploaded/self-signed/local-CA certs, WAF primitives, error pages, robots.txt, metrics.

**Non-goals (for now)**
- OWASP CRS or ModSecurity-style deep inspection. The WAF is deliberately simple and fast.
- Clustering/HA. It is a single node, though state is easy to back up and restore. Fleet mode is designed for after v0.1.
- L4 (TCP/UDP) streams. These come later, after HTTP is solid.
- Native AOT. Blazor Server, EF Core and YARP rule it out today. We use ReadyToRun + Dynamic PGO instead.

---

## 1. Solution layout

```
Paduraksa.slnx
+- src/
|  +- Paduraksa.Core            Typed IDs, primitives, Result types, capability interfaces (vault, clock)
|  +- Paduraksa.Ids.Generator   Roslyn incremental source generator for [TypedId] structs
|  +- Paduraksa.Contracts       DTOs for API/MCP/Dashboard/YAML. No EF, no entities.
|  +- Paduraksa.Storage         Shared EF Core/SQLite infrastructure: typed-ID conventions and converters, SQLite pragmas, UTC timestamps. No entities, no DbContext.
|  +- Paduraksa.Data            EF Core (SQLite) entities, DbContext, migrations, entity-specific converters
|  +- Paduraksa.Vault           TRUST BOUNDARY: keys, certs' private keys, secrets. Everything `internal`.
|  +- Paduraksa.Acme            RFC 8555 client + DNS providers. Signs via vault capabilities.
|  +- Paduraksa.Notifications   Notification senders (ntfy, Gotify, Discord, webhook, email over MailKit) and their payload formats. Secrets come through Core capabilities.
|  +- Paduraksa.Mcp             MCP in code mode: the sandbox's child process (Jint) and the parent's pool, watchdog and frame protocol, the spec graph, the request policy and the tool descriptions. No ASP.NET Core, no other project.
|  +- Paduraksa.ProxyProtocol   PROXY protocol v1/v2 parser and reader, plus IpKey/IpParser (BCL only; a NuGet package)
|  +- Paduraksa.ProxyProtocol.AspNetCore   Kestrel `UseProxyProtocol` connection middleware over it (a NuGet package; Paduraksa itself doesn't use it)
|  +- Paduraksa.Proxy           HOT PATH: runtime snapshot model, gate middleware, policies, TLS selector, YARP glue, metrics
|  +- Paduraksa.Application     Use cases: config commands, validation, revisions, audit, snapshot compiler, YAML, auth
|  +- Paduraksa.Admin           Admin web surface: auth, REST /api/v1, /mcp, /metrics, OpenAPI
|  +- Paduraksa.Dashboard       Blazor Server components (RCL)
|  +- Paduraksa.Server          Composition root + CLI. Hosts the public and admin apps in one process. Binary: `paduraksa`
+- tests/
|  +- Paduraksa.Tests.Unit
|  +- Paduraksa.Tests.Data          (EF Core mapping on real SQLite: typed IDs, pragmas, seeding; covers Storage and Data)
|  +- Paduraksa.Tests.Proxy         (hot path: gate, client IP, TLS selection, YARP glue, zero-alloc; end-to-end through PaduraksaHost)
|  +- Paduraksa.Tests.Integration   (real Kestrel, real SQLite, Pebble ACME test server in CI)
|  +- Paduraksa.Tests.Issuance      (ACME issuance on real SQLite against the fake CA and a Pebble container; the one test project that lifts the key-material ban)
|  +- Paduraksa.Tests.Dashboard     (bUnit component tests; a Playwright end-to-end smoke test against a real PaduraksaHost, skipped without Chromium)
|  +- Paduraksa.Tests.Architecture  (dependency rules, DTO rules, typed-ID rules)
+- bench/
   +- Paduraksa.Benchmarks          (BenchmarkDotNet: hot-path policies, lookups, zero-alloc assertions)
   +- Paduraksa.Bench.Runner        (the real PaduraksaHost for the end-to-end baseline, `run-baseline.sh`)
```

### Dependency rules (enforced by architecture tests)

```
Core <-- everything
Contracts --> Core
Storage --> Core                     (generic EF/SQLite infrastructure; no entities)
Data --> Core, Storage
Vault --> Core, Storage              (has its OWN store and DbContext; never references Data)
Acme --> Core                        (signing/creds via Core capability interfaces)
Notifications --> Core               (secrets via Core's notification-secret capability, HMAC via IMacKeySource)
Mcp --> (nothing)                    (Jint only; the admin app owns the transport and the in-process dispatcher, the server starts the child)
ProxyProtocol --> (nothing)          (BCL only; the parser and the address types, a NuGet package of its own)
ProxyProtocol.AspNetCore --> ProxyProtocol   (Kestrel middleware; also a NuGet package, and no other project references it)
Proxy --> Core, ProxyProtocol        (no Data, no Storage, no Application, no EF: the hot path can't reach the DB)
Application --> Core, Contracts, Data, ProxyProtocol, Proxy, Acme, Notifications
Admin, Dashboard --> Application, Contracts   (never Data: UI/API can only see DTOs); Admin also --> Mcp
Server --> everything (composition only)
```

The two rules that matter most: **Proxy cannot reach the database**, and **Admin and Dashboard cannot see entities**. Both are compile-time facts, not conventions.

---

## 2. Development rules and how they are enforced

| Rule | Mechanism |
|---|---|
| **Typed IDs all the way to EF** | `[TypedId]` source generator; EF conventions map every typed ID automatically; BannedApiAnalyzer bans `Guid.NewGuid()` repo-wide; an arch test fails if any `Contracts`/entity property named `*Id` is `Guid`/`string`/`long`. |
| **DTOs decoupled from entities** | `Admin`/`Dashboard` can't reference `Data` (project graph + arch test). Mapping is explicit (hand-written or Mapperly source-gen), never reflection auto-mapping. DTOs are `sealed record`s. Entities never get serialized (an arch test checks that no `Data` type appears in a JSON context). |
| **Trust boundary for key material** | `BannedSymbols.KeyMaterial.txt` bans private-key export/load APIs, `AesGcm` and `HMAC*` in **every project except `Paduraksa.Vault`** (already wired up in `Directory.Build.props`). The vault exposes *capabilities* (sign, decrypt-and-use, select-cert), never bytes. Its types are `internal`, and only the Server composition root registers it. |
| Warnings are errors, nullable on | `Directory.Build.props` |
| **Prose spelling** | British, like the rest of the repo: *enrol*, *enrolled*, *enrolment*; *normalised*, *behaviour*. Identifiers and wire names are left as they are (`BeginEnrollmentAsync`, `me/totp/enrollment`), so code and URLs don't change. |
| **No secret in a log - one decided exception** | The setup token is logged, in a boxed banner at Warning (see [dashboard.md](design/dashboard.md), "First-run setup"). Decided by the owner: it is single-use, worthless once an Owner exists (and deleted then), and anyone who can read the log already has the host's shell, and with it the token file. Everything else (passwords, cookies, API tokens, keys, DNS credentials, EAB keys) never reaches a log, a problem or the audit trail. |
| Central package versions | `Directory.Packages.props` |

---

## 3. Process and hosting model

Detail: [design/hosting.md](design/hosting.md).

One process runs **two web apps** plus a non-web **core host**, all sharing one set of singletons:

```
                    +---------------------- paduraksa process ----------------------+
 :80  (H1)  ------> | PUBLIC APP                                                     |
 :443 (H1/H2/H3) -> |  conn middleware > TLS(SNI) > Gate > host policies > YARP ------+--> upstreams
                    |          ^                 ^                                   |
                    |          +-- SnapshotStore (volatile ref, immutable snapshot) |
                    |                            ^ publish                           |
 :9443 (H1/H2) ---> | ADMIN APP  Dashboard, /api/v1, /mcp, /metrics > Application ---+
                    |                                                  |   |         |
                    |                                Data (SQLite) <---+   +--> Vault |
                    +----------------------------------------------------------------+
```

- **Public app:** the proxy. Listens on 80 (HTTP) and 443 (TCP for H1/H2, UDP for H3).
- **Admin app:** the dashboard, REST API, MCP and metrics, on 9443 with a certificate from the internal local CA. A plaintext request gets one redirect, never a page.
- **Core host:** owns every once-per-process background service (ACME scheduler, metric sampler, log drain). It starts first, so a snapshot exists before any port opens, and stops last.
- **Why two apps, not one with port filters:** admin endpoints cannot be routed on public ports because the public pipeline does not contain them. Each pipeline gets its own middleware, auth and limits. Shared singletons are registered as instances in both containers.

Decisions worth knowing:
- A wrong, missing or unsafe master key **stops the process** (exit 2). Running without the vault would only move the failure to the first certificate that needs a key.
- Exit codes: 0 stopped on request, 1 a host failed on its own, 2 startup failed, 3 invalid config.
- Shutdown order: `/readyz` goes 503, public drains (WebSockets get a 1001 close), admin stops, core stops. One 30 s timeout, with a share kept for core's flush (so compose needs `stop_grace_period: 35s`).
- The admin certificate is issued from the vault's local CA and kept current. Trusting the root once makes the admin UI trusted in the owner's browsers.
- The admin UI can optionally be exposed through the proxy itself (an internal Unix-socket listener, an authenticated hop, a lock-out guard that refuses changes that would cut the caller off).

---

## 4. Typed IDs

Detail: [design/config.md](design/config.md).

Every ID is a typed struct from the entity to the EF column, so a `HostId` can never be passed where a `UserId` is expected. A source generator emits parsing, formatting, JSON, YAML, EF and OpenAPI support.

| Kind | Use for | Storage |
|---|---|---|
| `UuidV7` (default) | Config entities, users, tokens, revisions, certs, orders | 16-byte big-endian BLOB, so byte order is time order |
| `UuidV4` | IDs visible publicly where creation time must not leak | 16-byte BLOB |
| `Int64Ordered` | High-volume append-only tables (audit, rollups) | `INTEGER PRIMARY KEY`; predictable, so internal only |

The string form is `prefix_` plus fixed-width base62 (`host_0J9zb3xQk1Lr8m2V7aYc4D`), so string order equals numeric order. API tokens are different on purpose: `pdk_...` with a checksum so secret scanners can find leaked ones.

---

## 5. Data model

Detail: [design/config.md](design/config.md); the identity tables are in [design/identity.md](design/identity.md).

- **Two database files, kept apart.** `paduraksa.db` holds config revisions, users, audit and metric rollups. `vault.db` holds encrypted key material and secrets, owned only by the vault. A config export or a config-database leak never carries keys.
- **The config is one document.** Revisions store it as canonical JSON (Brotli-compressed, with its SHA-256). Hosts, routes, upstreams, profiles, access lists, rule sets and the rest are objects inside it. Certificates, ACME accounts, DNS providers and notification channels are separate operational tables, because they hold state outside the document.
- **Settings layer.** Every host setting is nullable: null means inherit. The layers are built-in defaults, global defaults, profile, host, then a route's own subset. One merge (`HostSettingsLayers`) serves the compiler, the validator and the dashboard, so a form can never promise a value the proxy would not apply.
- **Host kinds:** Proxy, Redirect, Respond and Admin. Serving files from disk (Static) is cut from v1.

---

## 6. Config lifecycle

Detail: [design/config.md](design/config.md).

A change flows through one command pipeline, whatever surface it came from:

1. authorize;
2. apply the mutation to the current document;
3. validate (field rules and cross-object rules);
4. **compile a candidate snapshot** in memory (a failure here persists nothing);
5. commit a new revision and an audit event in one transaction;
6. **publish**: hand YARP its new config, wait for it to apply, then atomically swap the snapshot;
7. notify the dashboard.

Key properties:
- **Optimistic concurrency.** Each top-level object has a version, and two admins editing the same host get a conflict with a diff.
- **Dry run everywhere.** The dashboard's review, MCP's two-step writes and `import --dry-run` all run steps 1-4 only.
- **Rollback is a new revision.** History is never rewritten.
- **Startup recompiles the latest revision.** The database is the source of truth. A copy of the live document is also kept in `last-good.json`, so the proxy still comes up serving traffic if the database is unreadable (a degraded start, reported as such).
- **State that must survive a publish** (rate-limit buckets, connection counts, metric counters, forward-auth caches) never lives in a snapshot. Snapshots hold only keys into process-lifetime state, derived from stable IDs.
- **The gate, not ASP.NET routing, picks the YARP endpoint**, from the same snapshot that supplied the policies. A request therefore cannot mix generations.

---

## 7. Hot path

Detail: [design/hot-path.md](design/hot-path.md) for the budget and pipeline; [design/waf.md](design/waf.md) for the policies; [design/observability.md](design/observability.md) for logs and metrics; [design/cloudflare.md](design/cloudflare.md) for Cloudflare.

**The budget.** No DB, no locks, no LINQ, no closures, no string building, no backtracking regex. **0 B** of allocation in Paduraksa code per request in steady state, enforced by allocation tests. Everything is precomputed at compile time. Spike S3 measured the gate at 0 B on H1 and H2.

### How a request flows

1. **Connection (before TLS).** The PROXY protocol header (if enabled on that listener) is parsed so the real client address feeds everything after. A global IP deny list and a per-IP connection cap are applied. Origin lockdown (Cloudflare-only) is enforced here when every host asks for it.
2. **TLS handshake.** The SNI picks a prebuilt set of TLS options from the live snapshot (certificate, minimum version, ALPN). Unknown SNI gets the default certificate or a refused handshake.
3. **Gate: who is the client.** One volatile read gets the snapshot. The client address and the trusted request scheme are resolved (a Cloudflare peer or a configured proxy may supply them, nobody else).
4. **Gate: special answers.** A published ACME HTTP-01 token on plain HTTP is answered at once. The host is looked up by span, so no string is built. An unknown host gets the default behaviour (404, 421 or close).
5. **Gate: connection checks.** The host's TLS policy is re-checked per request (clients coalesce connections), so a weaker connection gets 421. Authenticated Origin Pulls is checked first where configured.
6. **Gate: host chain.** Host policies run before anything else the host answers: access list by address and country, rule set, and the rest. A blocked request gets 403, not a redirect.
7. **Gate: request limits and redirect.** Target and header size limits (414 and 431), then the HTTP-to-HTTPS redirect from precomputed bytes.
8. **Gate: fixed answers.** A managed `robots.txt`, maintenance mode, and Redirect and Respond hosts are answered from precomputed bytes.
9. **Gate: route.** The most specific route is chosen on a canonical view of the path (match leniently, forward unchanged). No match is a 404.
10. **Route chain.** The route's own policies run in order: access list (with a password check that waits in a bounded line when it has to), rule set, rate limit, forward-auth, then proof of work. A declared body over the limit is a 413.
11. **YARP.** The gate sets the endpoint from its own snapshot. YARP applies affinity, load balancing and passive health, and the proxy's transforms overwrite the forwarding headers, drop forged `cf-*` headers and strip identity headers the auth provider owns.
12. **Response.** The host's response headers (`Server`, `Alt-Svc`, HSTS, request ID) apply to every answer, including errors. Replaced error pages are written from byte segments.
13. **Telemetry.** On completion a fixed-size struct goes into a lock-free ring, and counters and histograms are updated. Background services read them. No request ever waits on a log or a database.

### Subsystems at a glance

- **WAF building blocks** ([waf.md](design/waf.md)). All compiled per snapshot:
  - *Named objects*: access lists (IP, country and basic auth), rule sets (path, method and user agent), rate-limit policies, forward-auth providers.
  - *Rate limiting*: a fixed-size lock-free token-bucket table, memory-bounded, with accepted and bounded races.
  - *Proof of work*: a stateless, HMAC-signed challenge solved in the browser.
  - *Error pages and maintenance mode*: custom pages written from byte segments at 0 B.
  - *Request limits*: body, header and URL size, with Kestrel's own limits as the outer bound.
  - *Why named objects:* the owner decided there is one way to express IP rules, never an inline list.
- **Observability** ([observability.md](design/observability.md)). The request path only fills fixed buffers. The access log, the live tail and the analytics are consumers of one ring. Metrics are striped counters and 101-bucket histograms, sampled every second, persisted as one-minute rollups. Downtime is drawn as a gap, never stitched. Notifications go through a durable outbox, at least once.
- **Client IP and Cloudflare** ([cloudflare.md](design/cloudflare.md)). A `Proxy` or `Tunnel` mode makes the real client address a toggle. Edge ranges are refreshed daily. A changed range list republishes the live config with no new revision.
- **The request simulator** ([hot-path.md](design/hot-path.md)). It runs the real gate code over a real compiled snapshot (live, an old revision or a draft), so its answer cannot drift from production. Gate steps report through a struct tracer the JIT removes in production.

---

## 8. Vault (key-material trust boundary)

Detail: [design/vault-and-certificates.md](design/vault-and-certificates.md).

The vault is the only place key material lives. It exposes capabilities (sign, serve a certificate, verify a password), never bytes. It has its own database and its own `DbContext`.

- **At rest:** envelope encryption. Each secret has its own AES-256-GCM key, wrapped by a master key that comes from a Docker secret, an env var or a generated file. The associated data binds each value to its ID and purpose.
- **Sanctioned egresses** (the only places a key leaves): serving TLS, upstream mTLS, the Data Protection key ring and notification secrets.
- **Passwords** for access-list users are vault credentials. The gate holds a handle and a verified-check cache, never a hash.
- **Backup** is one passphrase-encrypted archive of both databases and the last-known-good file. Its keys are re-wrapped under a backup-only key, so the source's master key never enters the archive. Restore is CLI-only and undoes itself on failure.
- **One server per data directory**, enforced by a file lock.

---

## 9. Certificates and ACME

Detail: [design/vault-and-certificates.md](design/vault-and-certificates.md).

- **Own ACME client** (RFC 8555), because the account key must stay in the vault. HTTP-01 is answered by the gate. DNS-01 has Cloudflare and deSEC providers, a propagation check that asks the authoritative name servers directly, and automatic `_acme-challenge` CNAME delegation.
- **Renewal** uses the ARI window if the CA offers one, otherwise renews at a third of the lifetime remaining, with jitter and backoff.
- **The inventory.** The compiler picks, per name, the most specific valid certificate (exact beats wildcard). A missing or unusable certificate is a warning and a degraded name, never a failed start. A certificate change republishes without creating a revision.
- **Local CA.** An internal root issues leaves for LAN hosts and the admin port. It is unlimited unless the operator chooses names, and it can be rotated. Nothing renews the root yet.
- **Expiry monitoring** covers every certificate, whatever its source.

---

## 10. Authentication and authorization

Detail: [design/identity.md](design/identity.md).

- **Admin auth.** Local users with password and TOTP. Sessions are opaque cookies (only the hash is stored), API tokens are `pdk_...` bearer tokens. A token's permissions are the intersection with its owner's current ones. Roles are permission sets: Owner, Admin, Operator, Viewer, Auditor. Operator is effectively a trusted role.
- **One permission check** (`IPermissionEvaluator`) serves every surface. There is no allow-everything default.
- **Lockout and rate limits** are race-proof and bounded in memory. A lockout at the password stage ends nothing, so someone who knows a username cannot sign the owner out.
- **Proxied-host auth:** access lists with basic auth, forward-auth (authentik, Authelia, oauth2-proxy). Native OIDC is later.
- **Not built:** passkeys and OIDC for the admin.

---

## 11. Admin surfaces

Detail: [design/admin-surfaces.md](design/admin-surfaces.md).

All four (dashboard, REST, MCP, CLI) call the same Application services with the same DTOs, authorization and audit. The audit record notes the channel.

- **REST API** (`/api/v1`): minimal APIs, OpenAPI 3.1 plus Scalar. Typed-ID routes, `If-Match` concurrency (428 when missing, 412 when stale), `?dryRun=true`, RFC 9457 problems with stable dotted codes. A change stored but not live is a 500 `config.publishFailed`.
- **MCP in code mode** (off by default). Two tools, `search` and `execute`. A model's JavaScript runs in a **killable child process** with no token, no files and no network, limited and killed from outside by the parent. Writes are two-step (a dry run first). A refused list keeps secrets and destructive endpoints out of reach.
- **YAML import and export** round-trips to the identical revision hash. Merge and replace modes, names for references, hard limits against billion-laughs input.
- **CLI:** the same binary. `config export|import` are clients of the running instance's API, never of the database.
- **Declarative mode** (after v0.1): watched files become the config for managed objects.

---

## 12. Dashboard

Detail: [design/dashboard.md](design/dashboard.md).

Blazor Server with MudBlazor, on the admin port only. Every change goes through a review drawer (diff, warnings, Apply, Undo). Editing happens in stacking drawers. A **draft** collects edits across pages and applies them as one revision, with a review token so that what was confirmed is exactly what is applied, or nothing.

- Sessions are re-checked in the background, so a revoked session ends the open page.
- No secret stays in component state longer than the step that shows it.
- Insight pages: logs, live tail, security overview, analytics. Settings pages: defaults, profiles, ACME, DNS, notifications, users, tokens, backup, YAML.
- Shared conventions (segmented controls, 36 px icon buttons, copyable code, no filler copy) are listed in the dashboard page.
- First-run setup is one form (token, then Owner). There is no wizard. An overview checklist lists the open tasks.

---

## 13. Risks and spikes

Detail: [design/milestones.md](design/milestones.md).

Seven spikes de-risked the design before the features that needed them. All are done.

| # | Spike | Result |
|---|---|---|
| S1 | HTTP/3 with per-SNI certificates | Works on QUIC. A browser upgrade test is still pending. |
| S2 | YARP config churn | The gate selects the endpoint, and routes carry no Host match. |
| S3 | Zero-alloc gate | 0 B per request. |
| S4 | Typed IDs with EF Core and SQLite | Worked as planned. |
| S5 | Two web apps in one process | Implemented as the real host. |
| S6 | State continuity across publishes | Health, rate limits and counters stay continuous. |
| S7 | Baseline against nginx and Caddy | About 0.54 to 0.71x nginx's throughput. p99 tail stalls under investigation. |

---

## 14. Milestones

Detail: [design/milestones.md](design/milestones.md).

| M | What | Status |
|---|---|---|
| M0 Foundations | Typed IDs, CI, architecture tests, host, data layer | Done |
| M1 Proxy core | Snapshot, gate, YARP, TLS, H1/H2/H3, Cloudflare client IP | Done |
| M2 Config store | Commands, revisions, compiler, last-known-good | Done |
| M3 Vault and certificates | Vault, ACME, DNS-01, local CA | Done |
| M4 Admin backend | Identity, REST API, simulator, admin through the proxy | Done |
| M5 Dashboard and container | Dashboard, container, install docs | Done |
| M6 HSTS and logs | HSTS, `Server` header, request ID, access and error logs | Done |
| M7 Access lists, auth and WAF | Access lists, forward-auth, proof of work, rate limits, limits | Done |
| M8 Custom error pages | Custom pages, maintenance mode, upstream error replacement | Done |
| M9 Observability | Metrics, live tail, analytics, health, notifications | Done |
| M10 Interop | YAML, MCP, backup and restore, drafts | Done; the NPM importer is not built |
| M11 Release v0.1 | Image publishing, docs site, threat model, fuzz run, load tests | Open (threat model done) |

**Open items inside done milestones:** a local country database, bcrypt `htpasswd` import, native OIDC, a forward-auth trace that makes the real call, a warning for a rule no request can reach, per-route error pages, local CA root renewal, the browser H3 upgrade test.

**Later:**
- Native OIDC for the admin and per host.
- Bot heuristics and header rules.
- Per-tag RBAC.
- L4 streams.
- Declarative mode.
- **Fleet mode** (control plane and data planes).
- A country map and protection-over-time series.
- A plugin model for policies.
- An out-of-process vault.

---

## 15. Testing and quality

Detail: [design/testing.md](design/testing.md).

- **Unit** tests are table-driven, with golden files for the compiler. **Integration** tests use real Kestrel, real SQLite and a validating Pebble ACME server. Most use one shared host per class.
- **Architecture tests** (ArchUnitNET) enforce the dependency graph, the typed-ID rules and the hot-path namespace bans.
- **Zero-allocation assertions** guard every gate and response path.
- **Property tests** (CsCheck) check the path canonicaliser, glob matcher, rule sets, access lists, the forward-auth cache and the config pipeline against slow, obvious models.
- **Coverage-guided fuzzing** (SharpFuzz) covers the PROXY protocol, proof-of-work parsing, basic auth, client address headers, forward-auth answers, error pages, the access-log tail, the MCP sandbox protocol and YAML import. Committed corpora replay in CI on every commit.
- **Security:** a threat model with a gaps list, CodeQL, the NuGet audit, and an audit-chain verifier.

---

## 16. Deployment

Detail: [design/deployment.md](design/deployment.md).

- **Container:** multi-arch (amd64 and arm64), Ubuntu Noble with a pinned `libmsquic`, non-root, ports mapped 1:1 so `Alt-Svc` and redirects never see a translated port, one `/data` volume. The compose file publishes the admin on loopback by default.
- **Bare metal:** a self-contained publish with a systemd unit and `LoadCredential` for the master key.
- **Runtime:** Server GC with DATAS, tiered PGO, ReadyToRun. SQLite floor 3.35.
- **Fleet mode** (after v0.1): one binary with `--role all|control|data`. Data nodes connect out over mTLS, compile config locally, keep serving when the control plane is gone, and get certificate keys by vault-to-vault transfer. Config rollouts can be staged by node group. Single-node stays the default and gets none of this machinery.

---

## 17. Open questions

Proposed resolutions, pending owner sign-off unless marked decided.

| # | Question | Proposed |
|---|---|---|
| 1 | License | **Decided: Apache-2.0** (`LICENSE`, `NOTICE`). Adoption matters more for infra tools; AGPL only pays off if closed forks are a real worry. |
| 2 | UI kit | **MudBlazor**; revisit only if it gets in the way. |
| 3 | Admin port default | **HTTPS 9443 with the internal CA** + root-CA download + **plain-HTTP loopback-only fallback**. Never plain HTTP on the LAN. |
| 4 | Geo DB | **DB-IP Lite** by default (no account), MaxMind GeoLite2 optional with a license key. **Not in M7**: countries come from `CF-IPCountry` only; the database is a gap with a named seam (see waf.md, "Country rules"). |
| 5 | NPM importer | **After v0.1**: the data-model mapping is bigger than it looks. |
| 6 | L4 streams | **After 1.0**. Keep the HTTP/YARP story clean first. |

---

## 18. Changelog

The changelog moved to [../CHANGELOG.md](../CHANGELOG.md), newest first.
