# The hot path

The hot path is everything a request touches between the socket and the upstream: the connection stage, the TLS selector, the gate, the policy chains and the YARP hand-off. It runs under a strict budget (no DB, no locks, 0 B of allocation in our own code), and each step reads one immutable snapshot. This page describes the budget, the pipeline stage by stage, the response headers, connection limits, HTTP/3 and the request simulator that runs the same gate code.

Contents:
- [Budget and rules](#budget-and-rules)
- [Request pipeline](#request-pipeline)
- [Response headers](#response-headers-built-in-m6)
- [The connection stage](#the-connection-stage)
- [The listeners' limits](#the-listeners-limits)
- [Upstream timeouts](#upstream-timeouts)
- [HTTP/3 strategy](#http3-strategy)
- [Request simulator](#request-simulator-why-was-this-blocked)

The WAF policies themselves are in [waf.md](waf.md). Metrics, logs and analytics are in [observability.md](observability.md). Cloudflare handling is in [cloudflare.md](cloudflare.md).

## Budget and rules

The request path is held to a strict budget:
- No DB, no `await` on anything except I/O to the client or upstream, no locks (`lock`, `SemaphoreSlim`), no LINQ, no closures, no string building, no backtracking regex.
- **0 B** of per-request allocation in Paduraksa code, steady state, on keep-alive connections. Kestrel's and YARP's own allocations are out of scope but measured. Benchmark tests enforce it by asserting `GC.GetAllocatedBytesForCurrentThread()` deltas.
- Precompute everything at compile time: byte arrays for responses and headers, `SearchValues<string>` for substring sets, CIDR tries, country bitsets, redirect targets.
- Options validate themselves. An invalid value throws at `init` or construction, never later on the hot path. The snapshot compiler checks config first and reports it as an error.

**Spike S3 confirmed this** (`docs/spikes/S3-zero-alloc-gate.md`; .NET 10.0.13, YARP 2.3.0). The gate measured **0 B per request on H1 and H2** in every end-to-end run, and took 145 to 225 ns for the synchronous section. For realistic browser traffic the accepted costs are:

| | B/request |
|---|---|
| Kestrel itself | ~1 KB |
| Routing + YARP hand-off | 152 B |
| Whole proxied request | ~4.1 KB on H1, ~4.4 KB on H2 |

### Mechanisms adopted in M1

- **Pipeline.** The public app is built on the **generic host with an explicit pipeline**, not `WebApplication`:
  `Configure(app => { app.Use(gate); app.UseRouting(); app.UseEndpoints(e => e.MapReverseProxy(...)); })`.
  - YARP calls `AddAuthorization()` unconditionally, and `WebApplication` then auto-inserts `UseAuthorization()` *ahead of the gate*. That costs bytes and is a hazard if a `FallbackPolicy` is ever set.
  - A guard test with a spy `IAuthorizationPolicyProvider` asserts it never runs.
- **Gate shape.** One synchronous gate middleware. It decides what to do, and the response body is written *after* the measured section. Responses come from precomputed bytes plus shared header values set through the typed header properties.
  - The one exception is a policy that cannot decide at once (an access list's password check, `PolicyAction.Defer`). The gate awaits it after the decision and resumes the chain after that policy (see "Access lists" in [waf.md](waf.md)). Every synchronous path stays 0 B.
- **Per-connection state.** An immutable `ClientInfo`, set as a connection feature in `ListenOptions.Use`. It is shared by all H2 streams on the connection, so it must stay immutable. Never use `IConnectionItemsFeature.Items`: it is one non-thread-safe dictionary for all streams.
- **Per-request state.** One object cached in `IPersistentStateFeature` and reset each request. Stream reuse happens only on graceful end, so RST, cancels and Drop reallocate. That is accepted.
- **Host lookup.**
  - `Headers.Host[0]` (H2 `:authority` is already folded in), with the port stripped by span.
  - Two `FrozenDictionary` tables (exact names, wildcard parents), `OrdinalIgnoreCase`, looked up via `GetAlternateLookup<ReadOnlySpan<char>>`: 11 to 22 ns, 0 B. `Request.Host.Host` allocates when a port is present.
  - The compiler **rejects duplicate host names** (otherwise the last one silently wins) and canonicalises IPv6-literal keys.
- **Client-IP parsing.** Our own span parser into `UInt128`, because `IPAddress.TryParse` allocates 40 or 80 B. Ours is stricter than the BCL: no octal, hex, short forms, zone IDs or whitespace. M1 keeps the edge-case list plus a differential fuzz against the BCL.
- **Policies.** Sealed `IRequestPolicy` classes returning a 16-byte `PolicyDecision` struct.
- **Tests.** `AllocAssert.Zero(state, static lambda)` for synchronous code. In-process Kestrel tests assert that the gate's decision, the access log's completion callback and the reapply response-start hook allocate 0 B. Kestrel and YARP numbers are tracked in `bench/`, not asserted.
- **Benchmarks.** They run under workstation GC, because BenchmarkDotNet's `Allocated` column is wrong under Server GC with the in-process toolchain. Non-monotonic `GC.GetTotalAllocatedBytes` samples are discarded.

## Request pipeline

A request passes six stages, A to F. Stages A and B run per connection, C is the gate, D the policy chains, E YARP, F telemetry.

### A. Connection

TCP uses Kestrel `ListenOptions.Use`. QUIC has no such hook, so the same checks run inside the QUIC `TlsHandshakeCallbackOptions` callback, where `Connection.RemoteEndPoint` is available (spike S1).

- **PROXY protocol** (opt-in per listener, TCP only). It is parsed here, before TLS, so the real client IP feeds connection limits, IP deny and SNI-stage logic.
  - v1 (text) and v2 (binary) are both accepted.
  - Only from configured trusted source CIDRs. A header from anyone else drops the connection.
  - The header must arrive within a short timeout.
  - When enabled on a listener it is mandatory, never optional.
  - The parser is a package of its own, `Paduraksa.ProxyProtocol` (with `IpKey` and `IpParser`, which it needs and the whole proxy uses), because the same fuzzed, allocation-free parser is useful to any .NET project that sits behind a load balancer. It is BCL only, so Paduraksa uses it as it is: one namespace change and no conversion layer, so the hot path is the same code at the same 0 B. `Paduraksa.ProxyProtocol.AspNetCore` is a generic Kestrel `UseProxyProtocol` over it, for other projects; Paduraksa keeps its own `ClientInfoMiddleware` (client info, lockdown, its counters) on the same `ProxyProtocolReader`. Both ship on nuget.org with the release tag ([development.md](../development.md#publishing-the-nuget-packages)).
- **Global IP deny** (a CIDR trie over `UInt128`) aborts before TLS. IPv4-mapped IPv6 is normalised first: on a dual-mode `[::]` listener TCP reports `::ffff:a.b.c.d` while QUIC reports `a.b.c.d`.
- **Per-IP concurrent-connection cap** (fixed-size table, CAS). See [The connection stage](#the-connection-stage).
- **`ClientInfo`** is computed once per connection (address, source) and stored as a connection feature. The country is per request (see "Country rules" in [waf.md](waf.md)).

As built in M1:
- `UseClientInfo` runs on both TCP listeners. On HTTPS it goes through `PublicHttpsListenerOptions.ConfigureTcpConnection`, before `UseHttps`.
- On QUIC, `QuicClientInfoGate` attaches `ClientInfo.FromEndPoint` to the connection in the handshake callback, and H3 requests see it (verified end to end in Docker).
- `IpKey` stores IPv4 and IPv4-mapped alike, so TCP and QUIC agree.
- The parser, the reader, `IpKey` and `IpParser` live in `src/Paduraksa.ProxyProtocol` (namespace `Paduraksa.ProxyProtocol`), and their tests, the fuzz target and the benchmark follow the same project. The package's public API is listed in its `PublicAPI.Unshipped.txt`.
- **Choices in the package split.**
  - One namespace for the parser and the address types, so a user needs one `using`; two assemblies, not three, because a `Paduraksa.Net.Primitives` assembly would hold two types and add a package to install.
  - The package's middleware has its own options type (`System.Net.IPNetwork` for the trusted proxies, a callback for counters) rather than Paduraksa's `CidrSet`-based one: no shared code to share where the config glue differs, and no behaviour change for Paduraksa.
  - TLVs are skipped, not exposed: the reader consumes the header, and keeping the bytes would allocate for every connection.
- The global IP deny and the per-IP cap are built (see [The connection stage](#the-connection-stage)).
- The QUIC `ConnectionClosed` gap (no close signal for a handshake that fails past the callback) is why QUIC counts established connections only.

### B. TLS handshake

`TlsHandshakeCallbackOptions` on both TCP and QUIC returns a per-SNI `SslServerAuthenticationOptions`.

- **Certificate selection.** SNI is looked up in the snapshot and mapped to a PREBUILT `SslServerAuthenticationOptions`, deduplicated on (cert, host TLS policy, transport).
  - The compiler builds `RuntimeSnapshot.Certificates`. Each enabled host's DNS names (exact and `*.wildcard`; IP literals never arrive as SNI) map to its certificate (a `CertificateId`), or to the default certificate with the host's own TLS settings (min version, TCP ALPN, H3 on or off).
  - Certificate IDs resolve through `IServerCertificateResolver` (the vault's source since M3, a placeholder before).
- **Per-request re-check.** The handshake sees only the SNI's host, and clients coalesce connections across every name a certificate covers. So the host's TLS settings are re-checked per **request** in the gate (stage C, `ConnectionTlsCheck`).
- **Chains.** Each option holds a shared `SslStreamCertificateContext.Create(cert, chain, offline: true)`. It is the only way the intermediate is reliably sent. Without it, TCP may fetch the intermediate via AIA mid-handshake, and QUIC sends the leaf only.
- **ALPN.** `ApplicationProtocols` is ALWAYS set explicitly: `[h2, http/1.1]` or the host's ALPN on TCP, `[h3]` on QUIC. Kestrel writes ALPN into a returned options object whose list is null (both transports), which silently breaks the other transport if instances are shared. A unit test asserts non-null ALPN.
- **Unknown SNI.** Serve the default cert, or reject by THROWING from the callback (the client sees EOF or a TLS alert; the server stays healthy). We count rejections ourselves, because QUIC logs nothing.
- **Cipher suites.** OpenSSL's defaults, NO `CipherSuitesPolicy`.
  - On Linux, `SslStream` caches the OpenSSL context and resumes sessions only without a policy.
  - With the M1 AEAD+ECDHE policy, every new connection cost about 1.5x the CPU: 5.3k against 8.4k handshakes/s (bench `handshake`, 2026-11-10).
  - A host that must refuse TLS 1.2's older suites sets `MinimumVersion = Tls13`. QUIC (TLS 1.3) is AEAD-only already.
- **Client certificates** (per host). The chain is built offline, with no AIA, CRL or OCSP fetches: a client-controlled AIA URL would be SSRF. Authenticated Origin Pulls hosts use `CustomRootTrust` with the origin-pull CA (see [cloudflare.md](cloudflare.md)), as built in M7.

### C. Gate middleware

The gate is first in the pipeline. It runs these steps in order (as built in M1):

1. **Snapshot.** `SnapshotStore.Current`, one volatile read.
2. **Client IP.** The PEER is the PROXY-protocol source from stage A if present, else the socket peer. The trusted-proxy policy then applies to that peer as usual: only a peer in the trusted-proxies list may supply the client through `X-Forwarded-For` or `CF-Connecting-IP` (native Cloudflare handling is in [cloudflare.md](cloudflare.md)). Otherwise the peer is the client.
   - Built as `TrustedProxyClientAddressSource`: the connection's `ClientInfo` (else `Connection.RemoteIpAddress`) through the snapshot's global `TrustedProxyPolicy`, 0 B. Header fallbacks are counted.
   - The whole `ClientIpResolution` stays on the request state for policies and the forwarding headers.
3. **Trusted request scheme**, in the same step (`ForwardedScheme`, 0 B).
   - From a trusted Cloudflare peer: `CF-Visitor` (exactly `{"scheme":"https"}` or `{"scheme":"http"}`; anything else is invalid), else its `X-Forwarded-Proto`.
   - From a trusted generic proxy: `X-Forwarded-Proto`, only with `TrustForwardedProto` (default off).
   - Otherwise the connection's scheme.
   - The HTTPS redirect and the upstream `X-Forwarded-Proto` both use it, so a TLS-terminating proxy (cloudflared to :80, Flexible SSL) cannot loop the redirect.
   - Cloudflare over plain HTTP for a plain-HTTP visitor (Flexible) is counted and logged once.
4. **ACME challenge.** On plain HTTP, `GET`/`HEAD /.well-known/acme-challenge/{token}` is answered from `AcmeChallengeStore` (a `ConcurrentDictionary` read through a span lookup, 0 B) with 200 `text/plain`, before everything below. A token that is not published falls through to the normal pipeline (built in M3, see [vault-and-certificates.md](vault-and-certificates.md)).
5. **Host lookup** (span-based, no string allocation). An unknown host gets the Default behaviour.
6. **Response headers, stage 1** (built in M6; one list since the settings refactor). The host's `Server` operation, 0 B, before the TLS check, so a 421 carries it too. The unknown-host answer and an ACME challenge follow the global defaults' list.
7. **TLS policy check** (HTTPS). The host's `TlsPolicy` against the request's connection: HTTP/3 (QUIC, TLS 1.3) against `Http3Enabled`, HTTP/2 against an HTTP/1.1-only ALPN, `ITlsHandshakeFeature.Protocol` against the minimum version.
   - A weaker connection gets **421 Misdirected Request**, so browsers retry on a connection of their own.
   - It is a few comparisons on per-connection data, 0 B.
   - Authenticated Origin Pulls has its own step before this one (see [cloudflare.md](cloudflare.md)).
8. **Response headers, stage 2.** The host's `Alt-Svc` and `Strict-Transport-Security` operations, on the answers after the TLS check (forwarded or answered), 0 B. See [HTTP/3 strategy](#http3-strategy).
   - `Alt-Svc` goes on TCP HTTPS only (not over HTTP/3), and only for hosts with names in the certificate table (not IP-literal-only hosts).
   - HSTS goes only when the TRUSTED request scheme is HTTPS, and never for an IP-literal or loopback name (`localhost`, `*.localhost`, `127.0.0.0/8`, `::1`). That is decided by a span check on the Host header's name.
   - Ours replaces an upstream's own header. With HSTS off, the upstream's passes through untouched.
9. **Host policy chain** (stage D). It also guards the redirect and fixed answers: a blocked request gets 403, not 301.
10. **Host request limits** (built in M7, see [waf.md](waf.md)). A request target over its limit is 414. More header lines or bytes than allowed is 431.
    - These are answered here, after the host chain (a client it refuses learns nothing from sizes) and before the redirect (which would echo the target).
    - The host's body limit was put on the request when the host was found.
11. **HTTP-to-HTTPS redirect**, answered from precomputed bytes.
12. **Managed `robots.txt`** (GET/HEAD only), answered from precomputed bytes. It comes after the allow list (still refused above) and after the plain-HTTP redirect, but before the fixed answer below and before route matching. So a Redirect or Respond host can carry one, and the route chain never sees the file.
13. **Redirect and Respond hosts**, answered from precomputed bytes.
14. **Route resolution.** The per-host list, most specific first, on the canonical matching view of the path (see [config.md](config.md)). 404 if none matches.
15. **Route policy chain** (stage D): the route's own policies only.
16. **Declared body** (built in M7). A `Content-Length` over the route's (else the host's) limit is 413, after the route's own chain and before anything reads the body.
17. **Endpoint.** `HttpContext.SetEndpoint(YARP Endpoint from the same snapshot)`, so the later `UseRouting` skips matching.

### D. Policy chains

There is one chain per host and one per route. Each is an `IRequestPolicy[]` of sealed classes evaluated in order. Host policies run before a route is known, so `State.Route` is null for them.

The order is:
1. **AccessList** (IP and country allow/deny, then basic auth). As built, the host chain only refuses by address and country; geo is part of it.
2. **RuleSet** (path, method and user-agent rules). As built in M7 it comes after the host chain's address-only list policy and before a route's list that has users, address checks included (see [waf.md](waf.md)).
3. **RateLimit.**
4. *RequestLimits is not a policy.* As built, the gate checks it (step 10 and 16 above).
5. **Robots.**
6. **Auth** (forward-auth, OIDC later).
7. **ProofOfWork.**
8. Future: bot heuristics, header rules.

Each returns a `PolicyDecision` struct: `Continue`, `Respond(status, page)`, `Challenge` or `Drop`.

### E. YARP

By now the endpoint is set by the gate. YARP then runs affinity, load balancing, passive health and the forwarder, with Paduraksa transforms:
- `X-Forwarded-*`, `X-Real-IP`, request ID, security headers and custom headers (precompiled transforms).
- Upstream errors and no healthy destinations lead to custom error pages.

**Forwarding headers, as built in M1.** YARP's own `X-Forwarded-*` transforms are off. These headers are always OVERWRITTEN:

| Header | Value |
|---|---|
| `X-Forwarded-For`, `X-Real-IP` | Exactly one address: the resolved client. No chain is kept or appended. IPv4 as a dotted quad (also for IPv4-mapped peers), IPv6 without brackets. Left out if unknown. |
| `X-Forwarded-Proto` | The trusted request scheme from stage C (the same one the redirect uses). |
| `X-Forwarded-Host` | Host or `:authority` as received, port included. |
| `Host` | The client's Host or `:authority`. See below. |
| `Forwarded`, `X-Forwarded-Prefix`, `X-Forwarded-Port` | Removed (only a client could have set them). |
| every `cf-*` header and `True-Client-IP` | Removed unless `CloudflareHeadersTrusted` (see below). |

- **`Host`.**
  - YARP's default would send the destination's address. This was fixed after M6, so name-based upstreams, absolute URLs and trusted-domain checks work.
  - On an HTTPS upstream it is also the SNI and the name the upstream's certificate is checked against, unless the upstream's TLS trust is `Pinned` or `Any` (see [config.md](config.md)). `Any` is YARP's `DangerousAcceptAnyServerCertificate`. `Pinned` is a validation callback the client factory installs from the cluster's metadata (`UpstreamCertificatePins`); a changed pin list builds a new client.
  - The upstream's Host header mode (Original, Destination or Custom; see "Upstream Host header modes" in [config.md](config.md)) replaces it per route. SNI follows whatever is sent.
- **`cf-*` headers.** All of them are matched case-insensitively (`CF-Connecting-IP`, `CF-Visitor`, `Cf-Access-Jwt-Assertion`, `Cf-Access-Authenticated-User-Email`, `cf-ipcity`, and so on). They and `True-Client-IP` are removed unless `CloudflareHeadersTrusted` holds (Cloudflare mode and a trusted peer). A trusted generic proxy does not qualify. It is one scan of the outgoing header names, 0 B. Otherwise a LAN client could forge Cloudflare Access SSO past a tunnel.
- The address string is cached on the pooled request state. The rest are constants or Kestrel's strings.

### F. Telemetry

Always on and lock-free: counters and a latency histogram per host, status class and upstream. See [observability.md](observability.md).

## Response headers (built in M6)

The compiler stores one array per host, `CompiledHost.ResponseHeaders`. It holds one operation per header: `Alt-Svc`, `Strict-Transport-Security`, `Server` and the response `X-Request-Id`.

Each operation carries:
- its precomputed `StringValues` (or the request ID as its source);
- what to do with it: set, replace the upstream's, remove, leave to the upstream, or not sent (with the reason);
- when: after host lookup, after the TLS check, or with the answer;
- what has to hold: the connection is TLS, the trusted scheme is HTTPS, the name is no IP literal or loopback, not over HTTP/3.

A host that does not send a header still has its entry, so the trace can say why. The list is capped at 16 operations (`ResponseHeaders.MaxResponseHeaderOperations`), and the compiler enforces that with an error. The gate's per-request outcomes live in a stack buffer of exactly that size, so a longer list can never reach it.

**What the gate applies.** A stage of that list and nothing else:
- `Server` right after host lookup, so gate answers (403, the HTTPS redirect, Redirect/Respond, 404, 421) and forwards all carry it at 0 B.
- Then `Alt-Svc` and HSTS, so a 421 carries neither.
- The default is `Server: Paduraksa`, with no version. Kestrel's own header stays off on both listeners.
- An unknown host and an ACME challenge use the global defaults' list, which carries neither `Alt-Svc` nor HSTS.

**The forwarded path.** YARP 2.3's `HttpTransformer.CopyResponseHeaders` special-cases only `Strict-Transport-Security`. A destination's `Server` is concatenated with what is already there. So one response transform, `ResponseHeadersTransform`, loops over the same list after the copy:
- `Paduraksa` and `Custom` replace the client-facing value, `Remove` drops it, and `Upstream` leaves it.
- The response `X-Request-Id` is ours or gone, as the host's propagation mode says.
- It also runs when the forward fails (the 502/504 path has no proxy response) and before a WebSocket upgrade is accepted.

**The one YARP path that clears the response.** An upstream that sends headers and then fails before the first body byte gets `context.Response.Clear()` and a bare 502, without re-running the transforms.
- The proxy registers a static `Response.OnStarting` hook. It checks YARP's `IForwarderErrorFeature` and, only then, calls `GateResponseHeaders.Reapply`, which walks the whole list again. The host's `Alt-Svc`, HSTS, `Server` and response `X-Request-Id` all come back, 0 B.
- YARP sets that feature before the response starts on every answer that clears or bypasses the transforms: no healthy destination (503), a failed request or timeout (502/504), malformed response headers (502) and a response-body error (502).
- On a successful forward the transforms already applied the headers, so the hook does nothing. A bodyless upstream response such as a 204 or a 304 starts at the end of the request, after the transforms, and is no longer applied a second time.

The response `X-Request-Id` is the one header the decision leaves to the answer, because its value is the one string a request materialises. The trace reports it as such.

## The connection stage

Answers threat model G1 and G8. Everything here runs before TLS, at stage A.

### Settings

`GlobalSettings.Connections` (`ConnectionSettings`) is global only and the last field. It writes no key in canonical JSON while it sets nothing.

| Field | Meaning |
|---|---|
| `maxConnectionsPerIp` | Built-in 256. `0` turns the cap off. At most 100 000. `ConnectionSettings.DefaultMaxConnectionsPerIp` and `ConnectionsSpec.DefaultMaxPerIp` are the same number, pinned by a test. |
| `denyListId` | **A named access list.** Decided by the owner: one way to express IP rules, never an inline list. |

- Validation codes: `connections.maxConnectionsPerIp.invalid` and `connections.denyList.unknown`. The second means deleting the list the settings name is refused, like deleting one a host names.
- **Decision:** only the list's `ip.deny` addresses apply at the connection stage. Its allow addresses, countries and users are request rules that mean nothing before TLS. A list with such rules compiles with the warning `connections.denyList.requestRules` at `/settings/connections/denyListId`, so a rule that does nothing here is said, not silently ignored.
- The dashboard's Global defaults page has a Connections card: a count field with the built-in as its placeholder, and a select of the access lists. The compile warning shows where config warnings do.

### Compile

- `RuntimeSnapshot.Connections` is a `ConnectionPolicy`: the cap, and the list's deny addresses as the same `CidrSet` the host chains use. The compiler reads `CompiledAccessList.Deny`, so there is one lookup and one parser.
- A missing list or an out-of-range cap is a compile error, with the validator's paths for the same two.
- The bootstrap snapshot is `ConnectionPolicy.Off`. A compiled config always carries the cap.

### The table

`ConnectionTable` (`Proxy/Net`) is the per-address state. There is one per process inside `ConnectionRuntime`, never in a snapshot.

- 65 536 entries of one `ulong` (512 KiB). Fixed, lock-free, open-addressed with a probe window of 8, like the rate-limit table.
- An entry is a 40-bit tag of the key's hash and a 24-bit count, claimed and changed by compare-and-swap of the whole word. 0 is empty. An entry whose count has gone back to 0 keeps its tag (an address busy between two connections keeps its place) until another address needs it.
- The key is `RateLimitPartition.Shared.ForIp`: the whole IPv4 address (IPv4-mapped is the same key, so TCP and QUIC agree) or the IPv6 /64. It is hashed with the per-process random seed, so nobody can pick addresses that collide with another's.
- **A full window fails open.** The connection is let through uncounted and `ConnectionStats.TableFull` counts it.
  - A flood from very many addresses can fill the table. After that, new addresses are not capped (what Kestrel gives without the table), but memory stays bounded and nothing is refused that should not be.
- **Accepted race.** Two threads that both miss a new key and claim different entries give it two entries for that moment (up to twice the cap). A release takes from whichever entry of its tag holds a connection, so counts stay balanced. A concurrency test and a randomised full-table test end at 0.
- A count at 2^24 is treated as over the cap.

### Decision order (`ConnectionRuntime.Admit`)

1. **The deny list**, for every connecting address, **trusted proxies included**. It is the operator's explicit list, so listing a proxy's address refuses the proxy.
2. **Cap off, or the peer is a trusted proxy:** admitted and never counted. `TrustedProxyPolicy.IsTrustedPeer` covers Cloudflare's edge ranges in `CloudflareProxy`, the cloudflared address in `CloudflareTunnel`, and the listed proxies in `Generic`. Every visitor behind a trusted proxy shares its address.
3. **The table.**

Peers behind a trusted proxy are judged by their real address later, at the gate (access lists, rate limits), not here. The connection stage sees the connecting address. With PROXY protocol that is the PROXY source (`ClientInfo.Address`), so a load balancer's visitors are each their own address. Otherwise it is the socket's.

A deployment that puts another proxy in front without listing it as trusted makes every visitor one address, and the cap their shared limit. Set the trusted proxies.

### TCP

`ConnectionLimitMiddleware` runs after `UseClientInfo` and `UseOriginLockdown` and before `UseHttps`, on both TCP listeners.
- With the stage off (`IsInert`: no deny list and cap 0) it passes the connection on at 0 B.
- A refused connection is aborted unread. The handshake never starts. It allocates the abort exception, like the PROXY protocol and lockdown drops.
- A counted one is released when the rest of the pipeline ends, even if it throws.

Allocations:
- **0 B** for every decision and table operation.
- **0 B** for a connection that is not counted (the stage off, and every trusted-proxy connection, which covers all of Cloudflare's).
- **One release continuation of 177 B** (measured, Release build) for a counted connection, against Kestrel's own thousands of bytes per connection. A pipeline that has already ended is released without it.

### QUIC

The handshake callback (`QuicConnectionLimits.CreateGate`, wrapping the origin-lockdown gate) applies the deny list and **reads** the cap, but takes no place. Kestrel never signals a handshake that fails after the callback returned (`QuicConnectionGate`). A place taken there could leak for good, and a spoofed source address would then lock a victim out.

The place is taken when the connection is established, by a multiplexed connection middleware (`QuicConnectionLimitMiddleware`, added through `IMultiplexedConnectionBuilder` on the HTTP/3 listener). Kestrel runs it only for accepted connections and ends it with the connection.

**Consequence:** handshakes still in flight are not counted. A burst of simultaneous handshakes from one address can pass the cap by the size of the burst. The established connections cannot.

Tests: unit tests with fake contexts, and two `KestrelQuicTlsTests` cases on a real QUIC listener, run through `run-quic-tests.sh` in Docker (passing 2026-11-13). They cover a held HTTP/3 connection counted once established, a second handshake from the address refused, room again after the first closes, and a deny-listed handshake refused. In the same run `AltSvcAdvertisesThePublicPortOnTcpOnly` fails the same way on `main`, unrelated to this work.

### Observability

- `ConnectionStats` are process counters: refusals by deny list and by cap per transport, table-full, and the number counted now.
- They feed `/metrics` as `paduraksa_connections_events_total{event="refused_deny_list"|"refused_per_ip_limit"|"table_full"}` and the gauge `paduraksa_connections_counted`.
- They also feed the security overview (`SecurityOverview.Connections`) and its Connections card: limit, deny list linked to its page, refusals by reason, counted now, and a table-full line only once it has happened.
- Refusals are logged at Information, at most once per 10 s per reason (`ConnectionRuntime.LogInterval`), with how many there were and the latest address. A flood is not a log flood.
- The simulator's connection step refuses a deny-listed peer on both listeners, and on QUIC after the handshake's name selection (as the callback does). It states the cap and whether the peer is a trusted proxy. The count itself depends on what is open and is not simulated. It says when the simulated client is on the list but is only the visitor behind a trusted proxy ("judged at the gate").

### Tests and bench

- `ConnectionTableTests`: cap and release, address keys, IPv6 /64 and IPv4-mapped, a full window failing open, an idle address keeping its entry, balance under 8 threads on one address and on 200 addresses over 16 entries, 0 B.
- `ConnectionRuntimeTests`: compile, deny order, trusted proxies exempt in all three modes, the cap, QUIC read-then-count, the rate-limited log, 0 B on every path.
- `ConnectionLimitMiddlewareTests`: the judged address with PROXY protocol, release however the pipeline ends, the QUIC pieces, allocations including the 177 B.
- `PublicConnectionLimitsTests` (a real host): a denied address closed before TLS on both listeners, the cap closing the next connection and a closed one making room, a trusted proxy never counted, the options being Kestrel's limits.
- `ServerLimitsKestrelTests`, `ConnectionStageSimulationTests` (with parity against the runtime), and the validator, canonical, mapping, dashboard, metrics and overview tests.

Bench (`bench/run-baseline.sh`, Paduraksa only, two quiet runs each of this branch and the commit it was cut from, 2026-11-13):

| Scenario | This branch | Before |
|---|---|---|
| HTTP/1.1 | 171.6k req/s | 172.7k (-0.6%) |
| HTTP/2 | 152.0k req/s | 151.4k (+0.4%) |
| New TLS connection per request | 9.7k req/s | 10.1k (-3%) |

- The TLS difference is inside the 5% spread this machine showed on that scenario between two runs of the same build. One handshake is about 100 us of CPU, and the stage adds a hash and a few atomic operations.
- Bytes per request were 4.1 KB (HTTP/1.1) and 4.5 KB (HTTP/2) for both. Per new connection it was 18.8 to 19.9 KB against 18.5 KB (177 B of that is the release continuation; the second run of this branch was the noisy one, with a gen1 GC).
- A third and fourth pair were discarded because another job started on the machine.

### Left out

- A connection *rate* limit (new connections per second per address).
- Per-listener caps, and a cap on the admin listener (it has `AdminAllowedSources`).
- The QUIC handshake burst above.
- A table size setting (the size bounds memory, and the failure mode is open).
- Weighting the cap by transport.

## The listeners' limits

`ServerLimits` (`Proxy/Net`) is the one place that states them and sets them on Kestrel. The options are `Paduraksa:Limits:*` (`PaduraksaLimitsOptions`). They are validated at startup with every failure listed, are not reloadable, and apply to the public listeners only: the admin listener keeps Kestrel's own.

| Option | Default | Kestrel's own | Note |
|---|---|---|---|
| `RequestHeadersTimeout` | 15 s | 30 s | First byte of a request to the end of its headers. A slower client gets 408 and is closed. |
| `KeepAliveTimeout` | 120 s | 130 s | Idle wait for the next request on HTTP/1.1 and HTTP/2. |
| `TlsHandshakeTimeout` | 10 s | 10 s | TCP handshake, set explicitly on the callback options. |
| `MinRequestBodyRate` / `MinRequestBodyGrace` | 120 B/s, 10 s | 240 B/s, 5 s | 0 turns it off. A grace must exceed 1 s (Kestrel's rule, validated). |
| `MinResponseRate` / `MinResponseGrace` | 240 B/s, 10 s | 240 B/s, 5 s | Same. |
| `MaxConnections` | 10 000 | none | Kestrel stops accepting at it. 0 is unlimited. |
| `MaxUpgradedConnections` | 2 000 | none | WebSockets. A new upgrade is refused at it. |
| `Http2MaxStreamsPerConnection` | 100 | 100 | |

- HTTP/3 has no counterpart. Kestrel's stable API has no stream limit for it (the QUIC transport's is a preview API), so a QUIC connection keeps the transport's default of 100.
- The rate limits are more forgiving than Kestrel's (a longer grace) because a person on a poor mobile link is the slow client worth keeping. The headers timeout is stricter because headers are tiny.
- **Long-lived connections.** Kestrel stops enforcing the request-body rate on an upgraded connection, and times the response rate only while a write waits for the client. An idle WebSocket or an event stream is therefore not cut by Kestrel (the upstream's idle timeout is a different limit; see [Upstream timeouts](#upstream-timeouts)). `ServerLimitsKestrelTests` runs both with rates and graces far stricter than any default, together with a slowloris (headers never finished: 408), a byte-by-byte header dribble and a slow upload (both cut).

## Upstream timeouts

Built in the upstream-timeouts branch. The listeners' limits above judge the client; these judge the upstream, and each upstream has its own.

**The bug it fixed.** Nothing set YARP's `ForwarderRequestConfig.ActivityTimeout` for a public cluster, so YARP's default of **100 s with no data in either direction** applied. It applies to an upgraded connection and to a streamed response too, so a Home Assistant dashboard, a chat app or an event stream with a quiet gap over 100 s was cut by the proxy. `ServerLimitsKestrelTests` could not see it: it only proves Kestrel's limits leave an idle stream alone. The connect timeout was also a fixed 10 s for every upstream.

**The setting.** `UpstreamConfig.Timeouts` is `{connect, idle}`, optional, and each value falls back to its default on its own. Canonical JSON writes no block when both are unset, so stored revisions keep their bytes.

| | Default | Range | Means |
|---|---|---|---|
| `connect` | 10 s (as before) | 100 ms to 5 min | TCP connect and TLS handshake to a destination. |
| `idle` | 1 hour | 1 s to 24 h; `00:00:00` = never | No data in either direction on a request, a response stream or an upgraded connection, YARP's activity timeout. Any data in either direction, and the arrival of the response headers, starts it again. |

Validator codes: `upstream.timeouts.connect.invalid` and `upstream.timeouts.idle.invalid`. The compiler checks the same ranges as a backstop.

**The default idle timeout is 1 hour.** Decided:
- **A quiet connection that is still wanted must not be cut.** The cut is a surprise to the person, since nothing on the page says why a terminal or a dashboard stopped. Apps that care ping every few seconds to a few minutes (Home Assistant, socket.io, ActionCable, ttyd, Jupyter), and a ping is data, so it resets the timer. What reaches the timeout is a connection with no heartbeat at all: a session someone left open, or a peer that vanished without a FIN.
- **An hour is the cost of that choice.** A vanished peer holds one of the 2 000 upgrade slots (`MaxUpgradedConnections`) and some memory for up to an hour, until a write fails or the timer fires. Shorter would cut the idle terminal during a meeting; "never" would leave the dead peer to TCP alone. An operator who knows their traffic sets it per upstream.
- **A request nobody answers also waits an hour.** One timeout covers a request, a stream and a tunnel, so the old 100 s that protected a stuck request is gone with the one that cut a WebSocket. A browser gives up on its own long before, and what the proxy holds meanwhile is one request. A time to first byte would separate the two; see "Left out".
- **What others do.** nginx's `proxy_read_timeout` is 60 s between two reads, and it is the famous cut of idle WebSockets (the fix is a longer value or pings from the app). Caddy's `reverse_proxy` has no idle limit on a stream by default (`stream_timeout` is opt-in). Traefik has none on a response either (`forwardingTimeouts.responseHeaderTimeout` is 0, and its 180 s entry point `idleTimeout` is for keep-alive connections between requests); its dial timeout is 30 s. Paduraksa sits between: bounded, but long enough to cost nobody a live session.

**Where it applies, at 0 B per request.**
- The compiler gives every public cluster a `ForwarderRequestConfig` with `ActivityTimeout` set, the default included (leaving it out is the bug). `00:00:00` compiles to `Timeout.InfiniteTimeSpan`, which YARP accepts. The forwarder reads the cluster's config it already holds, so nothing is added to a request.
- The connect timeout is a property of the HTTP client and `HttpClientConfig` has no field for it. A value other than the default rides in the cluster's metadata (`paduraksa.connect-timeout-ms`, beside the pins and the custom Host name), and `PaduraksaForwarderHttpClientFactory.ConfigureHandler` sets `SocketsHttpHandler.ConnectTimeout` from it. The default is no entry, so a cluster that sets neither has the metadata it always had.
- `CanReuseOldClient` compares the connect timeout besides the client config and the pins. An edit that changes it builds a new client (a cluster change that drops the warm pool); an edit to anything else keeps the client, and so does a change to the idle timeout, which is the cluster's, not the client's.
- A change applies to connections and requests made after it. One already running keeps the timeout it started with.
- `SocketsHttpHandler.ConnectTimeout` also covers the TLS handshake (`ATlsHandshakeThatNeverCompletesFailsAtTheConnectTimeoutToo` pins it), so `connect` means what its name suggests.
- A request cut by either timeout gets YARP's 504, with the gateway-timeout page.

**Tests.**
- `UpstreamTimeoutsTests` runs a real proxy (Kestrel, YARP, the Paduraksa client factory) in front of a real upstream, with a 2 s idle timeout standing in for the hour:
  - an idle WebSocket is cut at about 2 s, on HTTP/1.1 and on h2c;
  - one that keeps sending lives past it;
  - one quiet for 4 s survives the default and the explicit "never";
  - an event stream with an 8 s gap is cut at about 2 s and one with a 3 s gap under the default is not;
  - a request the upstream never answers gets a 504 at about 2 s;
  - a change published to the running proxy applies to the next connection.
- Connect: a listener whose accept queue is full (so a connect hangs, like a blackholed address, without depending on the network) fails at the configured 1 s and not at 10 s; a silent TLS server does too; republishing 8 s as 1 s rebuilds the client.
- `PaduraksaForwarderHttpClientFactoryTests`: reuse when nothing changed or only unrelated metadata did, rebuild when the connect timeout changed. `SnapshotCompilerTests`: what compiles onto the cluster, and the backstop errors. Unit: validator, canonical form, mapping, YAML.

**Left out.**
- **A time to the response headers (first byte).** YARP has no setting for it: the activity timeout also restarts at the headers, and a separate one would be a timer of our own on the forward path (a `CancellationTokenSource` per request), against the budget. Decided against for now.
- **Separate idle times for a request and a tunnel.** The one value would need a per-route or per-protocol split YARP does not offer.

## HTTP/3 strategy

**Spike S1** (`docs/spikes/S1-http3-sni.md`) showed that per-SNI selection and hot-swap work on Kestrel's QUIC listener in .NET 10. It works through `ServerCertificateSelector`, `TlsHandshakeCallbackOptions` and `ServerCertificateSelectionCallback` alike. So every host that enables H3 gets it, and the single-H3-cert fallback is not needed.

**Listener layout:** two `Listen` calls on :443 (dual-mode `[::]`).
- TCP: `Http1AndHttp2`, `DisableAltSvcHeader = true`, then the connection middleware (PROXY protocol, IP gate), then `TlsHandshakeCallbackOptions`.
- UDP: `Http3` with its own `TlsHandshakeCallbackOptions`, which also performs the stage-A gate. It is added only if `QuicListener.IsSupported`.

**Builder.** The public app runs on the generic host with `UseKestrelCore()`, which leaves out Kestrel's HTTPS configuration and the QUIC transport. `UsePublicHttpsTransports()` adds both (`UseKestrelHttpsConfiguration()` and `UseQuic()`). Without them H3 listeners fail at startup. Spike history: S1 found this under `WebApplication.CreateSlimBuilder`, which has the same gap.

**As built in M1.** `UsePublicHttpsTransports()` on the generic host's `UseKestrelCore()`, then `ListenPublicHttp` and `ListenPublicHttps` (see [hosting.md](hosting.md)). `run-quic-tests.sh` runs the QUIC tests of `Tests.Proxy` and an end-to-end H3 request through `PaduraksaHost` in Docker with libmsquic.

**Still open:** a real browser upgrading to H3 (needs a trusted cert), routable IPv6, and arm64 libmsquic. Carrying the QUIC-callback `ClientInfo` through to the request works: a feature set on the QUIC connection context in the callback is visible to the H3 request (`PublicHttp3Tests`, Docker).

**Either way, `Alt-Svc` is emitted by Paduraksa, not Kestrel.**
- Kestrel's automatic header is disabled (`DisableAltSvcHeader`).
- Ours is precomputed per host, only for H3-eligible hosts, using the **public** HTTPS port from settings (default 443), not the bound port. A port-mapped deployment therefore cannot advertise the wrong port.

H3 is off globally if `libmsquic` is not available (`QuicListener.IsSupported`). `paduraksa doctor` reports this. The stock `aspnet` image does not ship it: install `libmsquic` from packages.microsoft.com (about 21 MB including `libnuma1`; tested 2.6.3 on Ubuntu 24.04).

## Request simulator ("Why was this blocked?")

Admins test their rules before or after a change by asking what a request would get, and why. **The simulator runs the real gate code over a real compiled snapshot, never a reimplementation**, so its answer cannot drift from what production does.

### Design

- **Input.**
  - URL (scheme, host, port, path, query), method, and headers (including the user agent and cookies).
  - The peer IP plus how it arrives: direct, through a trusted generic proxy, via Cloudflare Proxy or Tunnel, with or without PROXY protocol.
  - The TLS context: the name sent in the handshake, ALPN and HTTP version, and TLS version.
  - The surfaces fill in sensible defaults, so a URL and an IP are enough.
- **Target config.** The live snapshot, any stored revision, or a *draft*: a document or pending command compiled through the dry-run path (steps 1 to 4 of the write path in [config.md](config.md)) and never published. "What would this change do to this request" is therefore answerable before committing. A user's open draft is simulated from its review, as one whole-document command.
- **Output.** The outcome (forward to upstream X, fixed answer, redirect, 421 or drop) plus the **trace**:
  - client-IP resolution (which header and hop were trusted);
  - host match (exact, wildcard or default);
  - the TLS-policy check;
  - the host's HSTS (sent, or why not: plain HTTP, an IP literal or loopback name, or off);
  - each policy in order with its verdict and reason;
  - the HTTPS redirect;
  - route match (which route, and the matching view of the path);
  - the upstream (cluster and the destinations currently healthy; load balancing is random, so not which one).
- **No cost on the hot path.** Gate steps report through a tracer passed as a **generic struct type parameter**. Production uses a no-op tracer, which the JIT removes entirely. The simulator uses a recording one. The 0 B allocation tests and the gate benchmarks guard it.
- **Stateful and outbound policies** are simulated without side effects. A rate limit reports "would allow, N tokens left" without taking one. Forward-auth reports "would call `<provider>`" (never the real call). Geo shows the country it resolved (the client step's `country` fact and the access-list step's).
- **Parity tests.** For generated configs and requests, the simulator's outcome must equal the real gate's. The M2 `CompiledBehaviourTests` harness is the seed. Each policy added later (M7) must add its trace step and parity cases.
- **Surfaces.** One Application function, `SimulateAsync(target, request)`. It is exposed as `POST /api/v1/simulate` (M4), as the MCP `simulate_request` tool (M10), and in the dashboard simulator page (M5). Access-log entries link into it with the request pre-filled (M9).

### As built (M4, simulator core)

**The tracer.** `IGateTracer` is passed as a struct type parameter to the one decision body, `GateMiddleware.Decide<TTracer>(context, snapshot, clientAddresses, challenges, tracer)`.
- Production's `Decide(context)` passes `NoGateTracer` (empty methods, so the JIT drops them).
- The Kestrel 0 B tests plus a direct `Decide<NoGateTracer>` allocation test guard it.
- The gate reports: client resolution (peer, source, trusted header, scheme source), the ACME hook, the host match (`HostTable.Find(..., out HostMatchKind)`), the TLS-policy check (`ConnectionTlsCheck.Check` names the 421 reason; `Satisfies` is `Check == None`), each policy by scope and index, the HTTPS redirect, fixed hosts, the route match with the canonical path, and the upstream hand-over.

**`GateSimulator.Run(snapshot, probe, challenges, listeners)`** (`Paduraksa.Proxy.Simulation`) runs the real `Decide` with the recording tracer, after the stand-ins for what the server does first, in the server's order:
1. **The listener's admission** (`ProxyProtocolAdmission.Check`, the connection middleware's own decision, from `ListenerFacts`). A PROXY header the listener does not expect, a missing one, an untrusted socket, or no HTTP/3 listener is `ConnectionRejected`.
2. **The TLS handshake** (`TlsHandshakeSelector.Select` over the snapshot's certificate table, which the listeners call too), plus the entry's own TLS-version and ALPN options: unknown SNI, HTTP/3 off, a TLS 1.2 client to a TLS 1.3-only name, the h2 to http/1.1 fallback.
3. **Kestrel's request checks** (`RejectedByServer`, 400):
   - on HTTP/1.1, anything but exactly one valid `Host`;
   - on HTTP/2 and HTTP/3, `:authority` is the host and a `Host` header is ignored;
   - the path is percent-decoded except `%2F` (a `%5C` becomes a backslash), escapes that are not UTF-8 stay as written, and dot segments are removed.
   All of this is checked against the real server over sockets in `SimulatorSocketParityTests`.

Other details:
- A snapshot not bound to YARP gets placeholder endpoints.
- Client resolution is `TrustedProxyClientAddressSource.ResolveCore` with no counters and no log line, so a simulation changes no counter.
- **Request limits** are the public listeners' outer bounds: 20 KiB of request line, 84 KiB and 128 headers, a token method of at most 32 characters, no control characters in the query, no backslash in the authority. The host's own limits are the gate's, traced as the **Request limits** step. They are counted as Kestrel counts them: the request line, URL and header names and values in UTF-8 wire bytes, so a character above ASCII costs more than one. The simulator is conservative where a header could be sent as Latin-1.
- A field that would change nothing (`transportIp` without PROXY protocol, `peerIp` on a direct request, `tls` on a plain-HTTP URL) is refused, not ignored.

**`RequestSimulator.SimulateAsync(target, request, origin)`** (Application) takes a target:
- live;
- `RevisionSimulationTarget`;
- `DraftDocumentSimulationTarget` (a `ReplaceConfigCommand` on the current head, retried once on a commit in between);
- `DraftCommandSimulationTarget`.

Drafts go through `ConfigService.PlanDraftAsync`, which is `AttemptAsync` as a dry run that hands back its compiled candidate.
- Validation, certificate rules and compile errors are the real command's, and are returned as `Invalid` with the dry run's errors.
- A stale version or missing object is `DraftRejected`.
- A command that changes nothing simulates the head it planned on.
- The same checks that reading applies (`apiVersion`, null list elements) run on any payload first.
- A corrupt head, a degraded store or an unreadable database is `Unavailable` with a reason, never an exception.
- Nothing is audited, stored or published.

**Concurrency.** At most 4 drafts or revisions compile at once (`RequestSimulator.MaxConcurrentCompiles`). One that waits more than 2 s is `Unavailable`. The live target takes no slot.

**Authorization.** `ISimulationAuthorizer` is asked for every target (see [identity.md](identity.md)).

**Parity tests.**
- `SimulatorParityTests` (Proxy): every outcome, 421 variants, all trusted-proxy modes and header shapes, PROXY protocol, ACME answered and fall-through.
- `SimulatedBehaviourTests` (Unit): 300 generated documents against the `GateHarness`.
- `SimulatorSocketParityTests` (real sockets).
- End-to-end tests through a real host.

### Stateful policies must not mutate when simulated (M7 rule)

The simulator calls `Evaluate` on the live policy instances. `RequestView.IsSimulation` is true on that path and a constant `false` in production (a static abstract member of the tracer type, folded away by the JIT).

A policy with state peeks:
- a rate limit reports "would allow, N tokens left" without taking one;
- a challenge issues no nonce;
- forward-auth starts no call unless the request opts in.

Built-in policies without state have nothing to guard. Each M7 policy adds its `Name` and `Explain` trace hooks and a parity case that checks it left its state alone. The access-list policy follows the rule (see [waf.md](waf.md)).

### One response-headers step (M6)

`IGateTracer.ResponseHeaders(context, host, operations, outcomes)` reports a stage of the host's compiled header list as `GateStepKind.ResponseHeaders` (contract `SimulationStepKind.ResponseHeaders`, "Response headers" in the trace view). The later stage extends the step the earlier one opened.

It lists each of `Alt-Svc`, `Strict-Transport-Security`, `Server` and the response `X-Request-Id` that the answer carries, with its value. For each one it does not carry, it gives the reason: off, no HTTP/3, plain HTTP, an IP literal or loopback name, the upstream's own, removed, or `the host's mode`.

The request ID is reported as sent when the response is written, because that is when its one string is materialised. This step replaced the separate HSTS and `Server` steps.

`SimulatorParityTests` compare the headers the real gate put on the response with the values the trace reports, for every host, scheme and negotiated connection it sweeps, loopback and IP-literal names included. `GateSimulatorTraceTests` covers the modes and the reasons.

### Decisions and gaps

1. **No key is loaded for a draft or a revision.** Whether a certificate is usable is decided from what the vault describes (`ICertificateCatalog.DescribeAsync`: public facts, no key opened).
   - A certificate the vault does not hold, the local CA root, or a chain outside its validity period is unusable, with the real loader's reasons (`CertificateInventoryLoader.ForSimulation` shares the validity logic). A draft binding to one is refused as a real command refuses it, and Auto hosts pick the same certificate.
   - Built-in (placeholder) certificates count as served by this process.
   - What is served is a self-signed placeholder per ID (`SimulationCertificateSource`).
   - The handshake step names the inventory certificate (`ServerCertificate.Id`, set by `CertificateInventoryLoader`). A context not built by the loader has an empty ID and is not named.
   - Without a catalog (no vault registered), every certificate the table calls valid counts as usable. When the certificate table cannot be read, the result says so.
2. **Destination health is live YARP state** (`IUpstreamHealth`, over `IProxyStateLookup`, attached by the public app through `DeferredUpstreamHealth`).
   - A destination of the target config reports the live proxy's answer for the destination with that ID **and address**, `Unknown` otherwise, with a note. The "can't be known for a config that isn't live" note is for non-live targets only.
   - The destination that serves a request is never named.
   - A destination the live proxy keeps out of rotation is `Unhealthy`. YARP counts a destination with no health data yet as available.
3. **The ACME HTTP-01 answer is traced for real.** `GateSimulator.Run` takes the live `AcmeChallengeStore`, and the gate's own `TryAnswerAcmeChallenge` answers or falls through. Challenges are process state, not config, so every target uses the live store.
4. **Trace facts** use stable camelCase wire names (`TraceNames`) for the gate's own values. Header facts use the header's own name (`Server`, `Strict-Transport-Security`, ...), because that is what the person is looking at on the wire. Policies name themselves explicitly (`static`, `ipAllowList`).
5. **Per-host trusted-proxy overrides** do not exist yet.
6. **The probe takes the HTTP and TLS versions as what the client negotiates.** An HTTP/2 request to a name that offers only http/1.1 is reported as the HTTP/1.1 request it becomes. A request DTO cannot say "no Host header" (the probe can).
7. **An admin host's implicit route** forwards to the admin app (see [hosting.md](hosting.md)). The trace says so, and `GateUpstream.Admin` is true with the `__admin` cluster, rather than naming a user upstream. The parity suite includes one.

Not in M4 core: MCP and the dashboard page.

