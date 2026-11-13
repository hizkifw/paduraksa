# Identity, authentication and authorization

Two separate domains: who may use the admin surfaces (dashboard, API, MCP, CLI), and who may reach a proxied host. This page covers the identity tables and their rules, the admin sign-in pipeline, roles and permissions, and the notes on proxied-host auth. The forward-auth and access-list mechanics are in [waf.md](waf.md), and the admin certificate and the internal listener are in [hosting.md](hosting.md).

Contents:
- [Identity entities](#identity-entities)
- [Identity core (M4)](#identity-core-m4)
- [Admin auth](#admin-auth)
- [The admin pipeline (M4)](#the-admin-pipeline-m4)
- [Proxied-host auth](#proxied-host-auth)

## Identity entities

| Entity | Notes |
|---|---|
| `User` (`usr_`) | Username, display name, password hash (ASP.NET `PasswordHasher` v3 PBKDF2-SHA512 or Argon2id), TOTP secret **ref** (vault), lockout, disabled |
| `Passkey` (`pk_`) | WebAuthn credentials (public keys only, so these live in the config DB). Built on .NET 10 passkey primitives. |
| `RoleAssignment` | User to role (Owner, Admin, Operator, Viewer, Auditor), scope = global (per-tag scopes later) |
| `ApiToken` (`tok_`) | Owner, scopes (`hosts:read`, `hosts:write`, `certs:*`, `mcp`, ...), expiry, last used, hash |
| `Session` (`ses_`) | Hash, user, IP, UA, created and last seen, revocable from the UI |
| `ExternalLogin` | OIDC subject to user (later) |
| `ConfigRevision` (`rev_`) | `Seq` (display number "#42"), parent rev, author, channel, message, `Document` (canonical config, Brotli-compressed), SHA-256, summary of changed objects |
| `AuditEvent` (`aud_`, Int64Ordered) | Timestamp, actor (user, token or system), channel (dashboard, api, mcp, cli, system), action, target typed-ID string, outcome, IP, details JSON, **hash chain** (`prevHash`, `hash`) for tamper evidence |

## Identity core (M4)

**Tables.** `Users`, `RoleAssignments`, `ApiTokens`, `Sessions` and `RecoveryCodes` (config DB, migration `Identity`). Passkeys are deferred to M5.

### Usernames and passwords

- Usernames are unique and case-insensitive (`NOCASE`, ASCII only): 3 to 64 characters of letters, digits and `. _ - @ +`.
- The password hash is `$pbkdf2-sha512$v1$i=<iterations>$<salt>$<hash>`: PBKDF2-HMAC-SHA-512, **210 000 iterations by default**, a 16-byte salt, a 64-byte hash, constant-time compare. A new algorithm or layout would be a new version tag.
- It is rehashed at sign-in when its iteration count is lower than the setting.
- An unknown username costs a derivation against a throwaway hash.
- The policy is a minimum length (12, counted in characters) with no composition rules.

### Roles and permissions

Roles are permission sets (`RolePermissions`):

| Role | Gets |
|---|---|
| Owner | Everything. |
| Admin | Config, certificates, providers, audit, simulate, user list. |
| Operator | Config, certificates, provider reads, simulate. |
| Viewer | Reads and simulate. |
| Auditor | Reads and audit. |

Only Owner holds `users:write`, `vault:manage`, `tokens:all` and `sessions:all`.

A token's scopes are permission names (`config:write`). Its effective permissions are the intersection with its owner's current ones, evaluated on every use. A token cannot issue tokens.

### Sessions

- The cookie is 32 random bytes in base64url. Only the SHA-256 is stored.
- Sessions have absolute and idle expiry. The last-seen write is throttled to once per `TouchInterval`.
- A password-only session is *partial* (short-lived) until the second factor completes it, and the cookie value changes when it does.

### Lockout

After `MaxFailedAttempts` consecutive failures (password, second factor, or a wrong current password at a sensitive step), the account refuses sign-in for `LockoutDuration`. Every refusal answers `InvalidCredentials`. Failures are audited under the system actor with the account as target.

**Counting is race-proof.** The attempt is counted *before* it is evaluated, in one write transaction (`BEGIN IMMEDIATE`) that refuses a locked account. It is re-checked (lock, disabled, second factor on) inside the success transaction. Parallel guesses therefore cannot outrun the limit or sign in past a lock. A success clears the count.

**What a lockout ends.** A lockout ends the user's sessions only where the attacker already holds the password: at the second factor, or at a sensitive step of a signed-in session (change password, TOTP enrol or disable, recovery codes), where a wrong current password counts too. A lockout at the password stage ends nothing, so someone who merely knows a username cannot keep signing the owner out.

### Other rules

- Unknown usernames are audited without the typed name (people type passwords there, and the chain is permanent).
- Starting a TOTP enrolment, like disabling it and regenerating recovery codes, needs the current password. Confirming the enrolment does not (the code from the authenticator is the proof).
- The last enabled Owner cannot be demoted, disabled or deleted.
- All identity events are audited in the transaction that makes them: `auth.login`, `auth.second_factor`, `auth.lockout`, `auth.logout`, `auth.session.revoke*`, `token.issue`, `token.revoke`, `user.*` and `setup.complete`.

## Admin auth

**Credentials (dashboard, API, MCP):**
- Local users: password, **TOTP** and **passkeys** (WebAuthn). ASP.NET Core 10's passkey support ships inside **Identity**, which we do not use. M4 included a short check of whether its WebAuthn primitives work standalone; otherwise the plan is **Fido2NetLib**. Budget for the latter.
- Lockout and rate limiting on login. Session list with revoke.

**Roles:**
- **Owner:** users, vault, everything.
- **Admin:** all config.
- **Operator:** all of the config and the certificates. `config:write` lets an Operator route to any internal address, set a forward-auth endpoint the gate calls, change trusted proxies and edit access-list credentials. Operator is therefore effectively a trusted role, not a restricted one. No users, vault, audit log or backups (threat model G5).
- **Viewer:** read-only.
- **Auditor:** audit log plus read-only.

They are implemented as permission sets, so custom roles can be added later.

**API.** Bearer `pdk_...` tokens with scopes and expiry. MCP uses the same tokens: a bearer token and nothing else, on the `mcp` channel (see [admin-surfaces.md](admin-surfaces.md)). Read-only is the safe default and the dashboard offers read scopes first. The MCP authorization spec (OAuth) is considered later.

**Pluggable providers.** `IAdminAuthProvider`: Local (v1), OIDC (authentik, Keycloak, Pocket-ID: claim-to-role mapping, JIT provisioning) and trusted-header (when the admin itself sits behind forward-auth).

**Hard rule for trusted-header auth.**
- It is accepted **only** on connections from the internal proxy listener (see [hosting.md](hosting.md)), from loopback, or from an explicitly configured CIDR.
- It cannot be enabled while the admin port is bound to a non-loopback address without that CIDR. Validation rejects the config, and the request path re-checks the peer address every time.
- Otherwise anyone on the LAN could spoof `X-authentik-username`.
- When forwarding to the admin, the public side strips any client-supplied copies of the trusted headers.

**Hardening.** Strict CSP, antiforgery, `SameSite=Strict` cookies, Data Protection keys in the vault, an optional admin IP allowlist, and every login and failure audited.

### Permission evaluation (M4)

One permission check answers every "may this origin do that": `IPermissionEvaluator` (registered by `AddPaduraksaIdentity`). The config service, the certificate, issuance and provider services, the simulator, the Admin endpoint filters and the dashboard's permission sets all use it.

`OperationPermissions` names what each kind of operation needs:
- `config:write` for every config command;
- `certificates:write` for certificate operations;
- `simulate` (plus `config:read` for a draft or a revision) for simulations.

Services that change things outside the command pipeline ask up front, before any CA order, key or pending row exists. They answer with a distinct refusal that is audited as denied:
- `CertificateService`: `CertificateChangeStatus.Denied`, which `CertificateOperations` maps to `Forbidden`. Its rollbacks run as the system, so a refused or revoked caller cannot leave a pending row.
- `AcmeIssuanceService.RequestCertificateAsync` and `RenewAsync`: `certificates:write`, `IssuanceOutcome.Forbidden`.
- `AcmeAccountService` and `DnsProviderService` mutations: `providers:write`, `IssuanceChangeStatus.Denied`.

Other rules:
- While the config store is unavailable the check is skipped. The permissions live there, and the operation answers `Unavailable` and starts nothing.
- Refused identity *changes* are audited. Refused reads (listing users, sessions, tokens) are not, since reads are not audited.
- The system and the CLI may do everything.
- There is no allow-everything default. Every service takes the evaluator, and tests pass theirs (`TestPermissions`).

**Settings** live under `Paduraksa:Admin`, with validated bounds:

| Setting | Bounds |
|---|---|
| Lockout attempts | 2 to 100 |
| Lockout duration | 1 min to 24 h |
| Session lifetime | 5 min to 30 d (idle at most the lifetime) |
| Minimum password length | 8 to 128 |
| PBKDF2 iterations | 100 000 to 10 000 000 |

**Not built:** passkeys (M5), and the OIDC and trusted-header providers.

## The admin pipeline (M4)

Code: `Paduraksa.Admin` (`AddPaduraksaAdminAuth`, `UsePaduraksaAdminAuth`, `MapPaduraksaIdentityApi`). `AdminApp` only calls them.

**Order:** the loopback listener's host check, security headers, the setup gate, routing, request body limit, CSRF check, authentication, then the endpoints.

Any endpoint can opt in to the mechanisms with public metadata:
- `.AllowAnonymousAdmin()`: no credentials needed; a stale cookie is ignored.
- `.WithAdminBodyLimit(bytes)`.
- `.AcceptsAdminContentTypes(...)`.
- `.RequireAuthenticated()`.
- `.RequirePermission(...)`.

Authentication covers `/api/` and only ever *sets* the `AdminCaller`. A request that already has one is left alone.

### Credentials

Two kinds are accepted:
- The session cookie `__Host-paduraksa-session` (`Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`). The value is the session secret, checked by `SessionService` on every request.
- `Authorization: Bearer pdk_...` (`ApiTokenService`, also checked every request, so a revoked token or session dies at once).

**A credential that is presented and wrong is a 401, never an anonymous request.** That covers:
- a bad token;
- an unknown or expired cookie (which is also cleared);
- a non-Bearer `Authorization`;
- two `Authorization` headers;
- the cookie twice;
- a cookie *and* a token together.

The one allowance: on the endpoints that take no credentials (sign-in, setup), a cookie that does not validate is ignored, so a browser with a stale cookie can sign in. A bad bearer token is refused there too.

A *partial* session (second factor owed) sets no caller. Only `auth/second-factor` and `auth/logout` use it.

The channel is `Api`. The client IP is the request's remote address (IPv4-mapped shown as IPv4). On the internal admin listener that is the forwarded client address the proxy resolved. Every other listener trusts no forwarded header.

### CSRF

For unsafe methods on `/api/`:
- A bearer request is exempt.
- `Sec-Fetch-Site`, if sent, must be `same-origin`.
- Otherwise `Origin` must equal the request's own `scheme://host:port`.
- With neither header (curl), a request is allowed unless it carries the session cookie.
- A cookie request with a body must be `application/json` (`+json`), or a type its endpoint lists with `AcceptsAdminContentTypes`.
- Refusals are a 403 problem with `code: csrf.refused`.

### Setup gate

While `SetupService.IsCompleteAsync` is false, only these are served: `/healthz`, `/readyz`, `/api/v1/setup`, and `AdminPipelineHooks.SetupAllowedPathPrefixes` (`/openapi`, `/api/v1/openapi.json`, `/api/docs`; the API branch adds more there). Everything else is a 503 problem (`code: setup.required`, pointing at `/api/v1/setup`).

- The state is read per request, so finishing setup opens the app at once.
- Paths are matched as written: `/api/v1/setup/` with a trailing slash is not the setup endpoint and stays gated.
- If the state cannot be read (config store down), the gate stays shut (`setup.unavailable`).
- `POST /api/v1/setup` takes the setup token, username and password, creates the Owner, signs them in (cookie set, 201), and audits both outcomes. A wrong token is 403, a finished setup 409, a weak password 422.

### Rate limiting

`System.Threading.RateLimiting` fixed windows apply on login, second factor and setup, independent of the lockout. The library under ASP.NET Core's rate limiter is used directly, because the per-username partition needs the parsed body.

- **Per client:** every attempt, successful or not, takes a permit. An IPv6 client is keyed by its /64.
- **Per username** (the second factor: per user): only *failed* attempts count, so the owner's own sign-ins never use the budget.
- A refusal is 429 with `Retry-After` (the time left in the window) and `code: auth.rateLimited`.
- Defaults are 20 per client and 10 failures per user per minute, set with `Paduraksa:Admin:RateLimit:{PermitsPerIp (1-10000), PermitsPerUser (1-1000), Window (1 s-10 min)}`.
- **Memory is bounded:** at most 10 000 tracked clients and as many usernames. Past that, new keys share one overflow bucket. Expired windows are swept (at most once a second).
- **Password derivation is bounded too.** Login, setup, the second factor and every re-authentication (password change, TOTP enrolment and removal, recovery codes) run in one of `Environment.ProcessorCount` slots. An attempt that gets none within 2 s is a 503 (`code: auth.busy`, `Retry-After: 1`) and derives nothing.
- Windows and slots are in memory.
- Per-client limits key on the request's remote address. On the internal admin listener they key on the forwarded client address (see the warning in [hosting.md](hosting.md) about proxying to the admin port).

### Hardening

Every admin response carries:
- `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`;
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, unless the response set its own (or its path is in `AdminPipelineHooks.CspExemptPathPrefixes`);
- `Cache-Control: no-store` on `/api/`;
- HSTS (`max-age=31536000`) on HTTPS requests, except for a loopback host name (`localhost`, `127.0.0.1`, `[::1]`), so a browser does not pin it.

Request bodies:
- Limited to `Paduraksa:Admin:MaxRequestBodyBytes` (default 64 KiB, range 1 KiB to 16 MiB). Over it is 413.
- An endpoint raises its own with `.WithAdminBodyLimit(bytes)`.
- Bodies of unknown length (chunked, HTTP/2 without Content-Length) are counted as they are read, so the limit holds for them too.

**Admin allowlist.** `Paduraksa:Listen:AdminAllowedSources` (addresses or CIDRs; empty means no restriction) is enforced per connection on the main listener, before TLS: the TCP handshake completes and the connection is then aborted unanswered.
- **Loopback is not implied** once the list is non-empty. List `127.0.0.1/32` or `::1/128` to allow it.
- IPv4-mapped IPv6 blocks are refused by the validator (clients are matched as IPv4).

**Strict DTO reading.** DTOs are read through the HTTP JSON options (`IdentityJsonContext.AddTo`). Unknown properties, missing members and nulls where not allowed are 400s. Typed IDs, role and permission names are as in the context. That sets `RespectNullableAnnotations` and `RespectRequiredConstructorParameters` on the admin's HTTP JSON options for every type.

### Loopback fallback

`Paduraksa:Listen:AdminLoopbackHttp` (a port, 0 = off, distinct from the other ports) adds a plain-HTTP listener bound by `ListenLocalhost` to 127.0.0.1 and ::1 only. Nothing configures another address. It is the same app and authentication.

- The cookie is still sent `Secure` (ASP.NET Core does not check the scheme). Chromium and Firefox store and send a `Secure` cookie for `http://localhost` and `http://127.0.0.1`. Safari does not.
- HSTS is not sent there, and the same-origin check uses the request's own `http` scheme.
- **DNS rebinding.** On that listener only a `Host` of `localhost`, `127.0.0.1` or `[::1]` with the listener's port is served. Any other is a 400 before anything else runs.
- The allowlist does not apply to it (it is loopback-only by construction).

### Wrong scheme

The admin port speaks TLS and nothing else, and Kestrel cannot answer plain HTTP on a TLS listener. A browser handed `http://<host>:9443/` used to get an empty response it could not explain.

A connection stage before TLS (`AdminPlainHttpRedirect`) peeks at the first byte:
- A TLS handshake record is left unread for the TLS middleware. The consumed and examined positions stay at the start, or the handshake would deadlock waiting for bytes the client only sends after the ServerHello.
- Anything else gets exactly one plaintext answer and a close.
  - A `GET` or `HEAD` with an origin-form target and a valid `Host` is a `308` to the same URL over `https`. `Location` is built from that `Host` verbatim, and the value is restricted to a DNS name or a well-formed IPv6 literal in brackets, each with an optional numeric port. A crafted header can therefore neither inject a header nor turn the authority into a path or another site. It carries `Cache-Control: no-store` (a `308` is otherwise cached for good) and an empty body.
  - Everything else is a plain `400`: no `Host`, a duplicate or unusable one, an absolute-form target, another method, an h2c preface.
  - A client that sends neither a ClientHello nor a complete head in ten seconds is dropped.
- The search for the empty line that ends a head is incremental. Only the bytes that arrived since the last read are scanned, with a three-byte overlap for a terminator split across reads. A client that drips its head a byte at a time therefore costs linear work rather than a re-decode per byte. One that connects and leaves without a byte is closed silently instead of answered.
- No endpoint, cookie or certificate is ever served over plain HTTP. The allowlist still refuses an untrusted source before a byte is read, and TLS on that port is untouched.
- Tests: one redirects over plaintext and then completes an ordinary HTTPS request to the same listener. Another runs the stage over a pipe pair to check that the TLS middleware's first read returns the ClientHello without waiting for more.

### The admin certificate and the root download

The listener serves a leaf the process issues from the local CA and keeps current. The full description is in [hosting.md](hosting.md). In short:
- the stable `AdminCertificate.Id`, source `local_ca`, label `Admin (internal CA)`;
- names from `Paduraksa:Admin:CertificateNames` plus localhost, the loopback addresses, the machine's host name and a specific `Paduraksa:Listen:AdminBind`;
- re-issued only when that set changes or a third or less of its lifetime is left, always under the same ID;
- neither deletable nor re-issuable by hand (`certificate.managed`);
- reserved for the admin port: left out of every inventory the compiler sees, and refused as a Specific binding or the default certificate;
- served through the vault's `IServerCertificateSource` with a per-handshake selection callback (hot-swap, no restart);
- ensured by a core-host service before the listeners bind, and re-checked daily;
- falling back to the placeholder with an Error log only when the vault has nothing servable;
- a `vault.db` lost under an intact config database is repaired on the next start.

The root is downloadable for trust-once: `GET /api/v1/ca/root.pem`, `GET /api/v1/ca/root.crt` (`application/x-x509-ca-cert`) and `GET /api/v1/ca` (JSON), plus `HEAD`. They are anonymous and open during setup (the prefix is one of the setup gate's allowed ones), and serve the public root only, from the in-memory `CaRootSource`.

`Paduraksa:Admin:CertificateNames` is validated at startup: DNS names and IP addresses, no wildcards or IPv6 zone ids, at most 95, so the certificate stays inside the vault's 100. Both numbers come from `CertificateLimits.MaxNames`.

The startup log prints the admin URLs and the root fingerprint at Information while setup is incomplete, alongside the setup token's banner.

### Not built

- Passkeys.
- A login-throttle store that survives a restart. The windows are in memory; the lockout is in the database.
- The internal listener and forwarded-header trust are built (see [hosting.md](hosting.md)).

## Proxied-host auth

`AuthProvider` per host or route.

- **Access lists:** IP allow/deny plus HTTP basic auth. Each user's password is a vault credential. The snapshot holds a handle on it, and the vault caches a verified check per credential and client address until five minutes after its last use. A check that finds no free slot waits in a bounded line. Built in M7 (see [waf.md](waf.md)).
- **Forward-auth** (authentik, Authelia, oauth2-proxy presets): a sub-request to the auth endpoint with the original method, URI and headers.
  - 2xx: copy the configured response headers upstream.
  - 401 or 302: relay to the client.
  - A short-TTL decision cache keyed by a hash of the session credentials keeps it cheap.
  - This path allocates (an outbound HTTP call), which is accepted and documented.
  - Built in M7 (see [waf.md](waf.md)).
- **Native OIDC per host** (later): Paduraksa as the relying party with a session cookie on the host's domain.
