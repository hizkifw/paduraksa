# Paduraksa — Design & Implementation Plan

> **Paduraksa**: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.
>
> Open-source, homelab-grade reverse proxy and lightweight WAF on **Kestrel + YARP**. You deploy it once and manage it from the dashboard, the REST API, MCP or the CLI. It covers what nginx-proxy-manager does, adds versioned config, an audit trail, real multi-user support and a hot path designed for speed from day one.

Status: **v0.8**: M0 in progress (see §18) · Target runtime: **.NET 10 (LTS)** · Single node, single process.

---

## 0. Goals / non-goals

**Goals**
1. **Performance.** The request hot path never touches the DB, takes no locks and allocates nothing in Paduraksa code that can be avoided. Config changes are published as immutable snapshots swapped atomically.
2. **UX.** A first-run wizard gets you from zero to a wildcard cert and your first host in under 5 minutes. Every change is previewed as a diff, versioned and reversible.
3. **Manage everything without a shell.** Dashboard, REST API, MCP and CLI are thin skins over **one** application layer, so features never drift between surfaces.
4. **Security by construction.** Typed IDs everywhere, DTO/entity separation, and a key-material trust boundary enforced by the compiler.
5. **Batteries included, kept small.** ACME (HTTP-01/DNS-01), uploaded/self-signed/local-CA certs, WAF primitives, error pages, robots.txt, metrics.

**Non-goals (for now)**
- OWASP CRS or ModSecurity-style deep inspection. The WAF is deliberately simple and fast.
- Clustering/HA. It's a single node, though state is easy to back up and restore.
- L4 (TCP/UDP) streams. These come later, after HTTP is solid (see §14).
- Native AOT. Blazor Server, EF Core and YARP rule it out today. We use ReadyToRun + Dynamic PGO instead.

---

## 1. Solution layout

```
Paduraksa.slnx
├─ src/
│  ├─ Paduraksa.Core            Typed IDs, primitives, Result types, capability interfaces (vault, clock)
│  ├─ Paduraksa.Ids.Generator   Roslyn incremental source generator for [TypedId] structs
│  ├─ Paduraksa.Contracts       DTOs for API/MCP/Dashboard/YAML. No EF, no entities.
│  ├─ Paduraksa.Data            EF Core (SQLite) entities, DbContext, migrations, converters
│  ├─ Paduraksa.Vault           TRUST BOUNDARY: keys, certs' private keys, secrets. Everything `internal`.
│  ├─ Paduraksa.Acme            RFC 8555 client + DNS providers. Signs via vault capabilities.
│  ├─ Paduraksa.Proxy           HOT PATH: runtime snapshot model, gate middleware, policies, TLS selector, YARP glue, metrics
│  ├─ Paduraksa.Application     Use cases: config commands, validation, revisions, audit, snapshot compiler, YAML, auth
│  ├─ Paduraksa.Admin           Admin web surface: auth, REST /api/v1, /mcp, /metrics, OpenAPI
│  ├─ Paduraksa.Dashboard       Blazor Server components (RCL)
│  └─ Paduraksa.Server          Composition root + CLI. Hosts the public and admin apps in one process. Binary: `paduraksa`
├─ tests/
│  ├─ Paduraksa.Tests.Unit
│  ├─ Paduraksa.Tests.Data          (EF Core mapping on real SQLite: typed IDs, pragmas, seeding)
│  ├─ Paduraksa.Tests.Integration   (real Kestrel, real SQLite, Pebble ACME test server in CI)
│  └─ Paduraksa.Tests.Architecture  (dependency rules, DTO rules, typed-ID rules)
└─ bench/Paduraksa.Benchmarks       (BenchmarkDotNet: hot-path policies, lookups, zero-alloc assertions)
```

### Dependency rules (enforced by architecture tests)

```
Core ◄── everything
Contracts ──► Core
Data ──► Core
Vault ──► Core                       (has its OWN store; never references Data)
Acme ──► Core                        (signing/creds via Core capability interfaces)
Proxy ──► Core                       (no Data, no Application, no EF: the hot path can't reach the DB)
Application ──► Core, Contracts, Data, Proxy, Acme
Admin, Dashboard ──► Application, Contracts   (never Data: UI/API can only see DTOs)
Server ──► everything (composition only)
```

---

## 2. Development rules → how they're enforced

| Rule | Mechanism |
|---|---|
| **Typed IDs all the way to EF** | `[TypedId]` source generator; EF conventions map every typed ID automatically; BannedApiAnalyzer bans `Guid.NewGuid()` repo-wide; an arch test fails if any `Contracts`/entity property named `*Id` is `Guid`/`string`/`long`. |
| **DTOs decoupled from entities** | `Admin`/`Dashboard` can't reference `Data` (project graph + arch test). Mapping is explicit (hand-written or Mapperly source-gen), never reflection auto-mapping. DTOs are `sealed record`s. Entities never get serialized (an arch test checks that no `Data` type appears in a JSON context). |
| **Trust boundary for key material** | `BannedSymbols.KeyMaterial.txt` bans private-key export/load APIs, `AesGcm` and `HMAC*` in **every project except `Paduraksa.Vault`** (already wired up in `Directory.Build.props`). The vault exposes *capabilities* (sign, decrypt-and-use, select-cert), never bytes. Its types are `internal`, and only the Server composition root registers it. |
| Warnings are errors, nullable on | `Directory.Build.props` |
| Central package versions | `Directory.Packages.props` |

---

## 3. Process & hosting model

One process, **two `WebApplication` instances** plus a non-web **core host**, one shared set of core singletons:

```
                    ┌──────────────────────── paduraksa process ────────────────────────┐
 :80  (H1)  ──────► │ PUBLIC APP                                                         │
 :443 (H1/H2/H3) ─► │  conn middleware ─► TLS(SNI) ─► Gate ─► host policies ─► YARP ─────┼─► upstreams
                    │          ▲                 ▲                                       │
                    │          └── SnapshotStore (volatile ref, immutable RuntimeSnapshot)│
                    │                            ▲ publish                               │
 :9443 (H1/H2) ───► │ ADMIN APP  Dashboard · /api/v1 · /mcp · /metrics ─► Application ──┤
                    │                                                     │   │          │
                    │                                   Data(SQLite) ◄────┘   └─► Vault  │
                    └────────────────────────────────────────────────────────────────────┘
```

