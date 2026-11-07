# Paduraksa — Design & Implementation Plan

> **Paduraksa**: the gate into the inner sanctum, flanked by guardian figures meant to keep evil out.
>
> Open-source, homelab-grade reverse proxy and lightweight WAF on **Kestrel + YARP**. You deploy it once and manage it from the dashboard, the REST API, MCP or the CLI. It covers what nginx-proxy-manager does, adds versioned config, an audit trail, real multi-user support and a hot path designed for speed from day one.

Status: **v0.14**: M0, M1 and M2 done, M3 foundations in place and the rest of M3 next (see §18) · Target runtime: **.NET 10 (LTS)** · Single node, single process.

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
│  ├─ Paduraksa.Storage         Shared EF Core/SQLite infrastructure: typed-ID conventions and converters, SQLite pragmas, UTC timestamps. No entities, no DbContext.
│  ├─ Paduraksa.Data            EF Core (SQLite) entities, DbContext, migrations, entity-specific converters
│  ├─ Paduraksa.Vault           TRUST BOUNDARY: keys, certs' private keys, secrets. Everything `internal`.
│  ├─ Paduraksa.Acme            RFC 8555 client + DNS providers. Signs via vault capabilities.
│  ├─ Paduraksa.Proxy           HOT PATH: runtime snapshot model, gate middleware, policies, TLS selector, YARP glue, metrics
│  ├─ Paduraksa.Application     Use cases: config commands, validation, revisions, audit, snapshot compiler, YAML, auth
│  ├─ Paduraksa.Admin           Admin web surface: auth, REST /api/v1, /mcp, /metrics, OpenAPI
│  ├─ Paduraksa.Dashboard       Blazor Server components (RCL)
│  └─ Paduraksa.Server          Composition root + CLI. Hosts the public and admin apps in one process. Binary: `paduraksa`
├─ tests/
│  ├─ Paduraksa.Tests.Unit
│  ├─ Paduraksa.Tests.Data          (EF Core mapping on real SQLite: typed IDs, pragmas, seeding; covers Storage and Data)
│  ├─ Paduraksa.Tests.Proxy         (hot path: gate, client IP, TLS selection, YARP glue, zero-alloc; end-to-end through PaduraksaHost)
│  ├─ Paduraksa.Tests.Integration   (real Kestrel, real SQLite, Pebble ACME test server in CI)
│  ├─ Paduraksa.Tests.Issuance      (ACME issuance on real SQLite against the fake CA and a Pebble container; the one test project that lifts the key-material ban)
│  └─ Paduraksa.Tests.Architecture  (dependency rules, DTO rules, typed-ID rules)
└─ bench/
   ├─ Paduraksa.Benchmarks          (BenchmarkDotNet: hot-path policies, lookups, zero-alloc assertions)
   └─ Paduraksa.Bench.Runner        (the real PaduraksaHost for the end-to-end baseline, `run-baseline.sh`)
```

### Dependency rules (enforced by architecture tests)

```
Core ◄── everything
Contracts ──► Core
Storage ──► Core                     (generic EF/SQLite infrastructure; no entities)
Data ──► Core, Storage
Vault ──► Core, Storage              (has its OWN store and DbContext; never references Data)
Acme ──► Core                        (signing/creds via Core capability interfaces)
Proxy ──► Core                       (no Data, no Storage, no Application, no EF: the hot path can't reach the DB)
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

One process, **two web apps** (the public app on the generic host with an explicit pipeline since M1, §7.1; the admin `WebApplication`) plus a non-web **core host**, one shared set of core singletons:

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
- **Exit codes:** 0 = stopped on request, including during startup. 1 = a host stopped on its own, a host failed to stop, or disposal failed after an otherwise clean run. 2 = startup failed (including no config to serve, §6.4) or the process couldn't be built. 3 = invalid config: failed validation or an unparsable appsettings file. Validation failures print a header plus one line per failure on stderr; any other failure to build or dispose the process prints a single line (`PADURAKSA_DEBUG=1` adds the stack trace).
- **Data directory:** a relative `Paduraksa:DataDirectory` resolves against the content root (where appsettings live, by default the app's base directory), never the working directory. A leading `~` is rejected because nothing expands it. It holds the config database `paduraksa.db` and `last-good.json` (§6.4), and is created at startup if missing.
- **Shutdown:** `/readyz` switches to 503, public drains, then admin stops, then core stops, then shared singletons are disposed. One `ShutdownTimeout` (30 s) is shared by all three, so compose files need `stop_grace_period: 35s`.
- **Signals:** handled only in `PaduraksaHost.RunAsync`. A second signal hard-kills.
- **Listener pinning:** the web apps get a last config source that blanks `urls`/`http_ports`/`https_ports` and sets `preferHostingUrls=false`. Env vars and appsettings can't rebind listeners behind our back.

Public listeners as integrated in M1 (`Hosting/PublicApp.cs`, `PublicListenerSetup`), all on dual-mode `[::]` (`0.0.0.0` without IPv6):
- **HTTP** (`Listen:Http`/tcp, H1) and **HTTPS** (`Listen:Https`/tcp, H1/H2): the stage-A `ClientInfo` connection middleware (PROXY protocol first when enabled on that listener), then on HTTPS the per-SNI TLS callback reading the live snapshot's certificate table (§7.2 B).
- **HTTP/3** (`Listen:Https`/udp): added when `Listen:Http3` is on and libmsquic is present. Its TLS callback selects the certificate and attaches `ClientInfo` (§7.2 A). Without libmsquic it is off, with one Information log at startup; `Alt-Svc` is then never sent.
- **One `TlsHandshakeSelector`** per process (shared singleton, its rejection counters are process state). The same options also derive the compiler's `SnapshotCompilerOptions` (public port, whether H3 is advertised, the certificate resolver), so listeners and snapshots can't disagree.
- **Certificates:** the process's `IServerCertificateSource` is the vault's, then (only when `Paduraksa:Development:PlaceholderCertificate` is on) `PlaceholderCertificates` for the IDs the vault does not know (§9.2). The placeholder is the in-memory self-signed certificate under a well-known, stable `PlaceholderCertificates.Id`; it is also a built-in inventory entry and the inventory's fallback default, so with it on, a config that names no default certificate still answers HTTPS for every host, and a stored document that names the ID keeps compiling across restarts. The option defaults to **on in a Debug build and off in a release build**, where a name no vault certificate covers is rejected at the handshake (the tests and the benchmark runner switch it on explicitly, and a release process with it on warns at startup). The admin listener's own certificate is a separate placeholder until the internal-CA admin certificate exists. The vault starts first in the core host: **a wrong, missing or unsafe master key stops the process** (exit 2, Critical log entry), because running without the vault would only move the failure to the first certificate that needed a key.

Options under `Paduraksa:Listen` (validated at startup, invalid → exit 3):

| Key | Default | Meaning |
|---|---|---|
| `Http`, `Https`, `Admin`, `AdminBind` | 80, 443, 9443, `0.0.0.0` | As before. TCP and UDP share `Https`. |
| `PublicHttpsPort` | 443 | The HTTPS port clients reach: advertised in `Alt-Svc` and used by the HTTP→HTTPS redirect. Set it only behind a port mapping. |
| `Http3` | `true` | Serve H3. Off, with an Information log, without libmsquic. Needs a fixed `Https` port (0 is rejected with H3 on). |
| `HttpProxyProtocol`, `HttpsProxyProtocol` | `Enabled: false`, `TrustedSources: []`, `HeaderTimeout: 00:00:03` | PROXY protocol v1/v2 on that TCP listener (HTTPS: read before TLS). Enabling it makes the header mandatory and requires at least one trusted source (address or CIDR); the timeout is at most 1 min. QUIC has no PROXY protocol. |

Why two apps and not one app with port filters:
- Admin endpoints **can't be routed** on public ports, because the public pipeline doesn't contain them. No reliance on `RequireHost` or port checks.
- Each pipeline gets its own middleware, auth and Kestrel limits (for example, admin allows large YAML uploads while public uses per-host limits).
- Shared singletons (`SnapshotStore`, `MetricsRegistry`, `Vault`, `IAcmeChallengeStore`, `HealthState`) are built once and registered **as instances** in both containers.

Ports (configurable, env-overridable):

| Purpose | Default | Notes |
|---|---|---|
| Public HTTP | 80/tcp | ACME HTTP-01, redirects, plain-HTTP hosts |
| Public HTTPS | 443/tcp + **443/udp** | H1/H2 over TCP, H3 over QUIC. Docs must stress mapping the UDP port. The container listens on 80/443 *directly* (no in-container remapping, see §16) so the advertised `Alt-Svc` port is correct; `Listen:PublicHttpsPort` overrides it behind a mapping. |
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
- Generated `New()` uses the process-wide generators on `TimeProvider.System`. Decided in M2: `Shared` stays there; `TimeProvider` *is* the clock abstraction, and code that needs a controllable clock (tests, the audit log and revision store) creates its own generator on its own `TimeProvider`.
- Every **source-generated** `JsonSerializerContext` that serializes typed IDs must register `TypedIdJsonConverterFactory` via `[JsonSourceGenerationOptions(Converters = [typeof(TypedIdJsonConverterFactory)])]`. The STJ source generator can't see the `[JsonConverter]` attribute our generator emits, and without the factory IDs serialize as `{}`.
- The generator is added as an analyzer to every project through `Directory.Build.props`. Prefix uniqueness is checked per compilation (PDKID004) and across all src assemblies by an architecture test.
- EF value converters can't call static abstract members inside expression trees (CS8927). Generic converters go through a plain generic helper (`static T Wrap<T, TV>(TV v) => T.FromValue(v)`).
- EF mapping (S4, `Paduraksa.Storage.Ids`): `configurationBuilder.AddTypedIdConventions()` swaps in a `PropertyDiscoveryConvention` that accepts typed IDs, so they're found from the model's own CLR member types (no scanning or type lists) in entities and complex types, as keys, FKs and nullables. Typed-ID properties are `ValueGenerated.Never`. **Inside JSON complex types IDs are stored as their canonical string** (`host_…`), not BLOB/INTEGER: the document stays readable and JSON members compare against a string parameter. ID lists (`List<RouteId>`) are primitive collections of canonical strings, as a column or inside JSON. Owned types mapped to JSON are rejected at model build; use complex types. **Hard rule: a JSON ID (TEXT) never equals a column ID (BLOB/INTEGER) in SQL**, so references inside JSON settings are resolved in memory by the compiler, never joined or compared in SQL. LINQ `==`/`!=`/`<`/`>`/`CompareTo`/`Contains`/`OrderBy` on IDs translate; `id.Value` doesn't (EF can't see through a converter).

### 4.3 Secrets that look like IDs (but aren't)

API tokens: `pdk_<tokenId base62>_<32 random bytes base62>_<crc32 base62>`. The prefix and checksum let secret scanners (GitHub, gitleaks) detect leaked tokens. The DB stores `tokenId` and `SHA-256(secret)`. A fast hash is fine for 256-bit entropy, and lookup by ID makes verification constant-time. Session cookies are opaque random 256-bit values, protected by Data Protection, with only the hash stored.

---

## 5. Data model (SQLite, EF Core 10)

Two database files, **deliberately separate**:
- `data/paduraksa.db`: config, users, revisions, audit, metric rollups. Owned by `Paduraksa.Data`. Both databases share the typed-ID conventions, SQLite pragmas and timestamp storage from `Paduraksa.Storage`.
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
| `AcmeAccount` (`acct_`) | Directory URL (LE / LE staging / ZeroSSL / Google / custom), contacts, EAB key id and **secret ref**, status, account URL, key algorithm |
| `AcmeOrder` (`ord_`) | One row per certificate: status, challenge type, names, CA order URL, staged-key token and CSR (resume state), last error, attempts, retry time, ARI window, next renewal (§9.3) |
| `DnsProvider` (`dnsp_`) | Kind (Cloudflare, deSEC, …), label, credential `SecretRef` (`sec_`) into the vault; the hosts the token may reach are stored with the secret |
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
- **Startup = recompile the latest revision.** The DB's latest committed revision is always the source of truth, and the process compiles it on every start. A crash between step 5 (commit) and step 6 (publish) therefore costs nothing except that the change goes live on the next start. (In-process, a failed publish after a commit is reported to the command, logged as an error and audited, with no retry: the commit stands, the previous snapshot stays live, and the next commit's publish or the next start makes the change live, since every snapshot is the whole config.)
- **Last-known-good.** After each successful publish, the compiled snapshot's *source document* is also written to `data/last-good.json`. This file is only a fallback: if the DB is unreadable or migration fails at startup, the proxy still comes up serving traffic while the admin UI shows a degraded banner. (As built, §6.4: also when the latest revision is corrupt or doesn't compile, or a startup publish fails; an empty database is restored from the file; `/readyz` stays ready with the body `degraded`; with nothing usable at all the start fails.)

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

