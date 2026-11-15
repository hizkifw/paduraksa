# Threat model: the full analysis (v0.1)

For contributors and security reviewers: what is claimed, where it is enforced and what is not covered.

This is the full analysis behind the short [Threat model](../threat-model.md) page, which says what Paduraksa defends against and what to do about it. The numbering continues from that page: its sections 1, 5 and 6 (deployment, non-goals, operational guidance) are there, and this page holds sections 2 to 4 and 7 to 9.

## In short

- Assets: every key and credential sits in `vault.db`, sealed under one master key. The config database holds no secrets, and backups are sealed by one passphrase (section 2).
- Seven trust boundaries (B1 to B7): the public listener, the gate and YARP, the admin app, the vault inside the process, the MCP child, outbound calls and the CLI with the data directory (section 3).
- The vault is a code-structure and review boundary, not a process boundary. A memory-safety bug anywhere in the process can read keys in memory.
- Operator and Admin are fully trusted with respect to the node's network position. `config:write` is a network pivot.
- The MCP sandbox assumes the script engine can be escaped, and limits what an escape reaches: no secrets, files, sockets or route back to the parent.
- Attackers considered: an anonymous client, a low-privilege admin, a stolen API token, a hostile upstream or auth provider, a prompt-injected MCP script, someone with `/data` or a backup, someone with a shell as the container user, and the supply chain (section 4).
- A process-level attacker (code execution as uid 10001) and volumetric DDoS are non-goals.
- Open gaps at the v0.1 review: G6, G10, G11 and G12 (section 8). A fixed gap is deleted from the list.
- Update this page in the same pull request as any change listed in section 9.

It has two readers:

- a contributor who wants to know which rules not to break;
- a security reviewer who wants to know what is claimed, where it is enforced and what is not covered.

