# The hot path

For contributors: how a request travels from the socket to the upstream, under what budget, and why.

## In short

- The hot path is everything a request touches between the socket and the upstream: the connection stage, the TLS selector, the gate, the policy chains and the YARP hand-off.
- The budget: no DB, no locks, no LINQ, no closures, and **0 B** of allocation in our own code in steady state. Every new gate or response path gets an allocation test.
- Everything is precomputed when the config is compiled into a snapshot. A request reads the snapshot once.
- The gate is one synchronous middleware. It resolves the client, host and route itself and hands YARP a ready endpoint, so ASP.NET routing never matches hosts.
- Per-connection checks (PROXY protocol, IP deny, the per-IP cap) run before TLS. The TLS selector hands out prebuilt options per SNI.
- Response headers, custom headers, path rewrite and compression are compiled per host or route and applied by transforms. Their costs are pinned by tests.
- The request simulator runs the same gate code over a compiled snapshot through a tracer that the JIT removes in production, so its answers cannot drift.
- Code: `src/Paduraksa.Proxy` (`Gate/`, `Snapshot/`, `Yarp/`, `Net/`, `Simulation/`). The WAF policies are in [waf.md](waf.md), metrics and logs in [observability.md](observability.md), Cloudflare in [cloudflare.md](cloudflare.md).