As built in M1 (`src/Paduraksa.Proxy`: `Snapshot/`, `Gate/`, `Yarp/`, `Config/`):
- **Types (M2):** the config model and the snapshot are keyed by typed IDs (§4): `Revision` is a `ConfigRevisionId` (`Empty` when there is no committed revision: the bootstrap snapshot, the empty startup config), `CompiledHost.Id` / `CompiledRoute.Id` are `HostId` / `RouteId`, and upstreams, destinations and certificate bindings use `UpstreamId` / `DestinationId` / `CertificateId`. YARP's route, cluster and destination IDs are their canonical strings (`rte_…`, `ups_…`, `dst_…`), formatted by the compiler; nothing on the request path formats an ID. YARP compares IDs ignoring case, so the compiler rejects route, upstream and destination IDs that differ only in case. `RuntimeSnapshot.Yarp` holds the YARP *content* (`YarpConfig`); the `IProxyConfig` with its change token and a unique `RevisionId` is created per publish by the provider. `Certificates` (`CertificateTable`) and `TrustedProxies` (`TrustedProxyPolicy`) are typed slots the compiler fills; `SnapshotStore` is the TLS selector's `ICertificateTableSource`, so a publish hot-swaps certificates for new handshakes.
- **Unbound → bound:** the compiler produces a candidate with generation 0 and no endpoints; the publisher `Bind`s it to YARP's endpoints and the next generation. The store starts at a bootstrap snapshot (generation 0, 503 for everything), so `Current` is never null.
- **Routes:** exact or segment-aware prefix, ordered by `Order`, then as listed; first match wins. **Match leniently, forward unchanged:** matching (and later path rules) uses a canonical *matching view* of the decoded path: `\`, `%2F` and `%5C` separate segments, repeated `/` collapse, `;…` segment parameters are dropped, trailing `.` and spaces are trimmed per segment, dot segments resolve, case is ignored. Many upstreams read those spellings as the same path, so `//admin`, `/admin;x`, `/admin.` or `/ADMIN` must hit the policies of `/admin`; over-matching only makes a policy stricter. The view costs 0 B (as is when already canonical, else on the stack, or a pooled buffer past 256 chars; Kestrel's request-line limit bounds it). The compiler rejects route paths that are percent-encoded or not canonical. Routes carry **only their own** policies. YARP routes are `/.paduraksa/r/{routeId}/{**rest}`, and the proxy pipeline rejects (404) any request whose endpoint the gate didn't set, so they can't be reached directly.
- **Forwarding:** the upstream gets the path exactly as received: a YARP request transform builds the URI from the raw request target (path part) plus the query, without re-escaping. YARP's default would turn `%2F` into `%252F`. M1 strips no prefixes.
- **Publisher:** single writer; a queued candidate is completed as `Superseded` when a newer one arrives. YARP's `IConfigValidator` plus a route → cluster check run first (`Invalid`). Content equal to what YARP runs skips the YARP round trip (policy-only edits). `ConfigurationApplyingFailed` → `Rejected`, and the previous YARP config is re-applied so provider and YARP agree; no outcome within `ApplyTimeout` (10 s) → `TimedOut`. On any failure the old snapshot stays live. **Before YARP exists** (the core host publishes first), an empty-YARP candidate publishes at once and any other waits for `MapPaduraksaProxy`, which validates it and hands it to YARP's initial load. Startup code therefore **never awaits** a publish: the core host's start returns before the public app starts, so it queues the publish and `/readyz` (`HasPublished`) reports when it's live. Disposing the publisher (shared-services disposal) stops the writer and fails anything queued or in flight.
- **Fixed answers** (Respond hosts, policy responses) with status 204, 205 or 304 must have an empty body (compile error otherwise) and are sent without `Content-Length`.
- **Default host:** 404 (default), 421, or close (abort: H1 closes the connection, H2/H3 reset the stream). The HTTP→HTTPS redirect is 301 for GET/HEAD and 308 otherwise; its `Location` (request host + raw target + public port) is the one string a gate-answered response allocates, built after the decision.

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

Spike S6 turns this into a test: N generations are published under load, and it asserts that rate-limit decisions, counter totals and YARP destination health are all continuous. M1 covers YARP health and request errors (`StateContinuityTests`); rate-limit and counter continuity are tested when those tables exist (M6, M7).


### 6.4 M2 build decisions

These refine §5 and §6.1 for the M2 build.

- **The config document is the source of truth.** `ConfigDocument` (in `Paduraksa.Contracts`, `apiVersion: paduraksa.dev/v1`) holds the global settings, profiles, upstreams and hosts with their routes. It contains references only, never secrets. Each revision stores it as canonical JSON (deterministic bytes), Brotli-compressed, with the SHA-256 of the uncompressed bytes. Startup, rollback, diff and export all read documents.
- **The EF config entities are a projection of the latest revision.** They are written in the same transaction as the revision, and serve queries (dashboard lists, "which hosts use this upstream") and DB-level invariants: a unique `COLLATE NOCASE` host name and foreign keys. Nothing reads them to rebuild config.
- **Host names are natural keys.** Each is stored normalised (§5.1), with no `hn_` ID.
- **Concurrency.** Every top-level object (global settings, profile, upstream, host) carries a `Version` that the pipeline increments on change. A command names the version it edited, so two admins editing different hosts never conflict. Whole-document commands (import, rollback) name the `baseRevision` and need it to be the latest. One writer at a time (`SemaphoreSlim`) in the Application layer.
- **Settings layering.** `HostSettings` fields are nullable; null (absent in JSON) means *inherit*. The compiler resolves built-in defaults → global defaults → profile → host → route (routes carry the route-level subset) into the concrete `ProxyConfigModel`. Nested settings (TLS) merge field by field; list-valued settings (an IP allow list) replace as a whole.
- **Errors carry a path.** Validation and compile errors carry a JSON-pointer path into the document (`/hosts/3/routes/0/path`), a stable code and a message, so every surface can point at the field.
- **Audit hash chain.** `hash = SHA-256(prevHash ‖ canonical bytes of the event)`; the first event's `prevHash` is 32 zero bytes. A verifier walks the chain in ID order and reports the first break.

As built in M2 (`src/Paduraksa.Application/Compilation`):
- **Mapping.** `ProxyConfigMapping.Map` is a pure function from the document's canonical form to `ProxyConfigModel`, so two spellings of one config compile alike. `ConfigCompiler` (the `IConfigCompiler`) runs it and `SnapshotCompiler` with the process's `SnapshotCompilerOptions`.
- **Layering.** Per host: built-in defaults → global defaults → its profile (a missing profile adds no layer; the validator rejects it, `host.profile.unknown`) → the host; per route, its allow list on top of the host's result. A null field inherits. TLS merges field by field. The IP allow list replaces as a whole, and an empty list means no restriction, which is how a layer (or route) lifts an inherited list. `ForceHttps` is judged after layering.
- **Allow lists: host chain and route chains.** The gate runs the host chain before the HTTPS redirect, fixed answers and route matching, so a route override can't *replace* a list that sits there alone. When every route ends up with the same list (none overrides the host's, or all override it alike), or the host has no routes, that list goes in the host chain only and guards everything the host answers. Otherwise each route carries its own effective list (the host's or its override), and the host chain carries the **union** of the routes' lists, dropping denied requests only if every route drops: a client no route admits is blocked on every path, over plain HTTP too (no redirect, no 403-vs-404 to map the host by, and a dropping host still looks dead), while one that some route admits is judged again by the route it hits. If some route is unrestricted the host chain carries no list, since anyone may reach that route. So a route can tighten, lift, or add a list. A test drives the real gate over generated documents (scheme × path × client IP) against a reference that reads the document directly.
- **For M6.** IP access must keep running ahead of the other host-level policies (forward-auth and the like), so an outsider never reaches them. M6 should also revisit matching the route before the HTTPS redirect, which would let per-route lists guard the redirect without the union.
- **Certificate binding (as built in M2; M3 resolves it against the inventory, §9.2).** Auto → no certificate of its own (the default one); Specific → its `CertificateId`; None → `HostSpec.Https = false`: no certificate-table entries and no `Alt-Svc`, and the gate answers HTTPS requests for the host 421 (a connection on the default or another host's certificate can't reach it). None with an effective `ForceHttps` is an error (validator: `host.certificate.noneWithForceHttps`; compiler at the host). Disabled destinations are left out; an upstream may have none left until an enabled host routes to it.
- **Compile errors carry paths.** `SnapshotCompiler` attributes each error to a host, route, upstream, destination (by typed ID) or the defaults/trusted proxies; `ConfigCompiler` maps that to a pointer into the document *as given* (`/hosts/{i}`, `/hosts/{i}/routes/{j}`, `/upstreams/{k}/destinations/{m}`, `/settings`, `/settings/trustedProxies`) with code `compile.failed`, each (path, message) once. An inherited value is reported where it is used (each host or route whose chain carries it); a duplicated ID points at its last occurrence. The mapping itself reports the few invalid values the model can't carry (an undefined certificate binding mode or load-balancing policy, a respond host without a content type). A test runs sample, edge and generated documents through the validator and the compiler: whatever validates must compile (certificate references aside, which only the compiler can resolve against the inventory, §9.2).

As built in M2 (`Paduraksa.Application.Commands.ConfigService`, commands and results in `Paduraksa.Contracts.Commands`):
- **Versions are revision numbers.** A revision gives every top-level object it changes (a route or destination change counts for its parent) its own `Seq` as the new version; untouched objects keep theirs, whatever a command sends. A version is therefore issued once, ever, and stays at most the latest `Seq`: a rollback to #37 committed as #52 gives what it changes version 52, so an editor holding any older version of those objects (including one from #37, or of an object deleted and restored) gets a conflict, while editors of objects the rollback leaves alone don't.
- **The empty config** is the default document with no revision. Startup writes nothing; the first command creates revision #1, and a whole-document command on an empty database names no base revision.
- **Errors** of whole-document commands point into the document as sent (a rollback: as stored). Object commands report the sent object's errors relative to it (`/names/1`), and errors the change causes elsewhere (a deleted upstream still in use) as related errors pointing into the canonical document after the change, like the change list. The sent object is validated last in its list, so a clash with another object is reported on it.
- **Commands that change nothing** (equal canonical config) commit nothing and return `unchanged`.
- **Audit:** commits in the commit's transaction; denied, invalid, conflict and not-found commands in a transaction of their own, written without the caller's cancellation once decided, a failed write logged rather than replacing the result; a publish failure after a commit as `config.publish` (failed), also when it happens after the command stopped waiting. Dry runs and no-ops aren't audited.
- **Publishing:** queued with the publisher under the writer lock, so snapshots go live in commit order; the command waits for the outcome outside the lock (bounded, then `pending`). A superseded publish reports the outcome of the newer publish that carries it. A failed publish is reported, logged and audited by the command whose publish it was; the commit stands, with no retry.
- **Another writer** (a second process on the same file) that commits between a command's load and its commit is seen inside the commit transaction; the command is prepared once more on the new head, and a second move is a conflict naming the real head.
- **Projection:** rewritten per touched object (delete its rows, insert them again) from the change list, with foreign keys deferred to the commit; the settings row names the revision the projection shows, and a projection that doesn't show the parent revision is rewritten whole. Host names are projected for every host with a copy of the host's `Enabled`, unique (`COLLATE NOCASE`) through a partial index over enabled hosts.

As built in M2 (startup: `ConfigService.StartAsync`, `Paduraksa.Application.LastKnownGood`; composition: `Server/Hosting/ConfigStore`, `ConfigStartup`):
- **Composition.** The config database (`<data dir>/paduraksa.db`), its context factory, the compiler (`ConfigCompiler` on `PublicListenerSetup.CompilerOptions`), the `LastKnownGoodFile` and the one `ConfigService` are shared instances, registered into all three hosts. The factory and the service come from a small private container (EF's factory and the service are meant to be resolved), which `SharedServices` owns and which alone disposes them. Disposal runs in reverse creation order after every host has stopped: the container (the service refuses new commands), then the publisher (stops its writer and fails whatever is queued, so nothing goes live after it), then the last-known-good file, created before the publisher (waits for its pending write); so the file never trails the last revision that went live. Certificate bindings resolve against the certificate inventory (§9.2); the placeholder certificate is its fallback default (§3).
- **Startup recompile.** The core host's `ConfigStartup` creates the data directory and runs `StartAsync` under the writer lock: migrate (`ConfigDatabase.InitializeAsync`), read the latest revision (none: the empty config), rewrite the projection whole if its settings row doesn't name that revision or can't be read (a restored or hand-edited database; any failure is logged and startup goes on, since the projection only serves queries and the next commit rewrites it anyway), compile, and queue the publish **without awaiting it** (as in M1: YARP attaches only when the public app starts). The outcome is logged when it arrives; a superseded startup publish (a commit's newer publish carries the config) isn't a failure.
- **Last-known-good** (`last-good.json`): `{format, revision, seq, sha256, document}`, the document being the revision's canonical bytes verbatim, so the hash is the stored `DocumentHash`; references only, no secrets. Offered after every successful publish (the startup one and each commit's, before the command returns) and written in the background, never under the writer lock; the newest offer wins, older ones are dropped. Each write goes to a new temp file with a random name (`CreateNew`, mode 0600 on Unix; leftovers of a crash are removed first, a symlink as a link), is flushed to disk and renamed over the target, which replaces a symlink there instead of following it. The directory isn't fsynced (.NET can't open a directory): safe but not durable, a power loss right after a write may leave the previous, still valid, file. A failed write is logged and never faults the writer. Reading uses one bounded read, refuses a symlink, checks format, IDs and hash, and never throws: a damaged file is logged as a warning (also on a healthy start) and replaced at the next publish. The empty config isn't written: it is no revision. One data directory serves one server process at a time (assumed, not enforced; the database tolerates another writer such as the CLI, this file doesn't).
- **Restore.** A database without revisions next to a usable last-known-good file has lost its config (a deleted or replaced `paduraksa.db`): the file's document is committed as revision #1 through the normal replace path (actor System, message "Restored from last-known-good #N (rev_…)", audited), logged at Critical, and startup goes on normally. If that commit is rejected or fails, the start is degraded on the last-known-good config (on the empty config if that doesn't compile either), accepting commands. To start over with an empty config, delete both files. A file newer than a database that has revisions (a restored backup) is only a warning: the database wins.
- **Degraded starts.** If the database can't be opened or migrated, or its latest revision can't be read (corrupt document, unreadable by this build, or columns a converter rejects: a damaged ID or timestamp), or it doesn't compile, the last-known-good document is compiled and published instead, logged at Critical, and `ConfigService.Status` says so (`Source = LastKnownGood`, the problem, the revision served). The store itself unusable: commands get `Unavailable` (`config.store.unavailable`, unaudited, since the audit log lives in that store) until the database is fixed or restored and the process restarted. Only the compile failed (say, after an upgrade made the compiler stricter): the database is fine, so commands are accepted and planned on the latest revision as always, and the first one that goes live ends the degraded state; refusing them would leave no way to fix the config short of editing the database.
- **A failed startup publish** (YARP rejects it or times out) leaves no live config. If the last-known-good file holds another revision and no command has committed since, that is published instead, degraded and accepting commands; otherwise the process stays up without a snapshot and `/readyz` says not ready, as in M1. (Tested with a publish the publisher refuses; every YARP setting the compiler emits is checked before YARP sees it, so no config makes YARP itself reject one.)
- **Readiness while degraded.** `/readyz` stays 200, with the body `degraded` instead of `ready`: the proxy serves traffic, and taking it out of rotation would turn a config-store problem into an outage. Keyword monitors can alert on the body; the cause is in the Critical log, and the admin UI shows the banner (M5).
- **Nothing to serve.** Neither a usable database revision nor a usable last-known-good document: `StartAsync` throws `ConfigStartupException` and the process exits 2 (startup failed), logged once at Critical by the host. An empty proxy that refuses changes would only look alive.
- **Change → live** through the whole process (real SQLite, YARP and Kestrel; one upstream and one host, two commits, until the first request for the new host comes back): median 11–15 ms on a dev machine with the rest of the suite running alongside.

