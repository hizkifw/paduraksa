# Process and hosting model

For contributors: how the process starts, stops and listens, and how the admin app can sit behind the proxy.

## In short

- One process runs two web apps (public and admin) and a non-web core host. They share one set of singleton instances.
- Two apps, not one with port filters: admin endpoints are absent from the public pipeline, so nothing depends on a host or port check.
- The core host starts first, so a snapshot exists before any port opens, and stops last. It owns every once-per-process background service.
- A wrong, missing or unsafe master key stops the process (exit 2).
- Shutdown: `/readyz` goes 503, upgraded connections are closed, public drains, admin stops, core stops on a budget of its own, so persisted data is never lost to a slow drain.
- The admin listener's certificate is a leaf from the vault's local CA, issued before the web apps bind.
- The admin UI can be exposed through the proxy over an internal listener, with an authenticated hop and a lock-out guard.
- Code: `src/Paduraksa.Server/Hosting` (`PublicApp`, `PublicListenerSetup`, `SharedServices`) and `AdminCertificateService`. Fleet mode is in [deployment.md](deployment.md).

## Contents

- [Layout of the process](#layout-of-the-process)
- [Host mechanics](#host-mechanics)
- [Public listeners](#public-listeners)
- [Listen options](#listen-options)
- [Ports](#ports)
- [The admin certificate](#the-admin-certificate)
- [Exposing the admin through the proxy](#exposing-the-admin-through-the-proxy)

## Layout of the process

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

Why two apps and not one app with port filters:
- Admin endpoints cannot be routed on public ports, because the public pipeline does not contain them.
- Each pipeline gets its own middleware, auth and Kestrel limits. The admin app allows large YAML uploads, for example, while the public app uses per-host limits.
- The shared singletons (`SnapshotStore`, `MetricsRegistry`, `Vault`, `IAcmeChallengeStore`, `HealthState`) are built once and registered as instances in both containers.

The public app runs on the generic host with an explicit pipeline ([hot-path.md](hot-path.md)). The admin app is a `WebApplication`.

## Host mechanics

All of this lives in `src/Paduraksa.Server/Hosting`.

- **Shared singletons.** `SharedServices` accepts only ready-made instances and registers the same objects into every container. It disposes them once, after all hosts have stopped. `PaduraksaOptions` is itself the shared instance: `IOptions`, `IOptionsMonitor`, `IOptionsSnapshot` and `IOptionsFactory` all resolve to it, and named options throw.
- **Core host.** It owns every once-per-process `IHostedService` (ACME scheduler, metric sampler and so on). Web apps must not register one.
- **Vault first.** The vault starts first in the core host. Running without it would only move the failure to the first certificate that needs a key.
- **Startup failure.** If any host fails to start, the others are stopped and the process exits 2.
- **Abort during startup.** A stop, a signal or a cancelled start token during startup aborts the start. Whatever had started is stopped, the process exits 0, and the abort is logged at Information.
- **Data directory.** A relative `Paduraksa:DataDirectory` resolves against the content root, never the working directory. A leading `~` is rejected because nothing expands it. The directory holds `paduraksa.db` and `last-good.json` ([config.md](config.md)), and is created if missing.
- **Signals.** Handled only in `PaduraksaHost.RunAsync`. A second signal hard-kills.
- **Listener pinning.** The web apps get a last config source that blanks `urls`, `http_ports` and `https_ports` and sets `preferHostingUrls=false`. Environment variables and appsettings cannot rebind listeners.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Stopped on request, including during startup. |
| 1 | A host stopped on its own, a host failed to stop, or disposal failed after an otherwise clean run. |
| 2 | Startup failed (including no config to serve, see [config.md](config.md)), the process could not be built, or the master key is wrong, missing or unsafe (Critical log entry). |
| 3 | Invalid config: failed validation or an unparsable appsettings file. |

Validation failures print a header and one line per failure on stderr. Any other failure to build or dispose prints one line, and `PADURAKSA_DEBUG=1` adds the stack trace.

### Shutdown

The order and the budgets:

1. **Readiness** goes to 503.
2. **The drain budget** is `ShutdownTimeout` (30 s) less core's reserve (a third of it, at most 10 s, so 20 s by default). The caller's stop token cuts it short too. It covers the scheduler's drain, the public drain and the admin stop.
3. **The ACME scheduler** finishes the certificate it is working on (10 s at most).
4. **Upgraded connections are closed** as the public drain starts, while Kestrel drains ordinary requests.
5. **Admin stops** on what is left of the drain budget.
6. **Core stops** on a budget of its own: what is left of `ShutdownTimeout`, but never less than the reserve, whatever the drain or the caller's token did.

Why the budgets are separate: the hosts once shared one. An open WebSocket held the public drain for the whole 30 s, and core then stopped on a cancelled token. `BackgroundService.StopAsync` returns at once on a cancelled token, before the rollup jobs' last flush, so the analytics and metrics in memory were lost.

Compose needs `stop_grace_period: 35s`. The stop can run about a reserve past `ShutdownTimeout` when the drain used everything, and the extra 5 s covers Kestrel's abort and a quick flush.

**Closing upgraded connections.** Every upgrade the public app accepted is tracked (`UpgradedConnections`, through the `BodyCounter` that already wraps the upgraded stream), including one accepted during the stop.
- A WebSocket (HTTP/1.1 `Upgrade: websocket`, or HTTP/2 extended `CONNECT` with `:protocol websocket`) gets a close frame, 1001 Going Away. The stream follows the upstream's frame headers, so the frame goes out between two of the upstream's frames, never inside one. A frame still unfinished after 1 s means no close frame.
- After the close frame, what the upstream still sends is dropped. The client's answering close goes through to the upstream, which closes, and the forward ends. If that takes more than 2 s, the request is aborted.
- Any other upgrade is aborted at once, because there is no protocol to close it with.
- Ordinary in-flight requests still get the drain.
- A WebSocket message costs a compare-and-swap and a few header bytes read, and allocates nothing.

**Kestrel's timers.** `KestrelServerImpl.StopAsync` disposes Kestrel's heartbeat first, and the heartbeat judges every connection against the minimum data rates and timeouts. A client trickling a request body at 10 B/s, cut in about 11 s while the server runs, was never cut once the stop began and held the drain for its whole budget.
- `KestrelDrainHeartbeat` calls `Heartbeat.OnHeartbeat()` once a second while the public listener stops. It does so only while Kestrel's own timer thread is gone, because two threads ticking a connection's timeout control would race.
- It reaches Kestrel's internals by reflection, once per stop. Where it can't (the server isn't Kestrel, or a release moved the members), it logs a warning and the drain's budget ends a slow upload. `ShutdownBudgetTests` fails if that happens on the SDK the tests run on.

**Tests.** `ShutdownBudgetTests` covers a trickling and a healthy upload at the stop, a proxied WebSocket over HTTP/1.1 and HTTP/2 (the stop takes under 5 s of a 30 s budget, the client gets 1001, the analytics are in the database) and a request that never finishes (core still gets an uncancelled token). `UpgradedStreamTests` pins the frame boundaries, the drop after the close, the aborts and the 0 B write.

## Public listeners

`Hosting/PublicApp.cs` and `PublicListenerSetup`. All listeners are dual-mode `[::]` (`0.0.0.0` without IPv6).

- **HTTP** (`Listen:Http`, tcp, H1) and **HTTPS** (`Listen:Https`, tcp, H1/H2). Both run the stage-A `ClientInfo` connection middleware first (PROXY protocol first when enabled on that listener). HTTPS then runs the per-SNI TLS callback, which reads the live snapshot's certificate table ("Request pipeline" in [hot-path.md](hot-path.md)).
- **HTTP/3** (`Listen:Https`, udp). On when `Listen:Http3` is on and libmsquic is present. Its TLS callback selects the certificate and attaches `ClientInfo`. Without libmsquic it is off, with one Information log at startup, and `Alt-Svc` is never sent.
- **One `TlsHandshakeSelector` per process.** It is a shared singleton because its rejection counters are process state. The same options derive the compiler's `SnapshotCompilerOptions` (public port, whether H3 is advertised, the certificate resolver), so listeners and snapshots cannot disagree.

**Certificates.** The process's `IServerCertificateSource` is the vault's. `PlaceholderCertificates` covers the IDs the vault does not know only when `Paduraksa:Development:PlaceholderCertificate` is on ([vault-and-certificates.md](vault-and-certificates.md)).

The placeholder:
- is an in-memory self-signed certificate under the stable `PlaceholderCertificates.Id`;
- is also a built-in inventory entry and the inventory's fallback default. With it on, a config that names no default certificate still answers HTTPS for every host, and a stored document that names the ID keeps compiling across restarts;
- defaults to **on in a Debug build and off in a release build**. In a release build, if no vault certificate covers a name, the handshake rejects it;
- is switched on explicitly by tests and the benchmark runner. A release process with it on warns at startup;
- is only the admin listener's last-resort fallback: that listener's certificate comes from the local CA (below).

## Listen options

Options under `Paduraksa:Listen` are validated at startup, and invalid values exit 3.

| Key | Default | Meaning |
|---|---|---|
| `Http`, `Https`, `Admin`, `AdminBind` | 80, 443, 9443, `0.0.0.0` | Listener ports and the admin bind address. TCP and UDP share `Https`. |
| `AdminInternal` | `true` | The admin app's internal listener, used by an admin host (see below). A Unix socket at `<data dir>/run/admin.sock` (0600, in a 0700 directory), or `127.0.0.1:<ephemeral>` where one cannot be bound. On by default and independent of the config, so creating an admin host needs no restart. Off, a config with an admin host is refused. No TLS: it is process-local plumbing. |
| `PublicHttpsPort` | 443 | The HTTPS port clients reach. Advertised in `Alt-Svc` and used by the HTTP-to-HTTPS redirect. Set it only behind a port mapping. |
| `Http3` | `true` | Serve H3. Off, with an Information log, without libmsquic. Needs a fixed `Https` port (0 is rejected with H3 on). |
| `HttpProxyProtocol`, `HttpsProxyProtocol` | `Enabled: false`, `TrustedSources: []`, `HeaderTimeout: 00:00:03` | PROXY protocol v1/v2 on that TCP listener (HTTPS: read before TLS). Enabling it makes the header mandatory and requires at least one trusted source (address or CIDR). The timeout is at most 1 min. QUIC has no PROXY protocol. |

## Ports

All ports are configurable and overridable by environment variable.

| Purpose | Default | Notes |
|---|---|---|
| Public HTTP | 80/tcp | ACME HTTP-01, redirects, plain-HTTP hosts. |
| Public HTTPS | 443/tcp and **443/udp** | H1/H2 over TCP, H3 over QUIC. The docs must stress mapping the UDP port. The container listens on 80/443 directly (no in-container remapping, see [deployment.md](deployment.md)) so the advertised `Alt-Svc` port is correct. `Listen:PublicHttpsPort` overrides it behind a mapping. |
| Admin | 9443/tcp | HTTPS with an internal-CA certificate. Bind address and allowed CIDRs are configurable. Never plain HTTP on the LAN: a plaintext `GET`/`HEAD` gets one `308` to `https://`, anything else one `400`, and the connection is closed. |
| Admin fallback | loopback only, plain HTTP (opt-in, for example `127.0.0.1:9080`) | For headless and SSH-tunnel setups. Refuses to bind to anything but loopback. |

## The admin certificate

The admin listener's certificate is the process's own leaf, issued by the vault's local CA and kept current. Trusting the root once makes the admin UI trusted in the owner's browsers.

### Root

The core host (after the vault and the config store) ensures the local CA root exists, through the quiet `CertificateOperations.EnsureAdminRootAsync` path: as the system, registering without republishing. There is never a second root. In order of preference it takes:
1. the inventory's active root, while the vault still holds its key;
2. else the root the vault already holds, adopted and never re-created, labelled `Recovered from the vault`;
3. else a new one.

`CertificateOperations.EnsureLocalCaAsync` stays the ordinary, republishing entry for the other surfaces.

### Leaf

- It is the leaf under the stable `AdminCertificate.Id` (`Paduraksa.Application.Certificates.AdminCertificate`), with source `local_ca` and the label `Admin (internal CA)`.
- A new `CertificateSource` value was rejected: the `Certificates` table's `CHECK` constraint would need a table-rebuilding migration for no behavioural gain.
- Its names are `Paduraksa:Admin:CertificateNames`, plus `localhost`, `127.0.0.1`, `::1`, the machine's host name and a specific `Paduraksa:Listen:AdminBind` address.
  - `CertificateNames` is validated at startup: DNS names and IP addresses, no wildcards, no IPv6 zone ids, at most 95 so the total stays inside the vault's 100.
- It is re-issued only when that name set changes or when a third or less of its lifetime is left (`AdminCertificatePolicy.ShouldReissue`, pure arithmetic over the stored facts), always under the same ID.
- Deleting it and re-issuing it by hand (both `certificate.managed`) are refused. A hand re-issue would swap the vault's key while the listener kept the old context for up to a day.

### Reserved for the admin port

Nothing in the document can bind the admin certificate, by construction:
- `CertificateInventoryLoader` leaves `AdminCertificate.Id` out of every inventory it builds for the compiler. Auto never picks it, and its validity boundaries never trigger a republish.
- `ConfigValidator` refuses it as a Specific binding (`host.certificate.unusable`) and as the default certificate (`settings.defaultCertificate.unusable`).

### Serving

- The listener gets a per-handshake `ServerOptionsSelectionCallback` (`AdminCertificateService.CreateTlsOptions`). It returns the vault's `SslStreamCertificateContext` wrapped in `SslServerAuthenticationOptions`, the same sanctioned egress the public listener uses.
- A renewal swaps the options, and the next handshake gets the new certificate with no restart.
- The private key never leaves the vault. The key-material ban list holds, and `PaduraksaKeyMaterialBoundary` is not set on Server.

### Startup and renewal

`AdminCertificateService` is a shared singleton and a `BackgroundService`, hosted by the core host only.

- `StartAsync` ensures the certificate **before the web apps bind**, so the admin port serves the local-CA certificate from its first request. A daily loop re-runs the check.
- The wait is bounded by `Paduraksa:Admin:CertificateStartupTimeout` (10 s, range 1 s to 5 min). The public proxy on 443 binds at most that long after the core host.
- A slow vault continues the work in the background, under a token the service owns. `StopAsync` cancels it and waits for it, so the run never outlives the hosts that own the vault and the config store.
- The run serves what the vault holds before it starts the inventory step, so the admin port shows the vault's certificate even while that step is slow.
- Registering the certificate never republishes the live config (`CertificateService.RegisterQuietlyAsync`). Nothing can bind it, so the step must not wait for the public app's YARP, and no snapshot generation moves.
- Creation and every re-issue are audited and visible in the certificate inventory.
- Issuance and re-issue are skipped while the config store refuses changes (a degraded start), as in `CertificateRecoveryStartup`. A due re-issue there would replace the vault's key and then fail to record it, every day.

### Fallback

- Even if the inventory step fails or the store is degraded, the run still serves the certificate the vault holds. It falls back to the placeholder only when the vault has nothing servable: none, or outside its validity period (`AdminCertificatePolicy.IsServable`).
- A failure must not discard a valid certificate. The admin sends HSTS for non-loopback names, so a browser that trusted the root has pinned the name, and the self-signed placeholder would be a hard error with no click-through.
- A rollback must not discard it either: a failed creation for the admin ID keeps the vault's key (the ID is fixed, so the pair it holds may be the only servable one) and rolls back only its row. The next run adopts whatever the vault has.
- Failures are logged at Error, with the exception.
- There is deliberately no Debug/Release switch here: the placeholder is the last resort, not a mode.

### A lost vault

If `vault.db` goes while the config database keeps its rows, the rows name a root and a leaf the vault no longer holds. The run asks `ICertificateCatalog` about the active root's ID and the admin ID. It retires the stale root (and any active row the vault holds no key for) as `failed`, makes a new root and issues a new leaf under the same ID. No operator step is needed, which matters because deleting the root, deleting the admin certificate and re-issuing it by hand are all refused.

### Root CA download

| Endpoint | Returns |
|---|---|
| `GET /api/v1/ca/root.pem` | PEM, `application/x-pem-file` |
| `GET /api/v1/ca/root.crt` | DER, `application/x-x509-ca-cert` (the type Android and iOS offer to install) |
| `GET /api/v1/ca` | fingerprint, subject, validity as JSON (DTO `CaRootCertificate`, a member of the API's `AdminJsonContext`, written through `EndpointSupport.Json`) |

- Each also answers `HEAD`.
- They are mapped with the resource API (`Api/CaEndpoint`, from `MapPaduraksaAdminApi`), so they get its problem details, OpenAPI description and JSON contract.
- All three are `.AllowAnonymousAdmin()`. The prefix is one of the setup gate's allowed ones by default (`AdminPipelineHooks.SetupAllowedPathPrefixes`), so the root can be offered before an Owner exists.
- They serve the **public** root only, from the in-memory `CaRootSource` the admin certificate service publishes on every run. Never a key, never any other certificate, and no vault read per anonymous hit.
- The DER is decoded from the stored PEM, never loaded into an `X509Certificate2` just to re-encode it.
- With no local CA yet they answer 404 as the standard `ApiProblem` with code `ca.notFound`.

### Startup log

When setup is incomplete, the first successful ensure logs the admin URLs (one per concrete name) and the root's SHA-256 fingerprint at Information, next to the setup-token Warning. A `docker logs` reader can then verify the certificate they are asked to trust. Never key material.

### Not built

- Nothing renews the local CA root. It is valid ten years, and the vault caps a leaf at the root's remaining validity.
- In the root's last 90 days each re-issued leaf therefore gets shorter, down to the root's expiry. After that the admin port falls back to the placeholder, and the root download still serves the (expired) root.
- Rotating the root is deferred ([vault-and-certificates.md](vault-and-certificates.md), where the local CA root is described).

### Docker

`Environment.MachineName` is the container ID in Docker, and a recreated container gets a new one. The name set changes and the certificate is re-issued on the next start. That is harmless (the ID is stable, the root unchanged), but it means a fresh serial after every `docker compose up --force-recreate`. `AdminCertificateNames.Compose` adds the machine name as one name among the loopback ones. Set `Paduraksa:Admin:CertificateNames` for a stable name to reach the admin UI by.

## Exposing the admin through the proxy

Some people want `proxy.example.com` to be the admin UI, with the wildcard certificate, SSO and WAF in front of it. Because the admin is a separate `WebApplication`, the public app reaches it like any other upstream. This is opt-in.

### Design

- The admin app gets an extra **internal admin listener**: a Unix domain socket in `data/run/admin.sock` (0600), or `127.0.0.1:<ephemeral>` where UDS is not available.
- The public app's YARP cluster `__admin` points at it (UDS via `SocketsHttpHandler.ConnectCallback`). WebSockets for the Blazor circuit pass through YARP unchanged.
- The admin app trusts `X-Forwarded-*` **only** on that internal listener. The public side always overwrites those headers before forwarding, never appends.
- The admin host is a normal `Host` (`Kind = Admin`): forced HTTPS, with its own WAF, rate-limit and auth policy. It cannot be deleted while it is the only way in (the lock-out guard, below).

**Cookies.** The session cookie is `__Host-` prefixed, which browsers accept only with `Secure`, `Path=/` and no `Domain`, and it is `SameSite=Strict`. It is bound to the host name, not the port.
- A browser sends `__Host-paduraksa-session` to every service on that name: `https://proxy.example.com` (the proxied admin), a proxied host on that name, and `https://proxy.example.com:9443`. A different name or `https://lan-ip:9443` is independent.
- **The public proxy therefore removes that one cookie from every forwarded request.** It is a request transform: one ordinal search of the `Cookie` values when the cookie is absent, a rebuilt header without it when present, every other cookie untouched. An upstream never receives a live admin session.
- Antiforgery works unchanged because everything stays same-origin.

**Warning: proxying a host to the admin port makes every client look like loopback.** If anything forwards a public name to `127.0.0.1:9443` (a proxy on this machine, or this one), each client's address is hidden behind the proxy's. All clients then share one throttle bucket, and a source allowlist that lists loopback lets the whole internet in.
- Don't do that with the main admin port. The internal listener is the supported way, and it carries the forwarded address under the authenticated hop rule below.
- Leave loopback out of `AdminAllowedSources` unless you mean it.

### The internal listener

`Paduraksa:Listen:AdminInternal` (default on, not reloadable) gives the admin app one extra internal listener. It is always present, so an admin host can be added without a restart.

The Unix socket at `<data dir>/run/admin.sock`:
- `run` is created 0700 (and set again if it exists). If `run` is a symlink, or not owned by the process uid where Linux exposes the owner, it is refused.
- Connecting to the real socket path tells a live second instance (startup fails, Critical) from a stale file (deleted). Unix-socket support is probed on a temporary sibling name only.
- The socket is `chmod`ed to 0600 from `ApplicationStarted`, and its identity is recorded then. `Dispose` removes it only while it is still this process's. On Linux the inode is held open, so a reused inode number cannot make a successor's file look like ours.

The TCP fallback:
- Where the probe fails (a platform without Unix sockets, a path too long for `sockaddr_un`), the listener uses `127.0.0.1:<ephemeral>` and logs a Warning with the reason.
- The port is recorded at `ApplicationStarted`. The proxy's connect callback waits up to 5 s for it, because the public app starts alongside the admin app.

Other properties:
- No TLS, and HTTP/1.1 only. The proxy's upstream connection is HTTP/1.1, and so are WebSocket upgrades. Plaintext stays inside the process boundary (or the loopback socket).
- A connection middleware on its `ListenOptions` marks the listener. Nothing infers it from a port.
- `/mcp`, `/metrics` and the dashboard use it too. `GetListenAddresses`, which reports client endpoints only, does not list it.

### The public route

An enabled admin host compiles to one implicit prefix route (`/`, whose `CompiledRoute.Id` is empty and whose `CompiledRoute.Admin` is true) against a single YARP cluster `__admin`.
- The cluster's one destination is symbolic (`http://admin.internal/`). `PaduraksaForwarderHttpClientFactory` gives that cluster a `SocketsHttpHandler.ConnectCallback` that ignores the destination and connects to the internal listener (`AdminInternalListener.ConnectAsync`). It also pins the outgoing request to HTTP/1.1 (`VersionExact`).
- WebSockets are ordinary HTTP upgrades to YARP and pass through unchanged. A test drives a real `101` through the public listener and the admin pipeline.
- The compiler rejects an admin host while `AdminInternal` is off, a second admin host, routes, redirect or respond on it, and `Https = false`.
- YARP's config stays a pure function of content: the symbolic address never depends on the chosen port.

### Forwarded headers and the authenticated hop

- The existing `ForwardedHeadersTransform` overwrites `X-Forwarded-For`, `X-Real-IP`, `X-Forwarded-Proto` and `X-Forwarded-Host` on every proxied request, the admin route included. It removes `Forwarded`, `X-Forwarded-Prefix`/`-Port` and untrusted `cf-*`.
- A separate transform removes any client-sent `X-Paduraksa-Internal` from every request, and adds this process's 32-byte random hop secret only on the admin route. No upstream ever sees it.
- `AdminForwardedHeadersMiddleware` trusts the three headers **only** on a connection marked as the internal listener (the connection middleware on its `ListenOptions`, not a peer address or port) **and** only when `X-Paduraksa-Internal` matches in fixed time. Otherwise it answers 403 `admin.internalHop` and rewrites nothing.
- It takes one value and never a chain:
  - the client address from `X-Forwarded-For` (unknown, never loopback, when absent, repeated or comma-separated);
  - the scheme from `X-Forwarded-Proto` (`http` or `https` only);
  - the host from `X-Forwarded-Host`.
- `Connection.RemoteIpAddress`, `Request.Scheme` and `Request.Host` then drive the sign-in throttle, audit `ClientIp`, CSRF's origin check, HSTS and the response `Secure` cookie, so all of them follow the public scheme and host.
- Every other listener (the main admin port, the loopback plain-HTTP fallback) ignores these headers. The fallback listener's DNS-rebinding `Host` check does not apply to the internal listener.

### Allowlist

- On the main admin port, `Paduraksa:Listen:AdminAllowedSources` runs per connection, before TLS.
- On the internal listener the peer is always the process, so the same list is applied per request to the **forwarded** client address (403 `admin.sourceDenied` when refused). If a request has no usable forwarded address, it is unknown and refused while the list is non-empty.
- The public host's own policies (IP allow list, rate limits and the rest of the WAF) are the primary gate for a proxied admin. The admin's list is a second, independent restriction.
- First-run setup (`GET`/`POST /api/v1/setup`) is refused on the internal listener (403 `setup.viaProxy`). Setup is reachable only on the admin port or the loopback plain-HTTP fallback.

### The session cookie

- `SessionCookieTransform` leaves `__Host-paduraksa-session` alone on an admin route (the admin is its destination) and still removes it from every other upstream.
- A response transform removes a `Set-Cookie` for that name from any non-admin upstream, so an upstream on the same name cannot plant a session over the owner's (cookie tossing). The admin's own `Set-Cookie` passes.
- Both work on the header collection the transform stage exposes (`HeadersCopied`).

### The admin host in the config

`kind: admin` goes through the existing `/api/v1/hosts` endpoints.
- At most one admin host exists (`host.admin.duplicate`).
- It has no routes (`host.routes.unexpected`), redirect or respond.
- Its certificate binding cannot be `none` (`host.admin.httpsRequired`), and the mapping forces the HTTPS redirect regardless of the settings layers.
- It has no upstream of its own: every path goes to `__admin`.
- Its profile, IP allow list and TLS settings are the usual host layers.
- A disabled admin host is allowed (staged), but the guard below treats losing the only enabled one as cutting access.

### The lock-out guard

If a command has `CommandOrigin.AdminRequest` set (authenticated on the internal listener), it carries the caller's public host, scheme and resolved client address into Application. The command pipeline compiles the candidate and runs the caller's own request through the real `GateSimulator`. The change is refused with 422 `host.admin.lockout` unless the outcome is a forward to the admin route.

What it catches:
- deletion, disabling, a kind change and a rename;
- a host or policy change that excludes the caller;
- a non-covering certificate;
- a trusted-proxies change.

A replace or rollback is allowed if it recreates the admin host under a new ID but keeps it reachable.

Overrides and scope:
- The CLI, the normal admin port and the loopback fallback carry no `AdminRequest`, so they need no force.
- `?force=true` on the API (or `ConfigCommand.Force` on any command) is the explicit override. The exception is removing the admin's HTTPS, which stays invalid because an admin host is HTTPS-only (the validator refuses mode `none`).
- Dry runs pass through the same check, so a preview reports the refusal too. A draft simulation (`PlanDraftAsync`) does not, so a what-if trace is never replaced by a lock-out.
- The audit event records `force` and `viaAdminProxy` in its details.

Modelling limits:
- The probe is a `GET /` over the caller's host, scheme and resolved client address, not the actual path or method.
- A change to the trusted proxies is refused outright while the request arrives through the proxy, because the caller facts cannot faithfully model the new proxy chain.
- The rule is before/after: a change is refused only if the probe forwards now and would stop. A condition that already fails the probe (a Specific certificate not covering the name, an existing modelling gap) doesn't block unrelated commands.

### The simulator

`GateSimulator` runs the same `Decide`. The admin route is traced as "forwarded to the admin app" (`GateUpstream.Admin = true`, cluster `__admin`) instead of naming a user upstream, and the route-match step says the admin route takes every path. The parity suite includes an admin host.

### Tests

`AdminThroughProxyTests` (real host, real sockets) is the end-to-end suite: sign-in and CSRF through the public listener, the cookie reaching only the admin, the throttle seeing forwarded clients separately, `X-Forwarded-*` overwritten, the socket modes, a real WebSocket upgrade, the lock-out guard and `force`. `AdminHostLockoutTests` covers the guard's cases, `AdminInternalListenerTests` the socket and its fallback, and `AdminForwardedHeadersTests` the malformed-header cases. The compiler, validator and simulator tests cover `Kind = Admin`.
