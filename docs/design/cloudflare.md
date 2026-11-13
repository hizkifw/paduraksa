# Client IP behind Cloudflare

Many homelabs sit behind Cloudflare's proxy (orange cloud) or a Cloudflare Tunnel. Getting the real client IP has to be a toggle, not a hand-maintained CIDR list. This page covers Paduraksa's native Cloudflare support: who is trusted, which headers are read, edge-range refresh, origin lockdown and Authenticated Origin Pulls.

It is a built-in **trusted-proxy provider**. The generic trusted-proxy model (CIDRs plus a header) and future providers use the same machinery.

Contents:
- [Modes and trust](#modes-and-trust)
- [Which header, and anti-spoofing](#which-header-and-anti-spoofing)
- [What the real IP feeds](#what-the-real-ip-feeds)
- [Optional extras](#optional-extras)
- [Cloudflare ranges, origin lockdown and Authenticated Origin Pulls (M7)](#as-built-m7)

## Modes and trust

**Modes:**
- `Off`.
- `Proxy`: Cloudflare connects to us directly.
- `Tunnel`: a local `cloudflared` connects to us.

The mode is set globally and meant to be overridable per host. M1 has the global setting only (`DefaultsSpec.TrustedProxies`: mode, trusted CIDRs, generic header, `True-Client-IP` and X-Forwarded-For fallbacks). The per-host override was planned for M2 and M7. The wizard asks "Is this server behind Cloudflare?" and offers to set it.

**Who is trusted:**
- **`Proxy`:** the TCP peer (after PROXY protocol, if any) must fall within **Cloudflare's published edge ranges**.
  - They ship as a baked-in list and are refreshed by a background job from `https://api.cloudflare.com/client/v4/ips`, falling back to `https://www.cloudflare.com/ips-v4` and `ips-v6`.
  - They are stored with a fetch timestamp and compiled into the snapshot as a CIDR trie.
  - A failed refresh keeps the last good list, and raises a dashboard warning if it is stale (more than 30 days old).
- **`Tunnel`:** the peer must be the configured `cloudflared` address (loopback, a Docker network CIDR or a specific IP). Edge ranges do not apply, because the TCP peer is `cloudflared`.

## Which header, and anti-spoofing

- **Which header.** `CF-Connecting-IP` (a single value, so there is no X-Forwarded-For chain parsing). Optionally `True-Client-IP` (Enterprise), or the right-most untrusted entry of `X-Forwarded-For` as a fallback.
  - The value is parsed span-based into the same `UInt128` client-IP representation used everywhere, with IPv4-mapped addresses normalised.
  - A missing or unparsable header from a trusted peer counts as a counter plus a debug log, and the peer IP is used.
- **Anti-spoofing.** When the peer is **not** trusted, **every `cf-*` header** and `True-Client-IP` are ignored, and they are **stripped before forwarding upstream**.
  - Cloudflare adds more `cf-*` headers over time, and Cloudflare Access's `Cf-Access-Jwt-Assertion` and `Cf-Access-Authenticated-User-Email` carry identity.
  - Upstreams always get a clean `X-Forwarded-For` and `X-Real-IP` built from the resolved client IP (see "Request pipeline" in [hot-path.md](hot-path.md)).

## What the real IP feeds

WAF IP allow/deny, geo, rate-limit keys, access lists, forward-auth headers, access logs and metrics.

Connection-stage limits necessarily see the Cloudflare edge IP. They must therefore exempt trusted Cloudflare peers from per-IP connection caps, or every visitor would share a handful of edge IPs. Built: the cap never counts a trusted peer, and the connection deny list still applies to every connecting address (see "The connection stage" in [hot-path.md](hot-path.md)).

## Optional extras

Per host or global.

- **Geo from `CF-IPCountry`.** When the peer is trusted, use it instead of a local Geo DB lookup. It is free and accurate. `XX` maps to "unknown" and `T1` to a Tor pseudo-country rules can name. Built in M7 as the only country source of that wave (see "Country rules" in [waf.md](waf.md)).
- **`CF-Visitor` scheme.** The original scheme for the HTTP-to-HTTPS redirect decision, when Cloudflare connects to the origin over plain HTTP ("Flexible" SSL, or `cloudflared` forwarding to port 80). This avoids redirect loops. Built in M1 (see "Gate middleware" in [hot-path.md](hot-path.md)) and always on in the Cloudflare modes. Flexible is counted and logged once, and the dashboard (M5) will warn that it is insecure.
- **Origin lockdown.** Reject (or 403) requests on a Cloudflare-mode host whose peer is not Cloudflare (or `cloudflared`), so the origin cannot be reached by IP to bypass Cloudflare's WAF. It is enforced at the connection stage when *all* hosts on a listener require it.
- **Authenticated Origin Pulls.** Request a client certificate from Cloudflare and verify it against Cloudflare's origin-pull CA (shipped, replaceable) on the TCP listener (per-SNI `ClientCertificateRequired`). This is the strong form of origin lockdown.
- **`CF-Ray`** is recorded in access logs and offered as a variable on error pages, next to our request ID, so issues can be correlated with the Cloudflare dashboard.
- **Certificates.** Cloudflare Origin CA certificates can be uploaded like any other (see [vault-and-certificates.md](vault-and-certificates.md)), with a hint that they are only trusted by Cloudflare. DNS-01 via the Cloudflare DNS provider works whether or not the proxy mode is on.
- **Request simulator.** It takes a "via Cloudflare" toggle, so users can see which IP and country the policies would see (see "Request simulator" in [hot-path.md](hot-path.md)).

## As built (M7)

### The host setting

`HostSettings.Cloudflare` (`CloudflareSettings`, the last field, merged field by field like the other nested records) has:
- `originLockdown`: `off` (default), `forbid` or `drop` (the shared enum `OriginLockdownMode`);
- `authenticatedOriginPulls`: bool, default off.

Both take effect only when the global trusted-proxy mode is `CloudflareProxy` or `CloudflareTunnel`. The compiler resolves them to `Off` otherwise, so the gate never looks at the mode.

Warnings:
- `cloudflare.needsMode`: a host sets either under `Off` or `Generic`.
- `cloudflare.aop.needsHttps`: Authenticated Origin Pulls on a host with no HTTPS. It can only refuse, and says so.
- `cloudflare.aop.tunnel`: Authenticated Origin Pulls under a Tunnel. `cloudflared` presents no client certificate, so it can only refuse, and says so.

### Edge-range refresh

`CloudflareRangeRefresher` (Application) is a hosted service of the core host. `Paduraksa:Cloudflare:RangeRefresh:Enabled` defaults to true, and the URLs, start delay and interval are overridable.

**What it fetches.** `https://api.cloudflare.com/client/v4/ips` (`result.ipv4_cidrs` and `ipv6_cidrs`), and the plain-text `ips-v4` and `ips-v6` when that fails or its list is refused.

**When.**
- Two minutes after start, then daily with up to an hour of jitter.
- A failure retries after 5 minutes, doubling to 6 hours.
- Each request has a 15 s timeout, no redirects and a 64 KiB body cap.

**What it accepts.** A list is accepted only if `EdgeRangeList.TryCreate` passes it:
- every entry is a strict CIDR with a prefix;
- at least three IPv4 and three IPv6 blocks;
- nothing wider than /8 (IPv4) or /12 (IPv6), so no `0.0.0.0/0`;
- at most 512.

Otherwise it is logged, counted as a failure and ignored, and the last good list stays.

**Storage.** An accepted list is stored with its fetch time in `<data dir>/cloudflare-ranges.json` (atomic write). A read goes through the same checks, so a damaged or edited file is ignored. It is in force from the first compile after a restart.

**Proxy never touches the network or the disk.**
- `CloudflareEdgeRangeSource` (a process singleton) holds the current list and the failure count.
- `SnapshotCompilerOptions.CloudflareRanges` points the compiler at it.
- `RuntimeSnapshot.EdgeRanges` records the list a snapshot was compiled with (null unless the mode is `CloudflareProxy`, the only one that trusts them).

**A changed list is not a config change.** The job calls `ConfigService.RepublishLiveAsync`, which compiles the live document again and publishes it with no new revision, exactly as a certificate change does.
- It does so only while the live snapshot trusts the edge ranges. A refetch of the same blocks only moves the list's age.
- The publish is **level-triggered**. `RepublishLiveAsync` reports whether it queued one. A change (a new list, a new warning, or a live snapshot whose list differs from the one in force) stays pending until it did, tried again on the failure backoff and at every refresh. One failed or skipped attempt therefore cannot leave a revoked range trusted.

The built-in list (`CloudflareIpRanges`) is the list in use until a fetched one is stored.

### The warning

In `CloudflareProxy` mode the compile adds `cloudflare.ranges.stale` (at `/settings/trustedProxies`, so it shows where config warnings do) when the list in use is older than 30 days or three or more refreshes in a row failed. The job compiles again when that warning appears, changes or clears.

- The dashboard's client-address card says "Cloudflare ranges: updated 3 days ago (22 ranges)", "built-in list from 2026-11-06 (22 ranges)", or the warning.
- The overview's attention list carries the warning.
- The simulator's client-address step has an `edgeRanges` fact: which list, how many ranges, when it was fetched and how many days ago.

### Origin lockdown

A request for a host with lockdown on whose peer is not a trusted peer of the snapshot's policy is refused before anything else of the host. The peer is the PROXY-protocol source when there is one, else the socket.

- It happens right after the host lookup and its `Server` header, before the TLS check, the policies and the redirect.
- `forbid` answers 403 with a built-in page. `drop` aborts the request. Both are constants, so the decision allocates nothing.
- It uses `ClientIpResolution.PeerTrusted`, the decision the client-IP resolution already made. A forged `CF-Connecting-IP` or `X-Forwarded-For` from an untrusted peer changes nothing.
- An unknown host is the default behaviour, not a lockdown.

**At the connection stage.** The compiler decides once per snapshot whether **every** host in it is locked down (`RuntimeSnapshot.LockdownAtConnection`). It counts the enabled hosts, and an admin host counts like any other.
- An untrusted peer is then dropped before TLS: `OriginLockdownMiddleware` on the HTTPS TCP listener (after `UseClientInfo`, so the address is the PROXY-protocol source's) and the QUIC handshake gate (`OriginLockdown.CreateQuicGate`, which also publishes the `ClientInfo`).
- Both modes drop there, since nothing can be answered before TLS. The TCP drop allocates the abort exception per connection like the PROXY-protocol one (the connection itself allocates far more). Both are counted in `OriginLockdownCounters`.

**The plain-HTTP listener has no connection-stage check on purpose.**
- An ACME HTTP-01 validation reaches a DNS-only record from the CA's own address, never from Cloudflare. So the gate answers a published challenge token before any host decision (as it does for every request) and leaves everything else of a locked-down host refused per request.
- Behind a proxied (orange-cloud) record the validation arrives from Cloudflare's ranges and passes either way.

**The admin host.** A locked-down admin host is a host like any other: only reachable through Cloudflare. The dashboard row says it covers the admin host but not the admin port (9443), which is a different listener.

The lock-out guard (see [hosting.md](hosting.md)) models a caller of a locked-down admin host as arriving through Cloudflare. Otherwise its probe would never have been forwarded and the guard would let a deletion through. A change that adds lockdown to the admin host is refused for a caller who may have come directly.

### Authenticated Origin Pulls

**The CA.** Cloudflare's zone-level origin-pull CA is `https://developers.cloudflare.com/ssl/static/authenticated_origin_pull_ca.pem`, fetched 2026-11-11, self-signed, valid until 2029-11-01, SHA-256 `9A1AC2B4...25A2B9`. It is an embedded resource (`cloudflare-origin-pull-ca.pem`, whose header comment records this), loaded by `OriginPullCa.Default`.

**The TLS entry.** A host with the setting gets its own TLS entry:
- `ClientCertificateRequired = true`.
- The chain is built offline (no AIA or revocation fetch) with `X509ChainTrustMode.CustomRootTrust` against that CA alone. Validity dates are checked, and a certificate from any other authority, a public one included, fails the handshake.
- The setting is part of the entry's deduplication key. The `TlsPolicy` record carries the `ClientCertificatePolicy`, which holds the roots by reference. Two such hosts on one certificate share options, and an ordinary host on it does not.
- The host's entry sets `AllowTlsResume = false`. A resumed TLS 1.3 session may carry no client certificate, so those hosts do a full handshake every time. Every other host keeps resumption.

**The gate's second line**, before the TLS check, refuses a request on a connection that carries no verified client certificate. Kestrel sets `ITlsConnectionFeature.ClientCertificate` only when the handshake accepted one.
- **421** when the connection is TLS and its SNI does not select the host's TLS entry (a coalesced connection, or no SNI at all: the handshake never asked, and Cloudflare's retry on a connection of its own will), like every other coalescing mismatch.
- **403** for plain HTTP, which cannot carry a certificate, and for a connection whose SNI does name the host and still has none.

**Not over HTTP/3.** Cloudflare does not connect to origins over QUIC. An Authenticated Origin Pulls host therefore compiles with `Http3Enabled = false` whatever its own setting says:
- no `Alt-Svc` for it (the header step says why);
- no QUIC options (its QUIC handshake is refused and counted as `Http3Disabled`);
- an HTTP/3 request for it on another name's connection is refused.

**Replacing the CA.** `Paduraksa:Cloudflare:OriginPullCaFile` (a PEM file read once at startup, relative to the content root) replaces it without a release. One that holds no certificate stops the start.

**Gap.** There is no admin upload of a replacement through the certificate machinery. The inventory models server certificates with keys, and a trust anchor without one is a new kind of object, so it stays a deployment setting for now.

Cloudflare's own zone setting must be turned on too, or no request gets through. The dashboard row says so.

### Simulator

New steps: `GateStepKind.OriginLockdown` and `GateStepKind.ClientCertificate`.
- The lockdown step reads "peer 1.2.3.4 is not a Cloudflare edge address: refused (403)", or in Tunnel mode "not the configured cloudflared address".
- The listener's admission step also reports the connection-stage drop: HTTPS before TLS, after PROXY protocol; QUIC after the handshake's name selection, as the callback does.
- The handshake step fails when the entry requires a client certificate the probe does not present.
- `GateProbe.ClientCertificate` defaults to "presented exactly when the arrival is `CloudflareProxy`". The existing "via Cloudflare" arrival therefore gives both outcomes, and a direct or tunnel arrival the refusals.
- `GateSimulator.Run` takes the time the edge-range age is read against.

### Tests

- `OriginLockdownTests`: compiler resolution, both modes, tunnel, PROXY protocol source, forged headers, order before the TLS check and policies, the ACME exception, the connection stage on a real TCP listener and the QUIC gate, simulator parity, 0 B on every path (also on Kestrel in `GateAllocationTests`).
- `AuthenticatedOriginPullsTests`: a test CA standing in for Cloudflare's, handed in through `SnapshotCompilerOptions.OriginPullCa`. It covers valid, absent, foreign, expired and self-signed certificates, no AIA fetch, the coalesced and non-SNI refusals, dedup keys, no `Alt-Svc`, the shipped CA's identity, the simulator, and 0 B.
- `CloudflareEdgeRangesTests`.
- `CloudflareRangeRefresherTests`: a fake Cloudflare with JSON, text fallback, refused lists, size, timeout, backoff, and republish only on change.
- `CloudflareRangeRefreshHostTests`: a real host against a loopback Cloudflare, including the restart.
- The QUIC cases (`KestrelQuicTlsTests`, trait `Transport=Quic`) run only through `run-quic-tests.sh`.

### The overview

The security overview's per-host row (`HostProtections.OriginLockdown` and `AuthenticatedOriginPulls`, read from `CompiledHost`) has a Cloudflare column: "Cloudflare only (403)" or "Cloudflare only (drop)", "Origin pulls", or None. Both are off in the compiled host unless the mode is a Cloudflare one, so the column says what the gate does.