---

## 7. Hot path

### 7.1 Budget & rules
- No DB, no `await` on anything except I/O to client/upstream, no locks (`lock`, `SemaphoreSlim`), no LINQ, no closures, no `string` building, no regex with backtracking.
- Per-request allocations in Paduraksa code: **0 B** steady state on keep-alive connections. Kestrel/YARP's own allocations are out of scope but measured. Enforced by benchmark tests asserting `GC.GetAllocatedBytesForCurrentThread()` deltas.
- Precompute everything at compile time: byte arrays for responses/headers, `SearchValues<string>` for substring sets, CIDR tries, country bitsets, redirect targets.
- Options validate themselves: an invalid value throws at `init` or construction, never later on the hot path (the snapshot compiler checks config first and reports it as an error).

**Spike S3 confirmed this** (`docs/spikes/S3-zero-alloc-gate.md`; .NET 10.0.13, YARP 2.3.0): the gate measured **0 B/request on H1 and H2** in every end-to-end run, taking 145–225 ns for the synchronous section. For realistic browser traffic, the costs we accept are:

| | B/request |
|---|---|
| Kestrel itself | ~1 KB |
| Routing + YARP hand-off | 152 B |
| Whole proxied request | ~4.1 KB on H1, ~4.4 KB on H2 |

Mechanisms M1 adopts:
- **Pipeline:** the public app is built on the **generic host with an explicit pipeline**: `Configure(app => { app.Use(gate); app.UseRouting(); app.UseEndpoints(e => e.MapReverseProxy(…)); })`, not `WebApplication`. YARP calls `AddAuthorization()` unconditionally, and `WebApplication` then auto-inserts `UseAuthorization()` *ahead of the gate*. That costs bytes and is a hazard if a `FallbackPolicy` is ever set. A guard test with a spy `IAuthorizationPolicyProvider` asserts it never runs.
- **Gate shape:** one synchronous gate middleware. It decides what to do, and the response body is written *after* the measured section. Responses come from precomputed bytes plus shared header values set through the typed header properties.
- **Per-connection state:** an immutable `ClientInfo`, set as a connection feature in `ListenOptions.Use`. It's shared by all H2 streams on the connection, so it must stay immutable. Never use `IConnectionItemsFeature.Items`, which is one non-thread-safe dictionary for all streams.
- **Per-request state:** one object cached in `IPersistentStateFeature` and reset each request. Stream reuse happens only on graceful end, so RST, cancels and Drop reallocate, and that's accepted.
- **Host lookup:** `Headers.Host[0]` (H2 `:authority` is already folded in), with the port stripped by span. Two `FrozenDictionary` tables (exact names, wildcard parents), `OrdinalIgnoreCase`, looked up via `GetAlternateLookup<ReadOnlySpan<char>>`: 11–22 ns, 0 B. `Request.Host.Host` allocates when a port is present. The compiler **rejects duplicate host names** (otherwise the last one silently wins) and canonicalises IPv6-literal keys.
- **Client-IP parsing:** our own span parser into `UInt128`. `IPAddress.TryParse` allocates 40/80 B. Ours is stricter than the BCL: no octal, hex, short forms, zone IDs or whitespace. M1 keeps the edge-case list plus a differential fuzz against the BCL.
- **Policies:** sealed `IRequestPolicy` classes returning a 16-byte `PolicyDecision` struct.
- **Tests:** `AllocAssert.Zero(state, static lambda)` for synchronous code, plus one in-process Kestrel test asserting gate bytes == 0. Kestrel and YARP numbers are tracked in `bench/`, not asserted.
- **Benchmarks:** they run under workstation GC, because BenchmarkDotNet's `Allocated` column is wrong under Server GC with the in-process toolchain. Non-monotonic `GC.GetTotalAllocatedBytes` samples are discarded.

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
   As built in M1: UseClientInfo on both TCP listeners (HTTPS: through PublicHttpsListenerOptions.
   ConfigureTcpConnection, before UseHttps); on QUIC, QuicClientInfoGate attaches ClientInfo.FromEndPoint
   to the connection in the handshake callback, and H3 requests see it (verified end to end in Docker).
   IpKey stores IPv4 and IPv4-mapped alike, so TCP and QUIC agree. Global IP deny and the per-IP cap are
   a marked seam in that gate (M6); M1 acquires nothing there, so the QUIC ConnectionClosed gap can't leak.