Contents:
- [Budget and rules](#budget-and-rules)
- [Request pipeline](#request-pipeline)
- [Response headers](#response-headers)
- [Custom headers](#custom-headers)
- [Path rewrite](#path-rewrite)
- [Response compression](#response-compression)
- [The connection stage](#the-connection-stage)
- [The listeners' limits](#the-listeners-limits)
- [Kestrel's lost HTTP/2 responses](#kestrels-lost-http2-responses)
- [Upstream timeouts](#upstream-timeouts)
- [Request timing](#request-timing)
- [HTTP/3 strategy](#http3-strategy)
- [Request simulator](#request-simulator-why-was-this-blocked)

## Budget and rules

The request path is held to a strict budget:
- No DB, no `await` on anything except I/O to the client or upstream, no locks (`lock`, `SemaphoreSlim`), no LINQ, no closures, no string building, no backtracking regex.
- **0 B** of per-request allocation in Paduraksa code, steady state, on keep-alive connections. Kestrel's and YARP's own allocations are out of scope but measured. Tests assert `GC.GetAllocatedBytesForCurrentThread()` deltas.
- Precompute everything at compile time: byte arrays for responses and headers, `SearchValues<string>` for substring sets, CIDR tries, country bitsets, redirect targets.
- Options validate themselves. An invalid value throws at `init` or construction, never later on the hot path. The snapshot compiler checks config first and reports an error.

**Spike S3** (`docs/spikes/S3-zero-alloc-gate.md`; .NET 10.0.13, YARP 2.3.0) confirmed this. The gate measured **0 B per request on H1 and H2** in every end-to-end run and took 145 to 225 ns for the synchronous section. The accepted costs for realistic browser traffic:

| | B/request |
|---|---|
| Kestrel itself | ~1 KB |
| Routing + YARP hand-off | 152 B |
| Whole proxied request | ~4.1 KB on H1, ~4.4 KB on H2 |

### Mechanisms

- **Pipeline.** The public app is built on the **generic host with an explicit pipeline**, not `WebApplication`: `Configure(app => { app.Use(gate); app.UseRouting(); app.UseEndpoints(e => e.MapReverseProxy(...)); })`.
  - YARP calls `AddAuthorization()` unconditionally, and `WebApplication` would then insert `UseAuthorization()` *ahead of the gate*. That costs bytes and is a hazard if a `FallbackPolicy` is ever set.
  - A guard test with a spy `IAuthorizationPolicyProvider` asserts it never runs.
- **Gate shape.** One synchronous gate middleware decides what to do, and the response body is written *after* the measured section, from precomputed bytes plus shared header values set through the typed header properties.
  - The one exception is a policy that cannot decide at once (an access list's password check, `PolicyAction.Defer`). The gate awaits it after the decision and resumes the chain after that policy (see "Access lists" in [waf.md](waf.md)). Every synchronous path stays 0 B.
- **Per-connection state.** An immutable `ClientInfo`, set as a connection feature in `ListenOptions.Use` and shared by all H2 streams on the connection. Never use `IConnectionItemsFeature.Items`: it is one non-thread-safe dictionary for all streams.
- **Per-request state.** One object cached in `IPersistentStateFeature` and reset each request. Stream reuse happens only on graceful end, so RST, cancels and Drop reallocate. That is accepted.
- **Host lookup.**
  - `Headers.Host[0]` (H2 `:authority` is already folded in), with the port stripped by span.
  - Two `FrozenDictionary` tables (exact names, wildcard parents), `OrdinalIgnoreCase`, looked up via `GetAlternateLookup<ReadOnlySpan<char>>`: 11 to 22 ns, 0 B. `Request.Host.Host` allocates when a port is present.
  - The compiler **rejects duplicate host names** (otherwise the last silently wins) and canonicalises IPv6-literal keys.
- **Client-IP parsing.** Our own span parser into `UInt128`, because `IPAddress.TryParse` allocates 40 or 80 B. Ours is stricter than the BCL: no octal, hex, short forms, zone IDs or whitespace. An edge-case list plus a differential fuzz against the BCL guard it.
- **Policies.** Sealed `IRequestPolicy` classes returning a 16-byte `PolicyDecision` struct.
- **Tests.** `AllocAssert.Zero(state, static lambda)` for synchronous code. In-process Kestrel tests assert that the gate's decision, the access log's completion callback and the reapply response-start hook allocate 0 B (`GateAllocationTests`). Kestrel and YARP numbers are tracked in `bench/`, not asserted.
- **Benchmarks** run under workstation GC, because BenchmarkDotNet's `Allocated` column is wrong under Server GC with the in-process toolchain. Non-monotonic `GC.GetTotalAllocatedBytes` samples are discarded.

## Request pipeline

A request passes six stages, A to F. A and B run per connection, C is the gate, D the policy chains, E YARP, F telemetry.

### A. Connection

TCP uses Kestrel `ListenOptions.Use`. QUIC has no such hook, so the same checks run inside the QUIC `TlsHandshakeCallbackOptions` callback, where `Connection.RemoteEndPoint` is available (spike S1).

- **PROXY protocol** (opt-in per listener, TCP only) is parsed before TLS, so the real client IP feeds connection limits, IP deny and SNI-stage logic.
  - v1 (text) and v2 (binary) are both accepted, only from configured trusted source CIDRs. A header from anyone else drops the connection.
  - The header must arrive within a short timeout. When enabled on a listener it is mandatory.
  - The parser is a package of its own, `Paduraksa.ProxyProtocol` (with `IpKey` and `IpParser`, which the whole proxy uses), because the same fuzzed, allocation-free parser is useful to any .NET project behind a load balancer. It is BCL only, so Paduraksa uses it as it is, at the same 0 B.
  - `Paduraksa.ProxyProtocol.AspNetCore` is a generic Kestrel `UseProxyProtocol` over it, for other projects. Paduraksa keeps its own `ClientInfoMiddleware` (client info, lockdown, its counters) on the same `ProxyProtocolReader`.
  - Both ship on nuget.org with their own `proxyprotocol-v*` tag ([development.md](../development.md#publishing-the-nuget-packages)). The public API is listed in `PublicAPI.Unshipped.txt`.
- **Global IP deny** (a CIDR trie over `UInt128`) aborts before TLS. IPv4-mapped IPv6 is normalised first: on a dual-mode `[::]` listener TCP reports `::ffff:a.b.c.d` while QUIC reports `a.b.c.d`.
- **Per-IP concurrent-connection cap** (fixed-size table, CAS). See [The connection stage](#the-connection-stage).
- **`ClientInfo`** is computed once per connection (address, source) and stored as a connection feature. The country is per request (see "Country rules" in [waf.md](waf.md)).

Wiring:
- `UseClientInfo` runs on both TCP listeners. On HTTPS it goes through `PublicHttpsListenerOptions.ConfigureTcpConnection`, before `UseHttps`.
- On QUIC, `QuicClientInfoGate` attaches `ClientInfo.FromEndPoint` to the connection in the handshake callback, and H3 requests see it.
- `IpKey` stores IPv4 and IPv4-mapped alike, so TCP and QUIC agree.
- The parser, reader, `IpKey` and `IpParser` live in `src/Paduraksa.ProxyProtocol` (namespace `Paduraksa.ProxyProtocol`), with their tests, fuzz target and benchmark.
- Choices in the package split:
  - one namespace for the parser and address types, so a user needs one `using`; two assemblies, not three, because a `Paduraksa.Net.Primitives` assembly would hold two types and add a package to install;
  - the package's middleware has its own options type (`System.Net.IPNetwork` for the trusted proxies, a callback for counters) rather than Paduraksa's `CidrSet`-based one, because the config glue differs;
  - TLVs are skipped, not exposed: keeping the bytes would allocate for every connection.
- The QUIC `ConnectionClosed` gap (no close signal for a handshake that fails past the callback) is why QUIC counts established connections only.

### B. TLS handshake

`TlsHandshakeCallbackOptions` on both TCP and QUIC returns a per-SNI `SslServerAuthenticationOptions`.

- **Certificate selection.** SNI is looked up in the snapshot and mapped to a PREBUILT `SslServerAuthenticationOptions`, deduplicated on (cert, host TLS policy, transport).
  - The compiler builds `RuntimeSnapshot.Certificates`. Each enabled host's DNS names (exact and `*.wildcard`; IP literals never arrive as SNI) map to its certificate (a `CertificateId`), or to the default certificate with the host's own TLS settings (min version, TCP ALPN, H3 on or off).
  - Certificate IDs resolve through `IServerCertificateResolver` (the vault's source).
- **Per-request re-check.** The handshake sees only the SNI's host, and clients coalesce connections across every name a certificate covers. So the host's TLS settings are re-checked per **request** in the gate (stage C, `ConnectionTlsCheck`).
- **Chains.** Each option holds a shared `SslStreamCertificateContext.Create(cert, chain, offline: true)`, the only way the intermediate is reliably sent. Without it, TCP may fetch the intermediate via AIA mid-handshake, and QUIC sends the leaf only.
- **ALPN.** `ApplicationProtocols` is ALWAYS set explicitly: `[h2, http/1.1]` or the host's ALPN on TCP, `[h3]` on QUIC. Kestrel writes ALPN into a returned options object whose list is null (both transports), which silently breaks the other transport if instances are shared. A unit test asserts non-null ALPN.
- **Unknown SNI.** Serve the default certificate, or reject by THROWING from the callback (the client sees EOF or a TLS alert; the server stays healthy). We count rejections ourselves, because QUIC logs nothing.
- **Cipher suites.** OpenSSL's defaults, NO `CipherSuitesPolicy`. On Linux, `SslStream` caches the OpenSSL context and resumes sessions only without a policy. An AEAD+ECDHE policy cost about 1.5x the CPU per new connection: 5.3k against 8.4k handshakes/s (bench `handshake`, 2026-11-10). A host that must refuse TLS 1.2's older suites sets `MinimumVersion = Tls13`. QUIC (TLS 1.3) is AEAD-only already.
- **Client certificates** (per host). The chain is built offline, with no AIA, CRL or OCSP fetches: a client-controlled AIA URL would be SSRF. Authenticated Origin Pulls hosts use `CustomRootTrust` with the origin-pull CA (see [cloudflare.md](cloudflare.md)).

### C. Gate middleware

The gate is first in the pipeline. It runs these steps in order:

1. **Snapshot.** `SnapshotStore.Current`, one volatile read.
2. **Client IP.** The PEER is the PROXY-protocol source from stage A if present, else the socket peer. Only a peer in the trusted-proxies list may supply the client through `X-Forwarded-For` or `CF-Connecting-IP` (Cloudflare handling is in [cloudflare.md](cloudflare.md)). Otherwise the peer is the client.
   - `TrustedProxyClientAddressSource`: the connection's `ClientInfo` (else `Connection.RemoteIpAddress`) through the snapshot's global `TrustedProxyPolicy`, 0 B. Header fallbacks are counted.
   - The whole `ClientIpResolution` stays on the request state for policies and the forwarding headers.
3. **Trusted request scheme,** in the same step (`ForwardedScheme`, 0 B).
   - From a trusted Cloudflare peer: `CF-Visitor` (exactly `{"scheme":"https"}` or `{"scheme":"http"}`; anything else is invalid), else its `X-Forwarded-Proto`.
   - From a trusted generic proxy: `X-Forwarded-Proto`, only with `TrustForwardedProto` (default off).
   - Otherwise the connection's scheme.
   - The HTTPS redirect and the upstream `X-Forwarded-Proto` both use it, so a TLS-terminating proxy (cloudflared to :80, Flexible SSL) cannot loop the redirect. Cloudflare over plain HTTP for a plain-HTTP visitor (Flexible) is counted and logged once.
4. **ACME challenge.** On plain HTTP, `GET`/`HEAD /.well-known/acme-challenge/{token}` is answered from `AcmeChallengeStore` (a `ConcurrentDictionary` read through a span lookup, 0 B) with 200 `text/plain`, before everything below. If a token isn't published, the request falls through (see [vault-and-certificates.md](vault-and-certificates.md)).
5. **Host lookup** (span-based, no string allocation). An unknown host gets the Default behaviour.
6. **Response headers, stage 1.** The host's `Server` operation, 0 B, before the TLS check, so a 421 carries it too. The unknown-host answer and an ACME challenge follow the global defaults' list.
7. **TLS policy check** (HTTPS). The host's `TlsPolicy` against the request's connection: HTTP/3 (QUIC, TLS 1.3) against `Http3Enabled`, HTTP/2 against an HTTP/1.1-only ALPN, `ITlsHandshakeFeature.Protocol` against the minimum version.
   - A weaker connection gets **421 Misdirected Request**, so browsers retry on a connection of their own. It is a few comparisons on per-connection data, 0 B.
   - Authenticated Origin Pulls has its own step before this one (see [cloudflare.md](cloudflare.md)).
8. **Response headers, stage 2.** The host's `Alt-Svc` and `Strict-Transport-Security` operations, on the answers after the TLS check (forwarded or answered), 0 B. See [HTTP/3 strategy](#http3-strategy).
   - `Alt-Svc` goes on TCP HTTPS only (not over HTTP/3), and only for hosts with names in the certificate table (not IP-literal-only hosts).
   - HSTS goes only when the TRUSTED request scheme is HTTPS, and never for an IP-literal or loopback name (`localhost`, `*.localhost`, `127.0.0.0/8`, `::1`), decided by a span check on the Host header's name.
   - Ours replaces an upstream's own header. With HSTS off, the upstream's passes through untouched.
9. **Host policy chain** (stage D). It also guards the redirect and fixed answers: a blocked request gets 403, not 301.
10. **Host request limits** (see [waf.md](waf.md)). A request target over its limit is 414, and more header lines or bytes than allowed is 431.
    - These are answered after the host chain (a client it refuses learns nothing from sizes) and before the redirect (which would echo the target).
    - The host's body limit was put on the request when the host was found.
11. **HTTP-to-HTTPS redirect,** from precomputed bytes.
12. **Managed `robots.txt`** (GET/HEAD only), from precomputed bytes. It comes after the allow list and the plain-HTTP redirect, but before the fixed answer below and before route matching, so a Redirect or Respond host can carry one and the route chain never sees the file.
13. **Redirect and Respond hosts,** from precomputed bytes.
14. **Route resolution.** The per-host list, most specific first, on the canonical matching view of the path (see [config.md](config.md)). 404 if none matches.
15. **Route policy chain** (stage D): the route's own policies only.
16. **Declared body.** A `Content-Length` over the route's (else the host's) limit is 413, after the route's own chain and before anything reads the body.
17. **Endpoint.** `HttpContext.SetEndpoint(YARP Endpoint from the same snapshot)`, so the later `UseRouting` skips matching.

### D. Policy chains

There is one chain per host and one per route, each an `IRequestPolicy[]` of sealed classes evaluated in order. Host policies run before a route is known, so `State.Route` is null for them.

The order:
1. **AccessList** (IP and country allow/deny, then basic auth). The host chain only refuses by address and country.
2. **RuleSet** (path, method and user-agent rules). It comes after the host chain's address-only list policy and before a route's list that has users, address checks included (see [waf.md](waf.md)).
3. **RateLimit.**
4. *RequestLimits is not a policy.* The gate checks it (steps 10 and 16 above).
5. **Robots.**
6. **Auth** (forward-auth, OIDC later).
7. **ProofOfWork.**
8. Future: bot heuristics, header rules.

Each returns a `PolicyDecision`: `Continue`, `Respond(status, page)`, `Challenge` or `Drop`.

### E. YARP

By now the endpoint is set by the gate. YARP then runs affinity, load balancing, passive health and the forwarder, with Paduraksa transforms:
- `X-Forwarded-*`, `X-Real-IP`, request ID, security headers and custom headers (precompiled);
- upstream errors and no healthy destinations lead to custom error pages.

YARP's own `X-Forwarded-*` transforms are off. These headers are always OVERWRITTEN:

| Header | Value |
|---|---|
| `X-Forwarded-For`, `X-Real-IP` | Exactly one address: the resolved client. No chain is kept or appended. IPv4 as a dotted quad (also for IPv4-mapped peers), IPv6 without brackets. Left out if unknown. |
| `X-Forwarded-Proto` | The trusted request scheme from stage C (the one the redirect uses). |
| `X-Forwarded-Host` | Host or `:authority` as received, port included. |
| `Host` | The client's Host or `:authority`, unless the upstream's mode says otherwise. See below. |
| `Forwarded`, `X-Forwarded-Prefix`, `X-Forwarded-Port` | Removed (only a client could have set them). A route that strips its prefix sends its own `X-Forwarded-Prefix` (see [Path rewrite](#path-rewrite)). |
| every `cf-*` header and `True-Client-IP` | Removed unless `CloudflareHeadersTrusted`. |

- **`Host`.**
  - YARP's default would send the destination's address. The client's Host is sent so name-based upstreams, absolute URLs and trusted-domain checks work.
  - On an HTTPS upstream it is also the SNI and the name the upstream's certificate is checked against, unless the upstream's TLS trust is `Pinned` or `Any` (see [config.md](config.md)). `Any` is YARP's `DangerousAcceptAnyServerCertificate`. `Pinned` is a validation callback the client factory installs from the cluster's metadata (`UpstreamCertificatePins`); a changed pin list builds a new client.
  - The upstream's Host header mode (Original, Destination or Custom; see "Upstream Host header modes" in [config.md](config.md)) replaces it per route. SNI follows whatever is sent.
- **`cf-*` headers.** All are matched case-insensitively (`CF-Connecting-IP`, `CF-Visitor`, `Cf-Access-Jwt-Assertion`, `cf-ipcity`, and so on). They and `True-Client-IP` are removed unless `CloudflareHeadersTrusted` holds (Cloudflare mode and a trusted peer; a trusted generic proxy does not qualify). It is one scan of the outgoing header names, 0 B. Otherwise a LAN client could forge Cloudflare Access SSO past a tunnel.
- The address string is cached on the pooled request state. The rest are constants or Kestrel's strings.

### F. Telemetry

Always on and lock-free: counters and a latency histogram per host, status class and upstream. See [observability.md](observability.md).

## Response headers

The compiler stores one array per host, `CompiledHost.ResponseHeaders`, with one operation per header: `Alt-Svc`, `Strict-Transport-Security`, `Server` and the response `X-Request-Id`.

Each operation carries:
- its precomputed `StringValues` (or the request ID as its source);
- what to do: set, replace the upstream's, remove, leave to the upstream, or not sent (with the reason);
- when: after host lookup, after the TLS check, or with the answer;
- what has to hold: the connection is TLS, the trusted scheme is HTTPS, the name is no IP literal or loopback, not over HTTP/3.

If a host doesn't send a header, it still has the entry, so the trace can say why. The list is capped at 16 operations (`ResponseHeaders.MaxResponseHeaderOperations`, a compile error beyond), because the gate's per-request outcomes live in a stack buffer of exactly that size.

**What the gate applies.** A stage of that list and nothing else:
- `Server` right after host lookup, so gate answers (403, the HTTPS redirect, Redirect/Respond, 404, 421) and forwards all carry it at 0 B.
- Then `Alt-Svc` and HSTS, so a 421 carries neither.
- The default is `Server: Paduraksa`, with no version. Kestrel's own header stays off on both listeners.
- An unknown host and an ACME challenge use the global defaults' list, which carries neither `Alt-Svc` nor HSTS.

**The forwarded path.** YARP 2.3's `HttpTransformer.CopyResponseHeaders` special-cases only `Strict-Transport-Security`, and a destination's `Server` is concatenated with what is there. So one response transform, `ResponseHeadersTransform`, loops over the same list after the copy:
- `Paduraksa` and `Custom` replace the client-facing value, `Remove` drops it, and `Upstream` leaves it;
- the response `X-Request-Id` is ours or gone, as the host's propagation mode says;
- it also runs when the forward fails (the 502/504 path has no proxy response) and before a WebSocket upgrade is accepted.

**The one YARP path that clears the response.** If an upstream sends headers and then fails before the first body byte, YARP calls `context.Response.Clear()` and sends a bare 502, without re-running the transforms.
- The proxy registers a static `Response.OnStarting` hook. It checks YARP's `IForwarderErrorFeature` and, only then, calls `GateResponseHeaders.Reapply`, which walks the whole list again, 0 B.
- YARP sets that feature before the response starts on every answer that clears or bypasses the transforms: no healthy destination (503), a failed request or timeout (502/504), malformed response headers (502) and a response-body error (502).
- On a successful forward the transforms already applied the headers, so the hook does nothing.

The response `X-Request-Id` is the one header the decision leaves to the answer, because its value is the one string a request materialises. The trace reports it as such.

## Custom headers

**The setting.** `HostSettings.Headers` and `RouteSettings.Headers` (`HeadersSettings`) hold two lists, `request` and `response`, of rules `{name, operation, value}`. The operation is `set` (replace every value), `add` (append) or `remove`.
- **Layers.** The merge keys on the header name, ignoring case, per direction. A layer's rule for a name replaces the lower layer's rule in place, and a new name goes after the inherited ones. A route merges over its host's resolved rules the same way. An inherited rule can only be dropped by replacing it with another rule for the name.
- **Bounds.** At most 32 rules per direction, one per name: per layer (the validator, `headers.tooMany`) and again after the layers merge, for a host and for each route (a compile error naming the count). A name is 1-64 token characters. A value is 1-4096 visible ASCII characters, spaces or tabs, with no leading or trailing whitespace, so it can't carry CR or LF. A `remove` takes no value.
- **Values are static.** No placeholders. Each candidate (client IP, request ID, host, scheme) already has a header the proxy sets (`X-Real-IP`/`X-Forwarded-For`, `X-Request-Id`, `X-Forwarded-Host`, `X-Forwarded-Proto`), so a placeholder would only rename it, at the cost of per-request formatting.
- **Refused names** (`CustomHeaderRules`, shared by the validator, the compiler and the dashboard; code `headers.name.reserved`):
  - both directions: `Connection`, `Content-Encoding`, `Content-Length`, `Expect`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `Proxy-Connection`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`;
  - request: `Host` (the upstream's Host header mode owns it), every `X-Forwarded-*`, `Forwarded`, `X-Real-IP`, `X-Request-Id`, every `cf-*`, `True-Client-IP`, `X-Paduraksa-Internal`;
  - response: `Strict-Transport-Security`, `Alt-Svc` and `X-Request-Id`, which have settings of their own with rules a static header would skip (no HSTS for an IP literal, no `Alt-Svc` over HTTP/3);
  - response `Location`, refused outright rather than kept off the gate's redirects: a static `Location` makes every answer of the host a redirect target (the HTTPS redirect, Redirect hosts, the proof-of-work return) and would override a route's `Location` mapping.
- **`Set-Cookie` takes only `add`** (`headers.operation.invalid` otherwise): a `set` or `remove` would take off every cookie the upstream sets and the host's sign-in session cookie, which the proxy writes on the same response.
- If a request rule sets a header an auth provider of the host vouches for (its identity headers, prefixes included), compiling fails: the strip runs before the rule, so the rule would forge the identity.
- Other codes: `headers.name.invalid`, `headers.name.duplicate`, `headers.operation.invalid`, `headers.value.invalid`, `headers.value.unexpected`. The compiler checks names and values again.

**Where they apply.**
- **Request:** `CustomHeadersTransform`, after the forwarding, identity and session-cookie transforms, so a rule has the last word on the names it may touch. A header `HttpClient` keeps on the content (`Content-Type`, `Content-Language`, ...) goes on the request's content. A body-less request has none, and the rule does nothing there.
- **Forwarded response:** `ResponseHeadersTransform`, after the built-in list. A rule for `Server` wins over the `Server` setting, and a `remove` of `Server` takes the built-in one off too (pinned in `ResponseHeaderMatrixTests`). An `add` joins the upstream's value.
- **The proxy's own answers** for a known host (error pages, redirects, maintenance, robots.txt, the proof-of-work page, a 421): the gate applies the response rules when it writes the answer, after the built-in headers. They apply because the rules people set (CSP, CORS, `X-Frame-Options`, cache headers) are about the site, and an error page is part of it. Before a route is known the host's rules apply, after it the route's.
  - If no host serves a name, it gets none (as it gets no HSTS), and an admin host has no custom rules: the admin app's headers are its own.
  - The 502 that YARP answers after clearing a response gets them back with the built-in ones (`GateResponseHeaders.Reapply`).
- **Simulator:** the upstream step lists each route rule as a fact under the header's own name (`request: set to ...`, `response: removed`), and a gate answer's rules extend the response-headers step. A value that looks like a credential (`Authorization`, `Proxy-Authorization`, `Cookie`, a name ending in `-Key`, `-Token` or `-Secret`) is shown as `(redacted)`, so it stays out of a trace an MCP script reads. Rule values are config, not vault secrets: the document stores them and the YAML export carries them.

**Cost.**
- A route without rules shares `CompiledHeaders.None`, or its host's instance when it has none of its own: one length test.
- A response `set` or `remove` stores or removes a precomputed `StringValues`: 0 B in Kestrel's store once the connection has held the name. An `add` onto a header that already has a value builds the one array that holds both, and only then.
- A request rule writes into `HttpRequestHeaders`, which allocates inside the framework like every header YARP copies. `CustomHeadersAllocationTests` pins that the rules cost no more than the same writes made directly.

## Path rewrite

**The setting.** `RouteConfig.Rewrite` is `{stripPrefix, addPrefix, rewriteLocation}`, per route (not a layered host setting, because it is about the route's own path). A rewrite that does neither writes no key in canonical JSON.
- `stripPrefix` takes the route's path off the front of what is forwarded: on `/app`, `/app/x` goes as `/x` and `/app` as `/`. Refused on a route whose path is `/` (`route.rewrite.stripPrefix.root`).
- `addPrefix` puts a fixed path in front: with both, `/app/x` goes as `/api/x`. It starts with `/`, has no trailing `/`, at most 256 characters of URL path (letters, digits, `-._~!$&'()*+,;=:@/`, valid percent escapes), with no empty, `.` or `..` segment (`route.rewrite.addPrefix.invalid`). The compiler checks both again (`PathRewrite`).
- **No regex rewrite.** Strip and add cover the cases people bring from nginx-proxy-manager's custom locations and Traefik's `stripPrefix`/`addPrefix`. A regex, even `NonBacktracking` with a length cap, adds an expression language to validate, simulate and explain for little more.

**On the raw path.**
- `RawPathTransform` keeps its semantics: the route is matched leniently on the canonical view and the path is forwarded as the client sent it. The rewrite works on that raw path, so nothing is decoded or re-encoded (`/app/group%2Fproject` goes as `/group%2Fproject`) and the query is untouched.
- The URI is still built in one concatenation, so a rewrite costs nothing over what the transform already built (`PathRewriteTests`).
- An absolute-form target (no leading `/`) is rewritten on the path as YARP escapes it.

**What the gate refuses with the host's 404.** The answer comes after the route match and before the route's chain, and the simulator says why. Each check is a span comparison, 0 B.
- **A spelling that can't be stripped.** Matching is lenient, so `//app/x`, `/app;v=1/x` and `/%61pp/x` all reach the `/app` route. The strip takes off the route's own spelling only (ASCII case ignored, at a segment boundary). Anything else would mean guessing what the upstream should receive.
- **A dot segment in any spelling, on any rewritten route** (strip, add, or both). Kestrel decodes `%2e` and resolves dot segments before matching, but the raw path is what is forwarded. A path that resolves back into the route can therefore step out of what the rewrite forwards: on `/app` stripped to `/api`, `/app/%2e%2e/app/x` is the route's `/app/x`, but reaches the upstream as `/api/%2e%2e/app/x`, which it resolves to `/app/x`, outside `/api`. An add-only route has the same hole.
  - `PathRewrite.HasDotSegment` refuses a segment that, with its `;` parameters dropped and `%2e`/`%20` decoded in any case, is only dots and spaces (`.`, `..`, `%2E%2e`, `.%2e`, `..;x`, `.. `), between `/`, `\`, `%2F` or `%5C`. An encoded slash inside an ordinary segment (`group%2Fproject`) still goes.
  - `PathRewrite.HasLooseEscape` refuses, anywhere in what is forwarded, the escapes an upstream that decodes loosely could turn into something the gate never saw: `%25` (a double encoding, `%252e`), `%c0`/`%c1` (an overlong UTF-8 dot, `%c0%ae`), IIS's `%u`, and `%00`-`%1f` and `%7f`.
  - An added prefix is held to both rules in config (`/a/%2e%2e` is refused).
  - A route without a rewrite keeps forwarding the raw path as before: the client's path is the upstream's, so there is nothing to step out of.
- **A route path that needs encoding** (`/café`, `/my app`; valid route paths are written decoded) is stripped in the spelling a client sends. The compiler percent-encodes it as UTF-8 (`/caf%C3%A9`, `/my%20app`) and the strip compares the raw path with that, ignoring the case of letters and hex digits. The decoded spelling is tried too, which no HTTP client sends raw but the simulator's probe may.

**`X-Forwarded-Prefix`.** A stripping route sends the stripped prefix in it (the route's path, percent-encoded as above, so ASCII), so an app that builds its own links (ASP.NET's forwarded headers, Spring, Django's `SCRIPT_NAME` setups) can. The client's copy is still removed first. A route that only adds sends none.

**Redirects.** `LocationRewriteTransform` gives an upstream's `Location` the client's prefix back when it points under what the route sends: a path-absolute one, or an absolute URL whose authority is the request's own `Host`. With strip and add on `/app` and `/api`, `Location: /api/login` reaches the client as `/app/login`.
- **The mapping guesses, and says how.** A path-absolute `Location` from a stripped upstream is ambiguous. `/login` is the upstream's own path (the client needs `/app/login`), but `/oauth2/sign_in` may be another route of the same host (oauth2-proxy beside the app), which the mapping would put under this one and 404. An app that reads `X-Forwarded-Prefix` already answers `/app/login`, which mapped again would be `/app/app/login`.
- So a `Location` that already starts with the client's prefix (at a segment boundary, ignoring case) is left alone, and `rewriteLocation: false` turns the mapping off for a route whose upstream redirects to its siblings. On is the default because an app that knows nothing of the prefix is the common case behind a strip.
- **The upstream's own address.** An absolute `Location` on the name the upstream was sent (a custom Host header, or the destination's address in Destination mode) is the upstream talking about itself. It gets the client's scheme and host, so an internal address never reaches the client, and its path is mapped as above.
- A `Location` elsewhere (another host or port, `//cdn/...`, `/\cdn/...`, which a browser also reads as protocol-relative, `/apiary`) is left alone. Scheme and host compare ignoring case.
- Only a stripping route rewrites it: an add-only route forwards the client's whole path under its prefix, so taking the prefix off would point outside the route.
- **Cost.** Nothing without a rewrite or a `Location`, and one string for a redirect it rewrites. `RouteTransformsKestrelAllocationTests` pins this on a real Kestrel over HTTP/1.1 and h2c: the gate's decision for a rewriting route and the response transform's custom rules with no `Location` at 0 B, a mapped `Location` at its one string.

**Simulator.** The upstream step has a `forwardedPath` fact and, for a strip, the `X-Forwarded-Prefix` it sends.

## Response compression

**The setting.** `HostSettings.Compression` is `{enabled, minimumSize}`, layered field by field. Built-in default: **off**, and 1 KiB once on. Validator code `compression.minimumSize.invalid` (0 to 1 MiB), checked again by the compiler. An admin host never compresses.

**What is compressed.** A forwarded response, when the client's `Accept-Encoding` takes Brotli or gzip, whose:
- content type is text-like (`text/*` except `text/event-stream`, JSON and `+json`, JavaScript, XML and `+xml`, SVG, WebAssembly, TTF/OTF/EOT fonts);
- status has a body (not 1xx, 204, 206 or 304), and it has no `Content-Range`;
- `Content-Encoding` is absent: an upstream that compressed passes through untouched;
- has no `Cache-Control: no-transform`, and no later middleware set `IHttpsCompressionFeature` to `DoNotCompress` (honoured on HTTP too);
- declared `Content-Length` is at least the minimum. A body without a declared length (chunked) is compressed, because its size isn't known when the decision is made.

Never touched: a `HEAD`, any `CONNECT`, an HTTP/2 or HTTP/3 extended CONNECT (a WebSocket there has no `Upgrade` header) and an HTTP/1.1 upgrade. Nor are the gate's own answers (error pages, redirects, robots.txt, maintenance), which are small, precomputed and written before the stage. The proxy's own pages written inside a forward (a replaced upstream error, YARP's 502/504 page) are skipped too: `ResponseWriter` marks the request, and the provider declines it.

Once an encoding is chosen (not for a client that takes only `identity` or `deflate`), the response is a different representation and its headers say so:
- a strong `ETag` is made weak (`W/"..."`), so a cache or range request doesn't treat the bytes as the upstream's while `If-None-Match` still revalidates (a weak comparison);
- `Accept-Ranges`, `Content-Digest`, `Digest` and `Repr-Digest` go, and the middleware drops `Content-Length` and `Content-MD5`.

The choice is made in `ShouldCompressResponse`, when the upstream's headers are in. The middleware's own question for the encoding comes earlier on a forward (YARP's `DisableBuffering` asks before the headers are copied).

Brotli is preferred over gzip at equal q-value, both at `CompressionLevel.Fastest`. .NET 10 has no zstd built in, so there is none.

**ASP.NET's middleware, gated.** `ResponseCompressionMiddleware` fits, with a stage in front of it (`ResponseCompressionStage`) and a provider whose `ShouldCompressResponse` is the host's (`CompressionPolicy`). The built-in part does what is easy to get wrong:
- the encoding from `Accept-Encoding`'s q-values;
- `Vary: Accept-Encoding` on a compressed response, and `Content-Length` and `Content-MD5` dropped;
- streaming: the encoder is flushed when YARP flushes the response, and finished at the end;
- `IHttpsCompressionFeature` is honoured (`EnableForHttps` is on; the host setting is the switch).

**Where it sits.** After the gate, so it sees forwarded requests only, and inside the access log's body counter, so the counted and logged bytes are the compressed ones (`CompressionEndToEndTests`).

**Cost.**
- Off for the host, no `Accept-Encoding`, or a `HEAD`: the stage tests a field and calls the next middleware, 0 B (`TheStageCostsNothingWhenItDoesNothing`).
- On, with an `Accept-Encoding` the client sent: the middleware's body wrapper and its async state, **per request**, whether or not the response then turns out to be compressed (the decision needs the response's headers, so the wrapper has to be there first). Per compressed response, on top: the encoder stream and its native state, and the weakened ETag's string when the upstream sent a strong one. The built-in middleware pools nothing.
- **Measured** (bench `compressed`, quick profile, not recorded, 2026-11-13, i5-13600K, proxy on 4 CPUs): a 16 KiB HTML page, `Accept-Encoding: gzip`, 64 connections. Paduraksa served 33,117 req/s at **61.7 us of proxy CPU per request**, against 28.6 us for the plain `h1` request in the same run, so about 33 us per response is the compression. Same run: HAProxy 49.4 us, Traefik 127.9 us, nginx 155.4 us, Caddy 158.8 us. The levels are each proxy's fastest or closest (nginx `gzip_comp_level 1`), so the output sizes differ and the row compares cost, not ratio.

**BREACH.** A response that carries a secret next to input an attacker controls leaks the secret through its compressed size. The proxy can't tell which responses do, so compression is opt-in per host and the dashboard field says why. See the threat model, 4.1.

## The connection stage

Answers threat model G1 and G8. Everything here runs before TLS, at stage A.

### Settings

`GlobalSettings.Connections` (`ConnectionSettings`) is global only and the last field. It writes no key in canonical JSON while it sets nothing.

| Field | Meaning |
|---|---|
| `maxConnectionsPerIp` | Built-in 256. `0` turns the cap off. At most 100 000. `ConnectionSettings.DefaultMaxConnectionsPerIp` and `ConnectionsSpec.DefaultMaxPerIp` are the same number, pinned by a test. |
| `denyListId` | **A named access list.** One way to express IP rules, never an inline list (the owner's decision). |

- Validation codes: `connections.maxConnectionsPerIp.invalid` and `connections.denyList.unknown`. The second means deleting the list the settings name is refused, like deleting one a host names.
- **Only the list's `ip.deny` addresses apply at the connection stage.** Its allow addresses, countries and users are request rules that mean nothing before TLS. A list with such rules compiles with the warning `connections.denyList.requestRules` at `/settings/connections/denyListId`, so a rule that does nothing here is said, not silently ignored.
- The dashboard's Global defaults page has a Connections card: a count field with the built-in as its placeholder, and a select of the access lists.

### Compile

- `RuntimeSnapshot.Connections` is a `ConnectionPolicy`: the cap, and the list's deny addresses as the same `CidrSet` the host chains use. The compiler reads `CompiledAccessList.Deny`, so there is one lookup and one parser.
- A missing list or an out-of-range cap is a compile error, with the validator's paths for the same two.
- The bootstrap snapshot is `ConnectionPolicy.Off`. A compiled config always carries the cap.

### The table

`ConnectionTable` (`Proxy/Net`) is the per-address state. There is one per process inside `ConnectionRuntime`, never in a snapshot.

- 65 536 entries of one `ulong` (512 KiB). Fixed, lock-free, open-addressed with a probe window of 8, like the rate-limit table.
- An entry is a 40-bit tag of the key's hash and a 24-bit count, claimed and changed by compare-and-swap of the whole word. 0 is empty. An entry whose count has gone back to 0 keeps its tag (an address busy between two connections keeps its place) until another address needs it.
- The key is `RateLimitPartition.Shared.ForIp`: the whole IPv4 address (IPv4-mapped is the same key, so TCP and QUIC agree) or the IPv6 /64. It is hashed with the per-process random seed, so nobody can pick addresses that collide with another's.
- **A full window fails open.** The connection is let through uncounted and `ConnectionStats.TableFull` counts it. A flood from very many addresses can fill the table. After that, new addresses are not capped (what Kestrel gives without the table), but memory stays bounded and nothing is refused that should not be.
- **Accepted race.** Two threads that both miss a new key and claim different entries give it two entries for that moment (up to twice the cap). A release takes from whichever entry of its tag holds a connection, so counts stay balanced. A concurrency test and a randomised full-table test end at 0.
- A count at 2^24 is treated as over the cap.

### Decision order (`ConnectionRuntime.Admit`)

1. **The deny list,** for every connecting address, **trusted proxies included**. It is the operator's explicit list, so listing a proxy's address refuses the proxy.
2. **Cap off, or the peer is a trusted proxy:** admitted and never counted. `TrustedProxyPolicy.IsTrustedPeer` covers Cloudflare's edge ranges in `CloudflareProxy`, the cloudflared address in `CloudflareTunnel`, and the listed proxies in `Generic`. Every visitor behind a trusted proxy shares its address.
3. **The table.**

Peers behind a trusted proxy are judged by their real address later, at the gate (access lists, rate limits). The connection stage sees the connecting address: with PROXY protocol the PROXY source (`ClientInfo.Address`), so a load balancer's visitors are each their own address, otherwise the socket's.

If a deployment puts another proxy in front without listing it as trusted, every visitor shares one address, and the cap is their shared limit. Set the trusted proxies.

### TCP

`ConnectionLimitMiddleware` runs after `UseClientInfo` and `UseOriginLockdown` and before `UseHttps`, on both TCP listeners.
- With the stage off (`IsInert`: no deny list and cap 0) it passes the connection on at 0 B.
- A refused connection is aborted unread, so the handshake never starts. It allocates the abort exception, like the PROXY protocol and lockdown drops.
- A counted one is released when the rest of the pipeline ends, even if it throws.

Allocations:
- **0 B** for every decision and table operation, and for a connection that is not counted (the stage off, and every trusted-proxy connection, which covers all of Cloudflare's).
- **One release continuation of 177 B** (measured, Release build) for a counted connection, against Kestrel's own thousands of bytes per connection. If the pipeline has already ended, the connection is released without it.

### QUIC

The handshake callback (`QuicConnectionLimits.CreateGate`, wrapping the origin-lockdown gate) applies the deny list and **reads** the cap, but takes no place. Kestrel never signals a handshake that fails after the callback returned (`QuicConnectionGate`). A place taken there could leak for good, and a spoofed source address would then lock a victim out.

The place is taken when the connection is established, by a multiplexed connection middleware (`QuicConnectionLimitMiddleware`, added through `IMultiplexedConnectionBuilder` on the HTTP/3 listener). Kestrel runs it only for accepted connections and ends it with the connection.

**Consequence:** handshakes still in flight are not counted. A burst of simultaneous handshakes from one address can pass the cap by the size of the burst. The established connections cannot.

### Observability

- `ConnectionStats` are process counters: refusals by deny list and by cap per transport, table-full, and the number counted now.
- They feed `/metrics` as `paduraksa_connections_events_total{event="refused_deny_list"|"refused_per_ip_limit"|"table_full"}` and the gauge `paduraksa_connections_counted`, and the security overview (`SecurityOverview.Connections`) and its Connections card.
- Refusals are logged at Information, at most once per 10 s per reason (`ConnectionRuntime.LogInterval`), with the count and the latest address.
- The simulator's connection step refuses a deny-listed peer on both listeners (on QUIC after the handshake's name selection, as the callback does). It states the cap and whether the peer is a trusted proxy. The count itself depends on what is open and is not simulated. It says when the simulated client is on the list but is only the visitor behind a trusted proxy ("judged at the gate").

### Tests and bench

- `ConnectionTableTests`, `ConnectionRuntimeTests` and `ConnectionLimitMiddlewareTests` cover the table (cap and release, address keys, a full window failing open, balance under threads, 0 B), the decision order and exemptions, and release however the pipeline ends, with the 177 B.
- `PublicConnectionLimitsTests` (a real host) covers a denied address closed before TLS on both listeners, the cap, and a trusted proxy never counted.
- Two `KestrelQuicTlsTests` cases run on a real QUIC listener through `run-quic-tests.sh` in Docker: a held HTTP/3 connection counted once established, a second handshake from the address refused, room again after the first closes, and a deny-listed handshake refused. `AltSvcAdvertisesThePublicPortOnTcpOnly` fails the same way on `main`, unrelated.
- `ServerLimitsKestrelTests`, `ConnectionStageSimulationTests` (with parity against the runtime) and the validator, canonical, mapping, dashboard, metrics and overview tests.

Bench (`bench/run-baseline.sh`, Paduraksa only, two quiet runs each, 2026-11-13):

| Scenario | With the stage | Before |
|---|---|---|
| HTTP/1.1 | 171.6k req/s | 172.7k (-0.6%) |
| HTTP/2 | 152.0k req/s | 151.4k (+0.4%) |
| New TLS connection per request | 9.7k req/s | 10.1k (-3%) |

- The TLS difference is inside the 5% spread this machine showed between two runs of the same build. One handshake is about 100 us of CPU, and the stage adds a hash and a few atomic operations.
- Bytes per request were 4.1 KB (HTTP/1.1) and 4.5 KB (HTTP/2) for both. Per new connection it was 18.8 to 19.9 KB against 18.5 KB (177 B of that is the release continuation).

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
- **Long-lived connections.** Kestrel stops enforcing the request-body rate on an upgraded connection, and times the response rate only while a write waits for the client. An idle WebSocket or event stream is therefore not cut by Kestrel (the upstream's idle timeout is a different limit; see [Upstream timeouts](#upstream-timeouts)). `ServerLimitsKestrelTests` runs both with rates and graces far stricter than any default, with a slowloris (headers never finished: 408), a byte-by-byte header dribble and a slow upload (both cut).

## Kestrel's lost HTTP/2 responses

Found by the soak (`h2-hang`): about one HTTP/2 response in a million was never sent. The proxy had answered (its access log has the `200`), the connection stayed up and kept serving other requests, and the client gave up after 30 s. The cause is a Kestrel bug: [dotnet/aspnetcore#69690](https://github.com/dotnet/aspnetcore/pull/69690), merged on `main` on 2026-10-29. The 10.0 backport ([#70100](https://github.com/dotnet/aspnetcore/pull/70100)) is not merged yet, so 10.0.14 still has the bug.

**The bug.**
- A stream's output producer is queued to the connection's frame writer by `Schedule()`, which sets `_isScheduled` so it is queued once. Only the writer clears the flag, when it takes the producer.
- A response that used up both its stream's and the connection's send window waits for the connection's window twice: as the window's last consumer, and again after a stream WINDOW_UPDATE finds the connection's window still shut. A 64 KiB download (one byte more than HTTP/2's initial 65 535) does this routinely.
- The connection's WINDOW_UPDATE then resumes it twice. If the writer has taken the first entry and is flushing the response's last frame when the second comes, the producer is queued once more. The writer drops that entry, because the response is complete, without clearing the flag.
- The stream goes back to Kestrel's per-connection pool with the flag still set (`StreamReset` doesn't clear it). The next request that reuses it runs normally, but every `Schedule()` returns at once: its response waits in the stream's pipe for good.
- That stream never completes, so its connection never finishes closing. When the client drops the connection, the GC collects it: Kestrel's `crit ... application never completed`.

**The workaround** (`KestrelHttp2StreamReuse`, `Proxy/Http`).
- At the start of every HTTP/2 request it clears the reused producer's `_isScheduled`. The access log's `Begin` calls it (the public pipeline's first step), and so does the first middleware of the admin app, which serves the dashboard over HTTP/2 too.
- Clearing is safe whatever the state. A flag set with no entry queued is the bug. A flag set with an entry still queued lets one more entry in, and the writer, with nothing new to write, writes nothing (a zero-length write without END_STREAM sends no frame).
- It reaches Kestrel's internals through `UnsafeAccessor`: `HttpProtocol.Output` (the stream's producer) and `Http2OutputProducer._isScheduled`. That is 0 B, and the h2c runs of `GateAllocationTests` measure it with the rest of `Begin`.
- It turns itself off when the running Kestrel has upstream's fix (the producer has `TryUnscheduleCompletedResponse`) or lacks the fields it reaches. A servicing release retires it with no change here. **Delete it once the image's runtime has the fix.**
- `KestrelHttp2StreamReuse.Healed` counts the requests it put right. The soak's startup hook writes it to `probe.log` as `h2_healed`.
- **Why this.** Kestrel has no switch to turn stream pooling off (the pool size is a constant), so the alternatives were waiting for the backport or turning HTTP/2 off. A one-field reset that disables itself is the least intrusive. It matches the hook upstream added (upstream clears the flag where the writer drops the entry; this clears it where the stream is reused).

**The regression test** (`Http2StreamReuseTests`) makes the race happen every time.
- It uses a hand-written HTTP/2 client and a connection whose output flushes the test holds. Kestrel's scheduler is set to inline (a test-only reach into its internals that Kestrel's own tests also use), so the first resume runs the writer up to the held flush and the second always lands after it.
- Each step waits on an ordering, not on time. A headers-only "marker" response that arrives after a step means the writer has finished that step. A marker whose flush ran inside its own request means the writer was parked, which the double resume needs.
- The follow-ups hold their streams open until all have started, so one of them must take `/big`'s pooled stream. A last marker, queued after all their responses, tells a lost response from a slow one.
- Waiting for the client to see the first 64 KiB is not enough: on CI the stream WINDOW_UPDATE then overtook the writer and the race didn't happen. The markers make the order certain.
- Plain Kestrel loses exactly one of the follow-up responses, and the workaround answers them all. Without the workaround the second test fails. If Kestrel ever has the fix, the first test expects no loss, and the class says the workaround can go.

## Upstream timeouts

The listeners' limits above judge the client. The timeouts below judge the upstream, and each upstream has its own.

**The bug it fixed.** Nothing set YARP's `ForwarderRequestConfig.ActivityTimeout` for a public cluster, so YARP's default of **100 s with no data in either direction** applied. It applies to an upgraded connection and to a streamed response too, so a Home Assistant dashboard, a chat app or an event stream with a quiet gap over 100 s was cut by the proxy. `ServerLimitsKestrelTests` could not see it: it proves only that Kestrel's limits leave an idle stream alone. The connect timeout was also a fixed 10 s for every upstream.

**The setting.** `UpstreamConfig.Timeouts` is `{connect, idle}`, optional, and each value falls back to its default on its own. Canonical JSON writes no block when both are unset, so stored revisions keep their bytes.

| | Default | Range | Means |
|---|---|---|---|
| `connect` | 10 s | 100 ms to 5 min | TCP connect and TLS handshake to a destination. |
| `idle` | 1 hour | 1 s to 24 h; `00:00:00` = never | No data in either direction on a request, a response stream or an upgraded connection (YARP's activity timeout). Any data in either direction, and the arrival of the response headers, starts it again. |

Validator codes: `upstream.timeouts.connect.invalid` and `upstream.timeouts.idle.invalid`. The compiler checks the same ranges as a backstop.

**The default idle timeout is 1 hour.**
- **A quiet connection that is still wanted must not be cut.** The cut is a surprise, since nothing on the page says why a terminal or a dashboard stopped. Apps that care ping every few seconds to a few minutes (Home Assistant, socket.io, ActionCable, ttyd, Jupyter), and a ping is data, so it resets the timer. What reaches the timeout is a connection with no heartbeat at all: a session someone left open, or a peer that vanished without a FIN.
- **An hour is the cost of that choice.** A vanished peer holds one of the 2 000 upgrade slots (`MaxUpgradedConnections`) and some memory for up to an hour, until a write fails or the timer fires. Shorter would cut the idle terminal during a meeting, and "never" would leave the dead peer to TCP alone. An operator who knows their traffic sets it per upstream.
- **A request nobody answers also waits an hour.** One timeout covers a request, a stream and a tunnel, so the old 100 s that protected a stuck request is gone with the one that cut a WebSocket. A browser gives up on its own long before, and what the proxy holds meanwhile is one request.
- **What others do.** nginx's `proxy_read_timeout` is 60 s between two reads, the famous cut of idle WebSockets. Caddy's `reverse_proxy` has no idle limit on a stream by default (`stream_timeout` is opt-in). Traefik has none on a response either (`forwardingTimeouts.responseHeaderTimeout` is 0; its dial timeout is 30 s). Paduraksa sits between: bounded, but long enough to cost nobody a live session.

**Where it applies, at 0 B per request.**
- The compiler gives every public cluster a `ForwarderRequestConfig` with `ActivityTimeout` set, the default included (leaving it out is the bug). `00:00:00` compiles to `Timeout.InfiniteTimeSpan`, which YARP accepts. The forwarder reads the cluster's config it already holds.
- The connect timeout is a property of the HTTP client, and `HttpClientConfig` has no field for it. A value other than the default rides in the cluster's metadata (`paduraksa.connect-timeout-ms`, beside the pins and the custom Host name), and `PaduraksaForwarderHttpClientFactory.ConfigureHandler` sets `SocketsHttpHandler.ConnectTimeout` from it. The default is no entry.
- `SocketsHttpHandler.ConnectTimeout` also covers the TLS handshake (`ATlsHandshakeThatNeverCompletesFailsAtTheConnectTimeoutToo`).
- `CanReuseOldClient` compares the connect timeout besides the client config and the pins. An edit that changes it builds a new client (a cluster change that drops the warm pool). An edit to anything else keeps the client, and so does a change to the idle timeout, which is the cluster's, not the client's.
- A change applies to connections and requests made after it. One already running keeps the timeout it started with.
- A request cut by either timeout gets YARP's 504, with the gateway-timeout page.

**Tests.** `UpstreamTimeoutsTests` runs a real proxy (Kestrel, YARP, the Paduraksa client factory) in front of a real upstream, with a 2 s idle timeout standing in for the hour. It covers an idle WebSocket cut (HTTP/1.1 and h2c), one that keeps sending surviving, the default and the explicit "never", event streams, a request nobody answers getting a 504, and a published change applying to the next connection. Connect is tested with a listener whose accept queue is full, and with a silent TLS server. `PaduraksaForwarderHttpClientFactoryTests` covers client reuse and `SnapshotCompilerTests` the cluster output and backstop errors.

**Left out.**
- **A time to the response headers (first byte).** YARP has no setting for it: the activity timeout also restarts at the headers, and a separate one would be a timer of our own on the forward path (a `CancellationTokenSource` per request), against the budget.
- **Separate idle times for a request and a tunnel.** It would need a per-route or per-protocol split YARP does not offer.

## Request timing

What timing every request by stage costs. What the stages are: "Request timing" in [observability.md](observability.md#request-timing).

- **Per request, 0 B, no lock:**
  - an atomic increment and a field read on the connection's `ConnectionTimings` (the feature lookup happens once per pooled request state);
  - three more `Stopwatch` reads (the gate's end, the response's start, and one end shared by the total, the send and the counters);
  - a timestamp per body read that returned bytes;
  - one more response callback registration (Kestrel reuses its callback stack);
  - a lock-free `ConditionalWeakTable` lookup at the end of a forward;
  - two more interlocked adds (the send's histogram and sum; two more on a TLS connection's first request).
- **Per connection:** one 40-byte object. **Per upstream connection:** one more, and the table's lock to add it, while the connect waits on the network.
- **Where the stages start** (the waterfall): two field reads, the connect's start from the table lookup already made, three subtractions and the forwarder's error feature at the end of a request, and one more condition per body read; 0 B. The access-log entry is 184 bytes (three `int` starts and a flag, the TLS handshake in an `int`, the status in a `short`), so the ring's slot is still 192 bytes with no padding left.
- **Allocation tests:** `GateAllocationTests` runs on TLS h1 and h2 as well as h1 and h2c, `BodyBytesTests.CountingAllocatesNothingOnKestrel` on TLS uploads, and `AccessLogAllocationTests` measures the response-start callback with the completion and reapply ones on h1, h2c and TLS.
- **Bench** (`bench/run-baseline.sh`, Paduraksa only, fixed concurrency of 64, branch against `main`, alternated). The box was shared and loaded for the whole hour, so only the runs that kept the proxy on at least 3.5 of its 4 CPUs count: two pairs of h2-tls. No branch h1 run was quiet.

| h2-tls, quiet runs | `main` | Branch |
|---|---|---|
| Requests/s | 146.2k, 144.1k (mean 145.2k) | 140.3k, 151.9k (mean 146.1k) |
| Proxy CPU per request | 24.7, 24.8 us | 25.2, 23.8 us |
| Bytes per request | 4 510, 4 876 | 4 662 |

- h2-tls: within the runs' own spread (about +0.6 % throughput and -1 % CPU per request on the means), so the cost is below what this box can resolve and inside the 3 % budget.
- h1: **not measured.** The branch's best h1 run had the proxy on 3.09 CPUs (148.1k, 20.9 us, +3 % CPU per request against `main`'s median of 20.2 to 20.5 us at 3.25 to 3.37 CPUs) and is not a clean pair. Re-run it on a quiet box before the v0.1 bench.
- Bytes per request did not move beyond the runs' spread, as the allocation tests say.

## HTTP/3 strategy

**Spike S1** (`docs/spikes/S1-http3-sni.md`) showed that per-SNI selection and hot-swap work on Kestrel's QUIC listener in .NET 10, through `ServerCertificateSelector`, `TlsHandshakeCallbackOptions` and `ServerCertificateSelectionCallback` alike. Every host that enables H3 gets it, and the single-H3-cert fallback is not needed.

**Listener layout:** two `Listen` calls on :443 (dual-mode `[::]`).
- TCP: `Http1AndHttp2`, `DisableAltSvcHeader = true`, then the connection middleware (PROXY protocol, IP gate), then `TlsHandshakeCallbackOptions`.
- UDP: `Http3` with its own `TlsHandshakeCallbackOptions`, which also performs the stage-A gate. It is added only if `QuicListener.IsSupported`.

**Builder.** The public app runs on the generic host with `UseKestrelCore()`, which leaves out Kestrel's HTTPS configuration and the QUIC transport. `UsePublicHttpsTransports()` adds both (`UseKestrelHttpsConfiguration()` and `UseQuic()`). Without them H3 listeners fail at startup. (`WebApplication.CreateSlimBuilder` has the same gap.) Then `ListenPublicHttp` and `ListenPublicHttps` (see [hosting.md](hosting.md)).

`run-quic-tests.sh` runs the QUIC tests of `Tests.Proxy` and an end-to-end H3 request through `PaduraksaHost` in Docker with libmsquic. A feature set on the QUIC connection context in the callback is visible to the H3 request (`PublicHttp3Tests`, Docker).

**Still open:** a real browser upgrading to H3 (needs a trusted cert), routable IPv6, and arm64 libmsquic.

**`Alt-Svc` is emitted by Paduraksa, not Kestrel.**
- Kestrel's automatic header is disabled (`DisableAltSvcHeader`).
- Ours is precomputed per host, only for H3-eligible hosts, using the **public** HTTPS port from settings (default 443), not the bound port. A port-mapped deployment therefore cannot advertise the wrong port.

H3 is off globally if `libmsquic` is not available (`QuicListener.IsSupported`), and `paduraksa doctor` reports this. The stock `aspnet` image does not ship it: install `libmsquic` from packages.microsoft.com (about 21 MB including `libnuma1`). The image pins 2.5.10 (`deployment.md`); the S1 spike tested 2.6.3.

## Request simulator ("Why was this blocked?")

Admins test their rules before or after a change by asking what a request would get, and why. **The simulator runs the real gate code over a real compiled snapshot, never a reimplementation**, so its answer cannot drift from what production does.

### Design

- **Input.**
  - URL (scheme, host, port, path, query), method, and headers (including the user agent and cookies).
  - The peer IP plus how it arrives: direct, through a trusted generic proxy, via Cloudflare Proxy or Tunnel, with or without PROXY protocol.
  - The TLS context: the name sent in the handshake, ALPN and HTTP version, and TLS version.
  - The surfaces fill in sensible defaults, so a URL and an IP are enough.
- **Target config.** The live snapshot, any stored revision, or a *draft*: a document or pending command compiled through the dry-run path (steps 1 to 4 of the write path in [config.md](config.md)) and never published. "What would this change do to this request" is answerable before committing. A user's open draft is simulated from its review, as one whole-document command.
- **Output.** The outcome (forward to upstream X, fixed answer, redirect, 421 or drop) plus the **trace**:
  - client-IP resolution (which header and hop were trusted);
  - host match (exact, wildcard or default);
  - the TLS-policy check;
  - each policy in order with its verdict and reason;
  - the HTTPS redirect;
  - route match (which route, and the matching view of the path);
  - the upstream (cluster and the destinations currently healthy; load balancing is random, so not which one);
  - the response headers step (below).
- **No cost on the hot path.** Gate steps report through a tracer passed as a **generic struct type parameter**. Production uses a no-op tracer, which the JIT removes entirely. The simulator uses a recording one. The 0 B allocation tests and the gate benchmarks guard it.
- **Stateful and outbound policies** are simulated without side effects (see [below](#stateful-policies-must-not-mutate-when-simulated)). Geo shows the country it resolved (the client step's `country` fact and the access-list step's).
- **Parity tests.** For generated configs and requests, the simulator's outcome must equal the real gate's. **Each policy added must add its trace step and parity cases.**
- **Surfaces.** One Application function, `SimulateAsync(target, request)`. It is exposed as `POST /api/v1/simulate`, as the MCP `simulate_request` tool and in the dashboard simulator page. Access-log entries link into it with the request pre-filled.

### The tracer and the simulator

**The tracer.** `IGateTracer` is passed as a struct type parameter to the one decision body, `GateMiddleware.Decide<TTracer>(context, snapshot, clientAddresses, challenges, tracer)`.
- Production's `Decide(context)` passes `NoGateTracer` (empty methods, so the JIT drops them).
- The Kestrel 0 B tests plus a direct `Decide<NoGateTracer>` allocation test guard it.
- The gate reports: client resolution (peer, source, trusted header, scheme source), the ACME hook, the host match (`HostTable.Find(..., out HostMatchKind)`), the TLS-policy check (`ConnectionTlsCheck.Check` names the 421 reason; `Satisfies` is `Check == None`), each policy by scope and index, the HTTPS redirect, fixed hosts, the route match with the canonical path, and the upstream hand-over.

**`GateSimulator.Run(snapshot, probe, challenges, listeners)`** (`Paduraksa.Proxy.Simulation`) runs the real `Decide` with the recording tracer, after stand-ins for what the server does first, in the server's order:
1. **The listener's admission** (`ProxyProtocolAdmission.Check`, the connection middleware's own decision, from `ListenerFacts`). A PROXY header the listener does not expect, a missing one, an untrusted socket, or no HTTP/3 listener is `ConnectionRejected`.
2. **The TLS handshake** (`TlsHandshakeSelector.Select` over the snapshot's certificate table, which the listeners call too), plus the entry's own TLS-version and ALPN options: unknown SNI, HTTP/3 off, a TLS 1.2 client to a TLS 1.3-only name, the h2 to http/1.1 fallback.
3. **Kestrel's request checks** (`RejectedByServer`, 400):
   - on HTTP/1.1, anything but exactly one valid `Host`;
   - on HTTP/2 and HTTP/3, `:authority` is the host and a `Host` header is ignored;
   - the path is percent-decoded except `%2F` (a `%5C` becomes a backslash), escapes that are not UTF-8 stay as written, and dot segments are removed.

   `SimulatorSocketParityTests` checks all of this against the real server over sockets.

Other details:
- A snapshot not bound to YARP gets placeholder endpoints.
- Client resolution is `TrustedProxyClientAddressSource.ResolveCore` with no counters and no log line, so a simulation changes no counter.
- **Request limits** are the public listeners' outer bounds: 20 KiB of request line, 84 KiB and 128 headers, a token method of at most 32 characters, no control characters in the query, no backslash in the authority. The host's own limits are the gate's, traced as the **Request limits** step. They are counted as Kestrel counts them, in UTF-8 wire bytes, so a character above ASCII costs more than one. The simulator is conservative where a header could be sent as Latin-1.
- A field that would change nothing (`transportIp` without PROXY protocol, `peerIp` on a direct request, `tls` on a plain-HTTP URL) is refused, not ignored.

**`RequestSimulator.SimulateAsync(target, request, origin)`** (Application) takes a target:
- live;
- `RevisionSimulationTarget`;
- `DraftDocumentSimulationTarget` (a `ReplaceConfigCommand` on the current head, retried once on a commit in between);
- `DraftCommandSimulationTarget`;
- `DraftSimulationTarget`, the caller's stored draft: `DraftService.ReadForSimulationAsync` reads it and the head with its entries applied (the replay a review starts from, without the writer's lock), and the simulator plans that document as a replace on the draft's head, retried once like a draft document. An unknown, discarded or someone else's draft is `DraftNotFound`; any entry changed elsewhere is `DraftBlocked`. See [admin-surfaces.md](admin-surfaces.md#the-simulators-targets).

Drafts go through `ConfigService.PlanDraftAsync`, which is `AttemptAsync` as a dry run that hands back its compiled candidate.
- Validation, certificate rules and compile errors are the real command's, returned as `Invalid` with the dry run's errors.
- A stale version or missing object is `DraftRejected`.
- A command that changes nothing simulates the head it planned on.
- The same checks that reading applies (`apiVersion`, null list elements) run on any payload first.
- A corrupt head, a degraded store or an unreadable database is `Unavailable` with a reason, never an exception.
- Nothing is audited, stored or published.

**Concurrency.** At most 4 drafts or revisions compile at once (`RequestSimulator.MaxConcurrentCompiles`). A compile that waits more than 2 s is `Unavailable`. The live target takes no slot.

**Authorization.** `OperationPermissions.ForSimulation` names what each target needs (see [identity.md](identity.md)): `simulate`, plus `config:read` for a revision or a draft that is not stored, or `config:write` for the caller's stored draft.

**Parity tests.**
- `SimulatorParityTests` (Proxy): every outcome, 421 variants, all trusted-proxy modes and header shapes, PROXY protocol, ACME answered and fall-through.
- `SimulatedBehaviourTests` (Unit): 300 generated documents against the `GateHarness`.
- `SimulatorSocketParityTests` (real sockets), and end-to-end tests through a real host.

### Stateful policies must not mutate when simulated

The simulator calls `Evaluate` on the live policy instances. `RequestView.IsSimulation` is true on that path and a constant `false` in production (a static abstract member of the tracer type, folded away by the JIT).

A policy with state peeks:
- a rate limit reports "would allow, N tokens left" without taking one;
- a challenge issues no nonce;
- forward-auth reports "would call `<provider>`" and starts no call unless the request opts in.

Policies without state have nothing to guard. **Each policy adds its `Name` and `Explain` trace hooks and a parity case that checks it left its state alone.** The access-list policy follows the rule (see [waf.md](waf.md)).

### The response-headers step

`IGateTracer.ResponseHeaders(context, host, operations, outcomes)` reports a stage of the host's compiled header list as `GateStepKind.ResponseHeaders` (contract `SimulationStepKind.ResponseHeaders`, "Response headers" in the trace view). The later stage extends the step the earlier one opened.

- It lists each of `Alt-Svc`, `Strict-Transport-Security`, `Server` and the response `X-Request-Id` that the answer carries, with its value.
- For each one it does not carry, it gives the reason: off, no HTTP/3, plain HTTP, an IP literal or loopback name, the upstream's own, removed, or the host's mode.
- The request ID is reported as sent when the response is written, because that is when its one string is materialised.

`SimulatorParityTests` compare the headers the real gate put on the response with the values the trace reports, for every host, scheme and negotiated connection it sweeps, loopback and IP-literal names included. `GateSimulatorTraceTests` covers the modes and the reasons.

### Decisions and gaps

1. **No key is loaded for a draft or a revision.** Whether a certificate is usable is decided from what the vault describes (`ICertificateCatalog.DescribeAsync`: public facts, no key opened).
   - A certificate the vault does not hold, the local CA root, or a chain outside its validity period is unusable, with the real loader's reasons (`CertificateInventoryLoader.ForSimulation` shares the validity logic). A draft binding to one is refused as a real command refuses it, and Auto hosts pick the same certificate.
   - Built-in (placeholder) certificates count as served by this process. What is served is a self-signed placeholder per ID (`SimulationCertificateSource`).
   - The handshake step names the inventory certificate (`ServerCertificate.Id`, set by `CertificateInventoryLoader`). A context not built by the loader has an empty ID and is not named.
   - Without a catalog (no vault registered), every certificate the table calls valid counts as usable. When the certificate table cannot be read, the result says so.
2. **Destination health is live YARP state** (`IUpstreamHealth`, over `IProxyStateLookup`, attached by the public app through `DeferredUpstreamHealth`).
   - A destination of the target config reports the live proxy's answer for the destination with that ID **and address**, `Unknown` otherwise, with a note. The "can't be known for a config that isn't live" note is for non-live targets only.
   - The destination that serves a request is never named.
   - A destination the live proxy keeps out of rotation is `Unhealthy`. YARP counts a destination with no health data yet as available.
3. **The ACME HTTP-01 answer is traced for real.** `GateSimulator.Run` takes the live `AcmeChallengeStore`, and the gate's own `TryAnswerAcmeChallenge` answers or falls through. Challenges are process state, not config, so every target uses the live store.
4. **Trace facts** use stable camelCase wire names (`TraceNames`) for the gate's own values. Header facts use the header's own name (`Server`, `Strict-Transport-Security`, ...), because that is what the person sees on the wire. Policies name themselves explicitly (`static`, `ipAllowList`).
5. **Per-host trusted-proxy overrides** do not exist yet.
6. **The probe takes the HTTP and TLS versions as what the client negotiates.** An HTTP/2 request to a name that offers only http/1.1 is reported as the HTTP/1.1 request it becomes. A request DTO cannot say "no Host header" (the probe can).
7. **An admin host's implicit route** forwards to the admin app (see [hosting.md](hosting.md)). The trace says so, and `GateUpstream.Admin` is true with the `__admin` cluster, rather than naming a user upstream. The parity suite includes one.
