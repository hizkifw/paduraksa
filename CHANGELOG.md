# Changelog

What changed in the design and the code, newest first. Every entry is a version of the design document (`docs/PLAN.md`), kept here as it was written, with only the formatting tidied.

Section numbers written as `(§7.4)` in the entries below refer to the numbering of the design document before it was split into `docs/PLAN.md` and `docs/design/`. This is where those sections live now:

| Old section | Now |
|---|---|
| 0, 1, 2 | `docs/PLAN.md` (goals, solution layout, development rules) |
| 3, 3.1 | `docs/design/hosting.md` |
| 4, 5, 6 | `docs/design/config.md` (identity tables of 5.2: `docs/design/identity.md`) |
| 7.1, 7.2, 7.5, 7.7 | `docs/design/hot-path.md` |
| 7.3 | `docs/design/waf.md` |
| 7.4 | `docs/design/observability.md` |
| 7.6 | `docs/design/cloudflare.md` |
| 8, 9 | `docs/design/vault-and-certificates.md` |
| 10 | `docs/design/identity.md` |
| 11 | `docs/design/admin-surfaces.md` |
| 12 | `docs/design/dashboard.md` |
| 13, 14 | `docs/design/milestones.md` (summary in `docs/PLAN.md`) |
| 15 | `docs/design/testing.md` |
| 16 | `docs/design/deployment.md` |
| 17 | `docs/PLAN.md` (open questions) |

## v0.30

Drafts finish M10; the threat model's gaps closed; counted body bytes; the dashboard made consistent; the design document split into an overview and per-area pages.