B. TLS handshake (TlsHandshakeCallbackOptions on both TCP and QUIC → per-SNI SslServerAuthenticationOptions)
   • SNI → snapshot → PREBUILT SslServerAuthenticationOptions, deduplicated on (cert, host TLS policy, transport).
     The compiler builds RuntimeSnapshot.Certificates: each enabled host's DNS names (exact and *.wildcard;
     IP literals never arrive as SNI) map to its certificate (a CertificateId), or to the default certificate with
     the host's own TLS settings (min version, TCP ALPN, H3 on/off); certificate IDs resolve through
     IServerCertificateResolver (the vault's source in M3, a placeholder until then).
   • the handshake only sees the SNI's host, and clients coalesce connections across every name a certificate
     covers, so the host's TLS settings are re-checked per REQUEST in the gate (stage C, ConnectionTlsCheck)
     Each holds a shared SslStreamCertificateContext.Create(cert, chain, offline: true): the only way the
     intermediate is reliably sent. Without it, TCP may fetch it via AIA mid-handshake and QUIC sends the leaf only.
   • ApplicationProtocols ALWAYS set explicitly ([h2, http/1.1] or the host's ALPN on TCP, [h3] on QUIC).
     Kestrel writes ALPN into a returned options object whose list is null (both transports), which
     silently breaks the other transport if instances are shared. A unit test asserts non-null ALPN.
   • unknown SNI: default cert, or reject by THROWING from the callback (client sees EOF / TLS alert; server
     stays healthy). Count rejections ourselves: QUIC logs nothing.
   • cipher suites (TCP, Linux): AEAD + ECDHE only via CipherSuitesPolicy (TLS 1.3 suites, ECDHE AES-GCM /
     ChaCha20-Poly1305 for TLS 1.2). Other OSes keep the OS defaults; QUIC (TLS 1.3) is AEAD-only already.
   • client certificates (per host): chain built offline (no AIA/CRL/OCSP fetches: a client-controlled AIA URL
     would be SSRF); M6 adds CustomRootTrust with the origin-pull CA.

C. Gate middleware (first in pipeline), in this order (as built in M1)
   • snapshot = SnapshotStore.Current (one volatile read)
   • resolve client IP: the PEER is the PROXY-protocol source from stage A if present, else the socket peer;
     the trusted-proxy policy then applies to that peer as usual: only a peer in the trusted-proxies list
     may supply the client through X-Forwarded-For / CF-Connecting-IP (native Cloudflare handling in §7.6),
     otherwise the peer is the client. Built as TrustedProxyClientAddressSource: the connection's
     ClientInfo (else Connection.RemoteIpAddress) through the snapshot's global TrustedProxyPolicy, 0 B;
     header fallbacks are counted. The whole ClientIpResolution stays on the request state for policies
     and the forwarding headers.
   • resolve the trusted request SCHEME in the same step (ForwardedScheme, 0 B): from a trusted Cloudflare
     peer CF-Visitor (exactly {"scheme":"https"} or {"scheme":"http"}, anything else invalid), else its
     X-Forwarded-Proto; from a trusted generic proxy X-Forwarded-Proto only with TrustForwardedProto
     (default off); otherwise the connection's. The HTTPS redirect and the upstream X-Forwarded-Proto both
     use it, so a TLS-terminating proxy (cloudflared → :80, Flexible SSL) can't loop the redirect.
     Cloudflare over plain HTTP for a plain-HTTP visitor (Flexible) is counted and logged once.
   • plain HTTP: GET/HEAD /.well-known/acme-challenge/{token} → AcmeChallengeStore (a ConcurrentDictionary
     read through a span lookup, 0 B) → 200 text/plain, before everything below; a token that isn't published
     falls through to the normal pipeline (as built in M3, §9.3)
   • resolve host (span-based lookup, no string allocation); unknown → Default behaviour
   • HTTPS: the host's TlsPolicy against the request's connection (HTTP/3 = QUIC/TLS 1.3 vs Http3Enabled,
     HTTP/2 vs an HTTP/1.1-only ALPN, ITlsHandshakeFeature.Protocol vs the minimum version) → 421
     Misdirected Request when the connection is weaker, so browsers retry on a connection of their own;
     a few comparisons on per-connection data, 0 B. M6 adds the client-certificate requirement here.
   • the host's precomputed Alt-Svc on TCP HTTPS responses (forwarded or answered), 0 B (§7.5); only for
     hosts with names in the certificate table (not IP-literal-only hosts)
   • HOST policy chain (D), so it also guards the redirect and fixed answers (blocked → 403, not 301)
   • HTTP→HTTPS redirect / Redirect and Respond hosts answered here from precomputed bytes
   • resolve route (per-host ordered list, on the canonical matching view of the path; §6.2) → 404 if none
   • ROUTE policy chain (D): the route's own policies only
   • HttpContext.SetEndpoint(YARP Endpoint from the same snapshot) so the later UseRouting skips matching

D. Policy chains: one per host, one per route (IRequestPolicy[], sealed classes, evaluated in order;
   host policies run before a route is known, so State.Route is null for them)
   IpAccess → Geo → RateLimit → RequestLimits → PathRules → UserAgentRules → Robots →
   Auth (basic / forward-auth / OIDC) → [future: PoW gate, bot heuristics, header rules]
   each returns PolicyDecision { Continue | Respond(status, page) | Challenge | Drop } as a struct

E. YARP (endpoint already set by the gate → affinity → load balancing → passive health → forwarder)
   with Paduraksa transforms
   • X-Forwarded-*, X-Real-IP, request ID, security headers, custom headers (precompiled transforms)
   • forwarding headers as built in M1 (YARP's own X-Forwarded-* transforms are off), always OVERWRITTEN:
     X-Forwarded-For, X-Real-IP = exactly one address, the resolved client (no chain is kept or appended;
       IPv4 dotted quad, also for IPv4-mapped peers; IPv6 without brackets); left out if unknown
     X-Forwarded-Proto = the trusted request scheme from stage C (the same one the redirect uses)
     X-Forwarded-Host = Host / :authority as received, port included
     Forwarded, X-Forwarded-Prefix, X-Forwarded-Port = removed (only a client could have set them)
     every cf-* header (case-insensitive: CF-Connecting-IP, CF-Visitor, Cf-Access-Jwt-Assertion,
       Cf-Access-Authenticated-User-Email, cf-ipcity, …) and True-Client-IP = removed unless
       CloudflareHeadersTrusted (Cloudflare mode and trusted peer); a trusted generic proxy doesn't qualify.
       One scan of the outgoing header names, 0 B. Otherwise a LAN client could forge Cloudflare Access SSO
       past a tunnel.
     The address string is cached on the pooled request state; the rest are constants or Kestrel's strings.
   • upstream errors / no healthy destinations → custom error pages

F. Telemetry (always on, lock-free): counters + latency histogram per host / status class / upstream
```

### 7.3 WAF building blocks (all compiled per snapshot)

| Feature | Implementation |
|---|---|
| IP allow/deny | `CidrSet` (M1): merged, sorted, disjoint `UInt128` ranges (IPv4-mapped), binary-searched, immutable. Chosen over a radix trie: membership needs no longest-prefix match, and the arrays are smaller and need fewer dependent loads (S3: 7.7 ns for 2,000 CIDRs). IPv6 rate keys are masked to /64 (`IpKey.MaskTo`). |
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
- **Builder:** the public app runs on the generic host with `UseKestrelCore()`, which leaves out Kestrel's HTTPS configuration and the QUIC transport. `UsePublicHttpsTransports()` adds both (`UseKestrelHttpsConfiguration()` and `UseQuic()`); without them H3 listeners fail at startup. *Spike history:* S1 found this under `WebApplication.CreateSlimBuilder`, which has the same gap.
- **Still open:** a real browser upgrading to H3 (needs a trusted cert), routable IPv6, and arm64 libmsquic. Carrying the QUIC-callback ClientInfo through to the request works: a feature set on the QUIC connection context in the callback is visible to the H3 request (`PublicHttp3Tests`, Docker).
- **As built in M1:** `UsePublicHttpsTransports()` on the generic host's `UseKestrelCore()`, then `ListenPublicHttp` and `ListenPublicHttps` (§3). `run-quic-tests.sh` runs the QUIC tests of `Tests.Proxy` and an end-to-end H3 request through `PaduraksaHost` in Docker with libmsquic.
- **Either way, `Alt-Svc` is emitted by Paduraksa, not Kestrel.** Kestrel's automatic header is disabled (`DisableAltSvcHeader`). Our header is precomputed per host, only for H3-eligible hosts, using the **public** HTTPS port from settings (default 443), not the bound port. That way a port-mapped deployment can't advertise the wrong port.
- H3 is off globally if `libmsquic` isn't available (`QuicListener.IsSupported`). `paduraksa doctor` reports this. The stock `aspnet` image doesn't ship it: install `libmsquic` from packages.microsoft.com (≈ 21 MB incl. `libnuma1`; tested 2.6.3 on Ubuntu 24.04).

### 7.6 Client IP behind Cloudflare (native)

Many homelabs sit behind Cloudflare's proxy (orange cloud) or a Cloudflare Tunnel. Getting the real client IP must be a toggle, not a hand-maintained CIDR list. It's a built-in **trusted-proxy provider**, which the generic trusted-proxy model (CIDRs + header) and future providers also use.

- **Modes:** `Off` · `Proxy` (Cloudflare connects to us directly) · `Tunnel` (a local `cloudflared` connects to us). Set globally and overridable per host (§5.3); M1 has the global setting only (`DefaultsSpec.TrustedProxies`: mode, trusted CIDRs, generic header, `True-Client-IP` and X-Forwarded-For fallbacks), and the per-host override comes with M2/M6. The wizard asks "Is this server behind Cloudflare?" and offers to set it.
- **Who is trusted:**
  - `Proxy`: the TCP peer (after PROXY protocol, if any) must fall within **Cloudflare's published edge ranges**. These are shipped as a baked-in list and refreshed by a background job from `https://api.cloudflare.com/client/v4/ips`, falling back to `https://www.cloudflare.com/ips-v4` / `ips-v6`. They're stored with a fetch timestamp and compiled into the snapshot as a CIDR trie. A failed refresh keeps the last good list and raises a dashboard warning if it's stale (more than 30 days old).
  - `Tunnel`: the peer must be the configured `cloudflared` address (loopback, a Docker network CIDR or a specific IP). Edge ranges don't apply because the TCP peer is `cloudflared`.
- **Which header:** `CF-Connecting-IP` (single value, so there's no X-Forwarded-For chain parsing). Optionally `True-Client-IP` (Enterprise) or the right-most untrusted entry of `X-Forwarded-For` as a fallback. The value is parsed span-based into the same `UInt128` client-IP representation used everywhere, with IPv4-mapped addresses normalised. A missing or unparsable header from a trusted peer counts as a counter + debug log, and the peer IP is used.
- **Anti-spoofing:** when the peer is **not** trusted, **every `cf-*` header** (Cloudflare adds more over time, and Cloudflare Access's `Cf-Access-Jwt-Assertion` / `Cf-Access-Authenticated-User-Email` carry identity) and `True-Client-IP` are ignored, and they're **stripped before forwarding upstream**. Upstreams always get a clean `X-Forwarded-For` / `X-Real-IP` built from the resolved client IP (§7.2 E).
- **What the real IP feeds:** WAF IP allow/deny, geo, rate-limit keys, access lists, forward-auth headers, access logs, metrics. Connection-stage limits (§7.2 A) necessarily see the Cloudflare edge IP. They must therefore exempt trusted Cloudflare peers from per-IP connection caps, or every visitor would share a handful of edge IPs.
- **Optional extras (per host or global):**
  - **Geo from `CF-IPCountry`:** when the peer is trusted, use it instead of the local Geo DB lookup. It's free and accurate. `XX`/`T1` map to "unknown".
  - **`CF-Visitor` scheme:** the original scheme for the HTTP→HTTPS redirect decision when Cloudflare connects to the origin over plain HTTP ("Flexible" SSL, or `cloudflared` forwarding to port 80). This avoids redirect loops. Built in M1 (§7.2 C) and always on in the Cloudflare modes; Flexible is counted and logged once, and the dashboard (M5) will warn that it is insecure.
  - **Origin lockdown:** reject (or 403) requests on a Cloudflare-mode host whose peer isn't Cloudflare (or `cloudflared`), so the origin can't be reached by IP to bypass Cloudflare's WAF. It's enforced at the connection stage when *all* hosts on a listener require it.
  - **Authenticated Origin Pulls:** request a client certificate from Cloudflare and verify it against Cloudflare's origin-pull CA (shipped, replaceable) on the TCP listener (§7.2 B, per-SNI `ClientCertificateRequired`). This is the strong form of origin lockdown.
  - **`CF-Ray`** is recorded in access logs and offered as a variable on error pages, next to our request ID, so issues can be correlated with the Cloudflare dashboard.
- **Certificates:** Cloudflare Origin CA certificates can be uploaded like any other (§9), with a hint that they're only trusted by Cloudflare. DNS-01 via the Cloudflare DNS provider (§9) works whether or not the proxy mode is on.
- **Request simulator** (§7.7) takes a "via Cloudflare" toggle, so users can see which IP and country the policies would see.

### 7.7 Request simulator ("Why was this blocked?")

Admins test their rules before or after a change by asking what a request would get, and why. **The simulator runs the real gate code over a real compiled snapshot, never a reimplementation**, so its answer can't drift from what production does.

- **Input:** URL (scheme, host, port, path, query), method, headers (including the user agent and cookies), the peer IP plus how it arrives (direct, through a trusted generic proxy, via Cloudflare Proxy or Tunnel, with or without PROXY protocol), and the TLS context (the name sent in the handshake, ALPN/HTTP version, TLS version). The surfaces fill in sensible defaults, so a URL and an IP are enough.
- **Target config:** the live snapshot, any stored revision, or a *draft*: a document or pending command compiled through the dry-run path (§6.1 steps 1–4) and never published. So "what would this change do to this request" is answerable before committing.
- **Output:** the outcome (forward to upstream X / fixed answer / redirect / 421 / drop) plus the **trace**: client-IP resolution (which header and hop were trusted), host match (exact/wildcard/default), the TLS-policy check, each policy in order with its verdict and reason, the HTTPS redirect, route match (which route, and the matching view of the path), and the upstream (cluster and the destinations currently healthy; load balancing is random, so not which one).
- **No cost on the hot path.** Gate steps report through a tracer passed as a **generic struct type parameter**. Production uses a no-op tracer, which the JIT removes entirely; the simulator uses a recording one. The 0 B allocation tests and the gate benchmarks guard it.
- **Stateful and outbound policies (M6 on)** are simulated without side effects: a rate limit reports "would allow, N tokens left" without taking one; forward-auth reports "would call `<provider>`" (an explicit opt-in may make the real call); geo shows the country it resolved.
- **Parity tests:** for generated configs and requests, the simulator's outcome must equal the real gate's (the M2 `CompiledBehaviourTests` harness is the seed). Each policy added later (M6) must add its trace step and parity cases.
- **Surfaces:** one Application function, `SimulateAsync(target, request)`. It is exposed as `POST /api/v1/simulate` (M4), as the MCP `simulate_request` tool (M8), and in the dashboard simulator page (M5). Access-log entries link into it with the request pre-filled (M7).

---

## 8. Vault (key-material trust boundary)

### 8.1 What it holds
TLS private keys (server certs **and** upstream mTLS client certs) · ACME account keys · EAB HMAC keys · DNS provider credentials · TOTP secrets · internal CA key · Data Protection key ring (cookies/antiforgery) · PoW/forward-auth HMAC keys · basic-auth isn't a secret (hashes only).

### 8.2 Public surface (capability interfaces in `Paduraksa.Core`, all implemented `internal` in Vault)

The contracts are in `src/Paduraksa.Core/Vault` (sketch, with the refinements made when they were written):

```csharp
interface IServerCertificateSource   // the ONE sanctioned egress for serving: Kestrel needs the key in-process
{   ValueTask<SslStreamCertificateContext?> LoadForServingAsync(CertificateId id, CancellationToken ct); }

interface IClientCertificateSource   // second sanctioned egress: upstream mTLS, for SslClientAuthenticationOptions.ClientCertificateContext
{   ValueTask<SslStreamCertificateContext?> LoadForUpstreamAsync(CertificateId id, CancellationToken ct); }

interface ICertificateIssuance       // keys are generated inside, only CSR / public data leave
{   ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken ct);   // CsrResult carries a StagedKeyId
    ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken ct);
    ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken ct);
    ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken ct);   // the root is a normal certificate
    ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken ct);
    ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken ct); }   // a deleted certificate takes its key with it

interface ICertificateImport         // uploads stream straight in; returns public info only
{   ValueTask<CertificateInfo> ImportAsync(CertificateId id, Stream pemOrPfx, ReadOnlyMemory<char> password, CancellationToken ct); }

interface IJwsSigner                 // ACME: Acme builds the JWS, the vault only computes the signature over it
{   ValueTask<AcmeAccountKeyInfo> CreateAccountKeyAsync(AcmeAccountId id, JwsAlgorithm alg, CancellationToken ct);   // public JWK + RFC 7638 thumbprint
    ValueTask<AcmeAccountKeyInfo?> GetAccountKeyAsync(AcmeAccountId id, CancellationToken ct);
    ValueTask<JwsFlattened> SignAsync(AcmeAccountId id, ReadOnlyMemory<byte> protectedHeader, ReadOnlyMemory<byte> payload, CancellationToken ct);
    ValueTask<bool> DeleteAccountKeyAsync(AcmeAccountId id, CancellationToken ct); }

interface IMacKey { MacAlgorithm Algorithm { get; } int TagLength { get; } int Sign(ReadOnlySpan<byte> data, Span<byte> tag); bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag); }
interface IMacKeySource { ValueTask<IMacKey?> OpenAsync(SecretId id, SecretKind expected, CancellationToken ct); }   // refused on a kind mismatch

interface ISecretStore               // no Get(). Secrets go in, capabilities come out.
{   ValueTask<SecretId> PutAsync(SecretKind kind, ReadOnlyMemory<byte> value, SecretOptions options, CancellationToken ct);   // label, MAC algorithm, allowed hosts
    ValueTask<SecretMetadata> ReplaceAsync(SecretId id, ReadOnlyMemory<byte> value, SecretOptions? options, CancellationToken ct);
    ValueTask<bool> DeleteAsync(SecretId id, CancellationToken ct);
    ValueTask<SecretMetadata?> DescribeAsync(SecretId id, CancellationToken ct); }

interface ISecretHttpHandlerFactory  // DNS provider creds: the vault returns the whole transport; the credential never reaches the caller
{   HttpMessageHandler CreateAuthenticatingHandler(SecretId id, SecretInjection how, SecretHttpTransportOptions transport); }
```

Notes on the refinements:
- **Serving flow.** The snapshot compiler is synchronous, so Application preloads the serving contexts (`LoadForServingAsync`) for the certificate inventory (§9.1) before each compile and gives the compiler an immutable `IServerCertificateResolver` over them. The proxy never sees the source, and the vault returns the same context instance for an unchanged certificate (the proxy deduplicates by instance). The vault never disposes an instance it handed out; a replaced one is dropped and collected. The context's `TargetCertificate` carries the key, so the guard against exporting it is the key-material ban list (`BannedSymbols.KeyMaterial.txt`), not the type.
- **Caller-chosen IDs, atomic replacement, staged keys.** Every way of replacing a certificate (`AttachChainAsync`, import, self-signed, local-CA re-issue) keeps its `CertificateId` and swaps key and chain in one transaction, so Specific bindings survive renewals. `CreateKeyAndCsrAsync` stages a key and returns a `StagedKeyId` that `AttachChainAsync` must present; a stale token throws `StagedKeyMismatchException`.
- **Import.** PEM must carry a PKCS#8 key (plain or encrypted), otherwise PFX under the vault's own `Pkcs12LoaderLimits`; at most 1 MiB is read; every failure surfaces as `CertificateImportException` with a reason. The leaf is also refused if it is an RSA key under 2048 bits (`WeakKey`) or a CA certificate (`CaCertificate`); an expired leaf is accepted (the inventory treats it as unusable until renewed). "Usable name" is decided by `ICertificateNameRules`, implemented by the inventory's own normaliser (`CertificateNameRules`) and handed to `AddPaduraksaVault`, so the vault never accepts names the inventory drops (a vault built without one uses its strict rule). The KDF cost of an encrypted PKCS#8 is bounded before it runs (PBKDF2 or PKCS#5/12 iteration count at most 600000, else `Unreadable`) and runs once per key type the certificates could have (normally once); everything found wrong while reading the certificate's extensions after parsing is `Unreadable`, never an exception.
- **Certificate metadata.** `CertificateInfo` has the SHA-256 fingerprint, the serial number as the DER INTEGER content bytes (leading `00` kept), the Authority Key Identifier (with the serial it is the RFC 9773 ARI certificate ID) and the issuers of the chain (for `preferred_chain` selection). The subject has a CN only for a DNS name of at most 64 characters, otherwise it is empty.
- **JWS.** Acme writes the protected header and payload; the vault base64url-encodes, signs (ES256/ES384 as fixed-width `R||S`, RS256 as PKCS#1 v1.5) and returns the flattened-JWS pieces. It refuses a header whose `alg` is not the key's. EAB's inner JWS is built by Acme and signed with an `IMacKey` (HS256). Account key rollover is deferred (§9).
- **MAC keys.** The algorithm is fixed in the secret's metadata when it is stored, and `OpenAsync` checks the expected kind. A handle resolves the current version on every call; `Verify` also accepts the previous version for a grace window after a rotation, `Sign` never does.
- **Secrets carry their destinations.** `SecretOptions.AllowedHosts` (required for `DnsApiToken`) is set by the admin when creating the DNS provider and stored with the secret. The authenticating handler uses its intersection with the provider code's own `SecretInjection.AllowedHosts` (normalised host names, compared with `Uri.IdnHost`).
- **TOTP.** Since there is no get, M4's TOTP needs vault-side generation: the vault creates the secret, returns only the one-time provisioning URI for the QR code, and verifies codes itself.
- **Async lookups, nullable misses.** `Describe` and the public-key lookups are async (they read the vault database), and a missing item returns `null`.

**As built (M3 vault certificates).**
- **Storage.** `CertificateKeys` holds one row per `CertificateId`: the PKCS#8 key sealed by the envelope cipher (AAD purpose `certificate-key`, or `certificate-key/local-ca` for the root, so a leaf row edited into the root fails to open), the public chain PEM (leaf first, intermediates, no root unless the certificate is its own root), a version that is both in the AAD and a concurrency token, and a unique-where-true `IsLocalCaRoot`. A replacement is an update of that row in one `SaveChanges`, which is what makes key and chain swap atomically. `StagedKeys` holds at most one row per certificate (the key sealed to its own `StagedKeyId`, plus the requested names): staging again updates the row in place, so the earlier token no longer names it. Every replacement route drops the ID's staged key in the same transaction, `DeleteAsync` takes both rows, and KEK rotation re-wraps both tables (counted in `KeysRewrapped`). `CertificateInfo` is recomputed from the stored chain; no metadata is duplicated.
- **Chains.** `AttachChainAsync` takes the first certificate as the leaf, requires its public key to be the staged key's and its SANs to cover every requested name (exact, or the wildcard of the parent; a wildcard does not cover the apex or a deeper name), also requires it to be valid now (not expired, not starting later than a five-minute tolerance), orders the rest behind it, and throws `CryptographicException` otherwise (the staged key keeps waiting); no staged row for the token is `StagedKeyMismatchException`, including after a replacement or delete. A certificate counts as the issuer of the one before only if the names link up, the key identifiers agree where both carry them, the issuer is a CA and its key verifies the signature (ECDSA or RSA PKCS#1 v1.5 with SHA-2); strays, same-name impostors, intermediates expired now and the root are dropped. Imports assemble the chain the same way.
- **Vault-signed certificates.** SAN (critical when the subject is empty), serverAuth EKU, key usage (`digitalSignature`, plus `keyEncipherment` for RSA), SKI, AKI (the root's SKI for a leaf), CA:false; serials are 126 random bits (the top two bits of 128 are fixed so the integer is positive and needs no sign byte); validity starts five minutes early. The root has CA:true with path length 0, `keyCertSign`/`cRLSign`, and the name `Paduraksa Local CA (<8 hex>)`; a leaf's validity is capped at the root's remaining validity and, for local-CA leaves and self-signed certificates, silently at 825 days (what Apple's trust store accepts); the root is not capped. The root has no name constraints and path length 0; name constraints (limiting it to the user's own domains) are deferred. The root is never served (`LoadForServingAsync` of its ID is `null`), cannot be replaced or staged over, and `EnsureLocalCaAsync` returns the existing one regardless of the ID it is given.
- **Contexts.** `CertificateContexts` implements both egresses over one cache of `SslStreamCertificateContext` (offline chain) per certificate: a load on a miss takes the vault's gate, writers invalidate inside it, so a context built from a replaced row can never be cached afterwards; the vault never disposes what it handed out. The cache is per process: a second process writing the same database would not invalidate it (the proxy's vault is the only writer; the KEK-rotating CLI does not change certificates). **Seals check the key.** Every operation that seals (certificate keys, staged keys, secrets, account keys) begins a transaction and re-reads the database's KEK check value first, refusing when it is not this process's KEK: a `rotate-kek` run while the service serves cannot make it seal under the old key (a rotation committing after the check makes the write fail). **Staged keys** are sealed to `{certificateId}/{stagedKeyId}` and a digest of their names, and a staging deletes staged keys older than 30 days (abandoned orders). A start whose generated `master.key` does not match the database names `master.key.old` or `master.key.new` when that one does (an interrupted rotation). `EnsureLocalCaAsync` for an ID other than the root's throws `LocalCaExistsException` carrying the real ID and info.
- **Import.** The upload is read into one pinned buffer (at most 1 MiB, stops reading at the cap), PEM key blocks are decoded from bytes (never through a string) and PFX is read under `CertificateImport.Limits` (32 certificates, one key, 600k iteration limits). Wrong password versus unreadable is told by the outer structure (ENCRYPTED PRIVATE KEY or PFX outer sequence parses: wrong password; otherwise unreadable), because the Pkcs12 and PKCS#8 info types are not in the shared framework. Contract addition: `CertificateImportFailure.NoUsableName` (a leaf with no DNS or IP SAN is refused before anything is stored, since nothing could ever select it).

**Why the handler factory returns the whole transport.** A caller-supplied inner handler would see every request header, including the credential; a returned `DelegatingHandler` would let the caller swap its `InnerHandler` until the first send; and a response's `RequestMessage` would carry the injected header back. So the factory returns a sealed `internal` vault handler over a `SocketsHttpHandler` the vault builds itself (no automatic redirects, https only, timeouts, DNS refresh and proxy from `SecretHttpTransportOptions`), adds the credential to a **clone** of the request and sets `response.RequestMessage` back to the caller's original. It refuses non-https requests, hosts outside the allowlist and secrets that are not `DnsApiToken`, on every request. It never follows a redirect: providers handle 3xx themselves, and a redirect to another host fails the host check, so it never carries the credential. Tests substitute the transport through an internal seam in Vault, never through Core.

The `ISecretHttpHandlerFactory` pattern is the key trick. A DNS-provider client in `Paduraksa.Acme` builds an `HttpClient` over a vault-owned handler. The Cloudflare API token is added to the outgoing request *inside* the vault assembly and never exists as a value in Acme code. RFC2136 TSIG gets the same treatment through `IMacKey`.

### 8.3 At-rest encryption
- **Envelope encryption.** Each secret has its own random DEK (AES-256-GCM). DEKs are wrapped by the **KEK**. The AAD is `secretId || kind || version`, which stops anyone swapping ciphertexts between rows.
- **KEK source** in priority order: `PADURAKSA_MASTER_KEY_FILE` (Docker secrets / systemd `LoadCredential`), `PADURAKSA_MASTER_KEY` env, then auto-generated `data/master.key` (0600). The dashboard warns when the KEK sits in the same directory as `vault.db`.
- `paduraksa vault rotate-kek --new-key-file <path|->` re-wraps the DEKs (as built: the new key is read from a file or stdin, never argv; with the generated `master.key` the command stores the new key itself (old file kept as `master.key.old`, new one written through `master.key.new`, all 0600 and flushed with the directory, the old file put back if the rotation fails; standard input that is a terminal is refused and a group-readable key file is warned about); with a key file or the environment as the source the new key must come from a file the operator keeps).
- Plaintext buffers are pinned and zeroed (`CryptographicOperations.ZeroMemory`) after use, best effort.
- **Export.** Config export excludes secrets. An explicit "full backup" produces an archive with `vault.db` encrypted under a user passphrase (Argon2id-derived), and it is audited.
- **As built (M3 vault core).** Format 1 of a sealed value is one BLOB: format byte, KEK ID (8 bytes), wrap nonce, DEK wrapped with AES-256-GCM (AES-KW does not exist in .NET, and GCM lets the wrap carry the AAD), data nonce, ciphertext, tag. The AAD of both operations is `domain || format || (KEK ID) || len(id) || id || len(purpose) || purpose || version`, with ID the typed-ID string (`sec_…`, `acct_…`) and the purpose carrying what decides how the value may be used: a secret's kind, MAC algorithm and allowed hosts, an account key's JWS algorithm. Editing that metadata in `vault.db` therefore makes the value fail to open (a change of hosts is a replace, which seals again). The KEK is base64 of 32 bytes from all three sources, and never becomes a string inside the vault when written; a source that is set but unusable is an error, never a fall-through. `master.key` is created exclusively with mode 0600 and never overwritten, and a new one is not generated if `vault.db` already holds data. An existing generated `master.key` readable by other users is refused (group access warns), judged on the open handle; an explicit key file is only warned about, since Docker secrets are mounted 0444. `vault.db` stores an HMAC check value of the KEK, compared at startup (`IVaultLifecycle.InitializeAsync`, which also reports whether the KEK sits beside the database), and runs with `secure_delete`. The previous value of a MAC secret verifies for 10 minutes after a replacement, then is deleted; `Sign` and `Verify` use cached key material and never wait for a rotation. **KEK rotation** (`IVaultKekRotation`) re-wraps every DEK in one transaction and checkpoints the write-ahead log, so the old wraps are overwritten; it re-wraps only, it does not re-key the data, so a copy of the old database plus the old KEK still opens that copy (to cut that off, replace the secrets). The order is the caller's: store the new KEK durably at its source, rotate, then retire the old one; the vault never stores the new KEK itself, and a crash with the new KEK stored nowhere after the commit would lose the vault. The authenticating handler allows only port 443 unless an allowed-host entry names the port (`host:8443`, in both the secret's and the caller's list), and it refuses a request whose `Host` header differs from its URL (domain fronting) and never copies it. Vault tests live in `Paduraksa.Tests.Vault`, a key-material boundary project.
- **Future hardening:** the capability interfaces are the seam for moving the vault into a separate process (gRPC over a Unix socket with peer credentials) without changing any callers.

---

## 9. Certificates & ACME

- **Own ACME client (RFC 8555)** in `Paduraksa.Acme`, about 1.5k LOC. We write our own instead of using Certes/ACMESharp because the account key must stay in the vault (`IJwsSigner`), and we want first-class **ARI (RFC 9773)** and **ACME profiles** (e.g. Let's Encrypt `shortlived`).
- **Challenges:** HTTP-01 (served by the public app's gate from an in-memory store). DNS-01 (provider plugins + propagation check by querying authoritative NS directly). TLS-ALPN-01 later.
- **DNS providers (v1):** Cloudflare, deSEC, Hetzner, DigitalOcean, Porkbun, DuckDNS, Route53, RFC2136, acme-dns, plus "manual" (show the TXT record and wait for confirmation) and a webhook provider. Interface: `CreateTxtAsync / DeleteTxtAsync / ValidateCredentialsAsync`. Every provider needs a **"Test credentials"** button in the UI.
- **CAs:** Let's Encrypt (prod/staging), ZeroSSL (EAB), Google Trust Services (EAB), Buypass, custom directory URL.
- **Account key rollover (RFC 8555 §7.3.5) is deferred past v1.** Recovery from a lost or compromised account key is a new account. The shape when it comes is additive on `IJwsSigner`: `StageAccountKeyAsync` (generate the new key, return its public JWK), `SignWithStagedAsync` (sign the inner `keyChange` JWS with it) and `CommitStagedAccountKeyAsync` (swap it in once the CA accepted the change). Nothing existing changes.
- **Renewal:** a background scheduler uses the ARI window if offered, otherwise renews at ⅓ of lifetime remaining, with jitter, exponential backoff and rate-limit awareness. On success the snapshot is recompiled and published (zero-downtime cert swap). On failure: notification + dashboard banner.
- **Auto-matching:** hosts with `CertificateBinding = Auto` get the most specific valid cert covering their names (exact beats wildcard). The dashboard flags hosts without coverage and offers "Issue cert for these names".
- **Other sources:** uploaded PEM/PFX (straight to the vault through the API's streaming endpoint, so the dashboard upload goes to that endpoint rather than through the Blazor circuit). Self-signed. **Local CA**: an internal root generated in the vault, issuing leafs for LAN-only hosts, with the root cert downloadable for installing as trusted.
- **Serving:** `SslStreamCertificateContext.Create(cert, chain, offline: true)` is prebuilt per snapshot, so no chain building happens during a handshake. **OCSP stapling is out of scope for v1.** Let's Encrypt dropped OCSP in 2025, and we don't want to promise cross-platform server-side stapling behaviour.
- **Expiry monitoring** covers every certificate, not just ACME ones. Uploaded and self-signed certs have no renewal path, so they get the same 30/14/7/1-day notifications and a dashboard badge. Local-CA leaves can be re-issued in one click (or automatically).

### 9.1 As planned for M3

Storage and compile-time decisions for certificates (decided before M3 work started):

- **Certificates, ACME accounts and DNS providers are not part of the config document.** They are operational resources in the config database (`Certificates`, `AcmeAccounts`, `AcmeOrders`, `DnsProviders`): metadata, the public chain and renewal state. Secrets appear only as `SecretId` references into the vault (§5.1, §8). Application services change them and audit every change; they do not create config revisions. Config export (§11.5) carries certificate metadata only.
- **The document references certificates only by binding:** Auto, Specific (`CertificateId`) or None. The compiler resolves bindings against the **certificate inventory** (the valid certificates and the names they cover), passed in alongside the document. Auto picks the most specific valid certificate covering the host's names: an exact name beats a wildcard, then the latest `NotAfter` wins.
- **A certificate change (issued, renewed, uploaded or deleted) recompiles the latest revision with the new inventory and publishes it**, without creating a revision. Deleting a certificate that a Specific binding uses is refused.
- **Replacing a certificate keeps its ID and is atomic.** Renewal, re-issue, a new upload and a new self-signed certificate on an existing `CertificateId` swap key and chain together in one vault transaction, so Specific bindings survive. ACME and manual CSR flows stage the new key first: `CreateKeyAndCsrAsync` returns a `StagedKeyId` that `AttachChainAsync` must present, so an overlapping second staging cannot silently take the first one's place (it fails with a documented exception instead).
- **The local CA root is a normal certificate.** `EnsureLocalCaAsync(CertificateId, …)` creates it under a caller-chosen ID, so it has a Certificates row, expiry monitoring and a downloadable public chain like any other. Rotating or deleting the CA root is later work (with a plan for leaves issued under the old root).
- **DNS credentials carry their own destinations.** The allowed hosts of a DNS token are metadata the admin sets when creating the DNS provider, stored with the secret (`SecretOptions.AllowedHosts`); the vault's authenticating handler uses their intersection with the provider code's own list, speaks only https, never follows redirects, and is the whole transport (§8.2).
- **Shared EF/SQLite infrastructure lives in `Paduraksa.Storage`** (§1): typed-ID conventions and converters, SQLite pragmas and the connection-string builder, the UTC timestamp converter. Vault may reference only Core and Storage, so it gets these without seeing any config entity.

### 9.2 As built: the certificate inventory (M3)

- **Storage.** `Certificates` (one row per `CertificateId`: source `acme`/`uploaded`/`self_signed`/`local_ca`/`local_ca_root`, status `pending`/`active`/`failed` with a message, subject, issuer, serial, AKI, SHA-256 fingerprint, key algorithm label, `NotBefore`/`NotAfter`, chain issuers, the **public** chain PEM, label, notes, created/updated) and `CertificateNames` (one normalised row per SAN, `NOCASE`, indexed). Expiry is not a status: it follows from `NotAfter` and the clock. Added as a new migration (`Certificates`), not a regenerated `Initial`, so a development database upgrades and parallel branches don't collide. No private key column exists; host bindings reference a certificate by ID with no foreign key (the document that names it is a revision, not a row).
- **`CertificateService`** (Application, DTOs in `Contracts.Certificates`): `RegisterAsync` (add, or replace the metadata under the same ID: a renewal), `RegisterPendingAsync`, `SetStatusAsync`, `UpdateDetailsAsync`, `DeleteAsync`, `ListAsync` (by source, status, or "covers this name"), `GetAsync` (with the chain). It never calls the vault: the vault flows call it after they created the key and chain. Every change is audited in its own transaction (`certificate.add`, `certificate.update`, `certificate.delete`; refusals too), creates **no revision**, and, when it affects serving (validity, names, chain, status), recompiles the latest revision with the new inventory and publishes it through the config writer (`ConfigService.ApplyCertificateChangeAsync`), so publishes stay ordered. Deleting a certificate that a Specific binding or the default certificate uses is refused with the paths of the referencing hosts. Names that are no DNS name, wildcard or IP address are dropped; none left is invalid.
- **Preload and the inventory.** Before each compile `CertificateInventoryLoader` reads the metadata (plus `IBuiltInCertificates`), judges each certificate against `TimeProvider` (active and inside its validity period), works out which valid certificates the document can choose (the named default, Specific bindings, the best match per name of every enabled Auto host), loads exactly those through `IServerCertificateSource`, and hands the compiler an immutable `CertificateInventory` whose resolver covers the loaded ones. A certificate the source can't load (null or an exception) is left out and the selection runs again, so Auto falls to the next best certificate. Contexts are used as the source returns them: an unchanged certificate keeps its instance, so the proxy's certificate table keeps one options object per certificate, and a renewal (a new instance) takes effect with the next publish. The inventory is a compile input, never mutated.
- **Selection is per name.** The proxy's certificate table maps names to certificates, so each DNS name of a host gets its own best certificate (`HostSpec.NameCertificates`), not one certificate for the whole host. A certificate covers a name when it lists it, or lists the wildcard of its parent (`*.example.com` covers `www.example.com`, not `example.com` or `a.b.example.com`); a wildcard host name is covered by the same wildcard. The best match is exact over wildcard, then the latest `NotAfter`, then the latest `NotBefore`, then the smallest ID. IP names never reach the table and ride the default certificate. Specific gives every name of the host that certificate (a certificate that doesn't cover them is a warning).
- **Degraded, never failed.** A name no valid certificate covers gets no certificate of its own: it is served with the default certificate, or rejected when there is none, and the compile reports `certificate.auto.noCoverage` (a `ConfigWarning` on the compilation, `ConfigService.Warnings` and the command result). A Specific binding, or a named default, whose certificate is missing, expired, pending, failed or without a key degrades the same way (`certificate.specific.unusable`, `certificate.default.unusable`), at startup, for the last-known-good fallback and for republishes: one bad certificate never fails the whole config, and an unreadable certificate table gives an inventory of the built-in certificates only. A **command** may not introduce such a binding (a new or changed Specific binding, or a changed default, naming an unusable certificate is `host.certificate.unusable` / `settings.defaultCertificate.unusable`, also in dry runs), but it is not blocked by a binding that went bad since. A rollback and the system's restore after a lost database are exempt: they put back a config that was accepted once, and degrade with warnings like startup does (the inventory may be gone with the database).
- **The placeholder.** (A year of validity: it is dev-only and new in every process, so only a process that runs for a year outlives it, and a restart replaces it.) `PlaceholderCertificates` is a source and a built-in inventory entry for the in-memory certificate under a well-known stable ID, and the inventory's fallback default. As built (vault certificates), it is composed **after** the vault (`CompositeServerCertificateSource`: a certificate the vault holds is never shadowed) and wired in at all only when `Paduraksa:Development:PlaceholderCertificate` is on (default: Debug builds yes, release builds no, §3); off, the source falls back to nothing, there is no built-in entry, and a document that still names the ID degrades like any missing certificate.
- **Validity follows the clock.** A certificate counts as valid from five minutes before its `NotBefore` (one named tolerance, for clock skew) until a second after its `NotAfter`. The inventory reports the earliest such boundary still ahead (`NextBoundary`, carried on the compilation as `ValidityChangesAt`); when a snapshot goes live the config service arms one `TimeProvider` timer for it (replaced by every newer live snapshot, cancelled on dispose), and when it fires it republishes the live config under the writer lock, coalesced with other publishes. So expiry and activation need no other trigger. A renewal under the same ID whose `NotBefore` is in the future leaves the names uncovered (with a warning) until that boundary, because the one row now describes the new certificate.
- **Republishing never publishes the empty config.** A certificate change or a validity republish compiles the database's latest revision, or, when the start is degraded on the last-known-good file or the database has no revision (it was lost), the last-known-good document. Without either it publishes nothing and logs why. A last-known-good republish doesn't end a degraded start. The compile happens inside the change's transaction, before the commit; after the commit nothing can fail the call.
- **After a database loss** the certificate rows are gone while the vault still holds the keys, so restored Specific bindings degrade with warnings until the certificates are registered again. Later work, not in this branch: the vault re-describes the certificates it holds keys for (`CertificateInfo` and source per ID) so the service can re-register them.
- **The vault flows** (`CertificateOperations`, DTOs in `Contracts.Certificates`; Application): upload (optionally over an existing uploaded certificate), self-signed, local CA root, local-CA leaf, re-issue (self-signed and local-CA only, same ID and names) and delete. Each is one authorizer call (`ICertificateOperationAuthorizer`, allow-all until M4), the vault call and the `CertificateService` registration (so audit and republish are the service's). **Ordering** decides what a failure leaves: a new certificate whose names are known (self-signed, leaf, root) is registered *pending* first (validates names, visible, never served), then the vault creates key and chain, then the same row is registered active; a failed vault call or a refused registration deletes the pending row and the key again (not cancellable), and a crash between leaves a visible pending row, never a hidden key. A new upload learns its names from the vault, so it goes vault first and is rolled back (key deleted) if the registration is refused; a key with no row is never loaded. Replacing an existing ID swaps in the vault first (it cannot be undone) and then updates the row; if that fails the result says so, it is logged, and repeating the operation repairs it. Delete asks the inventory first, which refuses (nothing touched in the vault) while a Specific binding or the default certificate uses it, then deletes the key; a failed key deletion is a warning in the result (the certificate is already gone from the inventory, the key unreachable). Deleting the local CA root is refused while local-CA leaves exist; a delete with no row still removes an orphaned key. The root has no names, so `RegisterAsync`/`RegisterPendingAsync` accept an empty name list for `CertificateSource.LocalCaRoot`. The label (and what the inventory would refuse about the request) is checked before the vault is called, so a replacement is never reported invalid after the vault took it. Only an *active* root row counts as the local CA: a root row a crash left pending is completed under its own ID, and when the vault holds the root under another ID (the inventory was lost) it is recorded under that ID (`LocalCaExistsException`), which also repairs the database-loss case for the root. The key deletions after a commit and the rollbacks run with `CancellationToken.None`, and a failed creation always deletes the key (idempotent), so a cancelled request leaves no orphan. Request DTOs carry `KeySpec` as its stable name (`ecdsa-p256`, `rsa-2048`, ...).
- **Built-in IDs** (`IBuiltInCertificates`) can't be registered in the table: the built-in would silently win.

### 9.3 As built: ACME issuance and renewal (M3)

- **Storage** (migration `AcmeIssuance`). `AcmeAccounts` (directory URL, contacts, status `active`/`deactivated`, account URL, key algorithm, EAB key id (`EabKid`, the CA's identifier, not a typed ID) and `EabSecretId`, ToS agreed, label), `DnsProviders` (kind `cloudflare`/`desec`, label, `CredentialSecretId`) and `AcmeOrders`: **one row per certificate** (unique `CertificateId`, cascade with the certificate; account and provider are `Restrict`; DNS-01 requires a provider by a check constraint), reused by every issuance and renewal, so "one issuance per certificate" is a property of the table. Enums by stable name with check constraints (a new DNS kind is an enum member plus a migration). The `Certificates` row stays the serving metadata (`source = acme`). Secrets appear only as `SecretId`; the staged key is a token, the stored CSR is public.
- **Services** (Application, DTOs in `Contracts.Issuance`): `AcmeAccountService` (create: `IJwsSigner.CreateAccountKeyAsync` → `newAccount`, EAB through `IMacKeySource`; a refused registration deletes the key again and records nothing; deactivate is final, refused while certificates use the account, and deletes the vault key; list/get), `DnsProviderService` (create over an existing `DnsApiToken` secret, delete refused while in use, "Test credentials"), `AcmeIssuanceService` (below), `AcmeRenewalScheduler` (hosted service), `AddPaduraksaIssuance` for the container. Accounts and providers are audited through `ConfigService.ApplyOperationalChangeAsync`: the config writer's lock and transaction, so every audit event stays on the one hash chain, with no revision and no publish. Server wiring is left to the vault wave (only the challenge store is registered in `SharedServices`).
- **Orchestrator state machine.** `RequestCertificateAsync` validates (names normalised like SANs; HTTP-01 refuses wildcards, DNS-01 refuses IPs and needs a provider; the account must be active), registers a pending certificate (`CertificateService.RegisterPendingAsync`), inserts the order row, and runs. `RenewAsync` is the one entry for "do the next step": a `valid` row starts a new order (decided from the row read under the certificate's guard) (the CA is told `replaces` = the ARI ID of the certificate being renewed, retried without it on `alreadyReplaced`), an unfinished or `failed` row continues. A run **follows the CA's order status**: read the stored order (an order that is `invalid` or `expired`, or that the CA refuses to show with a permanent problem such as 404, is forgotten; `accountDoesNotExist` is not: it marks the account deactivated), else `newOrder` (URL saved, row `ordered`); `pending` → fulfil every pending authorization (publish, respond, wait, always clean up); `ready` → `CreateKeyAndCsrAsync`, save the staged-key token and the CSR (row `finalizing`) **before** `finalize`; `processing` → wait; `valid` → download (preferred chain by issuer CN), `AttachChainAsync`, then **save the returned public `CertificateInfo` on the row (`AttachedInfo`)** and finish with `CancellationToken.None`: `CertificateService.RegisterAsync` (active; existing republish; retried a few times if the store is busy), plan the renewal, row `valid` (`AttachedInfo` cleared), audit `acme.order.issued`. A run does not start when the config store is degraded (nothing could be recorded). Row statuses: `pending`, `ordered`, `finalizing`, `valid`, `failed`.
- **Resume.** Because the CA's state decides, a restart (or a retry) continues the same order where it stands: finalize is submitted from the saved CSR, a `processing` or `valid` order is attached to the saved staged key, a challenge already `processing` is only waited for. Cancellation (shutdown) leaves the row as it was; every other failure is recorded. A row with `AttachedInfo` skips the download and attach and only registers, so cancellation or a refused registration after the attach cannot strand the new certificate. Remaining gap, hard crash only: the process dying between the vault's attach returning and `AttachedInfo` being saved leaves the vault serving the new certificate under the old metadata (the staged key is spent, so the retry fails and the next one starts a fresh order, which repairs it); closing it needs the vault to re-describe a certificate (§9.2, after a database loss).
- **Failures** are classified by `AcmeFailureKind` (and DNS provider/own failures), stored on the row (message, `Attempts`, `LastAttemptAt`, `RetryAt`) and audited (`acme.order.failed`). A certificate that was never issued (or has expired) becomes `failed` with the message; a renewal that fails while the old certificate is valid leaves it `active` and serving. An order that cannot be continued (invalid or deactivated authorization, invalid order, spent staged key, CSR refused) is dropped so the next attempt makes a new one; anything else resumes (a finalize that races with another and meets `orderNotReady` on a processing order continues with it). An account the CA no longer knows is marked deactivated. Messages are sanitised and cut to 1024 characters.
- **HTTP-01.** `AcmeChallengeStore` (Proxy; `IAcmeChallengeStore` in Core is the publish side): in memory, at most 1024 tokens, each counted by its holders (two issuances sharing a pending authorization keep it answered until both removed it), entries validated (base64url token, printable-ASCII key authorization) and stored as a ready `PrecomputedResponse`. The gate (`GateMiddleware`, optional constructor argument, registered once in `SharedServices`) answers **plain-HTTP GET/HEAD** `/.well-known/acme-challenge/{token}` for an exactly matching published token with 200 `text/plain` before host lookup and every policy, 0 B on hit and miss. Any other request under the path (unknown token, another method, HTTPS) falls through to the normal pipeline rather than a blanket 404, so an upstream that runs its own ACME client keeps serving its own challenges. A hit is never logged.
- **DNS-01 seam.** `IDnsProvider` (`CreateTxtAsync`/`DeleteTxtAsync`/`ValidateCredentialsAsync`, idempotent, `DnsProviderException` says transient or not) and `IDnsProviderFactory` live in `Paduraksa.Acme.Dns`. A wildcard and its base name publish two TXT records at one name; all are created, the orchestrator waits `DnsPropagationDelay` (30 s; the authoritative-NS propagation check replaces it), responds, and deletes the records afterwards even on failure. The real Cloudflare/deSEC providers come next.
- **Scheduler timing** (`RenewalSchedule` is pure arithmetic, `TimeProvider` and an injectable random source drive the rest). The row says what is due: **`NextRenewalAt`**: with an ARI window, a uniformly random point in it (never after `NotAfter`; a window in the past is due now); without, `NotAfter` − ⅓ of the lifetime, moved earlier by up to 10 % of that third (30 days of 90, 2 days of 6). An ARI pick is never later than that ⅓ rule (a window at the end of life must not become an outage). **`AriCheckAt`**: the response's `Retry-After` bounded to 15 min…24 h (6 h without); a recheck keeps the old pick if it is still inside the window, else picks again. **`RetryAt`** after a failure: exponential from the kind's base, ±20 % jitter that stays symmetric and never passes the cap (the doubling stops at cap/1.2): transient 5 min → 6 h, permanent 1 h → 24 h (a retry still happens: a DNS record may be added later), protocol violation 30 min → 24 h, rate limited 1 h → 7 d but never less than the CA's `Retry-After`. `RunOnceAsync` (every minute, after a start delay, one certificate at a time) lists the orders and calls `AcmeIssuanceService.RunDueAsync` for each, which **re-reads the row under the certificate's guard** and applies the due rules to that fresh row (the listing can be minutes old): unfinished order → resume; `failed` and `RetryAt` passed → retry; `valid` and `NextRenewalAt` passed → renew, else `AriCheckAt` passed → refresh ARI (also under the guard). Failures are logged and kept on the order and the certificate; notifications come with the channels (M7).
- **Tests.** The orchestrator, scheduler and services run in `Tests.Issuance` (its own project, because it alone lifts the key-material ban for its test code) on real SQLite and the real config writer against the ACME client's fake CA, a fake vault (`ICertificateIssuance` with staging rules) and a virtual clock; `Tests.Proxy` covers the gate; `Tests.Unit` the schedule arithmetic. `AcmePebbleTests` run the orchestrator against a real **Pebble** (Docker, `PEBBLE_VA_ALWAYS_VALID`, random ports, skipped when Docker or the image is unavailable; `PADURAKSA_PEBBLE=off` skips, `PADURAKSA_PEBBLE_DIRECTORY` uses a running one): real CSRs from a stand-in issuance, ARI by the computed certificate ID, `replaces`, IP and wildcard identifiers. Pebble fetching the challenge through the gate, and the real vault in place of the stand-in, come when the vault certificate flows are merged.

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
`GET /healthz` (process alive, snapshot loaded) and `GET /readyz` (listeners bound, a snapshot live, not draining; vault unlocked from M3) on the admin port, plus the loopback fallback listener when enabled. Unauthenticated, no details beyond status. Readiness means "serving traffic", not "the config database is reachable": a degraded start (§6.4) serves the last-known-good config and is still ready, so `/readyz` answers 200 with the body `degraded` instead of `ready`. These are for **external** monitors and orchestrators (Uptime Kuma, Kubernetes probes, Gatus), which can probe them over HTTP without spawning anything. The image's own Docker `HEALTHCHECK` is the exception (§16).

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
- The import format above is a friendlier DTO than the document (M8): optional IDs, references by name (`profile: media`, `upstream: jellyfin`), `kind:`. Import maps it onto the strict canonical `ConfigDocument` (§6.4), assigning IDs and resolving names; only that document is stored, and export writes it back in a form that imports to the same document.
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
3. **Request simulator / "Why was this blocked?"** (§7.7) Enter a URL, IP, UA and headers and see the exact decision chain (host match → each policy → route → upstream), against the live config, an old revision or the change being reviewed. The same function is exposed to the API/MCP, and access-log entries link straight into it.
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
| S3 ✅ *(0 B gate; see §7.1)* | **Zero-alloc gate on Kestrel** | Request-scoped state without `Items`, span-based host lookup, feature reuse per connection. | BenchmarkDotNet + in-process test shows 0 B/request from Paduraksa code. |
| S4 ✅ *(partial: `FrozenDictionary` lookup not covered)* | **Typed IDs × EF Core 10 × SQLite** | Convention converters, BLOB big-endian GUIDs (`UuidV7Generator` + `TryWriteBytes(span, bigEndian: true, out _)`), JSON complex types containing typed IDs, LINQ translation of comparisons, `FrozenDictionary` `OrdinalIgnoreCase` alternate span lookup. | Round-trip, query and index-ordering tests pass. **Result:** done as real code in `Paduraksa.Data` (the generic parts moved to `Paduraksa.Storage` in M3) (`tests/Paduraksa.Tests.Data`, real SQLite). Everything worked as planned: BLOB/INTEGER/JSON-string storage, `ORDER BY` = `CompareTo` = string order (same millisecond too), comparisons translate directly, and an Int64Ordered key is the rowid alias without `AUTOINCREMENT`. Changes: discovery replaces EF's `PropertyDiscoveryConvention` instead of scanning (§4); JSON stores strings. |
| S5 ✅ *(implemented as the real host)* | **Two WebApplications, one process** | Shared singletons, graceful shutdown order, logging, health. | Both apps start/stop cleanly; integration tests can host both. |
| S6 ✅ *(partial: YARP health in M1; (a) with M6, (b) with M7)* | **State continuity across publishes** (§6.3) | Rate limits and metrics must not reset on config edits. | Load test with publishes every 100 ms shows (a) continuous 429 behaviour, (b) monotonic counters, and (c) **YARP destination health preserved**: a destination marked unhealthy (active or passive) stays unhealthy across reloads that don't touch its cluster, and no healthy destination flaps. **M1** (`StateContinuityTests`): no request errors and (c), over real YARP reloads, policy-only edits and unchanged configs. (a) and (b) are tested when the rate-limit table (M6) and metric slots (M7) land; stubs would test nothing. |
| S7 ✅ *(baseline recorded; tail stalls under investigation)* | **Baseline perf vs nginx/Caddy** | Know where we stand early. | `oha`/`wrk2` numbers on the same box recorded in `bench/README.md`. **Result** (`bench/results/2026-11-07-baseline.md`, raw data alongside): Paduraksa reaches ≈0.54–0.71× nginx's throughput and 2.1–3.6× Caddy's; at fixed rates its median latency is within 0.15 ms of nginx's but its p99 is 1.4–2.3× nginx's, from process-wide stalls that aren't GC-driven and are still under investigation. |

---

## 14. Milestones

Each milestone ends with a working, testable increment.

| M | Scope | Done when |
|---|---|---|
| **M0 Foundations** ✅ *(done: typed IDs, CI, arch tests, host, data layer, spikes S1–S5)* | Repo, CI (build, test, format, arch tests), `[TypedId]` generator + base62 + EF/JSON/Yaml converters, Result types, clock abstraction, spikes S1–S5 | Generator tests green; arch rules enforced in CI |
| **M1 Proxy core** ✅ *(done: proxy core, end-to-end tests, benchmarks, S6 in scope, S7 baseline)* | Snapshot model + store, gate middleware, host table, client-IP resolution incl. Cloudflare `Proxy`/`Tunnel` modes with baked-in ranges (§7.6), TLS SNI selection, YARP provider, H1/H2/H3, HTTP→HTTPS redirect, default host, hard-coded test config | Benchmarks + S7 baseline; S6 continuity test (YARP health, no request errors under publishes); zero-alloc tests pass (gate incl. client-IP resolution in every trusted-proxy mode); end-to-end tests through `PaduraksaHost` (per-SNI certs + hot swap, redirect port, PROXY protocol, Cloudflare/generic client IP, upstream headers, `Alt-Svc`, H3 in Docker) |
| **M2 Config store** ✅ *(done: config store wired into the process, startup recompile, last-known-good with degraded starts; change → live median 11–15 ms end to end)* | EF model, migrations, Application command pipeline, validation, compiler, revisions, diff, rollback, audit (hash chain), last-known-good | Change → revision → live in < 100 ms; rollback works |
| **M3 Vault & certs** | KEK/DEK store, capabilities, upload, self-signed, local CA, ACME client (HTTP-01 → DNS-01 Cloudflare/deSEC), renewal scheduler, ARI | Pebble-based integration tests issue + renew; banned-API rules hold. Shared EF/SQLite infrastructure lives in `Paduraksa.Storage` (decided, §9.1). Closed in §9.2: the compiler tolerates a missing or unusable certificate (a warning, never a failed start), and the placeholder has a stable ID and is the fallback default. |
| **M4 Admin backend** | Admin app, local users, cookies, TOTP, roles, API tokens, REST API + OpenAPI, first-run setup-token gate, **request simulator core** (§7.7: gate tracer, `SimulateAsync` over live/revision/draft, `POST /api/v1/simulate`, parity tests) | API-only end-to-end setup possible; the simulator explains every gate outcome M1–M3 can produce and matches the real gate in parity tests |
| **M5 Dashboard + wizard** | Layout, hosts/certs/settings pages, review-diff flow, history, audit, wizard | New user from `docker run` to proxied host via UI only |
| **M6 WAF** | IP/geo/rate-limit/size/path/UA policies, Cloudflare range refresh job, `CF-IPCountry` geo, origin lockdown + Authenticated Origin Pulls (§7.6), presets, error pages, robots, maintenance mode, a simulator trace step for every new policy (§7.7) | Policy unit tests + simulator parity tests; S6 rate-limit continuity (429 behaviour continuous across publishes) |
| **M7 Observability** | Counters, histograms, `/metrics`, health checks, notifications, live tail, rollups | Dashboard overview fully live; S6 metric continuity (counters monotonic across publishes) |
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

- **Container:** `ghcr.io/<org>/paduraksa:<ver>`, multi-arch (amd64/arm64), Ubuntu Noble base + `libmsquic` (pinned), `ASPNETCORE_HTTP_PORTS` cleared, non-root. Listens on **80/443 (tcp+udp)/9443 inside the container**, mapped 1:1 in compose, so `Alt-Svc` and redirects never see a translated port. Non-root binding below 1024 works because Docker (20.10+) sets `net.ipv4.ip_unprivileged_port_start=0` in the container's network namespace. For `network_mode: host` or other runtimes, the image also gives the apphost `cap_net_bind_service` (`setcap`). If someone still remaps ports, `Paduraksa:Listen:PublicHttpsPort` drives `Alt-Svc` and redirects. One volume: `/data`. Health: `/healthz` and `/readyz` (§11.3). The image's own Docker `HEALTHCHECK` uses `paduraksa doctor --liveness` (which calls `/healthz` over loopback) at a 30 s interval, because the image ships no curl/wget. That costs a short-lived process per probe, which is acceptable at that interval. External monitors should hit `/healthz` directly.
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
- **v0.9:** S4 outcome: typed IDs through EF Core conventions, JSON IDs as strings and never joined against column IDs in SQL, pragmas and timeouts, Int64Ordered seeding (§4, §5). S3 outcome (0 B gate, realistic Kestrel/YARP costs, generic-host pipeline instead of `WebApplication` for the public app, per-connection/per-request state mechanisms, host lookup, IP parser, allocation-test rules) (§7.1). M0 complete.
- **v0.10:** M1 proxy core as built: snapshot model and store, gate, compiler input records, publisher state machine, route-matching semantics, public app on the generic host (§3, §6.2).
  - Integration: public listener layout and `Paduraksa:Listen` options (`PublicHttpsPort`, `Http3`, PROXY protocol per listener) (§3). Typed `Certificates`/`TrustedProxies` snapshot slots, certificate references through `IServerCertificateResolver`, and certificate hot swap by publishing (§6.2, §7.2 B). Client-IP resolution through the global trusted-proxy policy, ClientInfo on QUIC verified (§7.2 A/C, §7.5, §7.6). Exact upstream forwarding-header semantics, every `cf-*` header stripped unless trusted, one trusted request scheme for the redirect and `X-Forwarded-Proto` (§7.2 C/E, §7.6). Per-host TLS settings re-checked per request, 421 on a weaker coalesced connection (§7.2 B/C). S6 scoped to YARP health in M1; rate-limit and metric continuity moved to M6/M7 (§6.3, §13, §14).
- **v0.11:** M1 done. S7 baseline recorded in `bench/results/2026-11-07-baseline.md` (§13, §14). §1 lists `Tests.Proxy` and `Bench.Runner`. §7.5 describes the as-built builder; the slim-builder note is kept as spike history.
- **v0.12:** M2 done. The config document is the source of truth and the EF entities are its projection (§6.4). Typed IDs reach the proxy model. Canonical JSON, validation and diff work over the document. The compiler resolves the settings layers and places IP allow lists in the host and route chains (§6.4). Commands carry per-object versions that are revision numbers, and rollback is committed as a new revision. The audit log is a hash chain. Startup recompiles the latest revision, and `last-good.json` gives a degraded start or a restore (§6.1, §6.4, §11.3). The M3 open items now include the placeholder certificate (§14).
- **v0.13:** Request simulator designed (§7.7): runs the real gate over live, stored or draft config through a zero-cost generic tracer, with parity tests. Its core moves forward to M4; M6 adds a trace step per policy (§12, §14).
- **v0.14:** M3 foundations. Shared EF/SQLite infrastructure moved into `Paduraksa.Storage`, which Data and Vault reference (§1, §5; the §14 M3 open item is closed). The vault capability contracts are written in `Paduraksa.Core.Vault` (§8.2): the authenticating HTTP handler is the whole transport and never exposes the credential, secrets carry their allowed hosts and MAC algorithm, certificates are replaced atomically under a stable ID with staged keys, and enums persist by name. Certificate, ACME account and DNS provider storage and compile-time decisions are recorded (§9.1), and account key rollover is deferred (§9).