It describes the code **as built** at the v0.1 candidate. Where the code and [the plan](../PLAN.md) disagree, or a mitigation is missing, [Gaps found](#8-gaps-found) says so. File paths are relative to the repository root. "PLAN" means [PLAN.md](../PLAN.md) and the pages under this folder.

**Evidence levels.** A claim marked *(code)* was checked against the source. A claim marked *(PLAN)* rests on PLAN's "As built" text and the named tests, which were read but not re-run. "Pinned by" names the test that is meant to hold the claim.

Contents: [2 Assets](#2-assets) | [3 Boundaries and flows](#3-trust-boundaries-and-data-flows) | [4 Attackers](#4-attackers-mitigations-and-residual-risk) | [7 Test evidence](#7-where-the-claims-are-tested) | [8 Gaps found](#8-gaps-found) | [9 Review cadence](#9-review-cadence)

---

## 2. Assets

| Asset | Where it lives | Why it matters | Confidentiality / integrity / availability |
|---|---|---|---|
| **TLS private keys** (ACME, uploaded, self-signed, local CA root and leaves, upstream mTLS client certs) | `vault.db`, sealed per row ([vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface), [vault-and-certificates.md, At-rest encryption](vault-and-certificates.md#at-rest-encryption)) | Impersonate every proxied name; the local CA root, if its root was installed on devices, can mint a certificate for **any** name those devices trust unless the operator limited it to chosen names (a root is unlimited by default) | C and I |
| **ACME account keys, EAB HMAC keys** | `vault.db` | Order or revoke certificates for the owner's domains | C and I |
| **DNS provider credentials** (Cloudflare, deSEC) | `vault.db`, `DnsApiToken` secrets | Edit DNS for the owner's zones: hijack a domain, get certificates | C |
| **Notification secrets** (webhook URLs, tokens, SMTP password, signing keys) | `vault.db` | Spoof or read alerts; webhook URLs are bearer secrets | C |
| **TOTP secrets** | `vault.db`; recovery codes as SHA-256 in `paduraksa.db` | Second factor for admin sign-in | C and I |
| **Access-list password credentials** | `vault.db` (PBKDF2 verifiers, [vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface) "password credentials") | Basic-auth users of proxied hosts | C |
| **Data Protection key ring** | `vault.db` ([vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface)) | Dashboard antiforgery and circuit state; forging them | C and I |
| **PoW HMAC key** | `vault.db`, reserved ID `ProofOfWorkKey.Id` | Forging proof-of-work cookies, bypassing the challenge | C and I |
| **The master key (KEK)** | env, file or `data/master.key` | Opens everything above | C and I |
| **Config and its history** | `paduraksa.db` revisions, `last-good.json` | Controls what the proxy does; the history reveals topology (hosts, upstream addresses, access-list usernames). Holds **no secrets** by design (config export carries none, [vault-and-certificates.md, At-rest encryption](vault-and-certificates.md#at-rest-encryption)) | I mainly, C moderately |
| **Audit log** | `paduraksa.db`, hash chain ([identity.md, Identity entities](identity.md#identity-entities), [config.md, The config document and the compiler](config.md#the-config-document-and-the-compiler)) | Accountability for every change and sign-in; includes client IPs | I (tamper-evidence) |
| **Admin sessions and API tokens** | `paduraksa.db`, stored as SHA-256 of the secret (*code*: `SessionService.cs`, `ApiTokenService.cs`) | Act as a user with their permissions | C |
| **Availability of proxied services** | The running process, snapshot, certificates | The whole point; one node, no HA ([PLAN section 0](../PLAN.md#0-goals--non-goals)) | A |
| **Client data in transit and logs** | `/data/logs/access.log` (IPs, paths, queries, user agents, referers), analytics rollups, the live tail | Privacy of the proxied sites' visitors | C |
| **Backups** (`*.pdkbak`) | wherever the operator puts them | Contain **everything** in `paduraksa.db` and `vault.db`, sealed by one passphrase ([vault-and-certificates.md, Backup and restore](vault-and-certificates.md#backup-and-restore)) | C |

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
 |     |                                                    |   keys never leave; 4 sanctioned egresses ([vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface))                    |
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
  CIDRs, with a header timeout, and is mandatory once enabled ([hosting.md, Host mechanics](hosting.md#host-mechanics) Listen options, [hot-path.md, A. Connection](hot-path.md#a-connection)). QUIC has no PROXY protocol.
  Fuzzed (`proxy-protocol`).
- **Client IP resolution** (`TrustedProxyClientAddressSource`, [hot-path.md, C. Gate middleware](hot-path.md#c-gate-middleware), [cloudflare.md](cloudflare.md)): the peer is the PROXY source or the socket
  address. Only a peer in the trusted-proxy policy (Cloudflare edge ranges, the configured `cloudflared` address, or a
  generic CIDR list) may supply the client through `CF-Connecting-IP`, `True-Client-IP` or `X-Forwarded-For`. A header from
  anyone else is ignored. Parsing is our own strict span parser (no octal, hex, short forms or zone IDs). Fuzzed
  (`client-address`), with a differential property against the BCL.
- **Cloudflare ranges** are baked in (`src/Paduraksa.Proxy/Net/CloudflareIpRanges.cs`) and refreshed over https
  (`src/Paduraksa.Application/Cloudflare/CloudflareRangeFetcher.cs`); a list is refused if it is malformed, has a block
  wider than any real edge range, or is implausibly small or large (`CloudflareEdgeRanges.cs`, *code*).
- **TLS and SNI.** A prebuilt `SslServerAuthenticationOptions` per (certificate, host TLS policy, transport); an unknown
  name is rejected at the handshake in a release build (the placeholder certificate defaults to off in release, [hosting.md, Host mechanics](hosting.md#host-mechanics)).
  Because clients coalesce connections across names a certificate covers, the host's TLS policy is **re-checked per
  request** (421 Misdirected Request) ([hot-path.md, C. Gate middleware](hot-path.md#c-gate-middleware)). Client-certificate chains are built **offline**: a client-controlled AIA URL
  would otherwise be SSRF ([hot-path.md, B. TLS handshake](hot-path.md#b-tls-handshake)).
- **Request limits.** Per host: body 100 MiB by default, header count 100, header bytes 32 KiB, target 8 KiB, with the
  listeners' outer bounds (`src/Paduraksa.Proxy/Gate/RequestLimitsGate.cs`, [waf.md, The blocks at a glance](waf.md#the-blocks-at-a-glance) "request limits"). The slow-client and
  connection limits are set explicitly from validated `Paduraksa:Limits` options (headers timeout 15 s, keep-alive, TLS handshake
  timeout, minimum data rates with a grace, global and WebSocket connection caps, HTTP/2 streams; [hot-path.md, Request pipeline](hot-path.md#request-pipeline) *As built: the
  connection stage and the listeners' limits*, `src/Paduraksa.Proxy/Net/ServerLimits.cs`). Fixed G1 and G8.
- **Per-request header hygiene** ([hot-path.md, E. YARP](hot-path.md#e-yarp), `ForwardedHeadersTransform`): `X-Forwarded-For`, `X-Real-IP`, `-Proto`, `-Host`
  are overwritten, never appended; `Forwarded`, `X-Forwarded-Prefix/-Port`, every `cf-*` header and `True-Client-IP` are
  removed unless the peer is a trusted Cloudflare peer; a provider's identity headers are removed from every route of the host.

### B2. Gate, policy chain, YARP, upstreams

- **Order** is fixed ([hot-path.md, C. Gate middleware](hot-path.md#c-gate-middleware), D): client IP, scheme, ACME challenge, host, TLS check, host chain (access list address
  and country, rule sets), request limits, redirect, robots, Redirect/Respond hosts, route, route chain (rate limit, forward
  auth, PoW), body limit, then YARP. Refusals on the host chain precede the redirect so a blocked client gets 403, not 301.
- **Upstream TLS trust modes** (`UpstreamTlsTrust` in `src/Paduraksa.Core/Model/UpstreamEnums.cs`): `System` (default,
  normal validation against the `Host` name), `Pinned` (a validation callback from `UpstreamCertificatePins`) and `Any`
  (YARP's accept-any: encrypted but unauthenticated, as the enum's own comment says). Pinned by
  `tests/Paduraksa.Tests.Proxy/Yarp/PaduraksaForwarderHttpClientFactoryTests.cs`.
- **The admin host through the proxy** ([hosting.md, Exposing the admin through the proxy](hosting.md#exposing-the-admin-through-the-proxy)): the public app forwards one admin host to the internal listener. The admin
  app trusts `X-Forwarded-*` **only** on that listener **and** only with a matching 32-byte per-process hop secret
  (`AdminForwardedHeadersMiddleware`, `src/Paduraksa.Admin/Auth/AdminForwardedHeaders.cs`). The `__Host-paduraksa-session`
  cookie is removed from every request to every non-admin upstream, and a `Set-Cookie` of that name from a non-admin upstream
  is dropped (`src/Paduraksa.Proxy/Yarp/SessionCookieTransform.cs`). A lock-out guard refuses a change that would cut the
  caller's own access ([hosting.md, Exposing the admin through the proxy](hosting.md#exposing-the-admin-through-the-proxy)).

### B3. Admin clients to the admin app

- **Credentials.** Session cookie `__Host-paduraksa-session` (`Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`;
  *code*: `AdminSessionCookie.cs`) or `Authorization: Bearer pdk_...`. Both are looked up by SHA-256 on **every** request, so
  revocation is immediate. A credential that is presented and wrong is a 401, never anonymous; a cookie **and** a token
  together is a 401 ([identity.md, Admin auth](identity.md#admin-auth)).
- **CSRF** for unsafe methods on cookie requests: `Sec-Fetch-Site`, `Origin` against the request's own origin, JSON content
  type ([identity.md, Admin auth](identity.md#admin-auth)). Bearer requests are exempt. Pinned by `tests/Paduraksa.Tests.Integration/Admin/CsrfTests.cs` and
  `ApiCsrfTests.cs`.
- **Setup.** Until an Owner exists the admin app serves only the setup endpoint, behind a 256-bit setup token in
  `<data>/setup-token` (0600, also logged at Warning in a boxed banner; the one decided exception to "no secret in a log",
  [PLAN section 2](../PLAN.md#2-development-rules-and-how-they-are-enforced)). Compared in constant time; refused through the proxy (`setup.viaProxy`). The state is read per request
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
  HSTS except for loopback names, 64 KiB body limit, strict JSON binding ([identity.md, Admin auth](identity.md#admin-auth)). The dashboard's CSP allows
  `style-src 'unsafe-inline'` for MudBlazor and nothing inline for scripts (`src/Paduraksa.Dashboard/DashboardSecurity.cs`,
  *code*). Plain HTTP on 9443 gets a single 308 or 400 and nothing else is ever served ([identity.md, Admin auth](identity.md#admin-auth) "Wrong scheme").
- **Optional source allowlist** `Listen:AdminAllowedSources`, enforced per connection before TLS; loopback is not implied
  once it is set. Empty by default.

### B4. The vault boundary inside the process

- **Compile-time confinement.** `BannedSymbols.KeyMaterial.txt` bans PKCS#8 export and import, PEM key import, `AesGcm` and
  `HMACSHA256/512` in every project except `Paduraksa.Vault`, wired through `Directory.Build.props` (a project opts out
  only with `PaduraksaKeyMaterialBoundary=true`). `KeyMaterialBanTests` checks the list's coverage and runs the analyzer.
  `BannedSymbols.txt` bans `Guid.NewGuid` and, for the proxy, `Rfc2898DeriveBytes`. Architecture tests keep the vault's
  types `internal`, hold `VaultContractTests` (deny-by-default on what a capability interface may return) and
  `BackupBoundaryTests` (Argon2 and the backup key stay in the vault). *(code and PLAN)*
- **Capabilities, not bytes** ([vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface)): serving and upstream-mTLS contexts, CSR creation, JWS signing, MAC handles, a secret
  store with no `Get`, an authenticating HTTP handler that injects the DNS credential inside the vault, vault-side TOTP, a
  password credential that can be verified against and never read. **Four sanctioned egresses**: the serving context (Kestrel
  needs the key), the upstream client-cert context, the Data Protection element XML, and `INotificationSecretSource.UseAsync`
  (lends a value to a callback in a pinned buffer that is zeroed afterwards). The test lists each allowed position.
- **At rest** ([vault-and-certificates.md, At-rest encryption](vault-and-certificates.md#at-rest-encryption)): per-secret AES-256-GCM DEK wrapped by the KEK, with AAD binding the ID, purpose and version, so a
  swapped or edited row fails to open; `secure_delete`; a KEK check value. It is **not** protection against an attacker who
  can run code in the process or read process memory ([non-goal 1](../threat-model.md#what-it-does-not-defend-against)).
- **Important limit:** the boundary is a *code-structure and review* boundary, not a process boundary. A memory-safety
  or deserialisation bug anywhere in the process can read the vault's in-memory keys. [vault-and-certificates.md, At-rest encryption](vault-and-certificates.md#at-rest-encryption) names moving the vault to a
  separate process as the future seam.

### B5. The MCP child process

- **Off by default** (`Paduraksa:Mcp:Enabled=false`, a server setting): `/mcp` answers 404 before authentication and no child starts; switching it off
  stops the pool ([admin-surfaces.md, MCP in code mode](admin-surfaces.md#mcp-in-code-mode)). Since `server-settings`, the Owner or an Admin can switch it
  on from the dashboard. Decided by the owner: someone holding a stolen Owner session can do worse than that anyway. An environment value locks it either way.
- **Transport:** stateless, bearer token only; a cookie, alone or with a token, is a 401 ([admin-surfaces.md, MCP in code mode](admin-surfaces.md#mcp-in-code-mode)). Channel `mcp` in the audit
  log, the token as the actor.
- **The child** is `paduraksa mcp-sandbox`, started with its environment cleared except `DOTNET_*` runtime paths, `PATH`
  and `LD_LIBRARY_PATH` (*code*: `src/Paduraksa.Mcp/ChildProcess.cs`), so the master key and setup token are not in it. It
  holds no token, configuration or data directory. It talks length-prefixed JSON over stdin/stdout; every frame is bound to
  a per-run random ID.
- **The parent is the guard.** It enforces these from outside the child:
  - wall time 10 s, which ends a run that blocks without computing (the CPU limit never does; `Atomics.wait` did until the engine was told to refuse it);
  - CPU 5 s, for a run that computes inside one native call, which `MaxStatements` never counts (regex backtracking is also cut by a 1 s regex timeout);
  - resident memory 384 MiB, 50 requests and a 64 KiB result;
  - a watchdog that kills the process tree, a pool of at most 4 children, and replacement after any breach ([admin-surfaces.md, MCP in code mode](admin-surfaces.md#mcp-in-code-mode)).
- **The child also confines itself** (*code*: `src/Paduraksa.Mcp/ProcessHardening.cs`): `RLIMIT_CPU`, `RLIMIT_NOFILE`, `RLIMIT_CORE`, `PR_SET_NO_NEW_PRIVS`, `PR_SET_PDEATHSIG`, **Landlock for the file system**, and cgroup v2 limits where delegated. The file system mask follows Landlock's ABI, because a ruleset allows what it does not name.
- **Network, and the routes back to the parent.** Landlock network rules (ABI 4, Linux 6.7) deny every TCP bind and connect. A seccomp filter (`src/Paduraksa.Mcp/SyscallFilter.cs`) refuses:
  - `socket` and `socketpair` of any domain but `AF_UNIX`, which covers UDP, raw sockets and kernels older than 6.7;
  - `io_uring_setup`, `_enter` and `_register`;
  - the ways from the child to its parent, which is the same user in the same container with the vault, the tokens and the network: `ptrace`, `process_vm_readv` and `_writev`, `pidfd_open`, `pidfd_getfd`, `kcmp`, `bpf`, `userfaultfd` and `perf_event_open`, with `EPERM` (G2, fixed);
  - signals to any process but the child's own pid, and the `F_SETOWN`/`FIOSETOWN` commands that would have the kernel signal the parent on I/O. Where the kernel has it (Linux 6.12, with `TSYNC`), Landlock's signal scope refuses every signal out of the sandbox as well.

  The list is one table with a reason per row, shown in [admin-surfaces.md](admin-surfaces.md#mcp-in-code-mode), and a test pins every number per architecture. The rest of the syscall surface is the runtime's: there is no profile of the runtime's calls.
- **The request policy never leaves the parent** (`RequestPolicy`, `EndpointRules`, `RequestBridge`):
  - only paths of the generated OpenAPI document under `/api/v1/`, no encoding tricks, five methods;
  - an explicit **refused list**: backups, restore, token and user management, sessions, sign-in and setup, anything that carries a secret into a model, the live tail, raw metrics and the API description;
  - two-step writes (a dry run, or 428, unless `confirm: true`);
  - the API's own permission filters still run.

  What the policy treats as a read (a GET, `ReadOnlyPosts`) must write nothing in any state. Until 2026-11-14 a read, review or simulation of an expired draft deleted it. They now answer 410 `draft.expired`, and only the expiry job, a discard or a new draft removes one.

  A guard test fails when a sensitive-looking operation is on neither the refused nor the reviewed list (`McpRequestPolicyApiTests`). A fuzz target (`mcp-sandbox`) feeds the parent's protocol arbitrary bytes as the child's output.

### B6. Outbound calls (SSRF posture)

| Call | Who chooses the destination | Controls as built |
|---|---|---|
| ACME CA | Operator (account `directoryUrl`) | https only for the directory (`AcmeAccountService.cs`); every URL the CA returns must be absolute https (http only for loopback and only if an option allows it) (`AcmeTransport.cs`); no redirects, response cap 1 MiB (`AcmeClientOptions.cs`). Any host is accepted, private addresses included. |
| DNS provider | Provider code fixes the API host; the admin sets `AllowedHosts` with the secret | The vault's handler injects the credential, https only, port 443 unless named, allowlisted host only, no redirects, refuses a `Host` that differs from the URL (domain-fronting guard) ([vault-and-certificates.md, The public surface](vault-and-certificates.md#the-public-surface)). |
| Cloudflare ranges | Fixed built-in URLs | https, 15 s timeout, size cap, list sanity checks, last good list kept on failure (*code*). |
| Forward-auth endpoint | Operator (`AuthProvider.forwardAuth.url`, a **config** object) | No redirects, cookies or env proxy, 64 connections, platform TLS validation (no accept-any), 1-30 s timeout, at most 64 concurrent calls, relays only status, bounded body and a few headers ([waf.md, The blocks at a glance](waf.md#the-blocks-at-a-glance) forward-auth). Nothing a client sends chooses the endpoint. Any address is allowed. |
| Notification channels | Operator (`providers:write`) | Redirects refused, responses never echoed, fixed error lines, SMTP login refused without TLS. **SSRF decided and accepted**: any address, loopback and the admin port included ([waf.md, The blocks at a glance](waf.md#the-blocks-at-a-glance) M9 notifications). |
| Upstreams | Operator (`config:write`) | No restriction on address (userinfo, query and fragment refused). This is the proxy's job. |
| Upstream reachability probe (`UpstreamProbe`) | Operator (`config:write`) | TCP connect only, nothing sent or read ([dashboard.md, First-run setup](dashboard.md#first-run-setup)). |

### B7. The CLI and the filesystem

- **The CLI is the same binary.** `admin reset-password`, `admin disable-2fa`, `setup-token`, `vault rotate-kek`, `backup`,
  `restore`, `config export|import` are local administrative actions: they run as the invoking user with access to `/data`
  and the master key, and are audited as the `cli` actor, which "may do everything" ([identity.md, Admin auth](identity.md#admin-auth)). Whoever can run them already
  has the data directory. Passphrases and passwords come from a file, stdin, the environment or a no-echo prompt, **never
  argv** (a test parses the command line and finds no such option, [vault-and-certificates.md, Backup and restore](vault-and-certificates.md#backup-and-restore)).
- **`/data` permissions** (*code*): `master.key` created exclusively with mode 0600 and refused if others can read it
  (`KekProvider.cs`); `vault.db` 0600 (`VaultDatabase.cs`); `last-good.json` 0600 (`LastKnownGoodFile.cs`); `setup-token`
  0600; `run/` 0700 with `admin.sock` 0600; `logs/` 0700 with 0600 files (`RotatingLogFile.cs`); `paduraksa.db` and its WAL and
  shared-memory files 0600 (`PrivateSqliteFile.cs`, G7: an older install's wider modes are restricted at startup). The data
  directory itself keeps the mode its creator gave it (the image's `install -d -m 0750`, or 0700).
- **One server per data directory:** `run/server.lock`, an exclusive OS file lock (`DataDirectoryLock`), so a second server or
  a restore cannot run alongside ([vault-and-certificates.md, Backup and restore](vault-and-certificates.md#backup-and-restore)). Not enforced on file systems that ignore `flock` (a warning is logged).

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
- *Built (connection stage, G1 and G8):* a per-IP connection cap (built-in 256 per address, an IPv6 /64 counted as one, trusted
  proxies never counted) and a connection deny list (a named access list's deny addresses) refuse a connection **before TLS**
  (`ConnectionRuntime`, `ConnectionLimitMiddleware`, `QuicConnectionLimits`; [hot-path.md, Request pipeline](hot-path.md#request-pipeline)), with a fixed lock-free table whose
  overflow fails open and is counted; explicit Kestrel limits against slow clients: a 15 s headers timeout, minimum data rates
  with a grace, a TLS handshake timeout, a global connection cap and a WebSocket cap. Refusals are counted (`/metrics`, the
  Security overview's Connections card) and logged at Information at most every 10 s. `ConnectionTableTests`,
  `ConnectionRuntimeTests`, `ConnectionLimitMiddlewareTests`, `PublicConnectionLimitsTests`, `ServerLimitsKestrelTests`.
- *Residual:*
  - A flood **from many addresses** is bounded only by the global cap (10 000 connections by default) and the table's size. Past 65 536 entries a new address isn't counted (it fails open, `paduraksa_connections_events_total{event="table_full"}`). An attacker with many IPv6 /64s or botnet addresses is not slowed by a per-address cap.
  - A flood that reaches the global cap makes Kestrel stop accepting for everyone. The per-address cap is what keeps one host from doing that alone.
  - **Behind Cloudflare or another trusted proxy that doesn't speak PROXY protocol, the connection stage sees only the proxy**, so it caps and denies nothing per visitor. A visitor is judged at the gate, by rate limits and access lists, which are **opt-in per host** (a new host has none, G10). Use origin lockdown.
  - Over **QUIC** a burst of simultaneous handshakes from one address can pass the cap by the size of the burst, because a place is only taken once the connection is established.
  - There is **no limit on the rate of new connections**, and the **admin listener keeps Kestrel's own limits** (it has `AdminAllowedSources`).
  - Volumetric DDoS is a non-goal ([non-goal 4](../threat-model.md#what-it-does-not-defend-against)).

**Compression side channels ([BREACH](https://www.breachattack.com/)).** Response compression is off by default at every layer and opt-in per host.

**Custom headers and path rewrites.**
- *Built:* a header rule can't name a framing, hop-by-hop, forwarding, request-ID or Cloudflare header, nor (a compile error) an
  identity header an auth provider of the host vouches for, so configuration can't forge what the upstream trusts; values are
  bounded visible ASCII, so a value can't start another header line (`CustomHeadersValidationTests`, the compiler's backstop).
  A rewritten route (strip, add or both) answers 404 to a path it can't rewrite without a guess: a spelling of the prefix that
  isn't the route's own, or a dot segment in any spelling (`%2e%2e`, `.%2E`, `..;`, literal `..`). Kestrel resolves those before
  matching, but the raw path is forwarded, so `/app/%2e%2e/app/x` would otherwise reach the upstream as `/api/%2e%2e/app/x` and
  resolve outside the prefix the route forwards under (found in review; `PathRewriteTests`, `PathRewriteEndToEndTests`).
- *Residual:* a rule can still set anything else, `Authorization` and `Cookie` to the upstream included: that is the point of
  the feature, and an admin who can edit host settings can already point the host anywhere.

**Header and body abuse, request smuggling, path tricks.**
- *Built:* Kestrel and YARP do HTTP framing; the proxy never forwards `Forwarded` or a client's `X-Forwarded-*`; the gate and
  the simulator share one canonical **matching path** (`RoutePath`: backslash, `%2F`, `%5C`, dot segments, case rules) so
  `/admin` rules cannot be bypassed with `/%61dmin`, `/./admin` or `\admin`; pinned by `RoutePathPropertyTests`, including 150
  raw targets sent to a real Kestrel (it found a decoding bug in the simulator, [testing.md, Test levels](testing.md#test-levels)); route paths that are encoded or
  non-canonical are refused at config time (`route.path.encoded`, `route.path.notCanonical`).
- *Residual:* anything Kestrel or YARP get wrong is inherited (supply chain, 4.9). No property test covers the rate-limit table
  or the PoW page ([testing.md, Test levels](testing.md#test-levels) gaps).

**TLS and SNI tricks.**
- *Built:* connection coalescing re-check (421); unknown SNI refused in release builds; per-host minimum TLS version and ALPN;
  OpenSSL's default cipher policy (no custom policy, by measurement, [hot-path.md, B. TLS handshake](hot-path.md#b-tls-handshake)); offline client-cert chains; HSTS never sent for IP
  literals or loopback names.
- *Residual:* TLS 1.2 stays on by default (a host sets `MinimumVersion = Tls13` to refuse it). The placeholder self-signed
  certificate is on by default only in Debug builds and warned about in release ([hosting.md, Host mechanics](hosting.md#host-mechanics)); a release build with it on is operator error.

**Spoofed client IPs.**
- *Built:* see B1. An untrusted peer's `cf-*`, `True-Client-IP`, `X-Forwarded-*` are ignored and stripped; the resolved address
  is a single overwritten value upstream; the request ID from a client is honoured only from a trusted peer. Pinned by
  `ForwardedHeadersTests`, `OriginLockdownTests` and the `client-address` fuzz target (an untrusted peer is never moved by a
  header and gets no country).
- *Residual:* a **misconfigured trusted-proxy list** (for example trusting a CIDR the attacker is in, or `Proxy` mode
  without lockdown on a host reachable directly) lets the attacker choose their identity for every address-based policy.
  The dashboard and simulator ("via Cloudflare" toggle) exist to make this visible; they do not forbid it. Country rules
  use `CF-IPCountry` only and a request not behind Cloudflare has an **unknown** country ([waf.md, The blocks at a glance](waf.md#the-blocks-at-a-glance) country rules, "Gaps").

**Bots, crawlers, scanners.**
- *Built:* managed `robots.txt` (a request, not a control), user-agent rules, PoW, analytics that classify user agents and count
  bots that ignore `robots.txt` ([observability.md, Overview](observability.md#overview) analytics). None of these stop a determined client; the user-agent is attacker-controlled.

**Attacks on the PoW and rate limits themselves.**
- *Built:* the PoW challenge is HMAC-bound to host, masked client address and time; the cookie is HMAC-signed, `HttpOnly`,
  `SameSite=Lax`, `Secure` on the trusted scheme; verification allocates 0 B and uses the vault's `IMacKey`; if the key cannot
  be opened every protected route answers 503 (fail closed, [hot-path.md, Request pipeline](hot-path.md#request-pipeline) PoW). Fuzzed (`pow-cookie`, `pow-form`). The return target can
  never leave the site (fuzz property).
- *Residual:* the challenge is **stateless, so a solved one can be redeemed again** for ten minutes (a cookie for the same host
  and address only; accepted in [hot-path.md, Request pipeline](hot-path.md#request-pipeline)). A botnet can solve at difficulty 16. Rate-limit keys are the resolved client address, so
  attackers with many addresses, or users behind one NAT, are not separated; abuse of the 10 000-key bounded memory falls into a
  shared overflow bucket (admin throttle, [identity.md, Admin auth](identity.md#admin-auth)).

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
permission metadata and a test checks every mapped `/api/*` endpoint declares one (`ApiConventionTests`, [admin-surfaces.md, The REST API (`/api/v1`, admin port)](admin-surfaces.md#the-rest-api-apiv1-admin-port)).

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
    will call, widen trusted proxies, disable every protection on a host, and edit access-list passwords. [identity.md, Admin auth](identity.md#admin-auth) calls
    Operator "hosts/certs, no users/secrets"; in effect the Operator is a **network pivot inside the host's reach**. [identity.md, Admin auth](identity.md#admin-auth) and
    the dashboard now say so (G5). Assume Operator and Admin are fully trusted with respect to the node's network position.
  - A Viewer or Auditor can read topology (hosts, upstream addresses, access-list usernames, client IPs for the Auditor).
  - Per-user throttle and lockout are per process and reset on restart.
  - There is **no mandatory second factor**; TOTP is optional for everyone including the Owner. The setup checklist asks for it, and Users shows
    who has none (G4).

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
| read-only (`config:read`, `logs:read`, `metrics:read`, `certificates:read`) | Full topology and traffic metadata; access-log contents (client IPs, paths, user agents) with `logs:read`. No changes, no secrets. Each use writes the token's `LastUsedAt`, at most once a minute, so the token list can show it. |
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
  style-only CSP ([waf.md, The blocks at a glance](waf.md#the-blocks-at-a-glance) M8). Hop-by-hop framing is YARP's. Upstream TLS: `System` by default.
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
  - An engine escape lands in a process with no secrets, no files, no TCP or UDP socket, no way to ptrace, read the memory of, signal or take the
    descriptors of its parent, and a few seconds to live. The syscall surface is otherwise the runtime's, and a kernel exploit is out of scope ([non-goal 7](../threat-model.md#what-it-does-not-defend-against)). What the child saw in that run can leave only over the
    pipes it was given, which the parent reads under limits and which end in the model's answer.
  - Hardening is Linux only; elsewhere the child has the parent's limits and kill but no system-call hardening (it says so in its
    `ready` frame). On a kernel older than 6.7 there is no Landlock network rule and the seccomp filter alone closes the sockets; on a
    runtime whose seccomp profile blocks the `landlock_*` calls there is no Landlock at all (G14: the default profile of Docker 29 does
    not, checked on the real image). The server logs what applied and what did not ([admin-surfaces.md, MCP in code mode](admin-surfaces.md#mcp-in-code-mode), [MCP](../automation/mcp.md)).

### 4.7 Someone with read access to `/data` or a backup file

| What they obtain | What it gives | Mitigation |
|---|---|---|
| `vault.db` alone | Sealed rows. Useless without the KEK. | AES-256-GCM envelope, AAD-bound rows. |
| `vault.db` + `master.key` in the same directory (an install without the key file) | Every secret: certificate keys, DNS tokens, TOTP seeds, password verifiers, the local CA root. | The shipped compose file uses a Docker secret, so the key is not in the volume; where it is, the overview warns the Owner and startup logs a Warning (`VaultRuntime.cs`) (G3). |
| `paduraksa.db` | Users, PBKDF2 hashes of admin passwords, SHA-256 of session and API-token secrets (high-entropy, so not practically reversible), recovery-code hashes, TOTP references (not seeds), the config, the revision history, the audit log with client IPs, analytics. Admin password hashes are offline-crackable (PBKDF2-210000). | Mode 0600 with its WAL files (G7), inside the data directory's own mode. Backup is encrypted. |
| `last-good.json` | The whole live config (no secrets). | 0600. |
| `logs/` | Client IPs, paths (queries with sensitive keys redacted), user agents, referers. | 0700/0600; retention 30 files, 30 days, 1 GiB by default (`PaduraksaOptions.cs`). Paths are logged as sent, so a secret in a path is in the log ([observability.md, Overview](observability.md#overview) analytics). |
| a `*.pdkbak` file | Nothing without the passphrase: Argon2id (64 MiB, 3 passes, 4 lanes) then a chunked AES-256-GCM stream, header authenticated, truncation and reordering detected; the source master key is **not** in the archive (keys re-wrapped under a backup-only key) ([vault-and-certificates.md, Backup and restore](vault-and-certificates.md#backup-and-restore)). | The passphrase's strength (12-1024 characters, no composition rules). Pinned by `Paduraksa.Tests.Vault/Backup` (RFC 9106 vector, tamper and truncation cases). |
| the **old** `vault.db` plus the old KEK after `rotate-kek` | `rotate-kek` re-wraps DEKs only; the data is not re-encrypted ([vault-and-certificates.md, At-rest encryption](vault-and-certificates.md#at-rest-encryption)). | To cut off an old copy, replace the secrets themselves. |
| the setup token (before setup) | Become the Owner. | 0600 file, deleted once an Owner exists; also in `docker logs`. |

- *Residual:* anyone who can read `/data` **and** the master key owns the vault; nothing in the design defends that ([non-goal 1](../threat-model.md#what-it-does-not-defend-against)).
  `docker logs` readers see the setup token until setup is done.

### 4.8 Someone with a shell as the container user (uid 10001)

They can read the data directory, the master key wherever the process can read it, `/proc/<pid>/environ` (which holds
`PADURAKSA_MASTER_KEY` if that source is used) and, where `ptrace` is not blocked, process memory; they can call the CLI (which acts as
`cli`, "may do everything", [identity.md, Admin auth](identity.md#admin-auth): `admin reset-password` for any user, `admin disable-2fa`), and connect to
`run/admin.sock` (the hop secret is in process memory and the request must also carry it, so the socket alone is not enough).
**This is a non-goal** ([non-goal 1](../threat-model.md#what-it-does-not-defend-against)): the container user is the process. Mitigations that still reduce blast radius: read-only root
file system, dropped capabilities, `no-new-privileges`, the master key as a file readable only by uid 10001 and not stored in
an environment variable. The audit log records CLI actions (not the identity of the shell user).

### 4.9 Supply-chain risk in dependencies

- *Built:* central package versions (`Directory.Packages.props`); Dependabot weekly for NuGet, GitHub Actions and Docker base
  images (`.github/dependabot.yml`); the Dockerfile pins both base images **by digest**; libmsquic is pinned and its feed key
  fingerprint is checked in the build; the published image carries an SBOM (`ci.yml`, `sbom: true`); no native crypto
  libraries beyond the runtime's (Argon2 is a managed library on purpose, [vault-and-certificates.md, Backup and restore](vault-and-certificates.md#backup-and-restore)); the vault holds the only references to
  `Konscious.Security.Cryptography.Argon2` (`BackupBoundaryTests`); the proxy project may not reference Data, Storage or EF.
  Notable third-party code on sensitive paths: YARP, Kestrel (framework), Jint (MCP child only), MailKit, SQLite
  (bundled), MudBlazor and Blazor (dashboard), CsCheck/SharpFuzz (test only), ArchUnitNET (test only).
- *Residual:* CodeQL and an explicit `NuGetAudit` now exist (G9; high and critical advisories fail the build, a moderate one warns);
  there is no signed-image or provenance verification step documented
  for operators; floating `:main` tag in `deploy/compose.yaml` (the compose comment says a `:0.1` tag replaces it once there is
  a release). Jint runs stock in a child; a vulnerability in it is contained by the sandbox, not prevented.

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
| Connection stage (per-IP cap, deny list), slow-client limits | `tests/Paduraksa.Tests.Proxy/Net/ConnectionTableTests.cs`, `ConnectionRuntimeTests.cs`, `ConnectionLimitMiddlewareTests.cs`, `ServerLimitsKestrelTests.cs`, `tests/Paduraksa.Tests.Integration/Proxy/PublicConnectionLimitsTests.cs` |
| Log injection and redaction | `tests/Paduraksa.Tests.Proxy/Logging/AccessLogFormatterTests.cs`, fuzz `access-log-tail` |
| MCP boundary | `tests/Paduraksa.Tests.Integration/Mcp/*`, `Admin/Mcp*Tests.cs`, `tests/Paduraksa.Tests.Unit/Mcp/*`, fuzz `mcp-sandbox` |
| Backup and restore | `tests/Paduraksa.Tests.Vault/Backup`, `Admin/BackupApiTests.cs`, integration restore tests |
| Audit chain | `tests/Paduraksa.Tests.Data/Config/AuditChainVerifierTests.cs`, `AuditLogTests.cs` |
| One server per data directory | `tests/Paduraksa.Tests.Unit/Admin/DataDirectoryLockTests.cs` |
| Fuzz targets (`fuzz/Paduraksa.Fuzz.Targets`) | `proxy-protocol`, `pow-cookie`, `pow-form`, `basic-auth`, `client-address`, `forward-auth-answer`, `error-page`, `upstream-error-replacement`, `access-log-tail`, `yaml-import`, `mcp-sandbox`; every committed corpus and regression input is replayed on each commit by `tests/Paduraksa.Tests.Fuzz`; `scripts/fuzz.sh` and `.github/workflows/fuzz.yml` run them ([testing.md, Security](testing.md#security)). Property tests use CsCheck. |

Not fuzzed ([testing.md, Security](testing.md#security) "Left out"): the dashboard's parsing, the live tail's SSE framing, the notification senders' payloads, the ACME
response parser, multipart upload parsing (listed in [testing.md, Security](testing.md#security) "Security" as a target). The release-length run
(`scripts/fuzz.sh --time 3600 --merge`) was done on the existing targets before v0.1: two hours each, about 1.6 billion inputs, no crash.

---

## 8. Gaps found

Ordered by severity. "Gap" means a mismatch with PLAN, a missing mitigation that a reader would assume exists, or a default that weakens a documented property. These are the gaps still open at the v0.1 release review (2026-11-15).

The gaps fixed before it (G1 to G5, G7 to G9, G13 and G14) are no longer listed, and their IDs are not reused. Other pages and code comments that cite one of them (for example "threat model G4") mean the fix in git history and [CHANGELOG.md](../../CHANGELOG.md).

| # | Severity | Gap | Evidence | Suggested fix |
|---|---|---|---|---|
| G6 | **Low** | **The audit chain is tamper-evident only against partial edits.** The hash is an unkeyed SHA-256 chain in the same database; anyone who can write `paduraksa.db` can rewrite the whole chain and the verifier will pass. There is no external anchor. | `src/Paduraksa.Application/Audit/AuditChain.cs`; `AuditQueryService.VerifyAsync` returns the head hash | Document honestly (done here); suggest an HMAC with a vault MAC key and/or sending the head hash periodically to a notification channel, or an export the operator stores off-box. |
| G10 | **Low** | **A new host is unprotected until the operator opts in.** [dashboard.md, First-run setup](dashboard.md#first-run-setup) planned a security preset (Off / Balanced / Strict) in the wizard; the wizard and the presets were not built. Built-in defaults (`HostSettingsLayers.cs`): force-HTTPS off, HSTS off, PoW off, no rate-limit policy, access list or rule set until one is named, TLS 1.2 minimum, 100 MiB body limit. | [dashboard.md, First-run setup](dashboard.md#first-run-setup) "Owner decision: no wizard"; `src/Paduraksa.Application/Compilation/HostSettingsLayers.cs:28-60` | A "Balanced" preset in the global defaults (sane rate limit, the built-in rule-set presets, request limits) and a checklist item. At least say so in [Access control and WAF](../guide/security.md). |
| G11 | **Low** | **The local CA root is unlimited by default.** A leaked vault means a CA that devices which trusted the root accept for any name, for ten years. A root can be rotated (the old key is destroyed in the same step) and a new root can be limited to chosen domains and networks, which a client then enforces; the owner chose unlimited as the default so a first-run root never refuses a name the operator uses. | [vault-and-certificates.md, Local CA name constraints and root rotation](vault-and-certificates.md#local-ca-name-constraints-and-root-rotation) "As built: local CA name constraints and root rotation" | Closed in part: the controls exist and are opt-in. Re-evaluate the default if the trust-the-root card is seen to be installed on devices outside the operator's control. |
| G12 | **Low** (open) | **Fuzz and property coverage gaps already admitted in [testing.md, Security](testing.md#security) remain**: the rate-limit table and the PoW page have no property test, and the ACME response parser, multipart parsing and the dashboard have no fuzz target. The release-length run on the existing sixteen targets is done (two hours each, no crash). | [testing.md, Gaps](testing.md#gaps) and [Fuzzing](testing.md#fuzzing) "Left out" | Add the targets. |

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
- **Fleet or control-plane work** ([deployment.md, Fleet mode: control plane and data plane (after v0.1)](deployment.md#fleet-mode-control-plane-and-data-plane-after-v01)): this model covers one node; a second node needs its own trust-boundary section.
- **A fixed gap**: delete it from section 8 in the same pull request. What was done lives in git history and `CHANGELOG.md`.
- Before every **release** (v0.x tag): re-read section 8 and the "agree" list, re-run the fuzz harnesses per [testing.md, Security](testing.md#security), and confirm the evidence levels still hold. At least once per quarter otherwise.

**Last release review: 2026-11-15, before v0.1.** The fixed gaps were pruned from section 8 then. G12 stays open for the missing targets.