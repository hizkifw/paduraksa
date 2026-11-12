# Paduraksa threat model (v0.1)

This is the threat model for the single-node v0.1 release (PLAN §14, M11). It is written for three readers: a homelab
operator who wants to know what they are trusting, a contributor who wants to know which rules not to break, and a
security reviewer who wants to know what is claimed, where it is enforced and what is not covered.

It describes the code **as built** at the time of writing (PLAN v0.29, M0-M10 done, plus traffic analytics). Where the
code and PLAN disagree, or a mitigation is missing, it says so in [Gaps found](#8-gaps-found) instead of repeating the
intention. File paths are relative to the repository root; `§n` refers to [PLAN.md](PLAN.md).

**Evidence levels.** A claim marked *(code)* was checked against the source while writing this document. A claim marked
*(PLAN)* rests on PLAN's "As built" text and the named tests, which were read but not re-run. Tests were not run for this
document; "pinned by" names the test that is meant to hold the claim.

Contents: [1 Deployment](#1-what-paduraksa-is-and-how-it-is-deployed) |
[2 Assets](#2-assets) | [3 Boundaries and flows](#3-trust-boundaries-and-data-flows) |
[4 Attackers](#4-attackers-mitigations-and-residual-risk) | [5 Non-goals](#5-explicit-non-goals) |
[6 Operator guidance](#6-operational-guidance) | [7 Test evidence](#7-where-the-claims-are-tested) |
[8 Gaps found](#8-gaps-found) | [9 Review cadence](#9-review-cadence)

---

## 1. What Paduraksa is and how it is deployed

Paduraksa is a reverse proxy and lightweight WAF on Kestrel and YARP, run as **one process on one node** (§0, §3). The
process hosts two web apps and a non-web core host that share singletons (§3):

- the **public app** (`src/Paduraksa.Server/Hosting/PublicApp.cs`): the gate, host and route policy chains, YARP;
- the **admin app** (`src/Paduraksa.Server/Hosting/AdminApp.cs`): dashboard (Blazor Server), REST `/api/v1`, `/mcp`
  (off by default), `/metrics`, health endpoints;
- the **core host**: the vault, the config store, ACME scheduling, metrics sampling, notifications, log writers.

Assumed deployment, from `deploy/compose.yaml`, `deploy/paduraksa.service`, `Dockerfile` and `docs/install.md`:

| Item | Assumption |
|---|---|
| Packaging | The Docker image (Ubuntu Noble, uid 10001, read-only root file system, `cap_drop: [ALL]` plus `NET_BIND_SERVICE`, `no-new-privileges`), or a systemd unit with a hardened sandbox, `UMask=0077` and `LoadCredential=master.key`. |
| Public listeners | 80/tcp, 443/tcp and 443/udp (HTTP/3). Directly reachable from the internet or the LAN, or only from Cloudflare (`Proxy` or `Tunnel` mode, §7.6). |
| Admin listener | 9443/tcp, HTTPS only, with a leaf from the vault's local CA. Bound to `0.0.0.0` in the process by default (`PaduraksaOptions.cs`, `Listen:AdminBind`) with no source allowlist, and published by the compose example on `127.0.0.1` only (G4). Optionally also reachable through the proxy as an admin host over a process-internal Unix socket (§3.1). |
| Internal admin listener | `<data>/run/admin.sock`, 0600 in a 0700 directory, or `127.0.0.1:<ephemeral>` where sockets are unavailable. Plain HTTP, process-local. |
| Data | `/data`: `paduraksa.db` (config, revisions, users, sessions, audit), `vault.db` (sealed secrets), `master.key` (only if no key file or env var is set), `last-good.json`, `setup-token`, `run/`, `logs/` (§16, `docs/install.md` "Where things live"). |
| Master key | From `PADURAKSA_MASTER_KEY_FILE` (Docker secret or systemd credential), else `PADURAKSA_MASTER_KEY`, else a generated `data/master.key` (§8.3). |
| Users | A small number of trusted people with roles Owner, Admin, Operator, Viewer, Auditor (§10.1), plus API tokens. Not a multi-tenant service. |
| Cloudflare | Optional. In front as an orange-cloud proxy or a tunnel, it is a trusted source of client IP, country and scheme (§7.6). |

What this means for the model: the proxy is on the **internet-facing edge** (the public app is untrusted-input facing),
holds the **keys to the owner's TLS identity and DNS**, and runs a **second, privileged web surface** (the admin app) in
the same process. The design leans on three structural decisions, each of which this document tests:

1. The public pipeline does not contain the admin endpoints at all (§3, "Why two apps"), rather than filtering them.
2. Key material is confined to one project by compiler-enforced bans (§2, §8).
3. Untrusted code (MCP scripts) runs in a separate, killable process (§11.2).

---

## 2. Assets

| Asset | Where it lives | Why it matters | Confidentiality / integrity / availability |
|---|---|---|---|
| **TLS private keys** (ACME, uploaded, self-signed, local CA root and leaves, upstream mTLS client certs) | `vault.db`, sealed per row (§8.2, §8.3) | Impersonate every proxied name; the local CA root, if its root was installed on devices, can mint a certificate for **any** name those devices trust | C and I |
| **ACME account keys, EAB HMAC keys** | `vault.db` | Order or revoke certificates for the owner's domains | C and I |
| **DNS provider credentials** (Cloudflare, deSEC) | `vault.db`, `DnsApiToken` secrets | Edit DNS for the owner's zones: hijack a domain, get certificates | C |
| **Notification secrets** (webhook URLs, tokens, SMTP password, signing keys) | `vault.db` | Spoof or read alerts; webhook URLs are bearer secrets | C |
| **TOTP secrets** | `vault.db`; recovery codes as SHA-256 in `paduraksa.db` | Second factor for admin sign-in | C and I |
| **Access-list password credentials** | `vault.db` (PBKDF2 verifiers, §8.2 "password credentials") | Basic-auth users of proxied hosts | C |
| **Data Protection key ring** | `vault.db` (§8.2, M5) | Dashboard antiforgery and circuit state; forging them | C and I |
| **PoW HMAC key** | `vault.db`, reserved ID `ProofOfWorkKey.Id` | Forging proof-of-work cookies, bypassing the challenge | C and I |
| **The master key (KEK)** | env, file or `data/master.key` | Opens everything above | C and I |
| **Config and its history** | `paduraksa.db` revisions, `last-good.json` | Controls what the proxy does; the history reveals topology (hosts, upstream addresses, access-list usernames). Holds **no secrets** by design (config export carries none, §8.3) | I mainly, C moderately |
| **Audit log** | `paduraksa.db`, hash chain (§5.2, §6.4) | Accountability for every change and sign-in; includes client IPs | I (tamper-evidence) |
| **Admin sessions and API tokens** | `paduraksa.db`, stored as SHA-256 of the secret (*code*: `SessionService.cs`, `ApiTokenService.cs`) | Act as a user with their permissions | C |
| **Availability of proxied services** | The running process, snapshot, certificates | The whole point; one node, no HA (§0) | A |
| **Client data in transit and logs** | `/data/logs/access.log` (IPs, paths, queries, user agents, referers), analytics rollups, the live tail | Privacy of the proxied sites' visitors | C |
| **Backups** (`*.pdkbak`) | wherever the operator puts them | Contain **everything** in `paduraksa.db` and `vault.db`, sealed by one passphrase (§8.4) | C |

---

## 3. Trust boundaries and data flows

```
   Internet / LAN clients                    Cloudflare (optional)                 Operator browser / CLI / API client / MCP client
          |                                         |                                              |
          | 80, 443 tcp, 443 udp                    | (trusted peer ranges, CF-* headers)          | 9443 tcp (HTTPS), or public name via admin host
          v                                         v                                              v
 +--------------------------------- paduraksa process (one uid, 10001) ------------------------------------------------------+
 |                                                                                                                              |
 |  B1  PUBLIC APP                                         B3  ADMIN APP                                                         |
 |   conn: PROXY protocol (trusted CIDRs) -> ClientInfo     pre-TLS: source allowlist, plain-HTTP 308 stage                      |
 |   TLS: per-SNI cert (vault egress)                       session cookie | Bearer pdk_ token | (cookie never at /mcp)         |
 |   Gate: client IP -> scheme -> host -> TLS check ->      CSRF, setup gate, rate limit + lockout, body limit                   |
 |     host chain -> limits -> redirect/robots/respond ->   Application services: permission check + audit on every path         |
 |     route -> route chain (access list, rules, rate       |        |                  |                                         |
 |     limit, forward-auth, PoW)                            |        |                  +--> B5 MCP parent (RequestPolicy)        |
 |   YARP: overwritten X-Forwarded-*, stripped cf-*,        |        v                         |  frames over pipes                 |
 |     identity-header stripping, admin cookie stripped     |   B4  VAULT  (internal, capability interfaces)                        |
 |     |                                                    |   keys never leave; 4 sanctioned egresses (§8.2)                    |
 |     |   B2 upstream TLS: System | Pinned | Any          |        |                                                            |
 |     v                                                    |   vault.db, master key (B7: /data)           B5' MCP child process  |
 |   upstream services                                      |                                              no token, no files,    |
 |                                                          +--> B6 outbound (SSRF posture) ---+           limits + kill          |
 |   internal admin listener (unix socket 0600) <---- B2' public app proxies the admin host here                                  |
 +--------------------------------------------------------------------------------------------------------------------------------+
        |                |                   |                        |                   |                      |
        v                v                   v                        v                   v                      v
   ACME CAs         DNS providers      Cloudflare IP ranges     forward-auth          notification          upstreams
  (https only)     (vault-injected     (api.cloudflare.com,     endpoint (operator    channels (operator    (operator-chosen,
                    credentials)        cloudflare.com)         chosen URL)           chosen URL / SMTP)    any address)
```

### B1. Internet to the public listener

- **PROXY protocol** (opt-in per TCP listener) is parsed **before TLS**, accepted only from configured trusted source
  CIDRs, with a header timeout, and is mandatory once enabled (§3 Listen options, §7.2 A). QUIC has no PROXY protocol.
  Fuzzed (`proxy-protocol`).
- **Client IP resolution** (`TrustedProxyClientAddressSource`, §7.2 C, §7.6): the peer is the PROXY source or the socket
  address. Only a peer in the trusted-proxy policy (Cloudflare edge ranges, the configured `cloudflared` address, or a
  generic CIDR list) may supply the client through `CF-Connecting-IP`, `True-Client-IP` or `X-Forwarded-For`. A header from
  anyone else is ignored. Parsing is our own strict span parser (no octal, hex, short forms or zone IDs). Fuzzed
  (`client-address`), with a differential property against the BCL.
- **Cloudflare ranges** are baked in (`src/Paduraksa.Proxy/Net/CloudflareIpRanges.cs`) and refreshed over https
  (`src/Paduraksa.Application/Cloudflare/CloudflareRangeFetcher.cs`); a list is refused if it is malformed, has a block
  wider than any real edge range, or is implausibly small or large (`CloudflareEdgeRanges.cs`, *code*).
- **TLS and SNI.** A prebuilt `SslServerAuthenticationOptions` per (certificate, host TLS policy, transport); an unknown
  name is rejected at the handshake in a release build (the placeholder certificate defaults to off in release, §3).
  Because clients coalesce connections across names a certificate covers, the host's TLS policy is **re-checked per
  request** (421 Misdirected Request) (§7.2 C). Client-certificate chains are built **offline**: a client-controlled AIA URL
  would otherwise be SSRF (§7.2 B).
- **Request limits.** Per host: body 100 MiB by default, header count 100, header bytes 32 KiB, target 8 KiB, with the
  listeners' outer bounds (`src/Paduraksa.Proxy/Gate/RequestLimitsGate.cs`, §7.3 "request limits"). Slow-client timeouts and
  connection counts are **Kestrel's defaults**; Paduraksa sets none of them (*code*: only `MaxRequestBodySize`,
  `MaxRequestLineSize`, `MaxRequestHeaderCount` and the HTTP/2 header field size are assigned). See G1 and G8.
- **Per-request header hygiene** (§7.2 E, `ForwardedHeadersTransform`): `X-Forwarded-For`, `X-Real-IP`, `-Proto`, `-Host`
  are overwritten, never appended; `Forwarded`, `X-Forwarded-Prefix/-Port`, every `cf-*` header and `True-Client-IP` are
  removed unless the peer is a trusted Cloudflare peer; a provider's identity headers are removed from every route of the host.

### B2. Gate, policy chain, YARP, upstreams

- **Order** is fixed (§7.2 C, D): client IP, scheme, ACME challenge, host, TLS check, host chain (access list address
  and country, rule sets), request limits, redirect, robots, Redirect/Respond hosts, route, route chain (rate limit, forward
  auth, PoW), body limit, then YARP. Refusals on the host chain precede the redirect so a blocked client gets 403, not 301.
- **Upstream TLS trust modes** (`UpstreamTlsTrust` in `src/Paduraksa.Core/Model/UpstreamEnums.cs`): `System` (default,
  normal validation against the `Host` name), `Pinned` (a validation callback from `UpstreamCertificatePins`) and `Any`
  (YARP's accept-any: encrypted but unauthenticated, as the enum's own comment says). Pinned by
  `tests/Paduraksa.Tests.Proxy/Yarp/PaduraksaForwarderHttpClientFactoryTests.cs`.
- **The admin host through the proxy** (§3.1): the public app forwards one admin host to the internal listener. The admin
  app trusts `X-Forwarded-*` **only** on that listener **and** only with a matching 32-byte per-process hop secret
  (`AdminForwardedHeadersMiddleware`, `src/Paduraksa.Admin/Auth/AdminForwardedHeaders.cs`). The `__Host-paduraksa-session`
  cookie is removed from every request to every non-admin upstream, and a `Set-Cookie` of that name from a non-admin upstream
  is dropped (`src/Paduraksa.Proxy/Yarp/SessionCookieTransform.cs`). A lock-out guard refuses a change that would cut the
  caller's own access (§3.1).

### B3. Admin clients to the admin app

- **Credentials.** Session cookie `__Host-paduraksa-session` (`Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`;
  *code*: `AdminSessionCookie.cs`) or `Authorization: Bearer pdk_...`. Both are looked up by SHA-256 on **every** request, so
  revocation is immediate. A credential that is presented and wrong is a 401, never anonymous; a cookie **and** a token
  together is a 401 (§10.1).
- **CSRF** for unsafe methods on cookie requests: `Sec-Fetch-Site`, `Origin` against the request's own origin, JSON content
  type (§10.1). Bearer requests are exempt. Pinned by `tests/Paduraksa.Tests.Integration/Admin/CsrfTests.cs` and
  `ApiCsrfTests.cs`.
- **Setup.** Until an Owner exists the admin app serves only the setup endpoint, behind a 256-bit setup token in
  `<data>/setup-token` (0600, also logged at Warning in a boxed banner; the one decided exception to "no secret in a log",
  §2). Compared in constant time; refused through the proxy (`setup.viaProxy`). The state is read per request
  (`AdminSetupGateMiddleware.cs`, `SetupService.cs`). Pinned by `SetupGateTests.cs` and `SetupServiceTests.cs`.
- **Rate limiting and lockout.** Fixed windows per client (every attempt, /64 for IPv6) and per username (failures only);
  password derivation is bounded to `ProcessorCount` slots, else 503; lockout after 5 failures for 15 minutes by default
  (`IdentityOptions.cs`, *code*). Windows are in memory and restart with the process.
- **Roles and tokens.** Permission sets (`src/Paduraksa.Application/Identity/RolePermissions.cs`, *code*): Owner has
  everything; Admin has config, certificates, providers, users (read), audit, simulate, logs, metrics; Operator has config
  and certificates write, providers (read), simulate, metrics; Viewer has reads and simulate; Auditor has reads, audit and
  logs. A token's permissions are the **intersection** of its scopes and its owner's current permissions (`Narrow`), so a
  token never exceeds its owner; a token cannot manage tokens or sessions; `backup:create` is Owner-only.
- **Hardening.** Strict CSP (`default-src 'none'`), `nosniff`, `no-referrer`, `X-Frame-Options: DENY`, `no-store` on `/api/`,
  HSTS except for loopback names, 64 KiB body limit, strict JSON binding (§10.1). The dashboard's CSP allows
  `style-src 'unsafe-inline'` for MudBlazor and nothing inline for scripts (`src/Paduraksa.Dashboard/DashboardSecurity.cs`,
  *code*). Plain HTTP on 9443 gets a single 308 or 400 and nothing else is ever served (§10.1 "Wrong scheme").
- **Optional source allowlist** `Listen:AdminAllowedSources`, enforced per connection before TLS; loopback is not implied
  once it is set. Empty by default.

### B4. The vault boundary inside the process

- **Compile-time confinement.** `BannedSymbols.KeyMaterial.txt` bans PKCS#8 export and import, PEM key import, `AesGcm` and
  `HMACSHA256/512` in every project except `Paduraksa.Vault`, wired through `Directory.Build.props` (a project opts out
  only with `PaduraksaKeyMaterialBoundary=true`). `KeyMaterialBanTests` checks the list's coverage and runs the analyzer.
  `BannedSymbols.txt` bans `Guid.NewGuid` and, for the proxy, `Rfc2898DeriveBytes`. Architecture tests keep the vault's
  types `internal`, hold `VaultContractTests` (deny-by-default on what a capability interface may return) and
  `BackupBoundaryTests` (Argon2 and the backup key stay in the vault). *(code and PLAN)*
- **Capabilities, not bytes** (§8.2): serving and upstream-mTLS contexts, CSR creation, JWS signing, MAC handles, a secret
  store with no `Get`, an authenticating HTTP handler that injects the DNS credential inside the vault, vault-side TOTP, a
  password credential that can be verified against and never read. **Four sanctioned egresses**: the serving context (Kestrel
  needs the key), the upstream client-cert context, the Data Protection element XML, and `INotificationSecretSource.UseAsync`
  (lends a value to a callback in a pinned buffer that is zeroed afterwards). The test lists each allowed position.
- **At rest** (§8.3): per-secret AES-256-GCM DEK wrapped by the KEK, with AAD binding the ID, purpose and version, so a
  swapped or edited row fails to open; `secure_delete`; a KEK check value. It is **not** protection against an attacker who
  can run code in the process or read process memory (non-goal 5.1).
- **Important limit:** the boundary is a *code-structure and review* boundary, not a process boundary. A memory-safety
  or deserialisation bug anywhere in the process can read the vault's in-memory keys. PLAN §8.3 names moving the vault to a
  separate process as the future seam.

### B5. The MCP child process

- **Off by default** (`Paduraksa:Mcp:Enabled=false`): `/mcp` is not mapped, no child ever starts (§11.2).
- **Transport:** stateless, bearer token only; a cookie, alone or with a token, is a 401 (§11.2). Channel `mcp` in the audit
  log, the token as the actor.
- **The child** is `paduraksa mcp-sandbox`, started with its environment cleared except `DOTNET_*` runtime paths, `PATH`
  and `LD_LIBRARY_PATH` (*code*: `src/Paduraksa.Mcp/ChildProcess.cs`), so the master key and setup token are not in it. It
  holds no token, configuration or data directory. It talks length-prefixed JSON over stdin/stdout; every frame is bound to
  a per-run random ID.
- **The parent is the guard:** wall time 10 s, CPU 5 s, resident 384 MiB, 50 requests, 64 KiB result, a watchdog that kills
  the process tree, a pool of at most 4 children, replacement after any breach (§11.2). The child also applies
  `RLIMIT_CPU`, `RLIMIT_NOFILE`, `RLIMIT_CORE`, `PR_SET_NO_NEW_PRIVS`, `PR_SET_PDEATHSIG` and **Landlock for the file
  system** (*code*: `src/Paduraksa.Mcp/ProcessHardening.cs`); cgroup v2 limits where delegated.
- **Network:** Landlock network rules (ABI 4, Linux 6.7) deny every TCP bind and connect, and a seccomp filter
  (`src/Paduraksa.Mcp/SyscallFilter.cs`) refuses every `socket` but `AF_UNIX` and `io_uring_setup` (G2, fixed; the filter covers UDP, raw
  sockets and older kernels, Landlock covers a kernel in which the filter's assumptions might not hold). The rest of the syscall surface is
  the runtime's: there is no profile of the runtime's calls (PLAN §11.2).
- **The request policy never leaves the parent** (`RequestPolicy`, `EndpointRules`, `RequestBridge`): only paths of the
  generated OpenAPI document under `/api/v1/`, no encoding tricks, five methods; an explicit **refused list** (backups,
  restore, token and user management, sessions, sign-in and setup, anything that carries a secret into a model, the live
  tail, raw metrics, the API description); two-step writes (dry run or 428 unless `confirm: true`); the API's own permission
  filters still run. A guard test fails when a sensitive-looking operation is on neither the refused nor the reviewed list
  (`McpRequestPolicyApiTests`). A fuzz target (`mcp-sandbox`) feeds the parent's protocol arbitrary bytes as the child's output.

### B6. Outbound calls (SSRF posture)

| Call | Who chooses the destination | Controls as built |
|---|---|---|
| ACME CA | Operator (account `directoryUrl`) | https only for the directory (`AcmeAccountService.cs`); every URL the CA returns must be absolute https (http only for loopback and only if an option allows it) (`AcmeTransport.cs`); no redirects, response cap 1 MiB (`AcmeClientOptions.cs`). Any host is accepted, private addresses included. |
| DNS provider | Provider code fixes the API host; the admin sets `AllowedHosts` with the secret | The vault's handler injects the credential, https only, port 443 unless named, allowlisted host only, no redirects, refuses a `Host` that differs from the URL (domain-fronting guard) (§8.2). |
| Cloudflare ranges | Fixed built-in URLs | https, 15 s timeout, size cap, list sanity checks, last good list kept on failure (*code*). |
| Forward-auth endpoint | Operator (`AuthProvider.forwardAuth.url`, a **config** object) | No redirects, cookies or env proxy, 64 connections, platform TLS validation (no accept-any), 1-30 s timeout, at most 64 concurrent calls, relays only status, bounded body and a few headers (§7.3 forward-auth). Nothing a client sends chooses the endpoint. Any address is allowed. |
| Notification channels | Operator (`providers:write`) | Redirects refused, responses never echoed, fixed error lines, SMTP login refused without TLS. **SSRF decided and accepted**: any address, loopback and the admin port included (§7.3 M9 notifications). |
| Upstreams | Operator (`config:write`) | No restriction on address (userinfo, query and fragment refused). This is the proxy's job. |
| Upstream reachability probe (`UpstreamProbe`) | Operator (`config:write`) | TCP connect only, nothing sent or read (§12.1). |

### B7. The CLI and the filesystem

- **The CLI is the same binary.** `admin reset-password`, `admin disable-2fa`, `setup-token`, `vault rotate-kek`, `backup`,
  `restore`, `config export|import` are local administrative actions: they run as the invoking user with access to `/data`
  and the master key, and are audited as the `cli` actor, which "may do everything" (§10.1). Whoever can run them already
  has the data directory. Passphrases and passwords come from a file, stdin, the environment or a no-echo prompt, **never
  argv** (a test parses the command line and finds no such option, §8.4).
- **`/data` permissions** (*code*): `master.key` created exclusively with mode 0600 and refused if others can read it
  (`KekProvider.cs`); `vault.db` 0600 (`VaultDatabase.cs`); `last-good.json` 0600 (`LastKnownGoodFile.cs`); `setup-token`
  0600; `run/` 0700 with `admin.sock` 0600; `logs/` 0700 with 0600 files (`RotatingLogFile.cs`); `paduraksa.db` and its WAL and
  shared-memory files 0600 (`PrivateSqliteFile.cs`, G7: an older install's wider modes are restricted at startup). The data
  directory itself keeps the mode its creator gave it (the image's `install -d -m 0750`, or 0700).
- **One server per data directory:** `run/server.lock`, an exclusive OS file lock (`DataDirectoryLock`), so a second server or
  a restore cannot run alongside (§8.4). Not enforced on file systems that ignore `flock` (a warning is logged).

---

## 4. Attackers, mitigations and residual risk

Each subsection lists what the attacker can try, the mitigations as built (with the test that pins them), and what is left.
Severity labels refer to the numbered gaps in section 8.

### 4.1 Anonymous internet client (the public app)

**Floods and slow clients.**
- *Built:* per-host request limits (above); request line, header count and size bounds applied before routing; a rate-limit
  engine (`src/Paduraksa.Proxy/RateLimiting`, lock-free fixed table, partitioned by client address, IPv6 as /64, accepted
  races documented on the type; `tests/Paduraksa.Tests.Proxy/RateLimiting`); proof-of-work challenges for browsers; rule sets
  (path, method, user agent) that refuse before any PBKDF2 is spent; Cloudflare origin lockdown and Authenticated Origin
  Pulls (`OriginLockdownTests`, `AuthenticatedOriginPullsTests`). Admin brute force is throttled separately (B3).
- *Residual:* **no connection-level IP deny and no per-IP connection cap** (G1): the places for them exist as marked seams
  (`src/Paduraksa.Proxy/Net/QuicClientInfoGate.cs`). Slowloris and connection exhaustion are bounded only by Kestrel's own
  defaults and the OS (G8). Rate limits, access lists and rule sets are **opt-in per host**; a new host has none (G10). Volumetric
  DDoS is a non-goal (5.4). Behind Cloudflare, the connection stage sees only edge addresses.

**Header and body abuse, request smuggling, path tricks.**
- *Built:* Kestrel and YARP do HTTP framing; the proxy never forwards `Forwarded` or a client's `X-Forwarded-*`; the gate and
  the simulator share one canonical **matching path** (`RoutePath`: backslash, `%2F`, `%5C`, dot segments, case rules) so
  `/admin` rules cannot be bypassed with `/%61dmin`, `/./admin` or `\admin`; pinned by `RoutePathPropertyTests`, including 150
  raw targets sent to a real Kestrel (it found a decoding bug in the simulator, §15); route paths that are encoded or
  non-canonical are refused at config time (`route.path.encoded`, `route.path.notCanonical`).
- *Residual:* anything Kestrel or YARP get wrong is inherited (supply chain, 4.9). No property test covers the rate-limit table
  or the PoW page (§15 gaps).

**TLS and SNI tricks.**
- *Built:* connection coalescing re-check (421); unknown SNI refused in release builds; per-host minimum TLS version and ALPN;
  OpenSSL's default cipher policy (no custom policy, by measurement, §7.2 B); offline client-cert chains; HSTS never sent for IP
  literals or loopback names.
- *Residual:* TLS 1.2 stays on by default (a host sets `MinimumVersion = Tls13` to refuse it). The placeholder self-signed
  certificate is on by default only in Debug builds and warned about in release (§3); a release build with it on is operator error.

**Spoofed client IPs.**
- *Built:* see B1. An untrusted peer's `cf-*`, `True-Client-IP`, `X-Forwarded-*` are ignored and stripped; the resolved address
  is a single overwritten value upstream; the request ID from a client is honoured only from a trusted peer. Pinned by
  `ForwardedHeadersTests`, `OriginLockdownTests` and the `client-address` fuzz target (an untrusted peer is never moved by a
  header and gets no country).
- *Residual:* a **misconfigured trusted-proxy list** (for example trusting a CIDR the attacker is in, or `Proxy` mode
  without lockdown on a host reachable directly) lets the attacker choose their identity for every address-based policy.
  The dashboard and simulator ("via Cloudflare" toggle) exist to make this visible; they do not forbid it. Country rules
  use `CF-IPCountry` only and a request not behind Cloudflare has an **unknown** country (§7.3 country rules, "Gaps").

**Bots, crawlers, scanners.**
- *Built:* managed `robots.txt` (a request, not a control), user-agent rules, PoW, analytics that classify user agents and count
  bots that ignore `robots.txt` (§7.4 analytics). None of these stop a determined client; the user-agent is attacker-controlled.

**Attacks on the PoW and rate limits themselves.**
- *Built:* the PoW challenge is HMAC-bound to host, masked client address and time; the cookie is HMAC-signed, `HttpOnly`,
  `SameSite=Lax`, `Secure` on the trusted scheme; verification allocates 0 B and uses the vault's `IMacKey`; if the key cannot
  be opened every protected route answers 503 (fail closed, §7.2 PoW). Fuzzed (`pow-cookie`, `pow-form`). The return target can
  never leave the site (fuzz property).
- *Residual:* the challenge is **stateless, so a solved one can be redeemed again** for ten minutes (a cookie for the same host
  and address only; accepted in §7.2). A botnet can solve at difficulty 16. Rate-limit keys are the resolved client address, so
  attackers with many addresses, or users behind one NAT, are not separated; abuse of the 10 000-key bounded memory falls into a
  shared overflow bucket (admin throttle, §10.1).

**Basic-auth access lists.**
- *Built:* passwords are vault credentials with PBKDF2 (210 000 iterations); a name nobody has and a user with no password pay
  the same PBKDF2 (no timing oracle); verification is cached per credential, masked address and attempt; the PBKDF2 runs in a
  bounded queue off the request thread; the rule-set policy runs first so refused requests cost no PBKDF2; plaintext HTTP
  hosts are warned about (`accessList.insecureCredentials`). Pinned by `PasswordCredentialStoreTests`, fuzz `basic-auth`,
  `AccessListPropertyTests`.
- *Residual:* basic auth is a weak scheme; a host without forced HTTPS sends it in clear (warned, not refused). The queue and
  the per-credential cost are a CPU lever for an attacker who can guess usernames; the rate limit before it is the defence and
  is opt-in.

### 4.2 Authenticated low-privilege admin user

Roles are permission sets enforced by one evaluator (`IPermissionEvaluator`) that every service takes; endpoints also carry
permission metadata and a test checks every mapped `/api/*` endpoint declares one (`ApiConventionTests`, §11.1).

| Role | Can do (code: `RolePermissions.cs`) | Cannot |
|---|---|---|
| Viewer | read config, certificates, providers; simulate; metrics | write anything; read audit or logs; see secrets (none are ever returned) |
| Auditor | the reads, plus audit and access logs | write anything; simulate |
| Operator | write config and certificates; read providers; simulate; metrics | users, providers/DNS/notifications writes, backups, logs, audit |
| Admin | all config, certificates and providers writes; read users; audit, logs | users writes, backups, other users' tokens and sessions (`users:write`, `backup:create`, `tokens:all`, `sessions:all` are Owner-only) |

- *Built:* no secret is ever returned by any response type (tests walk DTOs, OpenAPI, audit and logs); a user cannot raise their
  own roles (`users:write` is Owner-only in practice and a last-Owner guard exists); a token never exceeds its owner.
- *Residual (by design, but not obvious from the role names):*
  - **An Operator (`config:write`) controls routing and policy.** They can point an upstream at any internal address (including
    the cloud metadata address or the admin socket host), publish it on any name, set a forward-auth endpoint that the gate
    will call, widen trusted proxies, disable every protection on a host, and edit access-list passwords. PLAN §10.1 calls
    Operator "hosts/certs, no users/secrets"; in effect the Operator is a **network pivot inside the host's reach**. PLAN §10.1 and
    the dashboard now say so (G5). Assume Operator and Admin are fully trusted with respect to the node's network position.
  - A Viewer or Auditor can read topology (hosts, upstream addresses, access-list usernames, client IPs for the Auditor).
  - Per-user throttle and lockout are per process and reset on restart.
  - There is **no mandatory second factor**; TOTP is optional for everyone including the Owner. The overview warns while an Owner or
    Admin has none (G4).

### 4.3 A stolen API token (by scope)

- *Built:* secret shown once, stored as SHA-256, checked on every request; optional expiry; revocable by its owner or by an Owner
  (`tokens:all`); scopes are an intersection with the owner's live permissions; **a token cannot create, list or revoke
  tokens or sessions** (so a leaked token cannot extend its own life or end the session watching it), cannot change users,
  passwords or second factors, and a backup needs `backup:create` which only an Owner's token can hold. CSRF does not apply to
  bearer requests, which is correct because a bearer token is not ambient. Every token-made change is audited with the token as
  actor. Pinned by `SessionAndTokenTests`, `UserEndpointTests`, `BackupApiTests`.
- *Residual by scope:*

| Token scope | What a thief gets |
|---|---|
| read-only (`config:read`, `logs:read`, `metrics:read`, `certificates:read`) | Full topology and traffic metadata; access-log contents (client IPs, paths, user agents) with `logs:read`. No changes, no secrets. |
| `config:write` | Everything an Operator can do (4.2), including taking the site down or exposing internal services. Reversible through revisions and rollback, and visible in History and Audit. |
| `certificates:write` | Upload or replace certificates, order ACME certificates, issue from the local CA. Can serve a different certificate for a name; cannot read keys. |
| `providers:write` | Replace DNS credentials, create notification channels pointing at any address (SSRF accepted, B6), change ACME accounts. |
| `backup:create` | A passphrase-encrypted archive of everything. Useless without the passphrase the thief chooses to supply, but **the thief chooses it**: they hold a copy of every secret under a passphrase they know. The API call takes the passphrase in its body. Treat this scope as equal to Owner. |

- Tokens are bearer secrets: on the admin port the only protection in transit is TLS from the local CA, whose root the client
  must trust.

### 4.4 Malicious or compromised upstream

- *Built:* an upstream cannot plant or read the admin session cookie (`SessionCookieTransform`); it never sees the hop
  secret or an admin-route header; `Server` is replaced or removed per host setting; HSTS from the gate overrides an upstream's;
  an upstream cannot choose a forward-auth identity because the gate removes client-supplied identity headers and sets only the
  provider's; error replacement (`ErrorPages.ReplaceUpstream`) discards the upstream body unread and never applies to
  upgrades, event streams or gRPC (`ReplaceUpstreamErrorTests`, fuzz `upstream-error-replacement`); custom error documents get a
  style-only CSP (§7.3 M8). Hop-by-hop framing is YARP's. Upstream TLS: `System` by default.
- *Residual:* an upstream controls its own response headers and body (cookies, redirects, `Set-Cookie` for the proxied name,
  scripts it serves, caching headers); the proxy does not sanitise or add a CSP to proxied responses. A compromised upstream
  on a shared name can still affect cookies for sibling hosts under that name (browser scoping; only the admin cookie is
  specifically defended). `UpstreamTlsTrust.Any` removes authentication of the upstream. Response size is not limited. Health
  checks make requests to every destination from the proxy's network position.

### 4.5 Malicious or compromised forward-auth provider or notification endpoint

- *Built (forward-auth):* **fail closed**: any unexpected status, unreachable endpoint, oversized refusal body or unlisted
  exception is 502/504/503, never an allow; the gate sends only an allow-listed set of client headers (so a client's invented
  headers never reach the provider), strips the provider's identity names from the client's request first (`_` and `-`
  equivalent, on every route of the host) and sets only what the provider returned; the decision cache stores only allows,
  keyed by a keyed hash of everything the provider is sent; relayed headers are checked for control characters. Fuzzed
  (`forward-auth-answer`), property-tested (`ForwardAuthCachePropertyTests`, `ForwardAuthHeaderPropertyTests`).
- *Residual:* a **compromised provider can allow anything** and set any identity header the provider's config lists; that is its
  job. The provider receives each client's cookies and `Authorization` header. The cache means a revoked session can still pass
  for up to `cacheSeconds` (default 10, max 300). No per-provider TLS pinning. Only the route chain is guarded (a plain-HTTP
  host is warned, `authProvider.insecureSession`); the admin host is never behind a provider.
- *Built (notifications):* fixed error lines (no response echo), redirects refused, secrets lent only to the sender, webhook
  signing with HMAC over timestamp and body, Discord mentions disabled, email recipients and subjects sanitised, STARTTLS
  required when a login is set.
- *Residual:* a hostile endpoint receives each notification (hostnames, certificate names, upstream names, sign-in anomalies),
  and the URL is a bearer secret held by whoever hosts it.

### 4.6 Prompt-injected MCP scripts

Scenario: the proxy's data holds attacker-controlled text (user agents, paths, hostnames in the tail and logs). An assistant
reads it and is tricked into writing a malicious script, or a malicious operator-side assistant writes one.

- *Built:* MCP is **off by default**; the token decides everything (read-only is the documented default and the dashboard
  offers read scopes first); the policy in the parent refuses secret-bearing, destructive and identity endpoints regardless of
  token; writes are two-step; the script's engine has no network API, no token, no files (Landlock) and runs in a killable child
  with hard limits; frames are bound to the run and a child that speaks out of turn is killed; output is bounded and cleaned.
  Tests: hostile tail text comes back as a string and nothing it says is executed or sent; `__proto__` keys pollute nothing;
  twenty formerly dangerous scripts end as results or `The script was stopped` (`SandboxScriptTests`, `SandboxBoundaryTests`,
  `SandboxHostileChildTests`, `McpWriteTests`, `McpReviewedReadsTests`).
- *Residual:*
  - With a **writing token**, an injected script can make any non-refused change the token allows after a confirm step that the
    script itself supplies (`confirm: true` is part of the script, not a human click). The two-step rule is a speed bump for
    mistakes, not a barrier against a hostile script. Use a read-only token.
  - A script can read everything the token can read and return it to the model (and so to the model provider).
  - One token can hold all four children for 30 s each (availability of MCP only).
  - An engine escape lands in a process with no secrets, no files, no TCP or UDP socket and a few seconds to live. The syscall surface
    is otherwise the runtime's, and a kernel exploit is out of scope (5.7). What the child saw in that run can leave only over the
    pipes it was given, which the parent reads under limits and which end in the model's answer.
  - Hardening is Linux only; elsewhere the child has the parent's limits and kill but no system-call hardening (it says so in its
    `ready` frame). On a kernel older than 6.7 there is no Landlock network rule and the seccomp filter alone closes the sockets; on a
    runtime whose seccomp profile blocks the `landlock_*` calls there is no Landlock at all (G14: the default profile of Docker 29 does
    not, checked on the real image). The server logs what applied and what did not (§11.2, install.md "MCP").

### 4.7 Someone with read access to `/data` or a backup file

| What they obtain | What it gives | Mitigation |
|---|---|---|
| `vault.db` alone | Sealed rows. Useless without the KEK. | AES-256-GCM envelope, AAD-bound rows. |
| `vault.db` + `master.key` in the same directory (an install without the key file) | Every secret: certificate keys, DNS tokens, TOTP seeds, password verifiers, the local CA root. | The shipped compose file uses a Docker secret, so the key is not in the volume; where it is, the overview warns the Owner and startup logs a Warning (`VaultRuntime.cs`) (G3). |
| `paduraksa.db` | Users, PBKDF2 hashes of admin passwords, SHA-256 of session and API-token secrets (high-entropy, so not practically reversible), recovery-code hashes, TOTP references (not seeds), the config, the revision history, the audit log with client IPs, analytics. Admin password hashes are offline-crackable (PBKDF2-210000). | Mode 0600 with its WAL files (G7), inside the data directory's own mode. Backup is encrypted. |
| `last-good.json` | The whole live config (no secrets). | 0600. |
| `logs/` | Client IPs, paths (queries with sensitive keys redacted), user agents, referers. | 0700/0600; retention 30 files, 30 days, 1 GiB by default (`PaduraksaOptions.cs`). Paths are logged as sent, so a secret in a path is in the log (§7.4 analytics). |
| a `*.pdkbak` file | Nothing without the passphrase: Argon2id (64 MiB, 3 passes, 4 lanes) then a chunked AES-256-GCM stream, header authenticated, truncation and reordering detected; the source master key is **not** in the archive (keys re-wrapped under a backup-only key) (§8.4). | The passphrase's strength (12-1024 characters, no composition rules). Pinned by `Paduraksa.Tests.Vault/Backup` (RFC 9106 vector, tamper and truncation cases). |
| the **old** `vault.db` plus the old KEK after `rotate-kek` | `rotate-kek` re-wraps DEKs only; the data is not re-encrypted (§8.3). | To cut off an old copy, replace the secrets themselves. |
| the setup token (before setup) | Become the Owner. | 0600 file, deleted once an Owner exists; also in `docker logs`. |

- *Residual:* anyone who can read `/data` **and** the master key owns the vault; nothing in the design defends that (5.1).
  `docker logs` readers see the setup token until setup is done.

### 4.8 Someone with a shell as the container user (uid 10001)

They can read the data directory, the master key wherever the process can read it, `/proc/<pid>/environ` (which holds
`PADURAKSA_MASTER_KEY` if that source is used) and, where `ptrace` is not blocked, process memory; they can call the CLI (which acts as
`cli`, "may do everything", §10.1: `admin reset-password` for any user, `admin disable-2fa`), and connect to
`run/admin.sock` (the hop secret is in process memory and the request must also carry it, so the socket alone is not enough).
**This is a non-goal** (5.1): the container user is the process. Mitigations that still reduce blast radius: read-only root
file system, dropped capabilities, `no-new-privileges`, the master key as a file readable only by uid 10001 and not stored in
an environment variable. The audit log records CLI actions (not the identity of the shell user).

### 4.9 Supply-chain risk in dependencies

- *Built:* central package versions (`Directory.Packages.props`); Dependabot weekly for NuGet, GitHub Actions and Docker base
  images (`.github/dependabot.yml`); the Dockerfile pins both base images **by digest**; libmsquic is pinned and its feed key
  fingerprint is checked in the build; the published image carries an SBOM (`ci.yml`, `sbom: true`); no native crypto
  libraries beyond the runtime's (Argon2 is a managed library on purpose, §8.4); the vault holds the only references to
  `Konscious.Security.Cryptography.Argon2` (`BackupBoundaryTests`); the proxy project may not reference Data, Storage or EF.
  Notable third-party code on sensitive paths: YARP, Kestrel (framework), Jint (MCP child only), MailKit, SQLite
  (bundled), MudBlazor and Blazor (dashboard), CsCheck/SharpFuzz (test only), ArchUnitNET (test only).
- *Residual:* CodeQL and an explicit `NuGetAudit` now exist (G9; high and critical advisories fail the build, a moderate one warns);
  there is no signed-image or provenance verification step documented
  for operators; floating `:main` tag in `deploy/compose.yaml` (the compose comment says a `:0.1` tag replaces it once there is
  a release). Jint runs stock in a child; a vulnerability in it is contained by the sandbox, not prevented.

---

## 5. Explicit non-goals

1. **Root on the host, or a shell as the process's uid.** Either can read the data directory, the master key (file or
   environment) and process memory. The vault is a code-structure boundary inside one process (B4), not a defence against a
   local attacker.
2. **A compromised master key together with the data directory** (or with a backup's source `vault.db`). That is a total
   compromise of every stored secret and the local CA.
3. **A malicious Owner.** The Owner holds every permission, including backups; roles below Owner are trusted with the node's
   network position (4.2).
4. **Volumetric DDoS and anything beyond one node's capacity.** There is no clustering or HA (§0), no connection-level IP deny or
   per-IP cap yet (G1), and no scrubbing. Use Cloudflare in front and origin lockdown.
5. **Deep inspection.** No OWASP CRS or ModSecurity-style payload inspection (§0). Rule sets match path, method and user agent;
   the proxy does not scan request or response bodies, and does not protect a vulnerable upstream application from its own
   bugs.
6. **Protecting proxied applications' own authentication.** Forward-auth and basic auth are gates in front of them, not a
   replacement.
7. **Kernel, runtime and framework vulnerabilities** (Kestrel, YARP, .NET, OpenSSL, the OS). The sandbox's boundary assumes a
   sound kernel. Keep the image up to date.
8. **Windows and macOS hardening of the MCP child**, and any platform that cannot apply Landlock (the child still has limits and
   a kill).
9. **Untrusted admin networks.** The admin port is designed for a trusted LAN or an access-listed name. Exposed to the internet it
   is protected by sign-in throttling, lockout and optional TOTP only (G4).
10. **Multi-tenancy.** Users share one config; roles limit capability, not visibility of topology.
11. **Fleet and control-plane trust** (§16.1): after v0.1, out of scope here.
12. **Browsers that do not honour the `__Host-` cookie rules or `SameSite`**, and Safari over the plain-HTTP loopback fallback
    (§10.1).
13. **Passkeys, OIDC and trusted-header admin sign-in**: not built; there is nothing to attack and nothing to rely on.

---

## 6. Operational guidance

Ordered by how much risk each step removes.

1. **Keep the master key off the data volume.** Use `PADURAKSA_MASTER_KEY_FILE` pointing at a Docker secret or systemd
   `LoadCredential` (the shipped compose file does, owner uid 10001, mode 0400), not `PADURAKSA_MASTER_KEY` (visible in
   `docker inspect` and `/proc/<pid>/environ`) and not the generated `data/master.key`. Back the key up **separately** from the
   volume. If you do not, the overview warns the Owner and the log carries a Warning (G3, install.md "The master key").
2. **Do not expose the admin port to the internet.** The compose file maps `9443` to loopback; keep it there, or map it to a management network, or set
   `Paduraksa__Listen__AdminAllowedSources` (loopback is not implied), or publish the admin only as an admin host behind an
   access list. Turn on TOTP for every user. Never proxy a public name to `127.0.0.1:9443`; use the admin host feature, which
   carries the real client address (§3.1).
3. **Cloudflare**: if you proxy through it, set the trusted-proxy mode, turn on **origin lockdown** and, for the strongest form,
   **Authenticated Origin Pulls**, and firewall 80/443 to Cloudflare's ranges. Without lockdown the origin can be reached
   directly by IP and Cloudflare's WAF bypassed.
4. **Turn protections on.** A new host has no rate limit, access list, rule set or PoW; add the ones you need, and use the
   Security overview and the request simulator to check what protects each host. Keep HTTPS forced for any host with basic auth
   or a forward-auth provider (the compiler warns when you do not).
5. **MCP**: leave it off unless you want it. If you enable it, create a **read-only** token (config/logs/metrics/certificates
   read), give it only to assistants you trust, and remember the model provider sees what the token can read. Never give an
   assistant a token with `backup:create` or `providers:write`.
6. **Backups.** Make them regularly (`paduraksa backup`, docs/install.md "Backups"), use a long random passphrase (the file is
   only as strong as it: Argon2id slows guessing but cannot save a weak one), store the passphrase apart from the archive and
   apart from the master key, and test a restore on a spare machine. Restoring needs the server stopped. A leaked archive plus
   its passphrase is every secret you hold. Rotate DNS tokens, ACME accounts and certificate keys if that happens.
7. **Tokens.** Prefer short expiry and the fewest scopes. Revoke tokens you no longer use. Do not reuse an Owner's token for
   automation (it can hold `backup:create`).
8. **Trust the local CA root deliberately.** Installing the root on a device lets the vault's CA key mint a certificate any
   name on that device. The root has no name constraints and no rotation yet (§3 "Not built", §9.1). Install it only on devices
   you administer and re-evaluate if the vault may have leaked.
9. **Logs and privacy.** The access log holds client IPs, paths and user agents for 30 days or 1 GiB by default
   (`Paduraksa__Logs__*`); tune retention to your obligations. Paths are logged as sent: do not put secrets in a URL path.
   Query parameters named like tokens or passwords are replaced by `REDACTED` (and `Paduraksa:AccessLog:LogQueryString=false`
   drops the query); `Cookie` and `Authorization` are never logged. Analytics hash visitors with a per-process random salt and
   store no addresses. The Auditor role and the `logs:read` scope can read IPs.
10. **Updates.** Pin the image tag or digest and update on a schedule; Dependabot bumps the base-image digests upstream. Read the
    release notes for security changes.
11. **File permissions.** Keep `/data` at 0750 or tighter and owned by uid 10001 (bind mount: `install -d -o 10001 -g 10001 -m
    0750`). Do not make backups or the key file world-readable; the CLI warns for the cases it can see.
12. **Containers.** Keep `read_only`, `cap_drop: [ALL]` (plus `NET_BIND_SERVICE`) and `no-new-privileges`. Leave room for four 384
    MiB sandbox processes in the memory limit if MCP is on.
13. **Monitoring.** Configure notification channels for certificate expiry, ACME failures, upstream health and sign-in anomalies;
    run the audit verifier (`GET /api/v1/audit/verify`) and record the head hash somewhere the server cannot write (G6).

---

## 7. Where the claims are tested

| Claim | Test or tool |
|---|---|
| Key material stays in the vault | `tests/Paduraksa.Tests.Architecture/KeyMaterialBanTests.cs`, `VaultContractTests.cs`, `BackupBoundaryTests.cs`, `DependencyRuleTests.cs`; compile-time bans in `BannedSymbols*.txt` |
| Admin session, CSRF, setup gate, plain-HTTP redirect | `tests/Paduraksa.Tests.Integration/Admin/CsrfTests.cs`, `ApiCsrfTests.cs`, `SetupGateTests.cs`, `SignInTests.cs`, `HardeningTests.cs`, `AdminPlainHttpRedirect*Tests.cs`, `tests/Paduraksa.Tests.Unit/Admin/AdminThrottleTests.cs` |
| Admin through the proxy (hop secret, cookie isolation, lock-out) | `AdminThroughProxyTests.cs`, `AdminForwardedHeadersTests.cs`, `tests/Paduraksa.Tests.Data/Commands/AdminHostLockoutTests.cs`, `tests/Paduraksa.Tests.Proxy/EndToEnd/SessionCookieTests.cs` |
| Tokens, scopes, sessions | `SessionAndTokenTests.cs`, `UserEndpointTests.cs`, `ApiConventionTests.cs` |
| Client IP, Cloudflare, lockdown | `tests/Paduraksa.Tests.Proxy/Gate/OriginLockdownTests.cs`, `Tls/AuthenticatedOriginPullsTests.cs`, `EndToEnd/ForwardedHeadersTests.cs`, `tests/Paduraksa.Tests.Unit/Cloudflare/CloudflareRangeRefresherTests.cs` |
| Path canonicalisation vs Kestrel | `tests/Paduraksa.Tests.Proxy/Snapshot/RoutePathPropertyTests.cs` |
| Access lists, rule sets, forward-auth | `AccessLists/AccessListPropertyTests.cs`, `ForwardAuth/*`, `tests/Paduraksa.Tests.Vault/Passwords/PasswordCredentialStoreTests.cs` |
| Request limits, error replacement | `Gate/RequestLimitsGateTests.cs`, `EndToEnd/RequestLimitsEndToEndTests.cs`, `EndToEnd/ReplaceUpstreamErrorTests.cs` |
| Log injection and redaction | `tests/Paduraksa.Tests.Proxy/Logging/AccessLogFormatterTests.cs`, fuzz `access-log-tail` |
| MCP boundary | `tests/Paduraksa.Tests.Integration/Mcp/*`, `Admin/Mcp*Tests.cs`, `tests/Paduraksa.Tests.Unit/Mcp/*`, fuzz `mcp-sandbox` |
| Backup and restore | `tests/Paduraksa.Tests.Vault/Backup`, `Admin/BackupApiTests.cs`, integration restore tests |
| Audit chain | `tests/Paduraksa.Tests.Data/Config/AuditChainVerifierTests.cs`, `AuditLogTests.cs` |
| One server per data directory | `tests/Paduraksa.Tests.Unit/Admin/DataDirectoryLockTests.cs` |
| Fuzz targets (`fuzz/Paduraksa.Fuzz.Targets`) | `proxy-protocol`, `pow-cookie`, `pow-form`, `basic-auth`, `client-address`, `forward-auth-answer`, `error-page`, `upstream-error-replacement`, `access-log-tail`, `yaml-import`, `mcp-sandbox`; every committed corpus and regression input is replayed on each commit by `tests/Paduraksa.Tests.Fuzz`; `scripts/fuzz.sh` and `.github/workflows/fuzz.yml` run them (§15). Property tests use CsCheck. |

Not fuzzed (§15 "Left out"): the dashboard's parsing, the live tail's SSE framing, the notification senders' payloads, the ACME
response parser, multipart upload parsing (listed in PLAN §15 "Security" as a target). The release checklist run
(`scripts/fuzz.sh --time 3600 --merge`) is an M11 item that this document does not claim was done.

---

## 8. Gaps found

Ordered by severity. "Gap" means a mismatch with PLAN, a missing mitigation that a reader would assume exists, or a default that
weakens a documented property. Nothing here was fixed in this branch.

| # | Severity | Gap | Evidence | Suggested fix |
|---|---|---|---|---|
| G1 | **Medium** | **No connection-level IP deny and no per-IP concurrent-connection cap.** PLAN §7.2 A lists both as stage-A steps; PLAN's "as built in M1" note calls them a marked seam for M7, and M7 built the host-level access list but not these. | `src/Paduraksa.Proxy/Net/QuicClientInfoGate.cs:33` ("Neither exists yet, so every connection is admitted"); no per-IP cap anywhere in `src/Paduraksa.Proxy` | Build them (CIDR trie from the snapshot, fixed table with CAS, exempt trusted Cloudflare peers, release on connection close incl. QUIC) or amend §7.2 A to say not built and add it to the roadmap. State the connection-flood posture in install.md. |
| G2 | ~~Medium~~ **Fixed** | **The MCP child's network was not confined.** Fixed on branch `security-gaps`: Landlock network rules deny TCP bind and connect (kernel 6.7+), a seccomp filter refuses every socket but `AF_UNIX` and `io_uring_setup` (any kernel, x86-64 and arm64). Reported in the `ready` frame and once in the server log. Residual: a kernel older than 6.7 relies on the filter alone; an architecture other than x86-64 and arm64 has no filter; local sockets and pipes stay. | `src/Paduraksa.Mcp/ProcessHardening.cs` (`RestrictNetwork`), `SyscallFilter.cs`; `SandboxHostileChildTests` (a connect, bind and UDP socket from the script thread fail), `SandboxBoundaryTests` | Done. |
| G3 | ~~Medium~~ **Fixed** | **The default install kept the master key beside `vault.db`, and the only signal was a log Warning.** Fixed on branch `security-gaps`: the overview's "things need you" list warns an Owner while the key is beside the vault (`VaultStatus.KekBesideDatabase`), says why and links to install.md "The master key"; the shipped `deploy/compose.yaml` now uses the key-file Docker secret by default. Residual: someone who runs the quick start without the secret still has the key in the volume (the warning stays until they move it), and only the Owner sees it. | `src/Paduraksa.Dashboard/Pages/HomeSnapshot.cs`, `Home.razor`; `OverviewAndNavigationTests` | Done. |
| G4 | ~~Medium~~ **Mitigated** | **The admin port bound `0.0.0.0:9443` with no allowlist by default, and second factor is optional.** Mitigated on branch `security-gaps`: the compose example publishes `127.0.0.1:9443:9443` (the LAN-address form is in a comment), install.md has an "Exposing the admin" section, and the overview warns an Owner or Admin whose account has no second factor, and an administrator about the others. Not changed: nothing forces a second factor, the server still binds `0.0.0.0` inside the container (the mapping decides), and a deployment that publishes the port wider has only throttling, lockout and optional TOTP in front of the sign-in. | `deploy/compose.yaml`; `PaduraksaOptions.cs`; `HomeSnapshot.cs`; `OverviewAndNavigationTests` | Optional: allow requiring TOTP for Owner and Admin roles. |
| G5 | ~~Low-Medium~~ **Fixed (documented)** | **A role description that understated the Operator.** Fixed on branch `security-gaps`: PLAN §10.1 and the dashboard's role descriptions (Users and roles, the user drawer, the token scope for `config:write`) now say that `config:write` lets an Operator route to any internal address, set a forward-auth endpoint the gate calls, change trusted proxies and edit access-list credentials, and that Operator is effectively a trusted role. No permission changed. Residual: the capability itself stays (Admin holds it too); splitting a `network:write` permission or an optional private-address deny remains a later option. | `src/Paduraksa.Dashboard/Identity/IdentityText.cs`; PLAN §10.1 | Optional: a `network:write` permission. |
| G6 | **Low** | **The audit chain is tamper-evident only against partial edits.** The hash is an unkeyed SHA-256 chain in the same database; anyone who can write `paduraksa.db` can rewrite the whole chain and the verifier will pass. There is no external anchor. | `src/Paduraksa.Application/Audit/AuditChain.cs`; `AuditQueryService.VerifyAsync` returns the head hash | Document honestly (done here); suggest an HMAC with a vault MAC key and/or sending the head hash periodically to a notification channel, or an export the operator stores off-box. |
| G7 | ~~Low~~ **Fixed** | **`paduraksa.db` had no explicit file mode.** Fixed on branch `security-gaps`: the file and its `-wal` and `-shm` are created 0600 (`PrivateSqliteFile`, shared with the vault's database), and an existing install's wider modes are restricted at startup with one warning in the log. Residual: the data directory keeps the mode its creator gave it (0750 in the image; a bind mount is the operator's), and a file system without Unix modes gets none. | `src/Paduraksa.Storage/Sqlite/PrivateSqliteFile.cs`, `ConfigDatabase.cs`; `ConfigDatabaseFileModeTests` | Done. |
| G8 | **Low** | **Slow-client and connection limits are Kestrel's defaults and are not stated.** PLAN §5.3 mentions "header timeouts" as host settings; none exist. No explicit `RequestHeadersTimeout`, `MinRequestBodyDataRate`, `MaxConcurrentConnections` or HTTP/2 stream limits; YARP's activity timeout is YARP's default; only a 10 s connect timeout is set. | `rg 'Limits\.'` in `src` shows only body size, request line, header count and the HTTP/2 header field size; `PaduraksaForwarderHttpClientFactory.cs:38` | State the effective defaults in install.md; decide whether to set explicit values and expose a per-host header timeout; the Kestrel defaults themselves were not re-checked against .NET 10 here. |
| G9 | ~~Low~~ **Fixed** | **CI had neither a CodeQL workflow nor an explicit vulnerability gate.** Fixed on branch `security-gaps`: `.github/workflows/codeql.yml` (C#, pushes to main, pull requests, weekly) and `NuGetAudit` set explicitly in `Directory.Build.props` (mode `all`, level `moderate`; NU1903 and NU1904 are errors, NU1902 stays a warning so that a moderate advisory with no fix yet cannot block every build; PLAN §15). A restore of the solution reported no advisory. Residual: the workflow has not run yet (it needs a push), nothing verifies image signatures or provenance for operators, and Dependabot is the only update path. | `.github/workflows/codeql.yml`, `Directory.Build.props` | Done. |
| G10 | **Low** | **A new host is unprotected until the operator opts in.** PLAN §12.1 planned a security preset (Off / Balanced / Strict) in the wizard; the wizard and the presets were not built. Built-in defaults (`HostSettingsLayers.cs`): force-HTTPS off, HSTS off, PoW off, no rate-limit policy, access list or rule set until one is named, TLS 1.2 minimum, 100 MiB body limit. | PLAN §12.1 "Owner decision: no wizard"; `src/Paduraksa.Application/Compilation/HostSettingsLayers.cs:28-60` | A "Balanced" preset in the global defaults (sane rate limit, the built-in rule-set presets, request limits) and a checklist item. At least say so in install.md. |
| G11 | **Low** | **The local CA root has no name constraints and no rotation.** A leaked vault means a CA that devices which trusted the root accept for any name, for ten years. Documented as deferred. | PLAN §8.2 "Vault-signed certificates", §3 "Not built" | Add name constraints limiting the root to the owner's domains, and warn in the trust-the-root card. |
| G12 | **Low** | **Fuzz and property coverage gaps already admitted in PLAN §15 remain**: the rate-limit table and the PoW page have no property test; the ACME response parser, multipart parsing and the dashboard have no fuzz target; the release-length fuzz run (M11) is not yet recorded. | PLAN §15 "Gaps", "Left out" | Add the targets, run `scripts/fuzz.sh --time 3600 --merge` for the release, record the sessions in §15. |
| G13 | **Info** | **Docs drift, not security:** the README still lists custom error pages, metrics, a CLI, YAML import/export and backups as "Planned"; all are built. | `README.md` ("Planned") | Update the README. |
| G14 | ~~Info~~ **Checked** | **Docker's default seccomp profile and Landlock.** Checked on the image built from this branch, run under Docker 29.9.3 on Linux 7.3.3 with the compose hardening and `Paduraksa__Mcp__Enabled=true`: the first MCP call logged `Landlock: no file system access (every thread); Landlock network: no TCP bind or connect (every thread); seccomp: only AF_UNIX sockets, no io_uring (every thread)`. A profile that blocks `landlock_*` was also run: the log says `Landlock: not available (errno 1)` and the call still works. | `src/Paduraksa.Mcp/SandboxProcessPool.cs` (the one-time log line) | install.md "MCP" documents the result and the operator's choice (a profile that allows the three calls, or accepting it). Podman and Kubernetes runtimes were not run; their profiles differ. |

Things that were checked and **agree** with PLAN (spot checks, *code*): the key-material ban list and its wiring; session and
API-token secrets stored only as SHA-256; the cookie attributes; the role permission table and token narrowing; the MCP child's
cleared environment; `master.key` created 0600 and refused when others can read it; `vault.db` and `last-good.json` created
0600; the log directory 0700 and files 0600; ACME directory URLs https-only; notification and ACME HTTP handlers refuse
redirects; the Cloudflare range list's sanity checks; the audit chain verifier is exposed at `GET /api/v1/audit/verify`.

---

## 9. Review cadence

Update this document when any of the following changes, in the same pull request:

- **A new listener or port**, a change of what the admin app serves, a new way to reach the admin (a new internal listener, an
  OIDC or trusted-header provider, passkeys), or a change to which proxy headers any listener trusts.
- **A new outbound integration** (another DNS provider, a new notification kind, a CA-specific flow, anything that makes the
  server open a connection to an address a user or a remote party chose): add it to the table in B6 with its SSRF controls.
- **A new secret kind** or a new sanctioned egress from the vault (`SecretKind`, `INotificationSecretSource`-style lending, a new
  `Core/Vault` interface): update section 2, B4 and `VaultContractTests`.
- **A new permission, role or token scope**, or any endpoint on the MCP refused or reviewed lists.
- **A change to the MCP sandbox** (limits, hardening, pool, protocol), or enabling MCP by default.
- **A new policy in the gate chain**, a change to client-IP resolution or trusted-proxy handling, or a new Cloudflare feature.
- **Backup format or key-handling changes**, a new place data is written under `/data`, or a new log or analytics field that
  carries client data.
- **Fleet or control-plane work** (§16.1): this model covers one node; a second node needs its own trust-boundary section.
- **A fixed gap**: remove it from section 8 and note the PR.
- Before every **release** (v0.x tag): re-read section 8 and the "agree" list, re-run the fuzz harnesses per §15, and confirm the
  evidence levels still hold. At least once per quarter otherwise.