- **Drafts ([dashboard.md](docs/design/dashboard.md), [admin-surfaces.md](docs/design/admin-surfaces.md)):** one open draft per user, kept across restarts for 30 days; every page and picker reads the config with the draft applied; Review and Apply commit the whole set as one revision with one audit event. Apply is whole or nothing: the review returns a token covering what would be committed, and an apply whose plan no longer matches it, or with any entry changed elsewhere, commits nothing and shows the fresh review (the owner's rule). `/api/v1/drafts` behind `config:write`; MCP applies need the review's token.
- **Threat model ([threat-model.md](docs/threat-model.md)):** assets, boundaries, attackers by boundary with the mitigations and their tests; its small gaps closed: the MCP child's network and its routes back to the parent shut by Landlock and a seccomp filter (G2), the master-key warning and the key-file secret in compose (G3), the admin port on loopback in compose (G4), the Operator role described honestly (G5), `paduraksa.db` at 0600 (G7), CodeQL and an explicit NuGet audit (G9), the README (G13).
- **Connections ([hot-path.md](docs/design/hot-path.md)):** a per-IP connection cap (256 by default) and a connection deny list from a named access list, both before TLS on TCP and QUIC, trusted proxies exempt (G1); the public listeners' slow-client and connection limits set explicitly (G8).
- **Upstreams and certificates ([vault-and-certificates.md](docs/design/vault-and-certificates.md)):** Host header modes (the client's, the destination's, a fixed name) with SNI following the Host sent, at 0 B per request; `_acme-challenge` CNAME delegation; DNS-01 tested against a validating Pebble; local CA root rotation, with new roots unlimited and name constraints only when the operator chooses names (the owner's decision).
- **Counted body bytes ([observability.md](docs/design/observability.md)):** the bytes actually sent and received per request, including the proxy's own answers and WebSocket frames, replace the declared `Content-Length` in metrics, analytics, Prometheus, the access log and the live tail; bytes in are new everywhere; 0 B per request, 0.4-2 % of throughput in the bench.
- **Notifications ([observability.md](docs/design/observability.md)):** a durable database outbox with at-least-once delivery (an event ID on every webhook), retries, and a notification history page and API.
- **Dashboard:** one SegmentedControl, CopyableCode and pill style across every page, floating labels, ChoiceCards, one primary action per list page; the MCP setup prefilled in the new-token drawer; no second-factor nagging on the overview (the setup checklist and Users show it); Analytics without its accuracy note, the visitors' error under the count.
- **API description ([admin-surfaces.md](docs/design/admin-surfaces.md)):** shipped as a generated file instead of generated on the first request of every boot (9 s of CPU), with a test that fails when it drifts.
- **Logs:** a client that went away mid-write is one Debug line and a counter, not an Error with two stacks.
- **Tests and CI ([testing.md](docs/design/testing.md)):** the load-sensitive flakes fixed at their cause and written up in development.md; CI runs two test apps at a time; the fuzz corpora grown by an hour per target (no crash).
- **Docs:** `docs/PLAN.md` is now an overview; the as-built detail moved to `docs/design/`, the changelog to this file.

## v0.29

M10 interop (all but drafts) and traffic analytics.

- **Backup and restore (§8.4):** an Owner-only, passphrase-encrypted (Argon2id, chunked AES-256-GCM) online archive of the config and the vault, its keys re-wrapped under a backup-only key so it restores on a fresh machine under that machine's master key; `paduraksa backup|restore`, `POST /api/v1/backup`, Settings > Backup; one server per data directory (`run/server.lock`).
- **YAML import and export (§11.4, §11.5):** a friendly format with names and optional IDs that round-trips to the identical revision, merge and replace through the pipeline with dry-run, the API, the CLI (a client of the running instance) and Settings > Export and import; a `yaml-import` fuzz target.
- **MCP in code mode (§11.2):** two tools, `search` and `execute`, running stock Jint in a child process that the parent limits from outside and kills on any breach (time, CPU, memory, requests, protocol, frames bound to their run); the request policy (refused list, confirm or dry-run, the token's identity) stays in the parent; off by default (`Paduraksa:Mcp:Enabled`).
- **Analytics (§7.4, §12):** visitors and content, clients and bots, computed in the access-log drain off the request path (HyperLogLog visitors with a per-process salt, Space-Saving top-N, an own user-agent classifier that skips work when the drain falls behind, robots.txt parity for bots ignoring it), 5-minute, hourly and daily rollups, and an Analytics page.
- **Tests (§15):** the CI-only flakes found on the 4-core runner fixed at their cause (fake clocks moved before a wait was armed, parallel test classes sharing process state, bUnit's 1 s default).

## v0.28

after M9: fuzz targets for M8/M9, quieter logs, an honest metrics timeline.

- **Fuzzing (§15):** `error-page`, `upstream-error-replacement` and `access-log-tail` targets, config generators that reach the M8 settings, and two long rounds of all nine targets (no crash; one allocation in the access-log formatter found and fixed).
- **Logs (§7.4):** a client that vanished mid-write is one Debug line and a counter (`paduraksa_connection_errors_total{kind="peer_gone"}`), not two Error stacks per connection.
- **Metrics (§7.4):** every observed minute is stored with its observed seconds, so downtime is a gap and never stitched; observed time is monotonic and a stall that served requests counts as observed; graceful stop writes the partial minute and a minute written twice merges; the 1 h view is seeded from the rollups at boot; the charts break their lines at gaps.
- **Dashboard (§12):** the hosts list keeps the maintenance and enable controls inside the row.

## v0.27

M9 done: the live tail.

- **Tail (§7.4):** the access-log drain, still the ring's one reader, offers each entry to a bounded in-memory hub after the file line; viewers get bounded drop-oldest channels with filters, and conversion and redaction happen outside the hub's lock. The tail shows only what the file would: one shared escaper, the file's query cap and redaction, and a host with logging off is in neither. The request path is unchanged at 0 B.
- **Surfaces (§12, §11.1):** a Live tail page under Insight (filters, pause, batched rendering, an entry drawer that opens the simulator), and `GET /api/v1/logs/tail` as Server-Sent Events behind `logs:read`.

## v0.26

M9 part 2a: upstream health state and notifications.

- **Health (§7.4):** a watcher reads YARP's verdict per destination once a second, off the request path; a change must hold 10 s, a flapping destination is reported once, and a publish reports nothing. Each change is audited and shown as status dots on Upstreams, the hosts list and the overview.
- **Notifications (§5.1):** ntfy, Gotify, Discord, a signed webhook and email (MailKit) channels, their secrets in the vault behind a lend-only capability; events for upstream down/up, certificate expiry at 30/14/7/1 days, ACME failure and recovery, and sign-in lockouts and new addresses; a bounded dispatcher with retries and storm digests; Settings > Notifications.

## v0.25

Property tests and coverage-guided fuzzing.

- **Property tests (§15):** CsCheck 5.0.0 with a fixed base seed, per-test case counts and `PADURAKSA_PROPERTY_SCALE` / `PADURAKSA_PROPERTY_SEED` for deeper runs. Differential checks of the canonical path (also against real Kestrel), the glob automaton, rule-set and access-list verdicts, the simulator against the real gate, the forward-auth cache key and owned headers, the access-log formatter, and the config pipeline (a document that validates compiles; canonical JSON is idempotent and round-trips).
- **Fuzzing (§15):** `fuzz/` with SharpFuzz and libFuzzer harnesses for PROXY protocol, the proof-of-work cookie and form, `Authorization: Basic`, the client-address headers and a forward-auth provider's answer; a committed corpus for each, replayed on every commit by `Paduraksa.Tests.Fuzz`; `scripts/fuzz.sh` and a manual and nightly workflow that uploads crashes.
- **Fixes the new tests found:** the simulator's decoding of `%5C` (a backslash, as Kestrel decodes it); the forward-auth cache key now includes the scheme and the standard hint headers (`User-Agent`, `Accept`, `Accept-Language`, `X-Requested-With`); the access log escapes NEL, the other C1 controls and U+2028 / U+2029; the validator refuses a custom `Server` header that the global defaults leave without a value, as the compiler does.

## v0.24

M9 part 1: metrics.

- **Counters (§7.4):** per host, route, upstream and destination in striped per-core tables with log-linear latency histograms, recorded from the access-log completion at 0 B; slots carry a version, so a request that outlives its slot's grace never counts for the next owner.
- **Exposure (§7.4):** `System.Diagnostics.Metrics` and a Prometheus `/metrics` on the admin port behind `metrics:read`.
- **History (§7.4):** a one-second sampler into an hour of ten-second buckets, one-minute SQLite rollups keyed by typed ID with retention, summed in SQL.
- **Dashboard (§12):** the home page shows live traffic: rates, errors, latency percentiles, two charts, the busiest hosts and the upstreams' errors. Bench: h1 -1.1 %, h2 -2.6 % throughput.

## v0.23

M8 done: custom error pages and maintenance mode.

- **Error pages (§7.3):** a page per status at every settings layer, either a message in the built-in layout or the operator's own document, with variables written from precompiled byte segments at 0 B; a dashboard preview that is the proxy's own rendering, byte for byte.
- **Maintenance mode (§7.3):** per host, with a status, `Retry-After` and a bypass named by an access list; never on an admin host.
- **Upstream errors (§7.3):** opt-in replacement (`html` or `any`) decided when the response starts; the upstream body is closed unread; never for upgrades, event streams, gRPC or the admin host.

## v0.22

M7 done: access lists, auth and WAF.

- **Access lists and passwords (§7.3, §8.2):** named access lists (`acl_`) replace the inline IP allow list, with IP and country allow/deny and HTTP basic auth, `satisfy` all or any, and a conversion of the old documents on read. Each user's password is a vault credential (a PBKDF2 verifier, and a verification cache whose entries live until five minutes after their last use), the document names its credential and nothing else, and a password check that finds no slot waits in a bounded line (32 places per slot, 16 per client, a 10-second wait) instead of being refused. A decision that needs the line is `PolicyAction.Defer`, which the gate awaits and resumes the chain after.
- **Geo (§7.3):** `CF-IPCountry` from a trusted Cloudflare peer only, as country rules of an access list (Tor and unknown included); a local database is left for later.
- **Browser check and rate limits (§7.2, §7.3):** the proof-of-work gate (HMAC-signed, address-bound, expiring cookies, a pure-JS solver in workers, the key in the vault) and rate-limit policies (one fixed table, 16-bit policy slots, a sweeper, `Retry-After`), with S6 continuity under publishes.
- **Request limits and rule sets (§7.3):** body, header and request-target limits per host with a body override per route, answered before the upstream sees any; rule sets (`rs_`): ordered path, method and user-agent rules (exact, prefix, a bit-parallel glob, a NonBacktracking regex) that deny, drop or allow, with three presets that are copied to edit.
- **Cloudflare (§7.6):** the edge ranges refreshed daily and stored without a revision, origin lockdown, Authenticated Origin Pulls with a replaceable CA, and the anti-spoofing header stripping.
- **Forward-auth (§10.2, §7.3):** auth providers (`authp_`) with authentik, Authelia and oauth2-proxy presets, referenced from host, profile, defaults and route settings; a `GET` carrying only the standard credential headers and the original request in `X-Forwarded-*`, the provider's identity headers copied upstream and the client's own removed first, a 401, 403 or redirect relayed (a sign-in redirect for oauth2-proxy), every failure closed (502, 503, 504); a per-provider, per-configuration decision cache of the allows of requests that carry credentials, keyed on them and on the provider's own headers, 0 B on a hit, and a call that allocates and is bounded to 64 at a time; a simulator step that makes no call; no vault key.
- **Robots and error pages (§7.3):** the managed robots.txt (passthrough, disallow all, block AI crawlers, custom), and the built-in HTML pages for every gate and upstream error, from one template, for a browser only. Custom pages stay in M8.
- **Security overview (§12):** `GET /api/v1/security/overview` and a page with the gate's counters and live rates and each host's protections (access list, rate limit, browser check, robots.txt, rules, sign-in, Cloudflare), with the routes that differ.
- **Simulator (§7.7):** a trace step for each new policy (access list, rate limit, browser check, robots.txt, request limits, rule set, forward-auth), with parity tests, under the rule that a simulation changes no state.
- **Config write path (§6):** after an external review, one writer in one process, `ConfigService` split by responsibility, one permission check for every surface, no optional constructor parameters in Application, and the unused projection tables dropped.
- **Dashboard (§12):** pickers for rate limits, access lists, rule sets and auth providers by name, each with a "New ..." that is staged into the host and applied in one revision; pages for each; every one checked in a real browser at 1280 and 390 px.
- **Tests (§15):** `scripts/test-ci-like.sh` runs the suite on the official SDK with four cores; the timing, key-file and upload tests were made to wait on signals; 0 B assertions cover the synchronous verdicts of the new policies.

## v0.21

The review of v0.20 folded in, before M7.

- **Settings (§5.3):** one merge for every consumer (`HostSettingsLayers`: `Merge`, `Resolve`, and `BuiltIn` from typed constants stated once); the shared enums in Core, with their wire spelling registered in both JSON contexts; canonical JSON drops a nested record that sets nothing (`ISettingsLayer.SetsNothing`); the per-host response headers are one compiled list (`ResponseHeaders`), applied by the gate, by the reapply after a forward and by one YARP transform.
- **Tests (§15):** the settings and response headers are table-driven (`SettingsInheritanceTests`, `ResponseHeaderMatrixTests`, `HostSettingsFieldsTests`, `CanonicalConfigGoldenTests`, `SharedEnumWireTests`), and a reflection test walks the merge from the contract; the integration suites share one host per class (`SharedFixtures`) and wait on signals instead of delays, which halves their time; a log flush wakes the writer instead of waiting for its tick.
- **Dashboard (§12):** the settings fields bind to the contract record through reusable inherit rows (`SettingRow`, `InheritSelect<T>`, `HstsMaxAgeField`, `AllowListField`), and the flat form model is gone; the three draft drawers share `DraftDrawer<TModel, TConfig>`.
- **Cleanups:** `SnapshotCompiler` split into partials by concern; the gate's response-header buffer bounded, with the bound enforced at compile time; the simulator counts request limits in UTF-8 wire bytes; the never-throw log rule lives in one `BestEffort` helper; `AdminCaller` is bound as a handler parameter, behind a convention test that every caller requirement has its filter; a publish that faults or is cancelled is logged.
- **Comments:** restating summaries, narration, history notes and nearly all `PLAN §` references taken out of the code; what stays is invariants and reasons.

## v0.20

M6 done.

- **Host settings (§5.3):** HSTS (`max-age`, `includeSubDomains`, `preload`; preload judged on each host's resolved settings), the access-log switch, where the request ID is sent (upstream and client, either, or off) and the `Server` header (`Paduraksa` without a version, a custom value, the upstream's own, or removed), each through global defaults → profile → host, precomputed per host and applied at 0 B. Kestrel's own `Server` header stays off.
- **Gate (§7.2):** `X-Request-Id` adopted from a trusted proxy or generated; HSTS only on trusted HTTPS and never for an IP-literal or loopback name, never on a 421; the per-host response headers re-applied after a forward YARP cleared. The simulator gains HSTS and `Server` header steps (§7.7).
- **Logs (§7.4):** the access log, one escaped, redacted line per request through a lock-free ring and a background drain, and the error log (Warning and up by default), both in `<data dir>/logs` with rotation by size and day, background gzip and retention by count, age and total size; the Logs page and `GET /api/v1/logs` behind the new `logs:read` permission (§12, §11.1).
- **Dashboard (§12):** filler copy cut (no overlines, leads only for what a page can't show), sticky sessions and active/passive health checks named as admins know them, and layout fixes in the upstream drawer and the logs page; the instance-name badge and its option are gone.

## v0.19

M5 done.

- **Container (§16):** the Noble image (libmsquic, non-root with `NET_BIND_SERVICE`, `doctor --liveness` healthcheck), `deploy/compose.yaml`, a systemd unit and `docs/install.md`; list options accept a comma-separated value; an opt-in container smoke test.
- **Data Protection (§8.2):** the admin's key ring sealed in the vault; the public app's ring is ephemeral.
- **Dashboard (§12):** Blazor Server with MudBlazor: sign-in with TOTP and revocation reaching open circuits, hosts, upstreams, review-before-apply with Undo, history with diff and rollback, the simulator, certificates with live ACME progress, ACME accounts, DNS providers, settings (defaults, profiles, users, API tokens), My account and the audit page; the first-run wizard (§12.1) with a local QR encoder.
- **Hygiene:** confusable and invisible characters removed and forbidden by a test; CI made deterministic on a shared runner (an atomic `master.key` creation among the fixes).

## v0.18

Milestones reordered after M5 for a usable homelab deployment: **M6 HSTS + logs** (HSTS settings; plaintext access and error logs on disk with rotation and gzip compression), **M7 access lists, auth & WAF** (old M6, plus basic auth, forward-auth and the PoW gate moved forward from Later), **M8 custom error pages** (with robots and maintenance mode, split out of old M6), **M9 observability** (old M7), **M10 interop** (old M8), **M11 release v0.1** (old M9). References in this plan are renumbered (the changelog keeps the numbers of its time). Code comments still carry the old tags (`TODO(M6)` meaning the WAF, `M7` metrics, `M8` interop, `M9` release) until they are next touched: old M6 → M7, M7 → M9, M8 → M10, M9 → M11.

## v0.17

M4 done.

- **Simulator (§7.7):** the gate reports to a struct tracer (no-op in production); `SimulateAsync` runs the real gate over live, stored or draft config, with Kestrel-faithful request building and real-socket parity tests.
- **Admin (§10.1, §11.1):** cookie and bearer authentication, CSRF, the first-run setup gate, throttling, hardening headers, the source allowlist and the loopback plain-HTTP listener; identity and resource endpoints with RFC 9457 problems (one dotted lowerCamelCase code convention), If-Match/ETag, dry runs, cursor pagination, OpenAPI 3.1 and offline Scalar docs; `POST /api/v1/simulate`.
- **Admin certificate (§3):** issued by the local CA, hot-swapped, recovered from the vault, never served to proxied hosts, with a lock-out-safe fallback and an anonymous root download.
- **Admin through the proxy (§3.1):** the internal admin listener (Unix socket or TCP fallback), the `__admin` cluster with a per-process hop secret, and a lock-out guard that simulates the caller's own request.
- **Stability:** awaited certificate recovery before the listeners, the scheduler gated on listener readiness, a narrowed ARI `replaces` retry, and the Integration suite made deterministic. The setup token is printed in a banner (the owner's one exception to "no secrets in logs").

## v0.16

M4 identity core built: users, roles and permission sets, sessions, API tokens, TOTP in the vault with replay protection, recovery codes, lockout, first-run setup token, the admin CLI commands and the permission-based authorizers (§5.2, §8.2, §10.1, §12.1). Session cookies carry no Data Protection layer (§4.3). The HTTP admin app is the next wave.

## v0.15

M3 done.

- **Vault (§8.2, §8.3):** envelope encryption (a DEK per value under the KEK, AES-256-GCM, AAD binding purpose, ID and version), KEK sources with a check value, `paduraksa vault rotate-kek`, and a KEK check in every sealing transaction. Secrets, MAC keys, the authenticating HTTP handler (refusals are `SecretTransportRefusedException`) and the JWS signer. Certificates: staged keys for CSRs, chain attach with key, name, validity and signature checks, PEM/PFX import with KDF limits, self-signed and local-CA leaves capped at 825 days, one cached `SslStreamCertificateContext` per certificate, and a public-data catalog.
- **Certificates (§9.2):** the inventory selects per name; the upload, self-signed, local-CA and delete flows order vault and metadata so nothing hidden is served; recovery re-registers what the vault holds after a database loss, and finishes an audited delete instead of resurrecting it. The placeholder is off in Release builds.
- **ACME (§9.3):** the RFC 8555 client with ARI; accounts, orders and DNS providers tables; issuance that resumes from the CA's order status and repairs an attach whose registration was lost; HTTP-01 from the gate; the renewal scheduler (ARI window, ⅓-lifetime fallback, kind-based backoff with `Retry-After` floors) runs in the core host and drains before the public app stops. Extra trusted roots for private CAs.
- **DNS-01 (§9.4):** Cloudflare and deSEC over the vault handler, batched deSEC writes, and a propagation check against the zone's authoritative servers with its own small DNS client.

## v0.14

M3 foundations. Shared EF/SQLite infrastructure moved into `Paduraksa.Storage`, which Data and Vault reference (§1, §5; the §14 M3 open item is closed). The vault capability contracts are written in `Paduraksa.Core.Vault` (§8.2): the authenticating HTTP handler is the whole transport and never exposes the credential, secrets carry their allowed hosts and MAC algorithm, certificates are replaced atomically under a stable ID with staged keys, and enums persist by name. Certificate, ACME account and DNS provider storage and compile-time decisions are recorded (§9.1), and account key rollover is deferred (§9).

## v0.13

Request simulator designed (§7.7): runs the real gate over live, stored or draft config through a zero-cost generic tracer, with parity tests. Its core moves forward to M4; M6 adds a trace step per policy (§12, §14).

## v0.12

M2 done. The config document is the source of truth and the EF entities are its projection (§6.4). Typed IDs reach the proxy model. Canonical JSON, validation and diff work over the document. The compiler resolves the settings layers and places IP allow lists in the host and route chains (§6.4). Commands carry per-object versions that are revision numbers, and rollback is committed as a new revision. The audit log is a hash chain. Startup recompiles the latest revision, and `last-good.json` gives a degraded start or a restore (§6.1, §6.4, §11.3). The M3 open items now include the placeholder certificate (§14).

## v0.11

M1 done. S7 baseline recorded in `bench/results/2026-11-07-baseline.md` (§13, §14). §1 lists `Tests.Proxy` and `Bench.Runner`. §7.5 describes the as-built builder; the slim-builder note is kept as spike history.

## v0.10

M1 proxy core as built: snapshot model and store, gate, compiler input records, publisher state machine, route-matching semantics, public app on the generic host (§3, §6.2).

- Integration: public listener layout and `Paduraksa:Listen` options (`PublicHttpsPort`, `Http3`, PROXY protocol per listener) (§3). Typed `Certificates`/`TrustedProxies` snapshot slots, certificate references through `IServerCertificateResolver`, and certificate hot swap by publishing (§6.2, §7.2 B). Client-IP resolution through the global trusted-proxy policy, ClientInfo on QUIC verified (§7.2 A/C, §7.5, §7.6). Exact upstream forwarding-header semantics, every `cf-*` header stripped unless trusted, one trusted request scheme for the redirect and `X-Forwarded-Proto` (§7.2 C/E, §7.6). Per-host TLS settings re-checked per request, 421 on a weaker coalesced connection (§7.2 B/C). S6 scoped to YARP health in M1; rate-limit and metric continuity moved to M6/M7 (§6.3, §13, §14).

## v0.9

S4 outcome: typed IDs through EF Core conventions, JSON IDs as strings and never joined against column IDs in SQL, pragmas and timeouts, Int64Ordered seeding (§4, §5). S3 outcome (0 B gate, realistic Kestrel/YARP costs, generic-host pipeline instead of `WebApplication` for the public app, per-connection/per-request state mechanisms, host lookup, IP parser, allocation-test rules) (§7.1). M0 complete.

## v0.8

Native Cloudflare client-IP handling: `Proxy`/`Tunnel` modes, auto-refreshed edge ranges, anti-spoofing header stripping, `CF-IPCountry` geo, origin lockdown, Authenticated Origin Pulls (§7.6, M1/M6).

## v0.7

Hosting hardened after review (§3). A stop or signal during startup aborts the start and exits 0. The exit codes are listed in full, including failed stops and disposal. Startup errors print without a stack trace. `IOptionsFactory` also resolves to the validated options. A relative data directory resolves against the content root.

## v0.6

License decided: Apache-2.0 (§17).

## v0.5

S2 outcome. The gate selects the YARP endpoint, routes are path-only, and `ConfigureEndpoints` metadata and the route-ID generation scheme are dropped. Publish waits for YARP's `ConfigurationApplied`. Destination-ID stability rules and the required custom-pipeline steps are recorded (§6.1, §6.2, §6.3, §7.2).

## v0.4

M0 outcomes folded in.

- The process runs three hosts (public, admin, core) with defined startup, shutdown and exit codes (§3).
- Custom UUIDv7 generator, with `Guid.CreateVersion7` banned. Int64Ordered is documented as predictable and gets `Observe` seeding. The STJ source-gen converter factory requirement is recorded (§4).
- S1 result: per-SNI and hot-swap work on QUIC, so the H3 fallback is dropped. Listener layout, ALPN and chain pitfalls, the QUIC-side IP gate and the libmsquic image notes are added (§7.2, §7.5, §16).

## v0.3

Final review comments.

- Spikes renumbered S1-S7. S6 (state continuity) now also verifies YARP health continuity and runs in M1 with S7 (baseline).
- Rate-limit keys carry a 16-bit policy slot so the sweeper can find each entry's policy (§6.3, §7.3).
- Confirmed both `MapReverseProxy` overloads return `ReverseProxyConventionBuilder` in YARP 2.3.0 (§6.2).
- Healthcheck wording reconciled (§11.3, §16).
- `__Host-` cookies need `Path=/` (§3.1).

## v0.2

Incorporated the first review.

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
