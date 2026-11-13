# WAF building blocks

The WAF is deliberately simple and fast: named policy objects (access lists, rule sets, rate limits, auth providers) are compiled into each snapshot, and the gate evaluates them in a fixed order with no allocation. This page covers each block, how it is configured, where it sits in the chain and what it costs. How the chain is run is in [hot-path.md](hot-path.md).

Contents:
- [The blocks at a glance](#the-blocks-at-a-glance)
- [Rate limiting](#rate-limiting)
- [Proof of work](#proof-of-work)
- [Access lists](#access-lists)
- [Country rules](#country-rules)
- [Rule sets](#rule-sets)
- [Forward-auth](#forward-auth)
- [Managed robots.txt](#managed-robotstxt)
- [Error pages](#error-pages)
- [Request limits](#request-limits)

Everything here is compiled per snapshot, so the gate does only lookups and arithmetic at request time. Notes marked "as built" record what the code does today, including the gaps.

## The blocks at a glance

| Block | Implementation |
|---|---|
| IP allow/deny | `CidrSet` (M1): merged, sorted, disjoint `UInt128` ranges (IPv4-mapped), binary-searched, immutable. Chosen over a radix trie because membership needs no longest-prefix match, and the arrays are smaller and need fewer dependent loads (S3: 7.7 ns for 2,000 CIDRs). IPv6 rate keys are masked to /64 (`IpKey.MaskTo`). |
| Access lists | A named object (`acl_`) with allow and deny `CidrSet`s, basic-auth users (each a username and a password credential in the vault, verified through its verifier and cache) and `satisfy` all or any. See [Access lists](#access-lists). |
| Geo blocking | M7 has **`CF-IPCountry` only**, as country rules of an access list: a two-letter code from a trusted Cloudflare peer, compiled per list into a bitset (676 letter pairs plus Tor and unknown), one bit test per request. See [Country rules](#country-rules). A local country database is **not built**. |
| Rate limiting | A fixed-size, open-addressed token-bucket table. Policies per host or route: requests per second, minute or hour plus a burst, keyed by IP, IP+path or header. Returns 429 with `Retry-After`. See [Rate limiting](#rate-limiting). |
| Request size limits | `IHttpMaxRequestBodySizeFeature` per host or route. Header count and size and URL length are checked cheaply in the gate. Global Kestrel limits act as the outer bound. See [Request limits](#request-limits). |
| Rule sets | A named object (`rs_`): an ordered list of rules with path, method and user-agent conditions and an action (deny, drop, allow). Presets are built in. See [Rule sets](#rule-sets). |
| Forward-auth | A named object (`authp_`): an endpoint that every request to a host or route naming it is checked against before it is forwarded. See [Forward-auth](#forward-auth). |
| Proof of work | A route policy: a browser without a valid `paduraksa_pow` cookie gets a small challenge page, solved by pure-JS SHA-256 workers. The verify endpoint returns an HMAC-signed, host- and client-bound cookie. `IMacKey`, no server-side table, 0 B on the valid-token path. See [Proof of work](#proof-of-work). |
| Robots.txt | Per host: passthrough or managed (preset: disallow all, block AI crawlers, or custom text). Served from pre-encoded bytes. |
| Error pages | Per status (and "upstream down", "blocked", "rate limited", "maintenance"). Templates are pre-split into byte segments at compile time. See [Error pages](#error-pages). |
| Maintenance mode | Per host (layered like the rest): a toggle with a status, a page and a bypass named by an access list. See [Maintenance mode](#maintenance-mode-m8). |

## Rate limiting

### The engine (M7, `src/Paduraksa.Proxy/RateLimiting`)

**Table layout.**
- The table is a power-of-two number of entries, 256 Ki by default (about 4 MiB). The class takes any power of two. `Paduraksa:RateLimiting:TableSize` is validated to 1024 to 16 Mi.
- Each entry is two `long`s, held in two `long[]` arrays:
  - a 64-bit **key fingerprint**, claimed with CAS. Linear probing covers at most 8 slots; if none is free, the oldest of those is evicted. The probe starts at the hash mixed with the policy slot: a client's hash is the same for every slot, and a slot is one policy on one host, so without the mix one client's buckets on more than 8 rate-limited hosts shared one window and evicted each other, and every eviction starts a full bucket (found by the soak test: a 20/s limit let through 88/s once 11 other rate-limited hosts were busy).
  - a packed **state**, updated with a CAS loop.
- State layout:
  - **bits 63-24:** the last-refill time in ms since process start. 40 bits is about 34.8 years, so no wraparound handling is needed.
  - **bits 23-0:** tokens in 1/16 units (unsigned 20.4 fixed point). The maximum is about 1,048,575 tokens of burst, with refill precision of 1/16 token.
- The refill rate is a policy parameter in tokens/s, applied as `elapsedMs x rate x 16 / 1000` with saturation at burst.
- The fingerprint is a 48-bit hash plus the 16-bit **policy slot** in the high bits. The fingerprint itself cannot be inverted, so the slot is stored alongside it. A 48-bit hash in a 256 Ki table makes false sharing between partition keys negligible (about 2^-30 per probe).
- Memory is bounded, so a flood of unique IPs cannot exhaust RAM. Collisions only make limits slightly stricter.

**The hot path.** `RateLimitTable.TryAcquire(ushort slot, ulong partitionHash, in RateLimitParameters, long nowMs, out int retryAfterSeconds)` is 0 B and lock-free.
- The time is passed in, so tests drive it. `RateLimitClock.NowMs` is `Stopwatch`'s ms since process start.
- `RateLimitParameters(double tokensPerSecond, int burst)` validates itself. It stores the rate in integer 1/16-token steps scaled by 1024, so "one a minute" is a policy and not a rounding error. It also stores `FullRefillMs` (burst / rate) for the sweeper.
- `retryAfterSeconds` is the ceiling of the wait for one whole token, at least 1.
- A token count above the current burst is clamped the next time the bucket is touched. That is how a lowered burst takes effect (see [config.md](config.md)).

**The accepted races** are listed on the table's type, each with its bound:
- k threads probing for the same absent key in a full window can each evict a different entry for it. That gives k buckets, at most (k - 1) bursts extra once, and all but one are never found again.
- A token update can land in a slot another thread has just evicted and re-claimed: at most one burst, once.
- A thread between another's key claim and its state write sees the previous occupant's state: a full bucket, once.
- The sweeper's cleared state (0) is a bucket a request refills from zero.

None is unbounded:
- Every touch recomputes the tokens from the state it read and clamps them to its policy's burst.
- After four probes that each lost their slot, the request is let through without a token rather than stalled. That is one request per four lost probes, at whatever rate window churn produces them.
- A clock sample older than the stored time earns no refill and never moves the time back, in a take and in the sweeper. Masked, it would read as 2^40 ms.

**Policy slots.** `RateLimitPolicySlots` maps a stable identity (a `string`; the compiler passes the policy and host typed IDs' string forms) to its 16-bit slot.
- `GetOrAdd(identity, nowMs)` runs on the compiler's cold path (one lock). `IsLive(slot)` and `Parameters` are lock-free, for the sweeper.
- Slot 0 is "no policy".
- Retirement makes a slot non-live at once but reusable only after a grace period (one minute, like the metric slots). The reuse clears its buckets first, so a new policy never reads an old one's tokens.
- `Publish` swaps an immutable `RateLimitPolicyParameters` (slot to parameters) atomically.
- With all 65 535 slots in use, it throws with a message that says so.

**The sweeper.** `RateLimitSweeper` is a `BackgroundService` that runs every 30 s (`Sweep(nowMs)` is public for tests).
- It clears an entry whose slot is no longer live, or whose last refill is older than its policy's `FullRefillMs`. An entry whose slot has no published parameters is left alone.
- It clears with a CAS on the state it read, so a request that touched the entry in between wins and the entry survives.
- A live slot's parameters are the ones published at that moment. A sweep across a publish may use either set, which moves a clear by one pass at most.

**Partitioning.** `RateLimitPartition` hashes a partition key to 48 bits with FNV-1a and a murmur3 finalizer.
- It is keyed by a per-process `RandomNumberGenerator` seed (a fresh `RateLimitPartition(seed)` exists for tests), and domain-separated for IP, IP+path and header.
- IPv4 is keyed whole, IPv6 by its /64 (`IpKey.MaskTo(32, 64)`). The path is the canonical matching view the gate uses.
- Constructing two seeds in a test shows the per-process part: the same key hashes differently.

**Registration.** `AddPaduraksaProxy` calls `AddPaduraksaRateLimiting`. The runtime (table, registry, counters), the table, the registry and the sweeper are singletons, and the sweeper is a hosted service. `RateLimitTableBenchmarks` (`bench/Paduraksa.Benchmarks`) covers one key, a 64 Ki working set and one contended entry.

### Integration (M7)

**The policy object.** A `RateLimitPolicy` is a top-level named object beside upstreams and profiles.
- Fields: a typed `RateLimitPolicyId` (`rlp_`), a unique name, notes, `requests` per `second`, `minute` or `hour`, a `burst`, and a key of `clientIp`, `clientIpAndPath` or `header`. A header name is required exactly when the key is `header`, and is validated as a token.
- The validator judges `requests` and the derived rate against the engine's own bounds: `1/16384` to 1,000,000 tokens a second, and burst 1 to 1,048,575. The codes are `rateLimit.requests.invalid`, `rateLimit.rate.invalid` and `rateLimit.burst.invalid`. A document that validates cannot make `RateLimitParameters` throw.
- Header rules: `rateLimit.header.required`, `.unexpected` and `.invalid`.
- References: `host.rateLimit.unknown` and `route.rateLimit.unknown`. They are reported where the value resolves, with the layer named, like HSTS's preload.
- Canonical JSON orders policies by ID, lowercases the header and drops a record that sets nothing.
- The document API has list, get, create, update and delete, with `config.rate_limit_policy.*` audit actions.
- Deleting a policy that a host or route names is refused by validation, as deleting an upstream in use is.

**Layering.**
- `HostSettings.RateLimit` is a nested `RateLimitSettings` record (`enabled`, `policyId`), merged field by field through global defaults, profile and host.
- `RouteSettings.RateLimit` is the same record on top of the host. A route inherits the host's policy, names another, or turns it off with `enabled: false`. The reference is kept while off.
- The built-in switch is on, so naming a policy is the whole job. Naming none means no limit (the built-in default).

**Where it runs.**
- The policy runs in the ROUTE chain after the route's access policies and before proof of work, so a client over budget gets 429 and not a challenge page.
- The effective policy is the route's own, else the host's.
- Redirect and Respond hosts have no routes and are never limited. The admin host's implicit route carries the host's policy.
- **Gaps:** unknown hosts, 404s, ACME challenges and requests the proof-of-work verify endpoint answers are not limited by this wave. The verify endpoint is answered before route matching, so there is no route policy to apply, and giving it one needs its own rule.

**Buckets belong to a (policy, host) pair.**
- The slot identity is `{policyId}/{hostId}`, built from stable IDs. Two hosts that name one policy do not share a client's budget. A route that resolves to the host's policy shares the host's buckets. A route that names another policy gets that pair's.
- A compile that will be published calls `GetOrAdd` for every enabled Proxy or Admin host's pair, and puts the slot, parameters and identity in the snapshot's registrations.
- A draft, a dry run or a simulation only looks an existing pair's slot up. A new pair compiles with slot 0 (the simulator reports its full burst), so no compile that never goes live can leak a slot.
- Only the publisher publishes them. Just before a snapshot goes live it re-asserts each pair's slot (an older publish that retired a pair a newer one re-added must not leave the live snapshot on a non-live slot). It swaps in the parameters for that snapshot's slots and retires the pairs it no longer has. A draft or a dry run therefore publishes no parameters and retires nothing, and the sweeper clears a retired pair's buckets.
- A slot already rented to another identity is logged, and the pair starts on a fresh slot at the next compile, so the two never share buckets.
- A rate or burst edit keeps the slot and therefore the buckets: the new numbers apply on the next touch, and a lowered burst clamps the stored tokens. Deleting a policy, or removing it from a host, retires the pair.
- `RateLimitRuntime` is the process singleton holding the table, the registry, the partitioner and `RateLimitStats` (`Allowed` and `Limited`, process-lifetime `Interlocked` counters the security page reads). `RateLimitPolicySlots` and the table are also registered for the sweeper and for tests.

**The partition** is:
- the resolved client IP (IPv4 whole, the hash of IPv6 by its /64);
- plus the canonical matching path for `clientIpAndPath`. Route matching ignores ASCII case, so the partition folds it too: `/API` and `//api;` spend one budget;
- or the header's value. An absent or empty header falls back to the client-IP partition, in the partitioner's other hash domain, so a header value can never collide with an address.

**The refusal.**
- A refusal is 429 with `Retry-After: <seconds>` from `TryAcquire` and a short plain-text body, through the gate's precomputed-response path. The host's `Server`, HSTS and `Alt-Svc` apply as on any gate answer.
- `GET`/`HEAD /robots.txt` is exempt before the clock and the partition are touched, like the proof-of-work gate's exemption. A crawler reads the file even when the route's bucket is empty, whether the host serves it or forwards it. The cheap length and substring check keeps the canonical match off every other path.
- The policy precomputes one string per possible `Retry-After` value, so the allowed and refused paths both allocate 0 B.
- `GateMiddleware.Decide`'s Kestrel allocation test measures a rate-limited route on both: the allowed path through a large burst, and the refused one through a one-token bucket. The direct policy tests cover the verdicts, the bucket scope, the partition kinds and the simulation's peek.

**The simulator peeks.** `RateLimitTable.TryPeek` computes the same refill and clamp without a CAS, and reports `tokensLeft` (after the token the request would take) or `retryAfterSeconds`.
- `RateLimitPolicy.Explain` reports "would allow, N token(s) left" or "would refuse, retry after S s", with the policy's name and the key kind.
- A parity test proves two simulations in a row leave the bucket where they found it.
- S6's rate-limit part is `RateLimitContinuityTests`. Under load, with a publish every 100 ms that changes unrelated config and the policy's label, the 2xx count stays within burst + rate x elapsed, 429s keep coming to the end, and a lowered burst applies on the next touch.

## Proof of work

Built in M7. It protects a route from clients that will not do work: a browser has to solve a small puzzle once, and then carries a cookie.

### Settings

- `HostSettings.ProofOfWork` has `Enabled`, `Difficulty` and `TokenLifetimeHours`. Built-in: off, 16 bits, seven days. It inherits through the layers.
- `RouteSettings.ProofOfWork` carries only `Enabled`, which overrides the host's resolved switch either way. An `/api` route can be exempted, and one path protected on a host that is otherwise open.
- The compiled route gets a `ProofOfWorkPolicy` after its other policies. A Redirect or Respond host has no routes and is never challenged. A route's values are the host's, because only the switch is overridable.
- `ProofOfWorkStats` are process-lifetime `Interlocked` counters: challenges, solved, rejected by reason, and 403s to non-browsers. They are registered beside the runtime and not shown yet.

### Protocol

- **The page.** A `GET` or `HEAD` that accepts `text/html` gets a page carrying a challenge: base64url of `version | issuedAt | nonce(16) | difficulty | tag(16)`.
  - The tag is HMAC-SHA-256 over a `paduraksa-pow-challenge-v1` prefix, the host's stable `HostId` bytes and the client IP masked to /32 (IPv4) or /64 (IPv6). The challenge is valid ten minutes.
  - Its JavaScript is a pure-JS SHA-256, because `crypto.subtle` needs a secure origin. It searches in `navigator.hardwareConcurrency` Web Workers (1 to 8) for a decimal counter where `SHA-256(challenge + ":" + counter)` has `Difficulty` leading zero bits.
  - It then posts `challenge`, `solution` and `return` to `POST /.well-known/paduraksa/pow`.
- **The verify endpoint.** The gate answers it itself, before route matching, on any host where the gate is on for the host or a route (elsewhere the path falls through). It checks:
  - the body (2 KiB);
  - the challenge's tag and age;
  - the solution;
  - the challenge's difficulty against the host's current one.
- **The answer.**
  - On success: `303` to a local `return` target, with a `paduraksa_pow` cookie. Its value is base64url of `version | expiresAt | difficulty | tag(first 16 bytes)`. Attributes: `Path=/; HttpOnly; SameSite=Lax; Max-Age`, and `Secure` on the trusted request scheme.
  - An expired challenge gets the same `303` without a cookie (a fresh challenge follows).
  - Anything else is `403`.
- **The valid-token path.** It reads the cookie from the `Cookie` header span into `stackalloc` buffers and verifies with `IMacKey`. It allocates 0 B (`GateMiddleware.Decide` allocation test).
- **The page's CSP:** `default-src 'none'; script-src 'sha256-...'; style-src 'sha256-...'; worker-src blob:; form-action 'self'; base-uri 'none'; frame-ancestors 'none'`. The hashes are computed once from the inline script and style. `Referrer-Policy: no-referrer` and `X-Content-Type-Options: nosniff` ride along, and the host's response headers apply as on any gate answer.

### Exemptions and failure

- `GET`/`HEAD /robots.txt` and the verify endpoint are never challenged.
- **The key** is one vault secret of kind `HmacSigningKey` (HMAC-SHA-256, 32 random bytes) under a reserved ID (`ProofOfWorkKey.Id`). It is created at first start and opened into the process runtime by a core-host step after the vault and before the public listener binds. Rotation stays the vault's `ReplaceAsync` plus its `Verify` grace window.
- **If the key cannot be opened**, every protected route answers `503` and one Error is logged. A request never goes through unchallenged.
- **Replay.** The challenge is stateless, so a solved one can be redeemed again within its ten minutes. That only yields a cookie for the same host and client address. It is the accepted trade-off: there is no table to grow, and nothing a second solve would not give.
- **Warning.** A host with the gate on that answers plain HTTP without forcing HTTPS is reported, because the cookie cannot be `Secure` there.
- `IMacKey.VerifyTruncated` compares a tag of 16 bytes or more as the prefix of the full tag (constant time). That is how the 16-byte token and challenge tags are checked. `Verify` itself takes only whole tags.

## Access lists

Built in M7. An `AccessList` is a top-level named object, like a rate-limit policy.

### The object

- Fields: a typed `acl_` ID, a unique name, notes, and:
  - `ip`: `allow` and `deny` as CIDRs or single addresses, plus `dropDenied`;
  - `basicAuth`: a `realm` (defaulting to the list's name) and `users`;
  - `satisfy`: `all` (the default) or `any`;
  - `passCredentialsUpstream`.
- Country allow and deny lists sit under `ip` beside `allow` and `deny` (see [Country rules](#country-rules)).
- `HostSettings.AccessList` and `RouteSettings.AccessList` are one nested record, `{ enabled, accessListId }`. It merges field by field through global defaults, profile, host and route, like the rate limit.
  - A route inherits the host's list, names another, or turns it off with `enabled: false`. The reference is kept while off.
  - The built-in `enabled` is on, so naming a list is the whole job.
- The old inline `ipAllowList` is no longer part of the contract.
- Canonical JSON orders lists by ID, users by name (ordinal) and CIDRs as the parser prints them. A list with no rules writes none.

Validation codes:

| Code | Meaning |
|---|---|
| `accessList.ip.dropDenied.withoutAddresses` | `dropDenied` set with nothing to deny. |
| `accessList.realm.invalid` | Bad realm. |
| `accessList.users.tooMany` | More than 1000 users. |
| `accessList.user.username.invalid`, `.duplicate` | Usernames are 1 to 64 visible ASCII characters, no `:`, case-sensitive. |
| `accessList.user.password.missing` | A user with neither a password nor a credential. |
| `accessList.user.password.empty`, `.tooLong`, `.unhashed` | Password problems. `.unhashed` means a plain password reached the validator. |
| `accessList.user.credentialId.duplicate` | Each user has a credential of their own, across the whole document. |
| `host.accessList.unknown`, `route.accessList.unknown` | Reference to a list that does not exist. |
| `address.invalid` | An address that does not parse, reported at its entry (`/accessLists/0/ip/allow/2`). |

### Passwords

A user is *written* with a plain `password` (create and update through the API and the dashboard) and *read* as `{ username, credentialId }`. The password lives in the vault (see "Password credentials" in [vault-and-certificates.md](vault-and-certificates.md)). The document names its credential and nothing else. A caller with `config:read` can therefore no longer attack a verifier offline.

**How a password gets from a command to the vault.**
- `AccessListPasswords` (Application) takes the plain password out of the command before it is planned, validated, stored or audited. It leaves the credential's ID in its place: the ID the list already has for that user name, else the one the write sent, else a new one.
- The passwords are collected, not set. The pipeline sets them in the vault only once validation, the compile and the guards have passed, and the command is not a dry run or a simulation. A refused or previewed command therefore changes no password.
- A compile that runs first is told what the vault does not hold yet (`PendingPasswords`):
  - for a command that will commit, that its credentials are about to be set, so a new user carries no warning the command is about to resolve;
  - for a dry run or a draft, a handle per pending password that carries the prepared, already-hashed password for that compile alone (`PrepareAsync`). The mapping uses it in place of the vault's own, so nothing reaches the vault or its cache until a real commit.
- The validator refuses a password that is still there, so no write path can store it. Canonical JSON drops the property. The audit entry holds the action and the ID only.
- A user written with neither a password nor a credential keeps the one the list has for that name. That is how an edit avoids asking for every password again. A new user with neither is `accessList.user.password.missing`.

**A password is a change.**
- The document's users do not differ when only a password does. So the pipeline reports the list as modified (field `users`) when a command carries one: it commits a revision, moves the list's version (an editor holding the old one conflicts), and is audited like any change to the list.
- The revision records that the list changed, and never the password.
- The API returns `credentialId` and never `password`.
- Whether the vault holds the password shows as the compile warning `accessList.user.noPassword`, in the result of the command and in the live warnings. It points at the user's own entry (`/accessLists/N/basicAuth/users/M`, the position in the canonical document the pipeline compiles). The dashboard's drawer reads it by that pointer to say "No password is set, so this user can't sign in".

**Rollback and import** bring back the users a revision named and give them the passwords the vault holds then. For a credential deleted since, that is none, and the warning appears. The credential lifecycle (deleted when the live config stops naming it) is in [vault-and-certificates.md](vault-and-certificates.md).

**Gaps.** An existing bcrypt `htpasswd` line cannot be imported (PBKDF2 only for now). A dry run or a draft hashes each pending password (one PBKDF2, tens of milliseconds) so it can be simulated.

### Upgrading older documents

**The inline list.** A stored document with the inline `ipAllowList` is converted when it is read into the current model (`LegacyAccessListUpgrade`, on the JSON, before the typed read). This covers the live revision, a rollback target, the last-known-good file and an imported document.
- Every layer that set `ipAllowList` gets `accessList: { enabled: true, accessListId }`. An empty list, which lifted what was inherited, becomes `{ enabled: false }`.
- Each distinct content (the sorted, canonical addresses and the drop flag) becomes one access list. It is named after the first place that used it in document order (`global-defaults`, `profile-lan-only`, `host-example-com`, `route-example-com-admin`, with `-2`, `-3` for a clash).
- Its ID is taken from a hash of the content, so converting a document twice gives the same bytes. Its version is 1.
- At startup, when the live revision needed converting, one revision with the converted document is written: actor System, a message naming the lists, audit action `config.upgrade`, every host, profile and list counted as touched so their versions move. If it cannot be stored, it runs as converted and the next change stores it.
- A write that sends `ipAllowList` is refused. The API answers 400 with a sentence that names access lists and `POST /api/v1/access-lists`.
- `LegacyAccessListUpgradeTests` run old documents through the real gate and compare outcomes with the old semantics, drop included.

**Passwords in the document.** A stored document whose users still carry `passwordHash` (written by the build before passwords moved into the vault) is converted the same way (`LegacyPasswordUpgrade`).
- Each user gets a `credentialId` taken from a hash of the list's ID, the user name and the verifier. A user that already names one keeps it. Converting twice gives the same bytes. The `passwordHash` property goes.
- The conversion is lossy by design, because the verifier does not stay in the document. So **every place that makes such a document live imports its verifiers first**, into the vault as they are, with no re-hash (`ImportAsync`, which changes nothing for a credential that already has a password):
  - startup (the latest revision, and the last-known-good file wherever it is used);
  - a rollback to an old revision (its verifiers are imported before the commit, so the user comes back with the password they had then);
  - the head a command builds on (when the startup write could not be stored).
- Import happens only for a command that will commit. A dry run or a draft imports nothing: it compiles against handles for those verifiers, like its passwords, and the next real command or start does it. Reading a revision to show it or diff it imports nothing.
- At startup, when the live revision needed converting (either conversion), one revision with the converted document is written as above. Its message says what was converted: the inline allow lists and the lists they became, the passwords moved into the vault, or both. The vault's credentials nothing names are swept.
- A write that sends `passwordHash` is refused. The API answers 400 with a sentence that says to send the plain `password` or leave it out.
- `LegacyPasswordUpgradeTests` (the conversion) and `ConfigServiceAccessListTests` (import at startup, stable on a second start, a rollback to an inline-hash revision, the sweep) hold it.

### Chains

The mapping builds `AccessListPolicySpec`s.
- A route's chain carries the whole list.
- The host chain carries `AddressOnly` (what the address alone can refuse). For a host whose routes name different lists whose address rules can all refuse, it carries one `AccessListUnionPolicySpec` of them instead. The reasoning is in [config.md](config.md).
- A host with no routes (Redirect, Respond, Admin) carries the whole list, with its password deferred on plain HTTP when the host redirects to HTTPS (the redirect is what answers next).
- A compile warning, `accessList.insecureCredentials`, says so for a host that asks for a password without forcing HTTPS.

### The gate (`Paduraksa.Proxy.AccessLists`, 0 B on every synchronous path)

**Rules.**
- A denied address is refused whatever it sends.
- A non-empty allow list admits only its addresses. With `satisfy: all` and users, the rest get 403. With `any`, they are asked for a password instead.
- No users means the address rules alone. No address rules means the password alone.
- `dropDenied` aborts the connection where the address decided.

**Basic auth.**
- `Authorization: Basic` is read from the header string into a `stackalloc` buffer: at most 64 + 1 + 256 decoded bytes, split on the first colon, UTF-8.
- The name is compared in constant time with every user's, with no early exit.
- The password is checked by the vault's verifier (`IPasswordVerifier`): a cache probe, then PBKDF2 against the credential's verifier, which the gate never sees.
- A name nobody has, and a user the vault holds no password for, still pay one PBKDF2 against a dummy verifier. Neither can be told from a wrong password by timing.

**Answers.**
- A missing or wrong credential is `401` with `WWW-Authenticate: Basic realm="<realm>", charset="UTF-8"` (precomputed per list, the realm quoted), `Cache-Control: no-store`, and the built-in `401 Unauthorized` page for `Accept: text/html` (plain text `authentication required` otherwise).
- The 403 is the existing access-denied page.
- When the list accepted the credentials, `GateRequestState.StripAuthorization` makes the forwarding transform remove the `Authorization` header before the upstream sees it, unless the list sets `passCredentialsUpstream`.
- A list that let an allowed address through without asking consumed nothing, so a bearer token meant for the upstream still reaches it.

### The verification cache

It lives in the vault (`PasswordVerificationCache`, held by the vault's `PasswordRegistry`). The vault documentation says what a key is made of and why a password change invalidates it.

- It is a fixed table of two arrays: 4096 entries, 64 KiB. Each entry is a 64-bit key and an expiry.
- The key is the first 8 bytes of SHA-256 over a per-process random key, the credential's stamp, the client address masked as the rate limit masks it (IPv4 whole, IPv6 by /64), and the credentials as sent.
- A hit needs no PBKDF2 and **pushes the expiry to five minutes from the hit**, but only once less than four minutes are left. A hot entry is written about once a minute (one lock-free store, 0 B), and an idle user drops out between four and five minutes after the last request, while an active one stays verified.
- Only a *successful* verification is ever inserted.
- Writers clear the expiry, store the key and then the new expiry. Readers read the key, the expiry and the key again, and believe it only if both reads agree. A reader never pairs one entry's key with another's expiry.
- A probe of four slots evicts the entry that would lapse first.
- A refresh stores its expiry with no further check, so it can land after a writer has replaced the slot's key. That cannot make an unverified key live (a key only enters through `Add`, after PBKDF2). It leaves a verified key with an expiry off by the skew of two clock reads, so the worst case is an entry living milliseconds longer or being verified again early.
- Removing a user or changing their password changes the credential's stamp (or empties the credential), so the old entries are never found again. There is no recompile and nothing to sweep.

### The verification line (`AccessListVerificationQueue`)

A cache miss no longer runs PBKDF2 inside the synchronous decision, and no longer answers 429 when the slots are taken.

**Flow.**
- `AccessListPolicy` returns `PolicyDecision.Defer` (`PolicyAction.Defer`). The decision is `GateAction.AwaitPolicy`, with the policy and its place in the chain in `GateRequestState.DeferredPolicy`, `Scope` and `Index`.
- Every other path (an allowed or denied address, a cache hit, missing or unreadable credentials) stays synchronous and 0 B.
- `GateMiddleware` awaits `IDeferredPolicy.CompleteAsync` and **resumes the same chain right after that policy**. That covers the rest of the host chain, the HTTPS redirect, the robots.txt and fixed answers, route matching, the route's chain after the list (its rate limit, then proof of work) and the endpoint. It uses the same methods `Decide` uses (`DecideAfterHostChain`, `DecideRoute`, which take the first policy to run). Nothing after the list is duplicated or skipped, and a request that waited is limited, challenged and forwarded (with `Authorization` stripped) exactly like one that did not.
- The continuation takes a place in the line and waits for a slot without holding a thread. It looks at the cache once more, runs the PBKDF2, and answers 401 or lets the chain go on.

**Sizing.**
- Slots: `max(8, ProcessorCount)`. Never fewer than 8: on a 2-core box a cap of 2 refused a page's third credentialed request on a cold cache. Over-subscribing only makes each check slower.
- The line is bounded three ways:
  - **32 places per slot** (256 at the default 8). At tens of milliseconds a check, the last of a full line waits a few seconds, inside the wait cap whatever the machine's size.
  - **16 places per client**, queued and running together, by the masked address as the cache keys it. It is a table of 1024 in-flight counters striped by a keyed hash (4 KiB), and as many running counts.
  - A **10 second maximum wait**.
- A request that cannot enter (line full, or its client already holds its 16) or that waits the maximum is answered 429 with `Retry-After: 1`.
- A request whose client went away (`RequestAborted`) leaves the line at once, frees its place and is dropped without an answer.

**Fairness.** Two rules decide who runs next, so a place is never wasted and nobody is starved.
- **One client runs at most 2 checks at once.** Its other requests wait in the line. A free slot goes to the first waiter in arrival order whose client is under that cap, so others overtake them.
- **Identical credentials never run twice at once.** Each deferred request carries its attempt's key (the vault's opaque name for these credentials from this client, `PasswordCacheProbe.AttemptKey`; 0 for a name nobody has, or a user with no password, which have no identity to share). A waiter whose fingerprint is running waits for it, then finds the result in the cache and pays no PBKDF2. A dozen identical cold requests cost one check. Identical *wrong* passwords are checked one after the other, each its own.
- The per-client share replaced the old "under pressure" rule (a client holding two or more was refused only while half the slots ran).
- The first version of the share was 4 places. That refused the fifth credentialed request of an ordinary HTTP/2 page burst, the very 429 the line exists to remove. Places are cheap (a pooled waiter), so the share only has to stop one source taking the whole line: a single source holds at most 16 of the 256 places and eats only its own 429s, while every other client still gets a turn within the wait. The share is a concurrency limit, not a rate.

**Implementation.**
- Waiters are pooled `IValueTaskSource` objects.
- One timer serves the whole line (arrivals wait the same maximum, so the head is always the next to expire).
- The line's one lock is held for a few field writes and a scan of the line, never while a waiter is completed.
- A granted waiter's continuation goes to the thread pool, never inline on the thread that freed the slot, which would stack one PBKDF2 on another.
- The pool hands a waiter back only after its cancellation registration is disposed.

**Allocation** (Release, 64-bit, `GC.GetAllocatedBytes`; measured the same on the distro runtime 10.0.14 and on Microsoft's 10.0.403 from `scripts/test-ci-like.sh`):
- Taking and giving back a free slot is 0 B (`AllocAssert`), as is a refusal at the door.
- A miss that finds a slot free allocates nothing beyond the 401 a missing credential gets: 1137 B against 1144 B in a test context, which is the answer's own cost.
- A miss that waits adds about 550 B (the boxed continuations, the cancellation registration, the thread-pool work item). Measured once, not asserted by a test.
- PBKDF2 itself is 0 B on both runtimes.
- The allocation tests of the synchronous paths (`GateAllocationTests`, `AccessListPolicyTests`) include the deferring decision.
- Not yet pinned by a test: a queued waiter cancelled while an identical-credential twin waits behind it. It is safe by construction, since the twin is served once the in-flight check exits.

**Counters.** `AccessListStats` are process counters the security overview shows: allowed, address refused, 401, cache hits, verifications, **queued, queue-full refusals, timeouts**, and busy as the sum of the two refusals.

**Gap.** A spray spread over many addresses (a botnet, or many /64s) can still fill the 256 places. First-time logins on every list then get 429 until it ends. Nothing throttles failed attempts per address over time. A credential already in the cache still gets through, since a hit needs no slot.

### Simulator

The policy's trace step names the list and gives:
- the address verdict: denied, allowed with the matching entry, not in the allow list, or unrestricted;
- the credentials verdict: not asked, missing, unreadable, unknown user, wrong password, no password (the user's credential holds none in the vault), ok, deferred, or busy (the line was full or the wait ran out);
- how `satisfy` combined them.

It gives these as a sentence and as facts (`accessList`, `address`, `credentials`, `satisfy`). The probe's `Authorization` header is read like a real one, and neither it nor a typed name is ever echoed.

A simulation neither reads nor refreshes the cache, moves no counter, and takes no verification slot or place in the line. It runs its PBKDF2 inline through `Verify(..., remember: false)`, as it always did, so it cannot warm the gate for a later request or exhaust the verifier. `AccessListSimulationTests` compare the simulator's outcome with the real gate's for every satisfy mode, address and credential.

### API and dashboard

- `/api/v1/access-lists` (list, get, create, replace, delete) has the same ETag, dry-run and 422 rules as the other objects. Deleting a list a host, profile, default or route names is refused by validation. Audit actions are `config.access_list.*`.
- `CommandErrors` also learned the rate-limit and access-list lists, which an invalid object command needs to point its errors.
- The dashboard has Security > Access lists: a list of rules and users of each, where one in use offers no delete. The editor drawer has:
  - the allowed and denied addresses, one per line, with an entry count;
  - the drop switch and the realm;
  - users, each with a password box that stays empty to keep the stored password, and a Generate button that fills 20 random letters and digits and shows them once with a warning;
  - the "address and password" or "address or password" choice, which appears once there is a user.
- Inline validation runs the real validator with a credential ID in place of a typed password, so nothing reaches the vault from the circuit.
- The review and the history show a list's passwords as the realm and the users by name: "alice (new password)", "alice (password changed)", "bob (removed)". A revision whose users are the same says "a password was changed". The combine choice shows as "Address and password" or "Address or password". Never a password, a credential ID or a wire value.
- The settings of a host, profile, the defaults and a route pick a list by name (inherit, none, or a list), with a staged "New access list..." entry exactly like the rate-limit picker.

## Country rules

Built in M7. Geo is `CF-IPCountry` only. There is no local country database in this wave, so a country exists only behind Cloudflare (Proxy or Tunnel mode) and is unknown everywhere else.

### Resolving the country

- `ClientIpResolver.Resolve` reads `CF-IPCountry` in the branch where it reads `CF-Connecting-IP`: a trusted peer in a Cloudflare mode (`ClientIpResolution.CloudflareHeadersTrusted`), and from no one else. A client's forged header, a trusted generic proxy's pass-through and a peer outside the edge ranges all give an unknown country.
- The result is `ClientIpResolution.Country`, a `CountryCode` (`Paduraksa.Core.Geo`). It is a compact slot: 0 unknown, 1 Tor, then the 676 letter pairs. A request carries no string, and the parse is 0 B.
- What counts as what:
  - Two ASCII letters in either case are a country, assigned or not. An unassigned pair parses and matches no rule.
  - `T1` is **Tor**, kept apart from unknown because "block Tor" is a common wish.
  - `XX`, a repeated header, a missing one and anything that is not two letters are **unknown**.
- The state exposes it as `GateRequestState.ClientCountry`. The unused `ClientInfo.CountryIndex` placeholder is gone: the country is per request, and `ClientInfo` is per connection.

### The model

- `AccessListIp.Countries` is `{ allow, deny }`. The codes are the ISO 3166-1 alpha-2 assignments (`Paduraksa.Core.Geo.Countries`, 249 codes with their English names, ASCII), plus `XK` (Kosovo, which Cloudflare reports and ISO does not assign), `T1` (Tor) and `XX` (unknown).
- Canonical JSON writes codes upper-case, sorted, once. It drops a list that says nothing and writes no `countries` at all for none, so a document without them keeps its bytes (the golden hash is unchanged).
- Validation:
  - `accessList.countries.code.invalid`: not in the list, reported at the entry (`/accessLists/N/ip/countries/allow/M`).
  - `.duplicate` (in either case) and `.conflict` (a code in both lists).
  - `accessList.ip.dropDenied.withoutAddresses` counts countries as something to refuse.
- Warning `accessList.countries.noSource` (at the host's `/settings/accessList`): an enabled host uses a list with country rules while the trusted-proxy mode is not `CloudflareProxy` or `CloudflareTunnel`. Every country is then unknown, so an allow list refuses everyone and a deny list nobody.
- The compile reports a code it does not know too.
- A list compiles to two `CountrySet`s (678 bits in 11 words), and a check is one bit test.

### Semantics

The country is part of the **address side**.
- **Deny wins.** A denied address or country is refused whatever the request sends, a password included (and `dropDenied` aborts there).
- A non-empty allow list admits only its members. So **an unknown country is refused when the list has a country allow list, unless `XX` is in it**. Tor is admitted only if `T1` is.
- When a list has both an address allow list and a country allow list, a request must be in **both**. Decided: each non-empty allow list is a restriction, and the stricter reading fails closed. "This address, or that country" is not expressible.
- `AddressVerdictOf` gives one verdict (denied, allowed, not in the allow list, unrestricted). The policy, the host chain's address-only stage, the union of lists and `RefusesByAddress` (the compile-time "can the address alone refuse" decision) all use it.
- `satisfy` then combines it with the password as before:
  - `all` asks for the password of an allowed visitor and refuses the rest with 403;
  - `any` lets an allowed visitor in without one and **asks the rest, a visitor from a country outside the allow list included, for a password**, which lets them in. A denied country is still refused.
- The drawer says so under "How they combine".

### Gate and simulator

- 0 B on every path. `AccessListCountryTests` measures allowed, refused by country, unknown, Tor dropped, a forged header and the password paths through `GateMiddleware.Decide`.
- `AccessListPolicy`'s trace step adds the facts `country` (`DE`, `T1`, `XX`) and `countryRule` (`allowed`, `notInAllowList`, `denied`, `unrestricted`) when the list has country rules. Its sentence names the country and the rule: "Germany (DE) is an allowed country", "France (FR) is not in the allowed countries (2)", "Tor exit nodes (T1) is a denied country".
- `SimulationRequest.country` (a code, `T1` or `XX`) is added as `CF-IPCountry` to a request that arrives through Cloudflare (`GateProbe.Country`).
  - On a direct or generic-proxy arrival it is refused (`simulation.request.country` and `.conflicting`), like any field that would change nothing.
  - A probe that carries the header itself on those arrivals shows the gate ignoring it: the client step says `country: ignored` beside `countryHeader`.
- `AccessListCountrySimulationTests` run the real gate and the simulator over every arrival (Cloudflare, a forged header, none), country (including lower case, garbage, `T1`, `XX`, missing) and satisfy mode.

### Dashboard

- The access-list drawer has a Countries section with two pickers (`CountryPicker`): a search by name or code in an autocomplete, and chips for what is held. "Germany (DE)", Tor exit nodes and Unknown country come first. A country the other side holds is not offered.
- Its copy says the country comes from Cloudflare. A one-line hint shows while the global mode is not a Cloudflare one.
- The list page's summary counts countries ("only 2 countries", "blocks 3 countries").
- The security overview's list cell says "countries only" or "addresses and countries only" for a list with no users.
- The simulator's Advanced panel takes a country for the Cloudflare arrivals.
- A browser test drives the picker and keeps screenshots at 1280 and 390 px.

### Gaps

1. **No local country database.** A request not behind Cloudflare, or whose Cloudflare header is missing, is unknown.
   - The seam for DB-IP Lite or GeoLite2: the country is produced in one place, `ClientIpResolver.Resolve`, and carried as `ClientIpResolution.Country`.
   - A database would fill it when the header did not: a lookup of the resolved client address in a snapshot-held, sorted `UInt128` range array with a `CountryCode` per range, binary-searched like `CidrSet`, built by a background job and compiled into the snapshot.
   - Rules, gate, trace and dashboard would stay as they are, and `accessList.countries.noSource` would stop applying.
   - Candidates: DB-IP Lite (CC-BY, no key) or MaxMind GeoLite2 (optional, needs a license key), with an auto-update job.
2. The trusted-proxy mode is global, so a host cannot use Cloudflare for countries while another does not.
3. The country is not in the access log or the metrics yet.
4. When Cloudflare sends no `CF-IPCountry` (its setting for it is off), every visitor is unknown, so an allow list refuses everyone. The trace's `country: XX` is the only sign, and the config cannot tell.

## Rule sets

Built in M7. A `RuleSet` is a top-level named object like an access list: a typed `rs_` ID, a unique name, notes and `rules`, an ordered list.

### The model

A rule has a `match` and an `action`.
- `match` states any combination of:
  - `path`: a `kind` (`exact`, `prefix`, `glob` or `regex`) and a `pattern`;
  - `methods`: a set, upper case;
  - `userAgent`: `contains` (case-insensitive substrings) and `empty`, which also takes a missing or blank user agent.
- A rule needs at least one condition, because one with none would match every request. Every condition it states has to hold.
- `action` is `deny` (403 and the built-in page), `drop` (abort, no answer) or `allow`.
- The first rule a request matches decides. An allow carves an exception out of the denies after it: it ends the set, and the rest of the chain still runs. A request no rule matches goes on.
- `HostSettings.RuleSet` and `RouteSettings.RuleSet` are one nested record, `{ enabled, ruleSetId }`, merged field by field through defaults, profile, host and route like the access list's.

**One set per layer** was enough. A second concern is more rules in the same set, in the order the operator wants. One list a request walks can express that, and two lists could not: an allow that must beat a deny sits in the same list as the deny. What it costs is that a preset and an operator's own rules cannot be stacked without copying the preset into a set of one's own, which "Copy to edit" does.

**Validation codes:**

| Code | Meaning |
|---|---|
| `ruleSet.id.reserved` | A document tried to use a preset's ID. |
| `ruleSet.rules.tooMany` | More than 200 rules. |
| `ruleSet.rule.empty` | No condition. |
| `ruleSet.rule.path.invalid`, `.encoded`, `.notCanonical` | The route-path rules: write a path decoded, and a pattern no request's canonical path could equal is refused. |
| `ruleSet.rule.path.glob.invalid` | At most 126 characters and wildcards, ASCII. |
| `ruleSet.rule.path.regex.invalid` | The regex is built at validation, NonBacktracking, and the parse error is the message. |
| `ruleSet.rule.methods.invalid` | A token, at most 16. |
| `ruleSet.rule.userAgent.invalid` | Printable ASCII, at most 100 of 128 characters. |
| `host.ruleSet.unknown`, `route.ruleSet.unknown` | Reference to a set that does not exist. |

- A regex that backtracks catastrophically on a backtracking engine (`^(a+)+$`) is **accepted**. It runs in time linear in the path, so it costs no more than a plain one.
- Canonical JSON orders sets by ID, writes methods upper case and sorted, and writes user-agent substrings trimmed, unique ignoring case and sorted.
- A stored document without `ruleSets` reads as none.

### Presets

`RuleSetPresets` (Contracts) has three read-only sets with reserved IDs. They are never part of a document (`ruleSet.id.reserved` refuses one that tries). They are named in a setting like any set, and compiled into a snapshot only when something names them.

| Preset | Contents |
|---|---|
| **Block AI crawlers** | The user agents of `RobotsText.AiCrawlers`, as one rule. The robots.txt mode and the rule set can therefore never disagree about who an AI crawler is. |
| **Block known scanners** | `sqlmap` (SQL-injection tool), `nikto` (web-server scanner), `nmap` (its scripting engine sends "Nmap Scripting Engine"), `masscan` and `zgrab` (internet-wide port and banner scans), `nuclei` (template scanner). |
| **Block empty user agent** | Refuses scripts and some health checks. |

- The scanner list is deliberately short and conservative: one name each whose default user agent contains it and no browser's does. It removes noise, not attackers; a real one changes the header.
- Like the crawler list, the presets are refreshed with each release.
- "Copy to edit" makes a new set (a fresh ID, "name (copy)") holding the same rules.

### Where it sits

The set runs before the access list that asks for passwords. A refused request then costs no PBKDF2 (the deferred check is the one expensive thing in the chain) and no rate-limit token.

Precisely:
- The mapping inserts the policy immediately **before the first `AccessListPolicySpec` with `Stage: Full` whose list has users**. That is the whole policy, address checks included. On a route whose own list has users, the list's address rules run *after* the set.
- What always runs before the set is the host chain's refusal by address: the `AddressOnly` policy, or the union of the routes' lists. It is cheap, and a denied address must still be dropped, not answered with a rule's 403.
- With no such list, the set goes after the access policies there are. A `Full` list without users is address rules only.

The resulting orders:
- In a route chain: set, then the list (addresses, then passwords), then rate limit, then proof of work.
- In a host chain: the address-only check or union, then the set. A host with no routes carries the whole list there, and the set goes before it when it has users.

**Host chain or route chain** follows the access list's rule (see [config.md](config.md)).
- When every route resolves to the same set, or the host has no routes (Redirect, Respond, Admin), it is in the **host chain**. It then also guards the 404 for a path no route takes and the HTTPS redirect: a denied client gets 403, not a 301 that maps the host.
- Otherwise each route carries its own set. A deny cannot be lifted by a union as an address allow list can.

**Exemptions.**
- `GET` and `HEAD /robots.txt` are never ruled on, like the rate limit and the proof-of-work gate. RFC 9309 reads a 4xx on that file as "no restrictions", so refusing it would tell a crawler it may go anywhere.
- The unknown-host answer and ACME challenges are answered before any chain and are not ruled on.
- The proof-of-work verify endpoint is answered after the host chain, so only a set that sits there rules on it.

### The matcher (`Paduraksa.Proxy.RuleSets`)

- Rules read the **canonical matching path** routes match on (`RoutePath`): `\`, `%2F`, `%5C`, repeated separators, `;` parameters, dot segments and trailing dots resolved, case ignored. No encoding trick or redundant spelling dodges a rule. It is computed only for a set whose rules read the path, on the stack or in a pooled buffer.
- **Exact and prefix patterns** sit in sorted arrays searched ignoring case. A prefix matches at each segment boundary of the request, with the route's own segment-bound semantics: `/admin` takes `/admin` and `/admin/x`, not `/administrator`.
- Rules with no path, a glob or a regex are scanned in order. The lowest index among everything that matched wins, so the document's order is the precedence whichever index found a match first.
- A **glob** is `*` within a segment, `**` across segments, and a `/**/` segment for none or more (so `/**/.env` takes `/.env`).
  - It is simulated as an automaton with one bit of state per token (a `UInt128`, so at most 126 tokens).
  - Each character is read once and nothing backtracks. A path cannot be shaped to make it slow: a test runs the classic quadratic case over 8,000 characters. It allocates nothing.
- **User-agent substrings** are one `SearchValues<string>` per rule with `OrdinalIgnoreCase`, over every `User-Agent` header line. A request that sends two cannot hide a name in the one the upstream reads.
- Methods compare ignoring case (over-matching only makes a deny stricter).
- Inside a rule, the cheap conditions run first and the path scan last.

### Allocation

Every verdict is 0 B. The deny answer is precomputed, and a non-canonical path is rewritten on the stack or in a pooled array. This is proved through `GateMiddleware.Decide` for:
- a pass, an allow, a prefix and an exact deny, a glob drop, a method deny and a user-agent deny;
- a blank user agent;
- a path canonicalised on the stack and in a pooled buffer;
- robots.txt;
- and on Kestrel (`RuleSetsAllocateNothingOnKestrel`).

A **regex** is the one thing that may not be.
- The NonBacktracking engine builds its automaton lazily, as it meets characters it has not seen, so a first match over a new shape can allocate once.
- Measured here, a regex rule matched repeated path shapes at 0 B once warmed (`ARegexRuleMatchesWithoutAllocatingOncePathShapesAreKnown`, on the distro runtime and on Microsoft's 10.0.403).
- A set without a regex never pays for it, and a rule runs its regex last, after the conditions that could rule it out.
- The regex has a 50 ms match timeout. The rule's action decides it, so it fails closed: a deny or a drop counts a timeout as a match, an allow as no match. `RuleSetCounters.Timeouts` counts it (shown under the set on the overview once above 0). The engine is linear in the path and a request line is at most 8 KiB, so it should never happen.

### Counters and the overview

- `RuleSetCounters` (denied, dropped, allowed, regex timeouts) are process-lifetime `Interlocked` longs per set, kept by `RuleSetRuntime` and carried by the compiled set. They survive a publish, and a simulation counts nothing.
- The security overview adds a **Rules** column: the set a host applies (a document set links to its page, a preset to the list that shows the presets), what it has refused or dropped, and the routes that apply another set, a preset or none.
- There is no global rule tile. The counts are per set, and a set that is deleted takes its history with it.

### Simulator

- The policy's step names the set, the rule (counted from 1, as the dashboard numbers them), what matched (`path starts with /admin, method POST`, `user agent contains 'sqlmap'`, `no user agent`) and the action. It gives them as a sentence and as facts (`ruleSet`, `rule`, `matched`, `action`), and says when none matched or that robots.txt is exempt.
- `RuleSetSimulationTests` put a matrix of paths (non-canonical spellings included), methods and user agents through the simulator and the real gate, and compare the outcomes.
- The lock-out guard now sends the caller's own `User-Agent` in its probe (`AdminRequestFacts.UserAgent`). A rule set that refuses the caller on a proxied admin host is caught, and one that refuses only other agents is not.

### API and dashboard

- `/api/v1/rule-sets` (list, get, create, replace, delete) has the same ETag, dry-run and 422 rules as the other objects, with `config.rule_set.*` audit actions. `GET /rule-sets/presets` lists the presets, and `GET /rule-sets/{id}` also reads one. A delete of a set a host still resolves to is refused.
- The dashboard has Security > **Rules**: the sets with what their rules do and who applies them, and the presets read-only under them with "Copy to edit" (`/rules/new?copy=...`).
- The drawer's rules read "When [path] [methods] [user agent] then [action]", with add, move and remove, and the real validator's messages at the fields (a regex's parse error, a rule with no condition).
- The settings of a host, profile, the defaults and a route pick a set by name in the Access section: inherit, none, a set, a preset, or a staged "New rule set..." that goes in with the host in one revision.
- The review shows a set's rules as numbered sentences.

**Gaps.**
- Nothing warns about a rule no request can reach (shadowed by an earlier one).
- No condition on the query string, headers or client address (geo and access lists decide by address).
- The `**` glob has no single-character wildcard.
- A set cannot be applied to the unknown-host answer or an ACME challenge.

## Forward-auth

Built in M7. Every request to a host or route that names a forward-auth provider is checked against the provider's endpoint before it is forwarded.

### The provider object

An `AuthProvider` is a top-level named object like an access list or a rule set: a typed `authp_` ID, a unique name, notes and a `forwardAuth` block. A second kind, native OIDC, is a later sibling property; exactly one is set.

The `forwardAuth` block holds:

| Field | Meaning |
|---|---|
| `url` | The endpoint. |
| `preset` | `authentik`, `authelia` or `oauth2Proxy`. None means a custom endpoint. |
| `timeoutSeconds` | 1 to 30, built-in 5. |
| `cacheSeconds` | 0 to 300, built-in 10. 0 remembers nothing. |
| `cacheScope` | `request` or `host`. |
| `requestHeaders` | Extra client headers sent to the endpoint. |
| `responseHeaders` | Copied upstream. A trailing `*` is a prefix. |
| `signIn` | `url` and `returnParameter`. |

A field left unset takes the preset's value. `ForwardAuthPresets.Resolve` (Contracts) is the one place that says which, for the validator, the mapping and the dashboard.

**Settings.** `HostSettings.AuthProvider` and `RouteSettings.AuthProvider` are one nested record, `{ enabled, authProviderId }`, merged field by field through global defaults, profile, host and route like the access list's.
- A route inherits the host's provider, names another, or turns sign-in off. The route that serves the provider's own pages has to.
- The reference survives while it is off, and naming a provider is enough (the built-in switch is on).
- **Naming does not imply on.** A layer that names a provider above a layer that switched sign-in off (the defaults or a profile says `enabled: false`, the host names one and leaves the switch alone) merges to off, like an access list's. That is also how "off, reference kept" keeps working. Through the API that would fail open without a word.
- So the mapping warns (`authProvider.namedButOff`) **at the layer that named it**: the host's `/settings/authProvider`, the profile's (`/profiles/{i}/settings/authProvider`, once however many hosts use the profile), or the route's, which names one under a host with sign-in off. Naming below the switch, or in the same layer, is deliberate and silent.

**Validation codes:**

| Code | Meaning |
|---|---|
| `authProvider.forwardAuth.required` | No kind set. |
| `authProvider.url.invalid` | Absolute http or https, no credentials, no fragment, at most 512 characters. |
| `authProvider.timeoutSeconds.invalid`, `authProvider.cacheSeconds.invalid` | Out of range. |
| `authProvider.headers.tooMany` | More than 32. |
| `authProvider.header.invalid` | Must be an RFC 9110 token the proxy does not own. `Host`, `Content-Length`, the hop-by-hop set, `Forwarded`, `X-Forwarded-*`, `X-Real-IP` and `X-Request-Id` are refused. A prefix is allowed only in the response list and at least three characters. |
| `authProvider.header.credential` | A response name that is, or as a prefix would own, `Authorization`, `Cookie` or `Proxy-Authorization`. See [The answer](#the-answer). |
| `authProvider.signIn.invalid` | A path on the host or an absolute http(s) URL, never a `//host` reference. |
| `authProvider.signIn.returnParameter.invalid` | Bad return parameter. |
| `host.authProvider.unknown`, `route.authProvider.unknown` | Reference to a provider that does not exist. |

The compiler checks every value again (`ForwardAuthUrls` and `ForwardAuthHeaderNames` in Core are shared).

Canonical JSON orders providers by ID and trims and sorts the header lists unique ignoring case. It writes a preset of custom as none, and keeps an empty response list (it says "none" where an absent one says "the preset's"). A stored document without `authProviders` reads as none. The golden hashes changed only by the empty list.

### The presets

Each is what its project documents, and nothing more.

| Preset | Endpoint and headers | Cache scope | Notes |
|---|---|---|---|
| **authentik** (the proxy outpost) | `/outpost.goauthentik.io/auth/traefik` appended to a bare address. `X-authentik-*` copied upstream. | `host` (a decision per application) | The outpost's own `/outpost.goauthentik.io/` has to be routed on the protected host with sign-in off, for the callback. |
| **Authelia** | `/api/authz/forward-auth`. `Remote-User`, `Remote-Groups`, `Remote-Name` and `Remote-Email`. | `request` (a decision per path and method) | Its access-control rules match resources, methods and networks. |
| **oauth2-proxy** | `/oauth2/auth` (it answers 202 or 401). `X-Auth-Request-User`, `-Email`, `-Groups` and `-Preferred-Username`. | `host` | Its bare 401 is turned into a redirect of a browser navigation to `/oauth2/start?rd=<the original URL>` (the nginx `error_page 401` pattern). oauth2-proxy has to be routed at `/oauth2/` on the host, with sign-in off. |

- `X-Auth-Request-Access-Token` is not copied by default. An operator who wants the token passed upstream adds the name.
- **`Authorization` cannot be named.** `--set-authorization-header` sends the ID token back in an `Authorization` response header, which nginx's `auth_request_set` copies over the client's.
  - A provider's identity headers are *added to* the request and *removed from the client's* on every route of the host (see [The answer](#the-answer)). So listing `Authorization` would strip the client's bearer token on the routes with sign-in off, and `Cookie` would break sign-in everywhere.
  - Replacing it only on the routes behind the provider was the alternative, and it was not built. It would make "the client's `Authorization` and cookies are forwarded as they were" untrue on guarded routes, and would cache a token for the allow's lifetime.
  - `X-Auth-Request-Access-Token` does the job for an upstream that can read it from another header. The preset copies no credential header.

### The call (`Paduraksa.Proxy.ForwardAuth`)

- It is a `GET` with no body, as Traefik's forward-auth and nginx's `auth_request` send. The original method travels as `X-Forwarded-Method`, and a request body is never read or sent.
- **Headers sent.** The standard set of client headers (`Cookie`, `Authorization`, `Proxy-Authorization`, `User-Agent`, `Accept`, `Accept-Language`, `X-Requested-With`) and the provider's own additions, **and no other client header**.
  - It is an allow-list rather than a deny-list. Hop-by-hop framing, a client's own `X-Forwarded-*` and whatever the client invented never reach the provider, and what is sent is what the operator can read in the editor.
  - The original request is told in `X-Forwarded-Method`, `-Proto` (the trusted scheme, the one the redirect uses), `-Host` (the `Host` as received), `-Uri` (the **raw** target as the client sent it, so two targets that decode alike stay different) and `-For` (the resolved client, exactly one address). `Host` is the endpoint's.
- **The client** is built per provider configuration and made on first use, so compiling opens nothing. `SocketsHttpHandler` settings: no redirects, cookies or environment proxy; at most 64 connections; connections idle 30 s and live 5 min; the response decompressed.
- **TLS** to an `https` endpoint is the platform's default validation. There is no "accept any certificate" option, so an internal endpoint is `http` on a private network or has a certificate the host trusts.
- The endpoint is the operator's own address. Nothing a client sends chooses it, so there is no request forgery to a client-chosen URL.

### The answer

**2xx lets the request on.** The provider's response headers (exact names, or any with a listed prefix) are collected: at most 64, and a value the upstream cannot carry is dropped (see below).

**Identity headers and stripping.** On the upstream request (`ForwardedHeadersTransform`), every client header that matches one of the provider's names is **removed first**, and the provider's are added. On a protected route an identity header can therefore only come from the provider, also for an allow with no headers.
- **Stripping is a property of the host, not of the route.** `CompiledHost.IdentityProviders` is every provider the host's routes ask: the host's own, resolved, and any a route names. The transform removes the identity headers of all of them on **every** route of the host, a route with sign-in off (the provider's own `/outpost.goauthentik.io/` or `/oauth2/` pages) included. A client's forged `Remote-User` cannot reach an upstream through the one route that skips the check.
- **`_` and `-` are one character** when deciding what a provider owns, for exact names and prefixes, ignoring case. A CGI or WSGI upstream folds `Remote_User` and `Remote-User` into the same variable, so the client's `Remote_Groups` or `X_authentik_username` is removed as the provider's `Remote-Groups` or `X-authentik-*` would be.
- A host with no provider asks none and strips none.
- The client's `Authorization` and cookies are forwarded as they were, since the upstream may want them. No provider can own them (`authProvider.header.credential`), so stripping a provider's identity headers never touches them.

**Refusals are relayed.** A 401, 403 or 407 and a 301, 302, 303, 307 or 308 are relayed. What is relayed: the status, the body (at most 64 KiB, with its content type and `Cache-Control: no-store`), `Location`, every `Set-Cookie`, `WWW-Authenticate` and `Proxy-Authenticate`, and nothing else from the provider's response. A redirect without a `Location` is a failure.

**A 401 to a browser navigation** (a `GET` or `HEAD` that accepts `text/html` and is not a script's XHR) goes to the configured sign-in URL instead. The original address is percent-encoded in the return parameter, as the one `Location` (the provider's own is replaced, never added to).

**A request for the sign-in page itself is never redirected.** When the request path (in the canonical matching view, ignoring case and a trailing `/`) is the sign-in path (a relative URL, or an absolute one on the request's own host), the provider's 401 is relayed as it is. Otherwise a page behind the same provider would send the browser to itself with `rd=` nested in `rd=` each time.
- The mapping warns when that could happen (`authProvider.signInUnreachable`): a provider's sign-in path on a host (a relative URL, or an absolute one whose host is one of the host's names, a wildcard name covering its subdomains) whose first route to take it, in matching order, is behind a provider too, or none takes it.
- A route takes a request by its match kind and path alone. The model has no method or header matcher, so the route the warning finds is the one a GET and a POST, with any headers, both reach.

**Relayed headers.** Every relayed header (`Location`, `Set-Cookie`, `WWW-Authenticate`, `Proxy-Authenticate`) must be printable ASCII or a tab. A control character would split the message, and a byte Kestrel cannot write would make it throw on the way out, which would be a 500. So a provider that sends one is a **502** and an error.

**Identity values may be any text but control characters.**
- An authentik user's `X-authentik-name` can hold "Jose" with an accent or Japanese text, and refusing it would answer 502 to that user on every uncached request.
- .NET 10's `SocketsHttpHandler` writes request header values as ASCII unless it has a `RequestHeaderEncodingSelector` (the default throws `Request headers must contain only ASCII characters`, which YARP answers as a 502). It reads response header values as Latin-1 unless it has a `ResponseHeaderEncodingSelector`, so a UTF-8 name would arrive as one character per byte.
- Both ends are therefore UTF-8, as Kestrel reads a client's headers (UTF-8, and a 400 for bytes that are not). The provider's client decodes a response as UTF-8. The forwarder's handler (`PaduraksaForwarderHttpClientFactory.ApplyDefaults`) writes every upstream header value as UTF-8, so the name reaches the upstream as the bytes the provider sent (authentik and Traefik pass them as they are).
- A UTF-8 encoding of non-ASCII text never contains a CR, LF or NUL byte, so it cannot split the message.
- A value with a C0 control character other than tab, DEL, U+FFFD (the decoder's mark for bytes that were not UTF-8, which cannot be passed on as sent) or a lone surrogate **costs that one header, not the request**. It is left off and the request is let on. The client's own copy is removed with the rest (the provider owns the name, so it is never what the upstream reads instead). A warning names the provider and the header, never the value, at most once a minute per provider.
- Only a call that fails outright, or a provider that sends more than 64 owned headers, is a 502.
- Any exception the call raises that nobody listed (a handler's own, a value the transport refuses) is the same: counted as an error, answered 502, never an allow. Only cancellation (the client going away) and out-of-memory escape.

**Failure closes.**
- Any other status, an endpoint that cannot be reached or answers with something the proxy cannot forward, and a refusal body over the limit all answer **502**.
- A timeout is **504**.
- A client that goes away while the call runs is dropped without an answer and counts as neither.
- A request that finds 64 calls to the provider already running is **503** with `Retry-After: 1`.
- Each has a built-in page. A failing endpoint is logged once a minute per provider.

**`robots.txt` is not exempt**, unlike the rate limit and the proof-of-work gate. Auth protects what the upstream serves, and a managed robots.txt is answered before the route chain anyway.

### The decision cache

`ForwardAuthDecisionCache` is a fixed table of 4,096 immutable entries per provider. A probe covers four slots. A writer replaces the same key, else an empty or lapsed slot, else the one that expires first. Memory is bounded and nothing is swept.

**Only an allow is stored.** A refusal is the answer to a request with no session, and remembering it would keep a person at the login page after they signed in.

**The key** is the first 64 bits of SHA-256 over a per-process random secret and the length-framed fields:
- the grain;
- the client address (Authelia matches networks, and sessions can be bound to one);
- the scheme and the host;
- the whole `Cookie`, `Authorization` and `Proxy-Authorization` headers (a session cookie's name is the operator's, and an answer may depend on more than one);
- at request grain, the method and the raw target;
- **every other request header the call carries**: the standard hints (`User-Agent`, `Accept`, `Accept-Language`, `X-Requested-With`) and the provider's own `requestHeaders` (a custom provider that decides on `X-Api-Key`). Each is whole and length-framed by its number of lines, so an absent header and an empty one differ.

So two requests that differ in anything the provider is sent never share an entry, and a keyless client from the same address is never served another's allow. The key covers everything the call sends.

Until v0.25 the standard hints and the scheme were left out, on the reasoning that they only change how a refusal is shown. A provider is free to decide on them (a rule that refuses scripted clients, one that wants HTTPS), so an allow remembered for one would have been served to another. The price is more entries and calls for a browser whose `Accept` differs between a page and its images.

**The grain is a property of the entry, not of the request.**
- `request` scope stores every allow at request grain.
- `host` scope stores an allow at **host grain** (no method, no target) **only when the provider vouched for an identity**, that is when at least one identity header was copied from its 2xx. Otherwise it stores at request grain. A lookup probes the host-grain key, then the request-grain key, still 0 B.
- What a request carries cannot decide it. Any visitor can send an analytics cookie or junk in a header the provider reads, so "carries credentials" says nothing about whether the allow was for someone. An anonymous allow for authentik's "Unauthenticated Paths" `/public` would have been served for `/admin` too. Such an allow now stands only for the same address, host, method, target and credentials.
- **A provider configured with no identity headers therefore always caches at request grain, even under `host` scope.** It has no way to say an allow names someone, so each path costs a call once per time-to-live.
- A `host` scope **assumes the provider decides per identity, not per path** for the allows that name someone (authentik and oauth2-proxy decide per application). It is the wrong grain for one that decides per path (Authelia, and a custom endpoint, which defaults to `request`). The operator picks it knowing that.
- A provider whose anonymous allows also carry owned headers (a prefix such as `X-authentik-*` copies whatever the provider sends under it) would have them taken for an identity. The response list should name only headers that identify a user.
- The time-to-live is **the delay a revoked session keeps working**, and the dashboard says so.

**Why SHA-256 and not an HMAC.** The key is SHA-256 over the secret and the fields: not an HMAC, and not a vault key. The banned-symbol rule keeps keyed MACs in the vault, and a key that no one ever sees needs none (as the password verification cache is named). A restart empties the caches anyway, so the "forward-auth HMAC key" the vault design once listed is not needed.

**Per provider and per configuration.** `ForwardAuthRuntime.Bind` returns the process's state when the provider's hash is the same. The hash covers the endpoint, timeouts, scope, both header lists and the sign-in, **not the name**. A publish that changes something else keeps the cache and the pooled connections, and a changed provider starts empty.

**A compile changes no state in the process**, whether or not it goes live. A failed publish, a dry run, a draft and a simulation all leave the live states alone. The one trace is the counters entry `Bind` adds for a provider ID the process has not seen, which every later snapshot shares.
- The snapshot carries the states its routes run on (`RuntimeSnapshot.AuthProviderStates`). The publisher's `Swap` calls `ForwardAuthRuntime.Publish` with them just before the store swap, as it does the rate-limit registrations. That makes them the live ones and retires the states a snapshot replaced or dropped.
- A retired state stays reachable from the snapshots that still hold it. Its `HttpClient` is **disposed after the timeout plus ten seconds, and only once no call is running**: a timer on the runtime's clock, which waits and tries again while a call is in flight. A call that begins later than that fails closed (502).
- The state's in-flight counter is also its lifecycle gate. A call takes a place by compare-and-swap and refuses a negative value. The disposal moves 0 to a closed marker in one step, so no call can start on a client that is being disposed, and none can create the lazily made client after it.
- A retired state is held by the runtime until it is disposed, so its timer is never collected before it fires.

**A retired state can come back.** Two commits can overlap: C1 changes a provider and is still publishing when C2 compiles, reverts the change, and so binds the state the process still has. C1 then retires it and C2 publishes it.
- `Publish` first makes every state of the snapshot live again (the disposal cancelled, a tick already on its way ignored by a generation number).
- `Bind` never hands out a state that is retired or disposed.
- A state that was already disposed (the publish waited longer than the grace period) gets a new client and keeps its cache, rather than being replaced by a new object. Every snapshot that holds it, this one included, runs on that object.

### The gate (Defer)

- `ForwardAuthPolicy.Evaluate` hashes the key in a stack buffer (a pooled array past 2 KiB) and reads the cache.
- A **remembered allow is synchronous and 0 B**, as is the verdict that finds none and returns `PolicyAction.Defer`.
- The gate then awaits `IDeferredPolicy.CompleteAsync`, the one call, and resumes the chain after the policy (the rate limit has already run, proof of work follows), exactly as an access list's password check does.
- The call allocates what an outbound HTTP request allocates. That is accepted, and is the reason allows are remembered. No number is claimed for it.
- Proved 0 B through `GateMiddleware.Decide` for a remembered allow, the deferring verdict and a request too large for the stack buffer, in both scopes, for a hit at either grain under `host` scope, and for the miss that probes both. Also on Kestrel (`AForwardAuthRouteAllocatesNothingOnKestrelOnceTheAllowIsRemembered`).
- A call is bounded three ways: 64 at once per provider (a lock-free counter, so no queue), the provider's timeout, and the client's own abort.

### Counters

`ForwardAuthCounters` (allowed, denied, redirected, cache hits, calls, errors, timeouts, busy) are process-lifetime `Interlocked` longs per provider. The runtime keeps them and the compiled provider carries them, so they survive a publish, and a simulation counts nothing.

### Simulator

- The policy's step names the provider and its endpoint and says "would call provider '...'" (a GET with the original request in `X-Forwarded-*`; a 2xx lets it on, a 401, 403 or redirect is relayed, anything else refuses). When the cache holds an allow for these credentials, it says how many seconds are left.
- It gives that as a sentence and as facts (`provider`, `endpoint`, `outcome` `wouldCall` or `cached`, `cacheScope`, `cacheSeconds`).
- It **makes no call** and shows the request going on, because the answer is not known. The original design's "an explicit opt-in may make the real call" was not built: a simulator that sends a probe's credentials to an authentication service is a side effect of its own.
- It reads the cache and nothing else: no call, no counter, no write or refresh.
- `ForwardAuthSimulationTests` check that against a fake endpoint that sees nothing, and compare the simulator's outcome with the real gate's for requests the endpoint allows.

### API, overview and dashboard

- `/api/v1/auth-providers` (list, get, create, replace, delete) has the same ETag, dry-run and 422 rules as the other objects, with `config.auth_provider.*` audit actions and an OpenAPI entry that says what a preset fills in. A delete of a provider a host still resolves to is refused.
- The security overview adds a **Sign-in** column: the provider a host asks, what it has let through and refused, its failed calls once there are some, and the routes that ask another provider or none. It also adds a card of the providers' summed counters.
- The dashboard has Security > **Sign-in**: the providers with their endpoint and who asks them, and a drawer. The drawer's preset says what it fills in, and its empty fields say what they will use: the wait, the time an allow is remembered, the resolved endpoint, and the preset's headers (which the person's own list can replace, even an empty one).
- The settings of a host, profile, the defaults and a route pick a provider by name in the Access section: inherit, none, a provider, or a staged "New auth provider..." that goes in with the host in one revision.
- `AuthProvidersEndToEndTests` drives all of it in a real browser against a real host and a fake endpoint, including a real request through the public listener, at 1280 and 390 px in dark and light.

### Deviations from the design, and gaps

- **Route chain only, no host chain.** A forward-auth check is never made before the HTTPS redirect, a 404 or a Redirect/Respond answer. A host that answers plain HTTP without forcing HTTPS is warned (`authProvider.insecureSession`). The admin host is never behind a provider (`authProvider.ignored`); a trusted-header admin login behind forward-auth (see [identity.md](identity.md)) is not built.
- **Keyed by the credentials, the address and the request**, not the "session-cookie hash" the design named. The whole `Cookie` header is hashed, so an unrelated cookie that changes costs a call, which is the safe side of the trade.
- **No single flight and no queue.** Identical cold requests each make their own call (they differ in target for `request` scope anyway). A request that finds the 64 places taken is refused 503 rather than waiting. A dead endpoint is bounded by the 64-call cap and the timeout, not by coalescing.
- **Allows only are cached**, an anonymous one at request grain only. A flood of distinct unauthenticated requests therefore reaches the endpoint at up to the cap. A rate limit before it (the order is fixed) is the defence.
- **No `Set-Cookie` relay on a 2xx.** A refusal's `Set-Cookie` is relayed but an allow's is not, so a provider that refreshes its session cookie on an allow does not refresh it through the proxy.
- `OPTIONS` preflights carry no credentials and are asked like any request. A CORS API behind a provider needs the preflight's route with sign-in off.
- No per-provider TLS trust setting (no pinning, no "accept any").
- A provider's own logout or `/oauth2/sign_out` is just a route.
- Native OIDC remains later (see [identity.md](identity.md)).

## Managed robots.txt

Built in M7.

- `HostSettings.Robots` (`Mode`, `CustomText`; built-in passthrough) inherits through the layers and has no route override. A host has one file, and every route shares it.
- `CompiledHost.RobotsTxt` is the ready `PrecomputedResponse` (`text/plain; charset=utf-8`, `Cache-Control: max-age=3600`), or null for passthrough.
- The two presets are encoded once for the process (`src/Paduraksa.Proxy/Gate/RobotsResponses.cs`). A custom text is encoded once per host at compile time, with `\r\n` and `\r` normalised to `\n` and a final newline ensured. The stored document keeps what the operator typed.
- **Where it answers.** The gate answers `GET`/`HEAD /robots.txt` after the host policy chain and the HTTPS redirect, before a Redirect/Respond host's fixed answer and before route matching.
  - An IP allow list or a redirect still comes first.
  - Those hosts can carry a file.
  - The route chain (rate limit, proof of work) never sees it.
  - Any other method falls through as an ordinary request.
- The path is the canonical matching view routes use, so a redundant spelling is the same file, and a query string does not matter.
- The answer is 0 B per request (`RobotsTxtGateTests` measures it).
- The AI crawler list lives once in `Paduraksa.Core.Model.RobotsText`, shared by the proxy and the dashboard copy. It goes stale as crawlers appear, so it is refreshed with each Paduraksa release rather than by an operator who would have to notice.
- The simulator reports a `robotsTxt` step either way: the mode served, or that the path goes on to the upstream.

## Error pages

### The built-in pages (M7)

- `HtmlPageTemplate` (`src/Paduraksa.Proxy/Pages`) builds the shared layout once: doctype, meta, the brand style, the logo, a centred card and the footer. It pre-splits it into byte segments around its named slots.
- Writing a page copies the segments into the response body pipe and HTML-encodes each slot with `HtmlEncoder`'s span overloads. The template's own work is 0 B.
- The CSP is hashed once from the inline style and script: `default-src 'none'`, the style hash, no script on a page without one, `form-action 'none'` unless the page has a form, and `frame-ancestors 'none'`. `Referrer-Policy: no-referrer` and `X-Content-Type-Options: nosniff` ride along.
- The proof-of-work challenge page uses it too, with the challenge, return target and difficulty as slots. A challenge request no longer builds a per-request body array.
- `ErrorPages` holds the built-in set: 403 (IP access, no JavaScript, failed check), 404, 413 (also a body over a host's limit), 414, 421, 429, 431, 502, 503 (no healthy destination, and the proof-of-work key unavailable) and 504.
- Each page has a muted status line, an h1, one sentence and the request ID in monospace. All ASCII, with no script or external resource.
- A request whose `Accept` includes `text/html` gets the page. Anything else gets the same plain text as before. `HEAD` gets headers only. `Cache-Control: no-store` and `Retry-After` are unchanged.
- YARP's upstream errors (502, 503, 504) get the page from the response-start hook that already re-applies the gate's headers. It runs after the forwarder, does work only when `IForwarderErrorFeature` is set, and adds no wrapper, continuation or allocation to the successful path.

### Custom error pages (M8)

`HostSettings.ErrorPages` is a layered setting: built-in, global defaults, profile, host. Each **entry is replaced whole** by a layer that sets it (a heading from one layer and a document from another make no page). A status no layer sets is served from its built-in page.

**Entries.**
- The statuses are the ones `ErrorPages` has built-ins for (403, 404, 413, 414, 421, 429, 431, 502, 503, 504) and the maintenance page.
- The document keys them by status (`"errorPages": {"404": {...}, "maintenance": {...}}`). The contract names them (`NotFound`, ...).
- An entry is a **message** or a **document**:
  - a *message* is a heading of up to 120 characters and a sentence of up to 500, each on one line. It is rendered into the built-in layout, so it keeps the strict hashed CSP and the request-ID line;
  - a *document* is the operator's whole HTML, up to 64 KiB of UTF-8, with no control characters.

**Variables** are `{status}`, `{statusText}`, `{requestId}`, `{host}` and `{cfRay}`.
- `ErrorPageText` (Core) is the one place the syntax lives, for the validator, the compiler and the dashboard.
- A variable is `{name}` with ASCII letters and digits. Any other brace is literal, so CSS (`a{color:red}`) needs no escaping. `{{` writes a literal `{`.
- An unknown name is `errorPages.variable.unknown`, and the message lists the allowed ones.

**Validation codes** are flat: `errorPages.heading.required`, `.tooLong`, `.invalid`, `.unexpected`; the same for `text` and `html`; and `errorPages.html.required`. The status is in the path (`/settings/errorPages/404/heading`). The repo's code convention wants lower-camel segments, so `errorPages.404.*` would have failed it. The compiler checks the same rules as a backstop.

**Compile time.** Each page is split into byte segments around its variables. The literal parts are HTML-encoded (a message) or kept as written (a document). The status and its name are folded into the bytes, since a page belongs to one status. `{requestId}`, `{host}` and `{cfRay}` are the only runtime slots.

**Serving.**
- The answers with a built-in page (one 404, one 429, ...) are shared by every host. The host's own page is therefore looked up **when the answer is written** (`ErrorPageSelection`), from the host the request matched, or from the global defaults' pages for an unknown host.
- The writer streams the segments into the response `PipeWriter`, HTML-encoding the three values. It is 0 B per refusal.
  - The host is cut at 253 characters and the ray at 64.
  - The ray is empty unless Cloudflare's headers are trusted, as in the access log.
  - Everything non-ASCII becomes an entity.
- The Accept, HEAD and plain-text behaviour, `no-store` and `Retry-After` are unchanged.
- A proof-of-work step's own refusals (`BrowserRequired`, `CheckFailed`, `PayloadTooLarge`, `CheckUnavailable`) and the 401 of a basic-auth list are not customizable. They say what the visitor must do.

**The document's CSP** is `default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`, with `Referrer-Policy: no-referrer` and `X-Content-Type-Options: nosniff`.
- Decided: the operator's own inline style and `data:` images and fonts work, and nothing runs or loads from elsewhere.
- There is no script at all, because the page is shown to anyone who can make the host fail.

**Preview.**
- `ErrorPagePreviews` (Proxy) compiles and writes a page with the proxy's own compiler and writer and sample values.
- `ErrorPagePreviewer` (Application) validates by the saving rules and wraps it.
- The dashboard (directly) and `POST /api/v1/error-pages/preview` return the document and its CSP, shown in an iframe with `sandbox=""` and `srcdoc`.
- A test holds a preview and a served page to the same bytes (`CustomErrorPagesGateTests.APreviewIsTheBytesAVisitorGets`).

**Simulator.** The outcome says which page a browser gets (`errorPage`: built-in or custom, its mode and the layer it came from).

### Maintenance mode (M8)

`HostSettings.Maintenance` merges field by field:

| Field | Meaning |
|---|---|
| `enabled` | Built-in off. |
| `status` | 503 (built-in), 200, 500, 502, 504, 403, 404 or 410. |
| `retryAfterSeconds` | 0 sends none. Up to a week. |
| `bypassAccessListId` | A named access list. |

The page is the host's `maintenance` entry, else a built-in message ("Down for maintenance"), compiled for the status.

**Where it sits.** After the host's policy chain, the request limits, the HTTPS redirect and the managed `robots.txt`. Before a Redirect/Respond host's fixed answer, the proof-of-work verify endpoint and route matching. It therefore covers every host kind but admin.

Decided:
- **After the host chain**, because a refused client must still be refused and learn nothing about an outage.
- **After the redirect**, because plain HTTP is sent to HTTPS first.
- **After robots.txt**, because a crawler that reads a managed file should not be told the whole site is gone. A passed-through robots.txt reaches the upstream's place in the chain and gets the 503, which is what a crawler should read during an outage.

**The bypass** is the named list's **address side**.
- A request skips the page when the list's allow rules (addresses, and countries in a Cloudflare mode) admit it and its deny rules do not refuse it.
- A list with only deny rules, or a request outside every allow rule, is served the page.
- A password is never asked for there. It is one way to express IP rules: no inline list.

**Compile and cost.** It is precomputed per snapshot (`CompiledMaintenance` on the host, null when off) and costs 0 B (`MaintenanceGateTests`).

**Admin host.** An admin host **never serves it**. The mapping drops it with `maintenance.adminIgnored` (a layer that turns it on for every host cannot lock the dashboard out), and the compiler refuses to carry it.

**Warnings and codes.** A bypass list with no allow rule warns (`maintenance.bypass.noAllowRules`). An unknown list is `host.maintenance.bypassUnknown`.

**Simulator.** It traces a `Maintenance` step for every request of a matched host: skipped (off), passed (bypassed, naming the list) or stopped.

### Replacing an upstream's error bodies (M8)

`ErrorPages.ReplaceUpstream` is `off` (built-in), `html` or `any`.

**How it works.** It is decided at response-header time by a YARP response transform (`ReplaceUpstreamErrorTransform`, after the host's response-header transform). It applies when the status has a page (the list above) and the content type is `text/html` (mode `html`) or anything (`any`).
- It answers through the same writer: the host's page or the built-in one, and plain text for a client that does not ask for HTML.
- It removes the headers that describe the dropped body (`Content-Length`, `Content-Encoding`, `ETag`, `Last-Modified`, ...). It keeps the status, cookies, `Retry-After` and the gate's own headers.
- It sets `SuppressResponseBody`, so YARP disposes the upstream response unread. Nothing is buffered or drained. `ReplaceUpstreamErrorTests.TheUpstreamsBodyIsDiscardedUnread...` has an upstream that would send 2 GiB and sees it cut off.

**Limits.**
- It never applies to an upgrade request, `text/event-stream`, `application/grpc*`, or a response that has started, however the host is set.
- The successful path pays one integer comparison and no wrapper, continuation or allocation (`ReplaceUpstreamErrorAllocationTests`).
- Decided: `any` also replaces an API's JSON errors, which is why it is a separate, explicit choice.
- YARP's own 502/503/504 (no upstream response at all) were already answered by the response-start hook and now use the host's page too.
- An admin host never replaces its upstream's errors, because the admin app answers API clients with JSON problems. The mapping turns it off there with `errorPages.replaceUpstream.adminIgnored`, as it does for maintenance.
- A disposed response closes that upstream connection, so an error storm with replacement on costs a new upstream connection per error. Draining a small declared body to keep the connection open is possible but not built.

### Error pages: left out and open (M8)

- Per-route pages.
- Localisation.
- Pages for the admin app.
- Statuses beyond the list (500, 400, 401). A basic-auth 401 carries `WWW-Authenticate`, and 500 and 400 are an upstream's more than the gate's.
- The security overview's per-host maintenance column. The hosts list shows a host in maintenance and has the wrench.

## Request limits

Built in M7.

### Settings

- `HostSettings.RequestLimits` (`maxBodySize`, `maxHeaderCount`, `maxHeaderBytes`, `maxUrlLength`) merges field by field through defaults, profile and host.
- `RouteSettings.RequestLimits` overrides the **body size only**. A route is not known when the headers and the target are judged, so the validator refuses the other three on a route (`requestLimits.route.*Unexpected`).
- Sizes are bytes in the document and typed with a unit in the dashboard.
- `maxBodySize` 0 is "no limit", said outright. Otherwise it is 1 byte to 1 TiB.
- The other bounds are in `RequestLimitBounds` (Core). The validator (`requestLimits.<field>.invalid`), the compiler's backstop, the dashboard and the listeners share them.

| Field | Built-in | Bounds |
|---|---|---|
| `maxBodySize` | 100 MiB | 0 (none), or 1 B to 1 TiB |
| `maxHeaderCount` | 100 | 8 to 128 lines |
| `maxHeaderBytes` | 32 KiB | 1 KiB to 64 KiB |
| `maxUrlLength` | 8 KiB | 256 B to 16 KiB |

**The built-ins.** The header and target limits are Kestrel's old defaults, so a host that sets none behaves as before.
- The body is the one change. The public listener had no limit at all (the owner's call until this setting existed).
- 100 MiB is three times Kestrel's own 30 MB, which breaks photo, document and package uploads. No limit at all lets one request fill a disk.
- A host that wants no limit sets 0 (in the global defaults for all of them). One that takes bigger uploads on one path sets its route.

### Kestrel's outer bound

Kestrel's defaults are 8 KiB for the request line, 32 KiB for the headers together, 100 header lines, 32 KiB for one field on HTTP/2 and HTTP/3, and 30 MB for a body (the listeners set that to null). They would refuse what a host with a higher limit admits.

`RequestLimitsGate.ConfigureTransport` therefore sets the outer bound from the same constants, each with 4 KiB of headroom so the gate answers before the transport can:
- **Request line:** 16 KiB of target plus the method and protocol (a method token has no length of its own).
- **Headers:** 64 KiB plus the longest target plus headroom. Kestrel sums names and values, and HTTP/2 and HTTP/3 count the pseudo-headers, so the target counts twice.
- **A single field** as large as all of them. One big cookie over HTTP/2 must not be reset by the server when HTTP/1.1 takes it.
- **128 lines.**

The hosts' built-ins sit below it, so raising the bound costs a default host nothing but the scan it already pays. The body limit stays null in Kestrel, and the gate puts the host's on every request.

### Where the gate checks

- **Body limit.** It is put on the request (`IHttpMaxRequestBodySizeFeature`, before anything reads the body) when the host is found. Even an answer that leaves the body unread (a redirect, a refusal) then cannot make the server drain past the limit. It is put again when the matched route has its own.
- **Target and headers.** After the host's policy chain, before the HTTPS redirect.
- **Declared body.** After the route's chain (a rate limit or a challenge answers first) and before the endpoint is set.

All three answer from precomputed pages (`ErrorPages`: 413, 414, 431, HTML for a browser, text otherwise), with the host's response headers like any gate answer.

What is counted:
- The target is the raw request target in UTF-8 wire bytes.
- The headers are counted as lines (a header sent twice is two) and bytes of names plus values.
- A body without a declared length (chunked, or HTTP/2 and HTTP/3 without one) is Kestrel's to cut off at the limit. It throws mid-read, which YARP reports as 413 to the client when the upstream has not answered yet. A response that has already started cannot change its status, so the client sees a reset stream (Kestrel's behaviour, not exercised here).

Checked on:
- HTTP/1.1 and HTTP/2 in `RequestLimitsEndToEndTests`;
- through `PaduraksaHost` in `PublicRequestLimitsTests` (under the limit forwards, over it is refused, and the upstream never receives the refused body, or more than the limit of a cut-off one);
- HTTP/3 in the Docker QUIC run (`PublicHttp3Tests.RequestLimitsHoldOverHttp3`).

### Kestrel's quirks, found by the tests

- On HTTP/1.1 a chunked body counts its chunk framing against the limit, so a chunked upload of exactly the limit is refused.
- When the gate or Kestrel answers while the client is still sending, the server closes the connection with data unread. A client may see the reset before the 413. A client that sends `Expect: 100-continue` gets the refusal first, since Kestrel sends the final status instead of `100`.
- A cut-off body logs a Warning from YARP's forwarder (the whole `BadHttpRequestException`), once per refused upload.

### Allocation

The checks allocate 0 B on every path, on Kestrel (`GateAllocationTests.RequestLimitsAllocateNothingOnKestrel`: allowed, with a body, and each refusal, on HTTP/1.1 and HTTP/2) and on `DefaultHttpContext` (`RequestLimitsGateTests.TheChecksAllocateNothing`).

- Enumerating Kestrel's request headers through `IHeaderDictionary` boxes the enumerator. So the scan copies them into an array with `ICollection.CopyTo`, which cannot be a `stackalloc` (it holds strings).
- The arrays come from a small pool of slots taken with `Interlocked.Exchange` and returned cleared. They are stocked by the first requests, then allocation-free whichever thread runs the gate. A `[ThreadStatic]` array cost 4 KB on every new thread's first request, which the allocation tests caught.
- Kestrel keeps `Content-Length` as a number and writes a new string for it on every copy (32 B on every request with a body). So the check takes the header out for the copy, counts it by hand (14 bytes of name and its digits) and puts it back in a `finally`.
- No bytes are summed once the target or the line count has decided.

### Simulator

`GateStepKind.RequestLimits` (contract `requestLimits`, "Request limits") is reported twice:
- the target and headers ("35 headers, limit 30: 431", with the sizes and the answer as facts);
- for a request that has a body (a declared length, or `Transfer-Encoding`), the body ("body 12 MiB declared, limit 10 MiB: 413", with where the limit comes from, host or route). A chunked body says the server cuts it off.

A simulated request declares a body with a `Content-Length` header, which must be one number (`simulation.request.headers`). `GateReason.RequestLimits` is the outcome's reason (a fixed answer). `SimulatorParityTests` sweeps every limit on both sides and both routes.

### Dashboard

- A **Request limits** section in the host settings (global defaults, profiles, hosts) with four rows, and "Body size on this route" in a route's settings.
- `SizeField` is the reusable size control: inherit (naming the layer below's size), "No limit" where 0 means that, presets, or a number with a unit (KiB, MiB, GiB) and the bytes that follow. A size no unit holds exactly (an API value of 1500 bytes) shows in KiB with decimals.
- `CountField` is the header lines' (empty inherits).
- The summary phrase reads "Request limits: body up to 10 MiB, 30 header lines, ...".

### Not built

- The body limit is not put on a request that is answered before a host is found (an ACME challenge, an unknown host's default answer). The server drains its unread body there up to Kestrel's own limit, which is none.
- A per-route header or target limit (judged before a route is known).
- A counter or security-overview column for refused requests. The overview's host table is already six columns wide, and the access log shows the 413, 414 and 431.
- A limit on an upgraded (WebSocket) connection, which Kestrel does not apply a body limit to.
- The proof-of-work verify endpoint keeps its own 2 KiB.