Implemented in M0 (`src/Paduraksa.Server/Hosting`):
- **Shared singletons:** `SharedServices` accepts only ready-made instances and registers the *same* objects into every container. It disposes them exactly once, after all hosts have stopped. `PaduraksaOptions` itself is the shared instance, and `IOptions`/`IOptionsMonitor`/`IOptionsSnapshot`/`IOptionsFactory` all resolve to it (named options throw).
- **Core host:** a non-web host owns every once-per-process `IHostedService` (ACME scheduler, metric sampler, …). It starts first, so a snapshot exists before any port opens, and stops last. Web apps must not register once-per-process background services.
- **Startup:** if any host fails to start, the others are stopped and the process exits 2. A stop, a signal or a cancelled start token that arrives during startup aborts the start instead: whatever had started is stopped again and the process exits 0. A requested abort is logged at Information, never as an error.
- **Exit codes:** 0 = stopped on request, including during startup. 1 = a host stopped on its own, a host failed to stop, or disposal failed after an otherwise clean run. 2 = startup failed or the process couldn't be built. 3 = invalid config: failed validation or an unparsable appsettings file. Validation failures print a header plus one line per failure on stderr; any other failure to build or dispose the process prints a single line (`PADURAKSA_DEBUG=1` adds the stack trace).
- **Data directory:** a relative `Paduraksa:DataDirectory` resolves against the content root (where appsettings live, by default the app's base directory), never the working directory. A leading `~` is rejected because nothing expands it.
- **Shutdown:** `/readyz` switches to 503, public drains, then admin stops, then core stops, then shared singletons are disposed. One `ShutdownTimeout` (30 s) is shared by all three, so compose files need `stop_grace_period: 35s`.
- **Signals:** handled only in `PaduraksaHost.RunAsync`. A second signal hard-kills.
- **Listener pinning:** the web apps get a last config source that blanks `urls`/`http_ports`/`https_ports` and sets `preferHostingUrls=false`. Env vars and appsettings can't rebind listeners behind our back.

Why two apps and not one app with port filters:
- Admin endpoints **can't be routed** on public ports, because the public pipeline doesn't contain them. No reliance on `RequireHost` or port checks.
- Each pipeline gets its own middleware, auth and Kestrel limits (for example, admin allows large YAML uploads while public uses per-host limits).
- Shared singletons (`SnapshotStore`, `MetricsRegistry`, `Vault`, `IAcmeChallengeStore`, `HealthState`) are built once and registered **as instances** in both containers.

Ports (configurable, env-overridable):

| Purpose | Default | Notes |
|---|---|---|
| Public HTTP | 80/tcp | ACME HTTP-01, redirects, plain-HTTP hosts |
| Public HTTPS | 443/tcp + **443/udp** | H1/H2 over TCP, H3 over QUIC. Docs must stress mapping the UDP port. The container listens on 80/443 *directly* (no in-container remapping, see §16) so the advertised `Alt-Svc` port is correct. |
| Admin | 9443/tcp | HTTPS with an internal-CA cert. The wizard offers the root CA for download. Bind address and allowed CIDRs are configurable. Never plain HTTP on the LAN. |
| Admin fallback | loopback only, plain HTTP (opt-in, e.g. `127.0.0.1:9080`) | For headless/SSH-tunnel setups. Refuses to bind to anything but loopback. |

### 3.1 Exposing the admin through the proxy (opt-in)

Some people want `proxy.example.com` to be the admin UI, with the wildcard cert, SSO and WAF in front of it. Because the admin is a separate `WebApplication`, the public app reaches it like any other upstream:

- The admin app gets an extra **internal listener**: a Unix domain socket in `data/run/admin.sock` (0600), or `127.0.0.1:<ephemeral>` where UDS isn't available. The public app's YARP cluster `__admin` points at it (UDS via `SocketsHttpHandler.ConnectCallback`). WebSockets for the Blazor circuit pass through YARP unchanged.
- The admin app trusts `X-Forwarded-*` **only** on that internal listener, and the public side always overwrites (never appends to) those headers before forwarding.
- Cookies: `__Host-` prefixed, which browsers only accept with `Secure`, `Path=/` and no `Domain`, plus `SameSite=Strict`. They're therefore bound to whichever origin the admin is served from, so sessions on `https://proxy.example.com` and `https://lan-ip:9443` are independent. Antiforgery works unchanged because everything stays same-origin.
- The admin host is a normal `Host` (`Kind = Admin`): forced HTTPS, its own WAF/rate-limit/auth policy. It can't be deleted while it's the only way in (lock-out guard).


---

## 4. Typed IDs

### 4.1 Declaration

```csharp
[TypedId("host", IdKind.UuidV7)]   public readonly partial record struct HostId;
[TypedId("usr",  IdKind.UuidV7)]   public readonly partial record struct UserId;
[TypedId("chl",  IdKind.UuidV4)]   public readonly partial record struct PowChallengeId; // public-facing, no timestamp leak
[TypedId("aud",  IdKind.Int64Ordered)] public readonly partial record struct AuditEventId; // high-volume
```

The generator emits for each:
- `Value` (`Guid` or `long`), `New()`, `Empty`/`IsEmpty`, `static abstract` contract `ITypedId<TSelf>`
- `ToString()` → `host_0J9zb3xQk1Lr8m2V7aYc4D`, plus `ISpanFormattable`, `IUtf8SpanFormattable`, `ISpanParsable`/`IUtf8SpanParsable` (prefix-validated, allocation-free)
- `System.Text.Json` converter (registered in a source-gen `JsonSerializerContext`), `TypeConverter` (Blazor routing and Minimal API binding), YamlDotNet converter
- EF Core `ValueConverter` + `ValueComparer`, applied by **convention** (`ConfigureConventions` → `AddTypedIdConventions()`, which recognizes `ITypedId<,>` member types during EF's property discovery), so no per-property config
- OpenAPI schema transformer: `{ type: string, pattern: "^host_[0-9A-Za-z]{22}$", example: … }`

### 4.2 Representations

| Kind | Use for | String form | DB storage |
|---|---|---|---|
| `UuidV7` (default) | All config entities, users, tokens, revisions, certs, orders | `prefix_` + 22-char base62 | 16-byte **BLOB, big-endian** (generated by our own RFC 9562 `UuidV7Generator`, which uses a 12-bit per-millisecond counter so IDs created within one ms still sort in creation order; `Guid.CreateVersion7()` fills those bits randomly and is banned. Written with `Guid.TryWriteBytes(span, bigEndian: true, out _)`) so byte order = time order and indexes stay append-mostly. Never EF's default TEXT GUIDs. |
| `UuidV4` | IDs that are publicly visible where creation time shouldn't leak (challenge IDs, share links) | `prefix_` + 22 base62 | 16-byte BLOB |
| `Int64Ordered` | High-volume append-only tables (audit events, metric rollups, access-log index) | `prefix_` + 11 base62 | `INTEGER PRIMARY KEY` (SQLite rowid: clustered, fastest). Layout: 44-bit ms timestamp (epoch 2025-01-01 UTC) + 19-bit monotonic counter seeded randomly at startup, sign bit always 0. Parsing rejects values above `long.MaxValue`. **Predictable, so internal/admin-only:** never use it for anything exposed publicly. Collision-free on a single node, provided the Data layer seeds the generator at startup with `Int64OrderedIdGenerator.Observe(MAX(id))`, which protects against a clock that's behind the last persisted ID. |

Base62 uses the alphabet `0-9A-Za-z` (ASCII order), **fixed width and left-padded**, so ordinal string sort equals numeric sort. Sorting IDs as strings in the UI or API gives the same order as the DB.

Generator notes (M0):
- Generated `New()` uses the process-wide generators on `TimeProvider.System`. Revisit when a clock abstraction lands.
- Every **source-generated** `JsonSerializerContext` that serializes typed IDs must register `TypedIdJsonConverterFactory` via `[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]`. The STJ source generator can't see the `[JsonConverter]` attribute our generator emits, and without the factory IDs serialize as `{}`.
- The generator is added as an analyzer to every project through `Directory.Build.props`. Prefix uniqueness is checked per compilation (PDKID004) and across all src assemblies by an architecture test.
- EF value converters can't call static abstract members inside expression trees (CS8927). Generic converters go through a plain generic helper (`static T Wrap<T, TV>(TV v) => T.FromValue(v)`).
- EF mapping (S4, `Paduraksa.Data.Ids`): `configurationBuilder.AddTypedIdConventions()` swaps in a `PropertyDiscoveryConvention` that accepts typed IDs, so they're found from the model's own CLR member types (no scanning or type lists) in entities and complex types, as keys, FKs and nullables. Typed-ID properties are `ValueGenerated.Never`. **Inside JSON complex types IDs are stored as their canonical string** (`host_…`), not BLOB/INTEGER: the document stays readable and JSON members compare against a string parameter. ID lists (`List<RouteId>`) are primitive collections of canonical strings, as a column or inside JSON. Owned types mapped to JSON are rejected at model build; use complex types. **Hard rule: a JSON ID (TEXT) never equals a column ID (BLOB/INTEGER) in SQL**, so references inside JSON settings are resolved in memory by the compiler, never joined or compared in SQL. LINQ `==`/`!=`/`<`/`>`/`CompareTo`/`Contains`/`OrderBy` on IDs translate; `id.Value` doesn't (EF can't see through a converter).

### 4.3 Secrets that look like IDs (but aren't)

API tokens: `pdk_<tokenId base62>_<32 random bytes base62>_<crc32 base62>`. The prefix and checksum let secret scanners (GitHub, gitleaks) detect leaked tokens. The DB stores `tokenId` and `SHA-256(secret)`. A fast hash is fine for 256-bit entropy, and lookup by ID makes verification constant-time. Session cookies are opaque random 256-bit values, protected by Data Protection, with only the hash stored.

---

## 5. Data model (SQLite, EF Core 10)

Two database files, **deliberately separate**:
- `data/paduraksa.db`: config, users, revisions, audit, metric rollups. Owned by `Paduraksa.Data`.
- `data/vault.db`: encrypted key material and secrets. Owned **only** by `Paduraksa.Vault`, through its own internal DbContext. Backing up or exporting config never sweeps up keys by accident.

SQLite settings: WAL, `synchronous=NORMAL`, `foreign_keys=ON`, `busy_timeout`. A single writer is fine because all writes go through the Application layer. Reads use `IDbContextFactory` (also required for Blazor Server).

### 5.1 Core config entities

| Entity | Key fields |
|---|---|
| `GlobalSettings` (singleton row) | `Defaults: HostSettings` (JSON complex type), default-host behaviour, trusted proxies, admin settings, ACME defaults |
| `Profile` (`prof_`) | Named preset of `HostSettings` overrides, e.g. "Public app", "LAN only", "Media server" |
| `Host` (`host_`) | `Kind` (Proxy, Redirect, Respond, Admin), `Enabled`, `ProfileId?`, `Settings: HostSettings` (overrides), `CertificateBinding` (Auto / specific `CertificateId` / None), `Tags`, `Notes`, concurrency token |
| `HostName` (`hn_`) | Host FK, `Name`, unique index `COLLATE NOCASE`. Normalized on write: trimmed, lowercased, IDN → punycode (`IdnMapping`), ≤ 253 chars, labels ≤ 63, wildcard only as the leftmost `*.` label, no trailing dot. |
| `Route` (`rte_`) | Host FK, `Order`, `Match` (path prefix/exact/regex, methods, headers), `UpstreamId`, per-route `Settings` overrides, transforms. Equivalent to NPM "custom locations". |
| `Upstream` (`ups_`) | LB policy, health check config, HTTP version, TLS-to-upstream (SNI, verify, custom CA, optional **client cert** `CertificateId` for mTLS), timeouts, affinity, **connection/DNS refresh**: `PooledConnectionLifetime` (default 60 s, so a restarted container with a new IP is picked up quickly), `PooledConnectionIdleTimeout`, connect timeout. A destination that fails to connect is passively marked unhealthy, which forces new connections (and so a fresh DNS lookup) on retry. |
| `Destination` (`dst_`) | Upstream FK, address, weight, enabled |
| `AccessList` (`acl_`) | CIDR allow/deny, basic-auth users (PBKDF2/Argon2 hashes) |
| `RuleSet` (`rset_`) / `Rule` (`rule_`) | Path/UA/header/method/country deny rules, ordered |
| `ErrorPageSet` (`eps_`) | Per-status templates (HTML), assets |
| `AuthProvider` (`authp_`) | ForwardAuth (authentik/Authelia/oauth2-proxy presets), OIDC (later) |
| `Certificate` (`crt_`) | Source (Acme/Uploaded/SelfSigned/LocalCa), SANs, NotBefore/NotAfter, issuer, public chain (PEM), fingerprint, renewal state. **No private key.** |
| `AcmeAccount` (`acct_`) | Directory URL (LE / LE staging / ZeroSSL / Google / custom), contact, EAB key **ref**, status |
| `AcmeOrder` (`ord_`) | Certificate FK, status, challenges, error log, ARI window |
| `DnsProvider` (`dnsp_`) | Type (Cloudflare, deSEC, …), non-secret settings, `SecretRef` (`sec_`) into the vault |
| `NotificationChannel` (`ntf_`) | ntfy/Gotify/Discord/webhook/email. Events: renewal failure, **expiry warning for every cert regardless of source** (uploaded, self-signed, local CA leaves *and* the local CA root; default 30/14/7/1 days), upstream down/up, login anomalies. |

### 5.2 Identity & governance

| Entity | Notes |
|---|---|
| `User` (`usr_`) | Username, display name, password hash (ASP.NET `PasswordHasher` v3 PBKDF2-SHA512 or Argon2id), TOTP secret **ref** (vault), lockout, disabled |
| `Passkey` (`pk_`) | WebAuthn credentials (public keys only, so these live in config DB). Built on .NET 10 passkey primitives. |
| `RoleAssignment` | User ↔ Role (Owner, Admin, Operator, Viewer, Auditor), scope = global (per-tag scopes later) |
| `ApiToken` (`tok_`) | Owner, scopes (`hosts:read`, `hosts:write`, `certs:*`, `mcp`, …), expiry, last used, hash |
| `Session` (`ses_`) | Hash, user, IP, UA, created/last seen, revocable from the UI |
| `ExternalLogin` | OIDC subject ↔ user (later) |
| `ConfigRevision` (`rev_`) | `Seq` (display number "#42"), parent rev, author, channel, message, `Document` (canonical config, Brotli-compressed), SHA-256, summary of changed objects |
| `AuditEvent` (`aud_`, Int64Ordered) | Timestamp, actor (user/token/system), channel (dashboard/api/mcp/cli/system), action, target typed-ID string, outcome, IP, details JSON, **hash chain** (`prevHash`, `hash`) for tamper evidence |

### 5.3 Global defaults → profile → host → route

`HostSettings` is one record with every tunable as `Override<T>` (`Inherit` | `Value(T)`), stored as an EF Core 10 JSON complex type. New settings mean no schema migration, just a defaulted field. The **compiler** resolves the layers into a flat, fully concrete `EffectivePolicy`. IDs inside `HostSettings` (JSON, stored as strings) are resolved by the compiler in memory, never by SQL joins or comparisons against ID columns (§4.2). The dashboard shows where each value comes from ("inherited from Global" or "from profile *LAN only*") with a one-click "override here" or "reset to inherited".

Host kinds in v1: **Proxy** (YARP), **Redirect** (precomputed target), **Respond** (fixed status + body from the compiled snapshot, e.g. 404/410/"coming soon"), **Admin** (§3.1). Serving files from disk (**Static**) is **cut from v1**: it needs path-traversal, symlink, MIME and caching semantics designed properly. When it comes back, the plan is a chroot-like root under `data/www/<hostId>`, symlinks disabled, `PhysicalFileProvider` with `ExclusionFilters.Sensitive`, ETag/Last-Modified, precompressed `.br/.gz` siblings.

Examples of `HostSettings` fields: force HTTPS, HSTS, HTTP/2/3 enable, min TLS version, request body limit, header timeouts, upstream timeouts, compression, rate-limit policy, geo policy, IP access list, rule sets, auth provider, error page set, robots mode, security headers preset, websocket, buffering, custom request/response headers, `X-Forwarded-*` behaviour.

---

## 6. Config lifecycle: commands, revisions, snapshots

### 6.1 Write path (cold, may allocate, may lock)

```
Dashboard / API / MCP / CLI / YAML import
        │  (Contracts DTO + baseRevision)
        ▼
Application command handler
  1. authorize (role/scope)                     → 403 + audit
  2. load current config, apply mutation
  3. validate (field rules + cross-object: dup hostnames, dangling refs, cert coverage, regex compile w/ timeout)
  4. COMPILE a candidate RuntimeSnapshot in memory   ← fails here, nothing is persisted
  5. BEGIN TX: write entities, ConfigRevision(doc, diff), AuditEvent; check concurrency token; COMMIT
  6. PUBLISH (single writer, coalescing to the latest candidate; see §6.2):
     a. swap the YARP provider config, cancel the old change token
     b. await YARP ConfigurationApplied / ConfigurationApplyingFailed (matched by RevisionId, timeout → alert)
     c. capture YARP's new Endpoint objects into the candidate's slot → Endpoint table
     d. SnapshotStore swap                       ← atomic reference swap; requests see it from here on
  7. notify: change feed (dashboard live update)
```

- **Optimistic concurrency.** Each entity has a concurrency token, and commands carry `baseRevision`. Two admins editing the same host get a conflict dialog with a diff instead of a silent overwrite.
- **Dry-run.** Every command supports `dryRun: true`, which runs steps 1–4 and returns the diff plus validation results. The dashboard's "Review changes" sheet, MCP's `preview_*` tools and `import --dry-run` all use this.
- **Rollback.** Load an old revision's document, run it through the *import* path (validate, compile) and commit it as a **new** revision ("Rollback to #37"). History is never rewritten.
- **Diff.** Structural diff over the canonical document (stable ordering by typed ID), rendered as an object-level change list plus a YAML side-by-side view.
- YARP's own `IConfigValidator` runs in step 4, so an invalid YARP config fails before commit. YARP otherwise swallows invalid configs: it logs and keeps the old one.
- **Startup = recompile the latest revision.** The DB's latest committed revision is always the source of truth, and the process compiles it on every start. A crash between step 5 (commit) and step 6 (publish) therefore costs nothing except that the change goes live on the next start. (In-process, a failed publish after a commit is a bug; it raises an alert and retries.)
- **Last-known-good.** After each successful publish, the compiled snapshot's *source document* is also written to `data/last-good.json`. This file is only a fallback: if the DB is unreadable or migration fails at startup, the proxy still comes up serving traffic while the admin UI shows a degraded banner.

### 6.2 RuntimeSnapshot (immutable, built once, read lock-free)

```csharp
public sealed class RuntimeSnapshot
{
    public required long Generation { get; init; }
    public required ConfigRevisionId Revision { get; init; }

    // Host resolution: exact names + wildcard suffixes, case-insensitive,
    // looked up via FrozenDictionary.GetAlternateLookup<ReadOnlySpan<char>>() → no string slicing.
    public required HostTable Hosts { get; init; }

    public required CompiledHost[] HostsByIndex { get; init; }     // dense index used by metrics arrays
    public required CertificateTable Certificates { get; init; }   // SNI → SslStreamCertificateContext (prebuilt chains)
    public required GlobalGate Global { get; init; }               // global IP deny trie, trusted proxies, conn limits
    public required IProxyConfig Yarp { get; init; }               // path-only routes + clusters, pure function of content
    public required Endpoint[] YarpEndpoints { get; init; }        // RouteSlot → YARP Endpoint, filled at publish step 6c
    public required DefaultHostBehaviour Default { get; init; }
}

public sealed class CompiledHost
{
    public required int Index { get; init; }                       // position in THIS snapshot only
    public required HostId Id { get; init; }
    public required MetricSlot Metrics { get; init; }              // stable across generations (§6.3)
    public required IRequestPolicy[] Policies { get; init; }       // ordered, already specialised
    public required ReadOnlyMemory<byte> RobotsTxt { get; init; }  // pre-encoded UTF-8, or empty = passthrough
    public required ErrorPages ErrorPages { get; init; }           // pre-rendered byte segments
    public required RedirectSpec? HttpsRedirect { get; init; }
    // ...
}
```

`SnapshotStore` is just `volatile RuntimeSnapshot _current` plus `Publish` (Interlocked.Exchange, then fire change token). Each request reads the reference **once** at the start and uses only that snapshot, so a request never sees half of one config and half of another.

YARP integration (decided by spike S2, `docs/spikes/S2-yarp-reload.md`): **the gate picks the YARP endpoint itself, and ASP.NET routing never matches hosts.**
- **IDs:** YARP route, cluster and destination IDs are stable typed-ID strings, with no generation or revision in them. The YARP config is a **pure function of content**, so an edit that only touches policy is a YARP no-op and costs ≈ nothing.
- **Routes carry no Host match.** Each YARP route is path-only (or a catch-all per cluster) and exists only to give YARP an endpoint with the right cluster and transforms. Host-header matching was ~90% of the reload cost (500 hosts: 15 ms → 1.9 ms; 2000 hosts: 64 ms → 11 ms).
- **The gate resolves host and route** from its snapshot `S` (a per-host ordered route list: prefix/exact/regex, NPM-style `Order`). It takes the policies **and** the YARP `Endpoint` from the same `S`, then calls `HttpContext.SetEndpoint(...)` *before* `UseRouting`, so endpoint routing skips matching. A request therefore can't mix generations, and there is no version check, no snapshot ring and no 503 path. It costs 0 B in the gate. S2 measured gate entry → proxy pipeline at 152 B/request, against 256 B with Host matching.
- **Publish order** (§6.1 step 6): YARP applies first, then the endpoint table is captured from `EndpointDataSource` (keyed by route ID), then the snapshot is swapped. Until the swap, requests keep using the old `S` with its old `Endpoint` objects. S2 showed those keep proxying correctly even after YARP removed or replaced them, following YARP's live cluster state, so in-flight requests are fine.
- **Custom pipeline:** we use `MapReverseProxy(Action<IReverseProxyApplicationBuilder>)`, which replaces YARP's default pipeline, so **session affinity, load balancing and passive health must be added back explicitly** (`UseSessionAffinity()`, `UseLoadBalancing()`, `UsePassiveHealthChecks()`). Without them, passive health silently does nothing.
- **Client reuse:** a custom `IForwarderHttpClientFactory` must reuse the existing client when cluster config is unchanged. A new `HttpClient` counts as a cluster change and rebuilds every route on it.
- `ConfigureEndpoints` metadata is **not** used. S2 showed YARP only re-runs it for new or changed routes, so unchanged routes would keep stale metadata.

### 6.3 State that survives a publish

Snapshots are immutable and are thrown away on every publish, so **mutable runtime state must never live inside one**. Otherwise every config edit would silently reset rate limits and break metric series. Long-lived state sits in process-lifetime singletons (`RuntimeState`). Snapshots only hold *keys* into it, and those keys are derived from **stable identities**, never from array positions.

| State | Lives in | Keyed by | On publish |
|---|---|---|---|
| Rate-limit buckets | One global fixed-size bucket table (§7.3) | key long = **16-bit policy slot** (stable, from an append-only `RateLimitPolicyId → slot` registry, like metric slots) + **48-bit hash** of the partition key (IP/64, IP+path, header) | Carried over untouched. If a policy's rate/burst changed, the new parameters apply on each bucket's next touch (tokens clamped to the new burst). Deleted policies' buckets are cleared by the sweeper (unknown slot means clear). |
| Per-IP connection counts | Global connection table | client IP | Not snapshot-related at all. Only the *limit* comes from the snapshot. |
| Forward-auth decision cache | Per-provider caches | `AuthProviderId` + provider **config hash** | Kept if the provider's effective config is unchanged, otherwise dropped (a changed auth config must never reuse old decisions). |
| Basic-auth verify cache | Global | `AccessListId` + config hash + credential hash + IP | Same rule as forward-auth. |
| Metrics counters/histograms | `MetricsRegistry` | **`MetricSlot`**, allocated per typed ID (`HostId`, `RouteId`, `UpstreamId`, `DestinationId`) by an append-only slot registry | The compiler asks the registry for each ID's slot, so the same host keeps the same slot forever. Slots of deleted objects are freed after a grace period (one rollup interval). |
| Persisted metric rollups | SQLite | **typed ID** + timestamp, never slot or index | n/a |
| YARP health state | YARP (per destination) | ClusterId + DestinationId (typed-ID strings) | Verified in S2: state survives every reload that keeps the IDs, including changes to the cluster's own policies. **Pitfalls:** removing and re-adding a destination resets it to *Unknown*, which YARP treats as routable, and changing a destination's address under the same ID keeps the old verdict. So rollback and import must keep destination IDs. Optionally, key destinations as `{DestinationId}~{hash8(Address, Host, Health)}` so an address change gets fresh state. This only fixes a stuck passive-unhealthy verdict, since the new key starts as Unknown, and it resets affinity for that destination. |
| ACME HTTP-01 tokens | `IAcmeChallengeStore` | token | Independent of config snapshots. |

Spike S6 turns this into a test: N generations are published under load, and it asserts that rate-limit decisions, counter totals and YARP destination health are all continuous.


---

## 7. Hot path

### 7.1 Budget & rules
- No DB, no `await` on anything except I/O to client/upstream, no locks (`lock`, `SemaphoreSlim`), no LINQ, no closures, no `string` building, no regex with backtracking.
- Per-request allocations in Paduraksa code: **0 B** steady state on keep-alive connections. Kestrel/YARP's own allocations are out of scope but measured. Enforced by benchmark tests asserting `GC.GetAllocatedBytesForCurrentThread()` deltas.
- Precompute everything at compile time: byte arrays for responses/headers, `SearchValues<string>` for substring sets, CIDR tries, country bitsets, redirect targets.

### 7.2 Request pipeline

```
A. Connection (TCP: Kestrel ListenOptions.Use. QUIC has no such hook, so the same checks run inside the
   QUIC TlsHandshakeCallbackOptions callback, where Connection.RemoteEndPoint is available; see S1)
   • PROXY protocol (opt-in per listener, TCP only): parsed HERE, before TLS, so the real client IP
     feeds connection limits, IP deny and SNI-stage logic. v1 (text) and v2 (binary) both accepted.
     Only from configured trusted source CIDRs; a header from anyone else = connection dropped.
     Header must arrive within a short timeout. When enabled on a listener it is mandatory, never optional.
   • global IP deny (CIDR trie over UInt128) → abort before TLS. Normalise IPv4-mapped IPv6 first:
     on a dual-mode [::] listener TCP reports ::ffff:a.b.c.d while QUIC reports a.b.c.d
   • per-IP concurrent-connection cap (fixed-size table, CAS)
   • compute ClientInfo once per connection (IP, country index) → connection feature

B. TLS handshake (TlsHandshakeCallbackOptions on both TCP and QUIC → per-SNI SslServerAuthenticationOptions)
   • SNI → snapshot → PREBUILT SslServerAuthenticationOptions, deduplicated on (cert, host TLS policy, transport).
     Each holds a shared SslStreamCertificateContext.Create(cert, chain, offline: true): the only way the
     intermediate is reliably sent. Without it, TCP may fetch it via AIA mid-handshake and QUIC sends the leaf only.
   • ApplicationProtocols ALWAYS set explicitly ([h2, http/1.1] or the host's ALPN on TCP, [h3] on QUIC).
     Kestrel writes ALPN into a returned options object whose list is null (both transports), which
     silently breaks the other transport if instances are shared. A unit test asserts non-null ALPN.
   • unknown SNI: default cert, or reject by THROWING from the callback (client sees EOF / TLS alert; server
     stays healthy). Count rejections ourselves: QUIC logs nothing.

C. Gate middleware (first in pipeline)
   • snapshot = SnapshotStore.Current (one volatile read)
   • port 80: /.well-known/acme-challenge/* → IAcmeChallengeStore (FrozenDictionary swap) → 200
   • resolve client IP: PROXY-protocol IP from stage A if present, else trusted-proxy-aware
     X-Forwarded-For / CF-Connecting-IP (only when the TCP peer is in the trusted-proxies list;
     native Cloudflare handling in §7.6)
   • resolve host (span-based lookup, no string allocation); unknown → Default behaviour
   • resolve route (per-host ordered list) → policies + YARP Endpoint from the same snapshot;
     HttpContext.SetEndpoint(endpoint) so the later UseRouting skips matching
   • HTTP→HTTPS redirect / Redirect and Respond hosts answered here from precomputed bytes

D. Host policy chain (IRequestPolicy[], sealed classes, evaluated in order)
   IpAccess → Geo → RateLimit → RequestLimits → PathRules → UserAgentRules → Robots →
   Auth (basic / forward-auth / OIDC) → [future: PoW gate, bot heuristics, header rules]
   each returns PolicyDecision { Continue | Respond(status, page) | Challenge | Drop } as a struct

E. YARP (endpoint already set by the gate → affinity → load balancing → passive health → forwarder)
   with Paduraksa transforms
   • X-Forwarded-*, X-Real-IP, request ID, security headers, custom headers (precompiled transforms)
   • upstream errors / no healthy destinations → custom error pages

F. Telemetry (always on, lock-free): counters + latency histogram per host / status class / upstream
```

### 7.3 WAF building blocks (all compiled per snapshot)

| Feature | Implementation |
|---|---|
| IP allow/deny | Binary radix trie over `UInt128` (IPv4-mapped), immutable. IPv6 rate keys are masked to /64. |
| Geo blocking | Country DB (DB-IP Lite: CC-BY, no key; MaxMind GeoLite2 optional with a license key) compiled into sorted range arrays and binary-searched. Country is resolved **once per connection** (or per request when behind a trusted proxy). Policy = `ulong[4]` bitset of blocked countries. DB auto-update job. |
| Rate limiting | Fixed-size open-addressed table (power of two, default 256 Ki entries ≈ 4 MiB). Each entry is two `long`s: a 64-bit **key fingerprint** (claimed with CAS, linear probing over at most 8 slots, else the oldest of those is evicted) and a packed **state** updated with a CAS loop. State layout: **bits 63–24 = last-refill time** in ms since process start (40 bits ≈ 34.8 years, so no wraparound handling needed); **bits 23–0 = tokens in 1/16 units** (unsigned 20.4 fixed point: max ≈ 1,048,575 tokens of burst, refill precision 1/16 token). The refill rate is a policy parameter in tokens/s, applied as `elapsedMs × rate × 16 / 1000` with saturation at burst. A background sweeper reads the **policy slot** from the high 16 bits of each key (the fingerprint itself can't be inverted, so the slot is stored alongside it). It looks up that policy's current full-refill window (burst / rate) and clears entries untouched for longer than that, or immediately if the slot no longer maps to a live policy. A 48-bit hash in a 256 Ki table makes false sharing between partition keys negligible (~2⁻³⁰ per probe). Bounded memory, so a flood of unique IPs can't exhaust RAM. Collisions only make limits slightly stricter. Policies per host or route: req/s + burst, keyed by IP / IP+path / header. Returns 429 + `Retry-After`. |
| Request size limits | `IHttpMaxRequestBodySizeFeature` per host/route. Header count/size and URL length are checked cheaply in the gate. Global Kestrel limits act as the outer bound. |
| Path deny rules | Exact/prefix in a trie. Glob compiled to a small matcher. Regex as an escape hatch: `RegexOptions.NonBacktracking` (ReDoS-safe) with a compile-time validation timeout. |
| User-agent deny | `SearchValues<string>` (vectorised multi-substring, case-insensitive), plus presets ("AI crawlers", "known scanners", "empty UA"). |
| Robots.txt | Per host: passthrough / managed (preset: disallow all, block AI crawlers, custom text). Served from pre-encoded bytes. |
| Error pages | Per status (and "upstream down" / "blocked" / "rate limited" / "maintenance"). Templates pre-split into byte segments at compile time, with variables (`{status}`, `{requestId}`, `{host}`) written straight into the response `PipeWriter`. **Replacing upstream error bodies** (opt-in per host) is decided at **response-header time** from status + content type. The upstream body is drained and discarded, never buffered. It's never applied to `101` upgrades, `text/event-stream`, gRPC (`application/grpc*`), or responses whose headers have already been flushed. |
| Maintenance mode | Per host, a toggle with a custom page and an IP bypass list. |

### 7.4 Observability (no locks on the hot path)
- **Counters:** `long[]` indexed by `metricSlot * K + metric` (stable slots, §6.3; never the snapshot's host index), striped per core (cache-line padded) to avoid contention. Summed only when read.
- **Latency:** fixed log-linear histogram buckets (≈ 1 µs → 60 s, ~120 buckets), updated with `Interlocked.Increment`. Two histograms are kept: total time and upstream time, so the proxy's own overhead is visible.
- **Exposed via** `System.Diagnostics.Metrics` (`ObservableCounter`/`ObservableGauge` callbacks read the arrays), so OpenTelemetry and Prometheus `/metrics` (admin port) work out of the box.
- **Dashboard live data:** a 1 s sampler writes into in-memory ring buffers (last ~1 h). A background job persists 1-minute rollups to SQLite **keyed by typed ID** (`HostId`, `UpstreamId`, …), with configurable retention.
- **Upstream health:** YARP active (HTTP probe, interval, path, expected status) and passive (failure-rate) health checks. State changes go to the audit log + notifications and appear as dashboard status dots.
- **Access log (optional):** a fixed-size ring buffer of structs referencing existing strings (no copies), drained by a background writer to JSONL/CLF with rotation. It powers a **live tail** in the dashboard (filter by host/status/IP) and can be sampled.
- **Request ID:** `X-Request-Id` (honour incoming from trusted proxies or generate one), shown on error pages so users can report it.

### 7.5 HTTP/3 strategy

**Spike S1 (see `docs/spikes/S1-http3-sni.md`) showed that per-SNI selection and hot-swap work on Kestrel's QUIC listener in .NET 10**, through `ServerCertificateSelector`, `TlsHandshakeCallbackOptions` and `ServerCertificateSelectionCallback` alike. So every host that enables H3 gets it, and the single-H3-cert fallback isn't needed.

- **Listener layout:** two `Listen` calls on :443 (dual-mode `[::]`).
  - TCP: `Http1AndHttp2`, `DisableAltSvcHeader = true`, then the connection middleware (PROXY protocol, IP gate), then `TlsHandshakeCallbackOptions`.
  - UDP: `Http3` with its own `TlsHandshakeCallbackOptions`, which also performs the stage-A gate. It's added only if `QuicListener.IsSupported`.
- **Builder:** under `WebApplication.CreateSlimBuilder`, call `UseKestrelHttpsConfiguration()` and `UseQuic()`, otherwise H3 listeners fail at startup.
- **Still open:** a real browser upgrading to H3 (needs a trusted cert), carrying QUIC-callback ClientInfo through to the request, routable IPv6, and arm64 libmsquic. These are M1 acceptance tests.
- **Either way, `Alt-Svc` is emitted by Paduraksa, not Kestrel.** Kestrel's automatic header is disabled (`DisableAltSvcHeader`). Our header is precomputed per host, only for H3-eligible hosts, using the **public** HTTPS port from settings (default 443), not the bound port. That way a port-mapped deployment can't advertise the wrong port.
- H3 is off globally if `libmsquic` isn't available (`QuicListener.IsSupported`). `paduraksa doctor` reports this. The stock `aspnet` image doesn't ship it: install `libmsquic` from packages.microsoft.com (≈ 21 MB incl. `libnuma1`; tested 2.6.3 on Ubuntu 24.04).

### 7.6 Client IP behind Cloudflare (native)

Many homelabs sit behind Cloudflare's proxy (orange cloud) or a Cloudflare Tunnel. Getting the real client IP must be a toggle, not a hand-maintained CIDR list. It's a built-in **trusted-proxy provider**, which the generic trusted-proxy model (CIDRs + header) and future providers also use.

- **Modes:** `Off` · `Proxy` (Cloudflare connects to us directly) · `Tunnel` (a local `cloudflared` connects to us). Set globally and overridable per host (§5.3). The wizard asks "Is this server behind Cloudflare?" and offers to set it.
- **Who is trusted:**
  - `Proxy`: the TCP peer (after PROXY protocol, if any) must fall within **Cloudflare's published edge ranges**. These are shipped as a baked-in list and refreshed by a background job from `https://api.cloudflare.com/client/v4/ips`, falling back to `https://www.cloudflare.com/ips-v4` / `ips-v6`. They're stored with a fetch timestamp and compiled into the snapshot as a CIDR trie. A failed refresh keeps the last good list and raises a dashboard warning if it's stale (more than 30 days old).
  - `Tunnel`: the peer must be the configured `cloudflared` address (loopback, a Docker network CIDR or a specific IP). Edge ranges don't apply because the TCP peer is `cloudflared`.
- **Which header:** `CF-Connecting-IP` (single value, so there's no X-Forwarded-For chain parsing). Optionally `True-Client-IP` (Enterprise) or the right-most untrusted entry of `X-Forwarded-For` as a fallback. The value is parsed span-based into the same `UInt128` client-IP representation used everywhere, with IPv4-mapped addresses normalised. A missing or unparsable header from a trusted peer counts as a counter + debug log, and the peer IP is used.
- **Anti-spoofing:** when the peer is **not** trusted, `CF-Connecting-IP`, `True-Client-IP`, `CF-IPCountry`, `CF-Ray` and `CF-Visitor` are ignored, and they're **stripped before forwarding upstream**. Upstreams always get a clean `X-Forwarded-For` / `X-Real-IP` built from the resolved client IP.
- **What the real IP feeds:** WAF IP allow/deny, geo, rate-limit keys, access lists, forward-auth headers, access logs, metrics. Connection-stage limits (§7.2 A) necessarily see the Cloudflare edge IP. They must therefore exempt trusted Cloudflare peers from per-IP connection caps, or every visitor would share a handful of edge IPs.
- **Optional extras (per host or global):**
  - **Geo from `CF-IPCountry`:** when the peer is trusted, use it instead of the local Geo DB lookup. It's free and accurate. `XX`/`T1` map to "unknown".
  - **`CF-Visitor` scheme:** the original scheme for the HTTP→HTTPS redirect decision when Cloudflare connects to the origin over plain HTTP ("Flexible" SSL). This avoids redirect loops. The dashboard warns that Flexible mode is insecure.
  - **Origin lockdown:** reject (or 403) requests on a Cloudflare-mode host whose peer isn't Cloudflare (or `cloudflared`), so the origin can't be reached by IP to bypass Cloudflare's WAF. It's enforced at the connection stage when *all* hosts on a listener require it.
  - **Authenticated Origin Pulls:** request a client certificate from Cloudflare and verify it against Cloudflare's origin-pull CA (shipped, replaceable) on the TCP listener (§7.2 B, per-SNI `ClientCertificateRequired`). This is the strong form of origin lockdown.
  - **`CF-Ray`** is recorded in access logs and offered as a variable on error pages, next to our request ID, so issues can be correlated with the Cloudflare dashboard.
- **Certificates:** Cloudflare Origin CA certificates can be uploaded like any other (§9), with a hint that they're only trusted by Cloudflare. DNS-01 via the Cloudflare DNS provider (§9) works whether or not the proxy mode is on.
- **Request simulator** (§12) takes a "via Cloudflare" toggle, so users can see which IP and country the policies would see.

---

## 8. Vault (key-material trust boundary)

### 8.1 What it holds
TLS private keys (server certs **and** upstream mTLS client certs) · ACME account keys · EAB HMAC keys · DNS provider credentials · TOTP secrets · internal CA key · Data Protection key ring (cookies/antiforgery) · PoW/forward-auth HMAC keys · basic-auth isn't a secret (hashes only).

### 8.2 Public surface (capability interfaces in `Paduraksa.Core`, all implemented `internal` in Vault)

```csharp
interface IServerCertificateSource   // the ONE sanctioned egress: Kestrel needs the key in-process
{   ValueTask<SslStreamCertificateContext> LoadForServingAsync(CertificateId id, CancellationToken ct); }

interface IClientCertificateSource   // second sanctioned egress: upstream mTLS (SocketsHttpHandler needs the key in-process)
{   ValueTask<X509Certificate2> LoadForUpstreamAsync(CertificateId id, CancellationToken ct); }

interface ICertificateIssuance       // keys are generated inside, only CSR / public data leave
{   ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> sans, CancellationToken ct);
    ValueTask AttachChainAsync(CertificateId id, ReadOnlyMemory<byte> pemChain, CancellationToken ct);
    ValueTask<CertificateInfo> CreateSelfSignedAsync(...);   ValueTask<CertificateInfo> IssueFromLocalCaAsync(...); }

interface ICertificateImport         // uploads stream straight in; returns public info only
{   ValueTask<CertificateInfo> ImportAsync(Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken ct); }

interface IJwsSigner                 // ACME: vault signs the JWS; account key never leaves
{   ValueTask<JwsResult> SignAsync(AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken ct);
    JsonWebKeyPublic GetPublicJwk(AcmeAccountId id); }

interface ISecretHttpHandlerFactory  // DNS provider creds: vault-owned DelegatingHandler injects auth headers
{   HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how); }

interface IMacKey { bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag); int Sign(ReadOnlySpan<byte> data, Span<byte> tag); }
interface ISecretStore { ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, ...); ValueTask DeleteAsync(SecretId id, ...); SecretMetadata Describe(SecretId id); }
// Note: no Get(). Secrets go in, capabilities come out.
```

The `ISecretHttpHandlerFactory` pattern is the key trick. A DNS-provider client in `Paduraksa.Acme` builds an `HttpClient` over a vault-owned handler. The Cloudflare API token is added to the outgoing request *inside* the vault assembly and never exists as a value in Acme code. RFC2136 TSIG gets the same treatment through `IMacKey`.

### 8.3 At-rest encryption
- **Envelope encryption.** Each secret has its own random DEK (AES-256-GCM). DEKs are wrapped by the **KEK**. The AAD is `secretId || kind || version`, which stops anyone swapping ciphertexts between rows.
- **KEK source** in priority order: `PADURAKSA_MASTER_KEY_FILE` (Docker secrets / systemd `LoadCredential`), `PADURAKSA_MASTER_KEY` env, then auto-generated `data/master.key` (0600). The dashboard warns when the KEK sits in the same directory as `vault.db`.
- `paduraksa vault rotate-kek` re-wraps the DEKs.
- Plaintext buffers are pinned and zeroed (`CryptographicOperations.ZeroMemory`) after use, best effort.
- **Export.** Config export excludes secrets. An explicit "full backup" produces an archive with `vault.db` encrypted under a user passphrase (Argon2id-derived), and it is audited.
- **Future hardening:** the capability interfaces are the seam for moving the vault into a separate process (gRPC over a Unix socket with peer credentials) without changing any callers.

---

## 9. Certificates & ACME

- **Own ACME client (RFC 8555)** in `Paduraksa.Acme`, about 1.5k LOC. We write our own instead of using Certes/ACMESharp because the account key must stay in the vault (`IJwsSigner`), and we want first-class **ARI (RFC 9773)** and **ACME profiles** (e.g. Let's Encrypt `shortlived`).
- **Challenges:** HTTP-01 (served by the public app's gate from an in-memory store). DNS-01 (provider plugins + propagation check by querying authoritative NS directly). TLS-ALPN-01 later.
- **DNS providers (v1):** Cloudflare, deSEC, Hetzner, DigitalOcean, Porkbun, DuckDNS, Route53, RFC2136, acme-dns, plus "manual" (show the TXT record and wait for confirmation) and a webhook provider. Interface: `CreateTxtAsync / DeleteTxtAsync / ValidateCredentialsAsync`. Every provider needs a **"Test credentials"** button in the UI.
- **CAs:** Let's Encrypt (prod/staging), ZeroSSL (EAB), Google Trust Services (EAB), Buypass, custom directory URL.
- **Renewal:** a background scheduler uses the ARI window if offered, otherwise renews at ⅓ of lifetime remaining, with jitter, exponential backoff and rate-limit awareness. On success the snapshot is recompiled and published (zero-downtime cert swap). On failure: notification + dashboard banner.
- **Auto-matching:** hosts with `CertificateBinding = Auto` get the most specific valid cert covering their names (exact beats wildcard). The dashboard flags hosts without coverage and offers "Issue cert for these names".
- **Other sources:** uploaded PEM/PFX (straight to the vault through the API's streaming endpoint, so the dashboard upload goes to that endpoint rather than through the Blazor circuit). Self-signed. **Local CA**: an internal root generated in the vault, issuing leafs for LAN-only hosts, with the root cert downloadable for installing as trusted.
- **Serving:** `SslStreamCertificateContext.Create(cert, chain, offline: true)` is prebuilt per snapshot, so no chain building happens during a handshake. **OCSP stapling is out of scope for v1.** Let's Encrypt dropped OCSP in 2025, and we don't want to promise cross-platform server-side stapling behaviour.
- **Expiry monitoring** covers every certificate, not just ACME ones. Uploaded and self-signed certs have no renewal path, so they get the same 30/14/7/1-day notifications and a dashboard badge. Local-CA leaves can be re-issued in one click (or automatically).

---

## 10. Authentication & authorization

There are two separate domains:

### 10.1 Admin auth (dashboard / API / MCP)
- Local users: password + **TOTP** + **passkeys** (WebAuthn). ASP.NET Core 10's passkey support ships inside **Identity**, which we don't use. M4 includes a short check of whether its WebAuthn primitives work standalone; otherwise we use **Fido2NetLib** (fido2-net-lib). Budget for the latter. Lockout and rate limiting on login. Session list with revoke.
- Roles: **Owner** (users, vault, everything), **Admin** (all config), **Operator** (hosts/certs, no users/secrets), **Viewer** (read-only), **Auditor** (audit log + read-only). Implemented as permission sets, so custom roles can be added later.
- API: Bearer `pdk_…` tokens with scopes and expiry. MCP uses the same tokens, read-only by default, with the MCP authorization spec (OAuth) considered later.
- **Pluggable providers:** `IAdminAuthProvider` with Local (v1), OIDC (authentik/Keycloak/Pocket-ID: claim → role mapping, JIT provisioning) and trusted-header (when the admin itself sits behind forward-auth).
- **Hard rule for trusted-header auth:** it's accepted **only** on connections from the internal proxy listener (§3.1), from loopback, or from an explicitly configured CIDR. It can't be enabled while the admin port is bound to a non-loopback address without that CIDR. Validation rejects the config, and the request path re-checks the peer address every time. Otherwise anyone on the LAN could spoof `X-authentik-username`. When forwarding to the admin, the public side strips any client-supplied copies of the trusted headers.
- Hardening: strict CSP, antiforgery, `SameSite=Strict` cookies, Data Protection keys in the vault, optional admin IP allowlist, every login/failure audited.

### 10.2 Proxied-host auth (`AuthProvider` per host/route)
- **Access lists:** IP allow/deny + HTTP basic auth (hashes compiled into the snapshot; PBKDF2 verification cost is cached per credential+IP for a short TTL).
- **Forward-auth** (authentik, Authelia, oauth2-proxy presets): a sub-request to the auth endpoint with the original method/URI/headers. 2xx → copy the configured response headers upstream. 401/302 → relay to the client. A short-TTL decision cache keyed by the session-cookie hash keeps it cheap. This path allocates (an outbound HTTP call), which is accepted and documented.
- **Native OIDC per host** (later): Paduraksa as the relying party with a session cookie on the host's domain.

---

## 11. Admin surfaces

All four call the **same Application services** with the same DTOs, authorization and audit. The audit record notes which channel was used.

### 11.1 REST API (`/api/v1`, admin port)
- Minimal APIs, grouped per resource, built-in .NET 10 OpenAPI 3.1 document + Scalar UI at `/api/docs`.
- Conventions: typed-ID path params (`/hosts/{id:HostId}`), `If-Match: <revision>` for concurrency, `?dryRun=true`, RFC 9457 problem details, cursor pagination (UUIDv7/Int64Ordered IDs make cursors trivial).
- Resources: hosts, routes, upstreams, profiles, certificates, acme-accounts, dns-providers, access-lists, rule-sets, error-pages, settings, revisions (+ diff, rollback), audit, users, tokens, sessions, metrics, health, `config/export`, `config/import`, `simulate`.

### 11.2 MCP (`/mcp`, streamable HTTP, official `ModelContextProtocol.AspNetCore` SDK)
- Tools: `list_hosts`, `get_host`, `upsert_host`, `delete_host`, `list_certificates`, `request_certificate`, `get_metrics`, `get_upstream_health`, `query_audit`, `diff_revisions`, `preview_change`, `apply_change`, `rollback`, `simulate_request`, `tail_access_log`.
- Mutating tools require a `write` scope and support `dryRun`. Destructive ones need `confirm: true`. Resources: the current config as YAML, the revision list.

### 11.3 Health endpoints
`GET /healthz` (process alive, snapshot loaded) and `GET /readyz` (DB reachable, vault unlocked, listeners bound) on the admin port, plus the loopback fallback listener when enabled. Unauthenticated, no details beyond status. These are for **external** monitors and orchestrators (Uptime Kuma, Kubernetes probes, Gatus), which can probe them over HTTP without spawning anything. The image's own Docker `HEALTHCHECK` is the exception (§16).

### 11.4 CLI (same binary, `System.CommandLine`)
`paduraksa serve` (default) · `setup-token` · `admin reset-password <user>` · `admin disable-2fa <user>` · `config export|import [--dry-run]` · `vault rotate-kek` · `backup|restore` · `doctor` (ports, permissions, H3/libmsquic, DNS, clock skew) · `migrate`.

### 11.5 YAML import/export
- YamlDotNet with a static (source-generated) context. Format:
  ```yaml
  apiVersion: paduraksa.dev/v1
  kind: Config
  settings: { defaults: {...} }
  profiles: [...]
  upstreams: [...]
  hosts:
    - id: host_0J9zb3xQk1Lr8m2V7aYc4D   # optional on import; matched by id, else by first hostname
      names: [jellyfin.example.com]
      profile: media
      routes: [{ path: /, upstream: jellyfin }]
  certificates: [...]   # metadata + binding only; never key material
  ```
- Import goes through the normal command pipeline (validate → compile → diff → one revision). Modes: `merge` and `replace`.
- The same canonical document is what revisions store, so export = "current revision as YAML".
- Later: optional **GitOps mode**, where a watched file or git repo is authoritative and the UI becomes read-only for managed objects.

---

## 12. Dashboard (Blazor Server)

**Stack:** .NET 10 Blazor Web App, interactive server render mode, served on the admin app only. Component library: **MudBlazor** (most complete, good data grids/dialogs). Alternative: Fluent UI Blazor (see open questions). Charts: lightweight SVG sparklines rendered by our own components, with ApexCharts for the detail views.

**UX principles**
- Live by default: status dots, request rates and cert expiry update over the existing circuit. The sampler pushes deltas, with no polling and no per-user timers on the hot path.
- **Every change goes through review**: an edit form leads to a "Review changes" drawer (diff + validation + warnings like "this host will lose its certificate"), then Apply. Undo in a toast rolls back to the previous revision.
- Inheritance is visible: every setting shows a badge for *Inherited · Profile · Overridden*.
- Command palette (Ctrl/⌘K): jump to a host, "issue cert", "rollback", "tail logs for …".
- Keyboard-first forms, dark/light, mobile-friendly host list.

**Pages**
1. **Overview:** traffic, error rate, p50/p95 latency, top hosts, blocked requests, cert expiries, unhealthy upstreams, recent audit events.
2. **Hosts:** a searchable/taggable list with live sparkline, health and cert status. Host detail tabs: General · Routes · Upstream · TLS · Security (WAF) · Auth · Pages · Advanced · Metrics · History.
3. **Request simulator / "Why was this blocked?"** Enter a URL, IP, UA and headers and see the exact decision chain (host match → each policy → route → upstream). The same function is exposed to the API/MCP, and access-log entries link straight into it.
4. **Certificates:** list, issue (wizard), upload, local CA, renewal timeline, ACME order logs.
5. **Upstreams & health:** destinations, probe results, history.
6. **Security:** global rule sets, rate-limit policies, geo policy, presets, blocked-request explorer.
7. **Live logs:** streaming tail with filters.
8. **History:** revision timeline, diff viewer, rollback.
9. **Audit:** filterable, exportable, with hash-chain verification status.
10. **Settings:** global defaults, profiles, DNS providers, ACME accounts, notifications, users & roles, API tokens, sessions, backup/export.

### 12.1 First-run wizard
Security: until setup completes, the admin app serves **only** the wizard. That requires a one-time **setup token** printed to the logs and the console (`paduraksa setup-token` reprints it), which stops anyone else on the LAN from claiming the instance.

1. Setup token → 2. **Owner account** (+ optional passkey/TOTP) → 3. **Admin access** (port, bind, allowed CIDRs, admin hostname) → 4. **ACME account** (email, CA, ToS) → 5. **DNS provider** (pick, enter creds → live *Test*) → 6. **Wildcard certificate** (`example.com` + `*.example.com`, with live progress through order → TXT → propagation → issued) → 7. **Global defaults** (security preset: *Off / Balanced / Strict*, HSTS, default-host behaviour, robots preset) → 8. **First host** (optional, with an upstream reachability test) → Done (summary + "export config" + backup reminder).

Each step is skippable where it makes sense (e.g., HTTP-01 only, no DNS provider), and the wizard can be resumed.

---

## 13. Risks & spikes (do these first)

| # | Spike | Why | Exit criterion |
|---|---|---|---|
| S1 ✅ *(partial: browser upgrade pending)* | **HTTP/3 + per-SNI cert selection** | Unverified whether Kestrel's QUIC listener invokes per-SNI selection. libmsquic availability in the container. | Either per-SNI works on QUIC with hot-swap, or the single-H3-cert fallback (§7.5) is implemented. **Plus:** the browser actually upgrades to H3 in the Docker deployment (`Alt-Svc` carries the public port, UDP 443 reachable). |
| S2 ✅ *(design changed: gate selects the endpoint)* | **YARP config churn + metadata binding** | Many hosts (500+) and frequent publishes: endpoint rebuild cost. `ConfigureEndpoints(Action<IEndpointConventionBuilder, RouteConfig>)` exists in YARP 2.3.0, but we need to confirm it re-runs on every dynamic reload with the new `RouteConfig`. | Publish < 50 ms at 500 hosts; no request errors during swaps under load; typed metadata on every endpoint matches its generation (or the route-ID fallback in §6.2 is in place). |
| S3 | **Zero-alloc gate on Kestrel** | Request-scoped state without `Items`, span-based host lookup, feature reuse per connection. | BenchmarkDotNet + in-process test shows 0 B/request from Paduraksa code. |
| S4 ✅ *(partial: `FrozenDictionary` lookup not covered)* | **Typed IDs × EF Core 10 × SQLite** | Convention converters, BLOB big-endian GUIDs (`UuidV7Generator` + `TryWriteBytes(span, bigEndian: true, out _)`), JSON complex types containing typed IDs, LINQ translation of comparisons, `FrozenDictionary` `OrdinalIgnoreCase` alternate span lookup. | Round-trip, query and index-ordering tests pass. **Result:** done as real code in `Paduraksa.Data` (`tests/Paduraksa.Tests.Data`, real SQLite). Everything worked as planned: BLOB/INTEGER/JSON-string storage, `ORDER BY` = `CompareTo` = string order (same millisecond too), comparisons translate directly, and an Int64Ordered key is the rowid alias without `AUTOINCREMENT`. Changes: discovery replaces EF's `PropertyDiscoveryConvention` instead of scanning (§4); JSON stores strings. |
| S5 ✅ *(implemented as the real host)* | **Two WebApplications, one process** | Shared singletons, graceful shutdown order, logging, health. | Both apps start/stop cleanly; integration tests can host both. |
| S6 | **State continuity across publishes** (§6.3) | Rate limits and metrics must not reset on config edits. | Load test with publishes every 100 ms shows (a) continuous 429 behaviour, (b) monotonic counters, and (c) **YARP destination health preserved**: a destination marked unhealthy (active or passive) stays unhealthy across reloads that don't touch its cluster, and no healthy destination flaps. |
| S7 | **Baseline perf vs nginx/Caddy** | Know where we stand early. | `oha`/`wrk2` numbers on the same box recorded in `bench/README.md`. |

---

## 14. Milestones

Each milestone ends with a working, testable increment.

| M | Scope | Done when |
|---|---|---|
| **M0 Foundations** *(in progress: typed IDs, CI, arch tests, host, S1, S2, S4, S5 done; S3 left)* | Repo, CI (build, test, format, arch tests), `[TypedId]` generator + base62 + EF/JSON/Yaml converters, Result types, clock abstraction, spikes S1–S5 | Generator tests green; arch rules enforced in CI |
| **M1 Proxy core** | Snapshot model + store, gate middleware, host table, client-IP resolution incl. Cloudflare `Proxy`/`Tunnel` modes with baked-in ranges (§7.6), TLS SNI selection, YARP provider, H1/H2/H3, HTTP→HTTPS redirect, default host, hard-coded test config | Benchmarks + S7 baseline; S6 continuity test (stub rate-limit table + metrics + YARP health); zero-alloc tests pass |
| **M2 Config store** | EF model, migrations, Application command pipeline, validation, compiler, revisions, diff, rollback, audit (hash chain), last-known-good | Change → revision → live in < 100 ms; rollback works |
| **M3 Vault & certs** | KEK/DEK store, capabilities, upload, self-signed, local CA, ACME client (HTTP-01 → DNS-01 Cloudflare/deSEC), renewal scheduler, ARI | Pebble-based integration tests issue + renew; banned-API rules hold. Open item: decide where shared EF/SQLite infrastructure (typed-ID conventions, pragmas) lives, since Vault may reference only Core. |
| **M4 Admin backend** | Admin app, local users, cookies, TOTP, roles, API tokens, REST API + OpenAPI, first-run setup-token gate | API-only end-to-end setup possible |
| **M5 Dashboard + wizard** | Layout, hosts/certs/settings pages, review-diff flow, history, audit, wizard | New user from `docker run` to proxied host via UI only |
| **M6 WAF** | IP/geo/rate-limit/size/path/UA policies, Cloudflare range refresh job, `CF-IPCountry` geo, origin lockdown + Authenticated Origin Pulls (§7.6), presets, error pages, robots, maintenance mode, request simulator | Policy unit tests + simulator parity tests |
| **M7 Observability** | Counters, histograms, `/metrics`, health checks, notifications, live tail, rollups | Dashboard overview fully live |
| **M8 Interop** | YAML import/export, CLI, MCP server, backup/restore, NPM importer (nice to have) | Round-trip export → import yields an identical revision hash |
| **M9 Release v0.1** | Docker image (multi-arch), compose example, docs site, threat model doc, fuzzing (parsers, YAML, ACME responses), load tests | Tagged release |
| **Later** | Forward-auth → native OIDC (admin + per-host) · **PoW gate** (HMAC-signed, IP-bound, expiring tokens; difficulty per host; JS worker solver; vault `IMacKey`) · bot heuristics · header rules · per-tag RBAC · L4 streams · GitOps mode · plugin model for policies · out-of-process vault |

---

## 15. Testing & quality

- **Unit:** policies (table-driven), compiler (config → snapshot golden files), typed IDs (fuzz parse/format round-trip), YAML round-trip, diff.
- **Integration:** real Kestrel on ephemeral ports, real SQLite in a temp dir, **Pebble** + **pebble-challtestsrv** containers for ACME, a fake DNS provider. `HttpClient` with H2/H3 to verify SNI/ALPN.
- **Architecture** (`tests/Paduraksa.Tests.Architecture`), built on **ArchUnitNET** (maintained; NetArchTest is effectively unmaintained) plus plain reflection tests for the ID/DTO rules: dependency graph (§1), no `Data` types in `Contracts`/`Admin`/`Dashboard`, no untyped `*Id` properties, Vault types internal, hot-path namespace bans (`System.Linq`, `Regex` with backtracking, `DbContext`).
- **Performance:** BenchmarkDotNet for each policy + host lookup with `[MemoryDiagnoser]` asserting 0 B. A nightly `oha` load test comparing p50/p99 and RPS against the previous run and nginx.
- **Security:** threat model doc, dependency scanning, CodeQL, fuzzing of ACME/JSON/YAML/multipart parsers, an audit-chain verifier.

---

## 16. Deployment

- **Container:** `ghcr.io/<org>/paduraksa:<ver>`, multi-arch (amd64/arm64), Ubuntu Noble base + `libmsquic` (pinned), `ASPNETCORE_HTTP_PORTS` cleared, non-root. Listens on **80/443 (tcp+udp)/9443 inside the container**, mapped 1:1 in compose, so `Alt-Svc` and redirects never see a translated port. Non-root binding below 1024 works because Docker (20.10+) sets `net.ipv4.ip_unprivileged_port_start=0` in the container's network namespace. For `network_mode: host` or other runtimes, the image also gives the apphost `cap_net_bind_service` (`setcap`). If someone still remaps ports, the public ports in settings drive `Alt-Svc` and redirects. One volume: `/data`. Health: `/healthz` and `/readyz` (§11.3). The image's own Docker `HEALTHCHECK` uses `paduraksa doctor --liveness` (which calls `/healthz` over loopback) at a 30 s interval, because the image ships no curl/wget. That costs a short-lived process per probe, which is acceptable at that interval. External monitors should hit `/healthz` directly.
- **Bare metal:** self-contained single-file publish + systemd unit (`AmbientCapabilities=CAP_NET_BIND_SERVICE`, `LoadCredential=master.key`).
- **SQLite:** `Microsoft.Data.Sqlite` ships its own bundled SQLite (SQLitePCLRaw `e_sqlite3`), so the host's SQLite version doesn't matter. Required floor: **3.35** (`RETURNING`, used by EF Core 10; WAL long predates it). `doctor` prints the runtime version.
- **Runtime tuning:** Server GC with DATAS (adapts heap to load, good for small homelab boxes), `TieredPGO`, ReadyToRun.
- **Backups:** `paduraksa backup`, which takes an online SQLite backup of both DBs + KEK handling instructions.

---

## 17. Open questions (proposed resolutions, pending owner sign-off unless marked decided)

| # | Question | Proposed |
|---|---|---|
| 1 | License | **Decided: Apache-2.0** (`LICENSE`, `NOTICE`). Adoption matters more for infra tools; AGPL only pays off if closed forks are a real worry. |
| 2 | UI kit | **MudBlazor**; revisit only if it gets in the way. |
| 3 | Admin port default | **HTTPS 9443 with the internal CA** + root-CA download in the wizard + **plain-HTTP loopback-only fallback** (§3). Never plain HTTP on the LAN. |
| 4 | Geo DB | **DB-IP Lite** by default (no account), MaxMind GeoLite2 optional with a license key. |
| 5 | NPM importer | **After v0.1**: the data-model mapping is bigger than it looks. |
| 6 | L4 streams | **After 1.0**. Keep the HTTP/YARP story clean first. |

---

## 18. Changelog

- **v0.2:** Incorporated the first review.
  - State continuity across publishes (§6.3, S6).
  - HTTP/3 fallback and `Alt-Svc` public-port handling (§7.5, S1, §16).
  - YARP metadata hook confirmed + route-ID fallback (§6.2, S2).
  - Verified the `Guid` big-endian APIs (§4.2, S4).
  - Passkey library budget (§10.1).
  - OCSP stapling cut (§9).
  - PROXY protocol moved to the connection stage, v1+v2 (§7.2).
  - Upstream DNS refresh + mTLS client certs (§5.1, §8.2).
  - Expiry warnings for all certs (§5.1, §9).
  - Admin-through-proxy design + trusted-header hard rule (§3.1, §10.1).
  - Static hosts cut from v1 (§5.1).
  - Hostname normalization, error-page replacement without buffering, startup recompile, rate-limit bit layout, SQLite floor, ArchUnitNET, `/healthz` and `/readyz`.
- **v0.3:** Final review comments.
  - Spikes renumbered S1–S7. S6 (state continuity) now also verifies YARP health continuity and runs in M1 with S7 (baseline).
  - Rate-limit keys carry a 16-bit policy slot so the sweeper can find each entry's policy (§6.3, §7.3).
  - Confirmed both `MapReverseProxy` overloads return `ReverseProxyConventionBuilder` in YARP 2.3.0 (§6.2).
  - Healthcheck wording reconciled (§11.3, §16).
  - `__Host-` cookies need `Path=/` (§3.1).
- **v0.4:** M0 outcomes folded in.
  - The process runs three hosts (public, admin, core) with defined startup, shutdown and exit codes (§3).
  - Custom UUIDv7 generator, with `Guid.CreateVersion7` banned. Int64Ordered is documented as predictable and gets `Observe` seeding. The STJ source-gen converter factory requirement is recorded (§4).
  - S1 result: per-SNI and hot-swap work on QUIC, so the H3 fallback is dropped. Listener layout, ALPN and chain pitfalls, the QUIC-side IP gate and the libmsquic image notes are added (§7.2, §7.5, §16).
- **v0.5:** S2 outcome. The gate selects the YARP endpoint, routes are path-only, and `ConfigureEndpoints` metadata and the route-ID generation scheme are dropped. Publish waits for YARP's `ConfigurationApplied`. Destination-ID stability rules and the required custom-pipeline steps are recorded (§6.1, §6.2, §6.3, §7.2).
- **v0.6:** License decided: Apache-2.0 (§17).
- **v0.7:** Hosting hardened after review (§3). A stop or signal during startup aborts the start and exits 0. The exit codes are listed in full, including failed stops and disposal. Startup errors print without a stack trace. `IOptionsFactory` also resolves to the validated options. A relative data directory resolves against the content root.
- **v0.8:** Native Cloudflare client-IP handling: `Proxy`/`Tunnel` modes, auto-refreshed edge ranges, anti-spoofing header stripping, `CF-IPCountry` geo, origin lockdown, Authenticated Origin Pulls (§7.6, M1/M6).
