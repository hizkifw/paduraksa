# Spike S1: HTTP/3 + per-SNI certificate selection

**Status: S1 is partially closed. The browser H3 upgrade is still pending.**

What's done:
- Per-SNI certificate selection works on Kestrel's QUIC/HTTP/3 listener, and so does hot-swap. So the [hot-path.md, HTTP/3 strategy](../design/hot-path.md#http3-strategy)
  single-H3-certificate fallback isn't needed. It still works (tested), so it stays available as a contingency.
- `Alt-Svc` with the public port, unknown-SNI rejection and a connection-stage IP gate on QUIC were all verified.

What's pending: the [milestones.md, Risks and spikes](../design/milestones.md#risks-and-spikes) exit criterion also requires that **a real browser actually upgrades to H3** in the Docker
deployment. That hasn't been tested (see [Not covered](#not-covered--follow-ups)).

## What was tested

| | |
|---|---|
| Runtime | .NET 10.0.14 (`Microsoft.AspNetCore.App` 10.0.14, commit `0427b9abbead`) |
| Images (pinned via `ARG`) | build `mcr.microsoft.com/dotnet/sdk:10.0.403-noble`, run `mcr.microsoft.com/dotnet/aspnet:10.0.14-noble` (Ubuntu 24.04.5, x64) |
| System.Net.Quic | 10.0.14; `QuicListener.IsSupported = True`, `QuicConnection.IsSupported = True` |
| libmsquic | **2.6.3** (pinned via `ARG LIBMSQUIC_VERSION`) from `packages.microsoft.com/ubuntu/24.04/prod`. It links the system `libcrypto.so.3` and pulls in `libnuma1` |
| Code | `spikes/S1-http3-sni/` (standalone project, not in `Paduraksa.slnx`). Run with `spikes/S1-http3-sni/run.sh` (non-root `$APP_UID`) |

The spike runs everything in one process inside the container:

- It builds a throwaway PKI (root CA → intermediate CA → ECDSA P-256 leaves `a.test`, `b.test`, `*.wild.test`,
  `default.test`, plus v2 replacements and a fixed "H3" cert covering `*.wild.test` and `a.test`). The test certs
  have **no AIA extension**.
- Every leaf is wrapped in `SslStreamCertificateContext.Create(leaf, [intermediate], offline: true)`.
- The certificates sit behind a `CertStore` that holds a `volatile` reference to an immutable map (exact match, then
  wildcard, then default). That's the shape [config.md, The runtime snapshot](../design/config.md#the-runtime-snapshot)/[hot-path.md, Request pipeline](../design/hot-path.md#request-pipeline) plan for.
- It starts one Kestrel app per mechanism, each on its own port.
- The client is `HttpClient` + `SocketsHttpHandler` with `RequestVersionExact` (2.0 over TCP, 3.0 over QUIC) and a
  fresh handler per probe, so every probe is a new connection. The served cert and chain are captured in
  `RemoteCertificateValidationCallback`. SNI values: `a.test`, `b.test`, `x.wild.test`, `unknown.test`, and
  `127.0.0.1` (an IP literal, so no SNI). The names resolve to 127.0.0.1 via `--add-host`.
- Phase **v1** probes everything. Then `store.Swap(mapV2)` runs, which is one volatile write with listeners
  untouched: `a.test` and the wildcard get new certs, and `b.test` is removed. Phase **v2** probes everything again.
- Two extra groups of probes cover the IP gate and IPv6 dual-mode. They use the container's eth0 address
  (`172.17.0.2`) as a second source address. Last comes a health probe of every server.

## Results (actual output of `run.sh`)

Each cell is `served-cert/chain-elements/certs-received`:
- *chain-elements*: 2 means the client could build leaf + intermediate.
- *certs-received*: `chain.ChainPolicy.ExtraStore.Count`, i.e. the certs the server actually sent. 2 = leaf +
  intermediate, 1 = leaf only.

Labels: `a1 b1 w1 def` = the v1 certs for `a.test`, `b.test`, `*.wild.test` and the default. `a2 w2` = the
certs swapped in. `h3fix` = the fixed H3 cert.

```
| mechanism         | phase | proto | a.test    | b.test    | x.wild.test | unknown.test | 127.0.0.1 | negotiated | alt-svc on response |
|-------------------|-------|-------|-----------|-----------|-------------|--------------|-----------|------------|---------------------|
| static-cert       | v1    | H2    | def/1/1   | def/1/1   | def/1/1     | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8443"          |
| static-cert       | v1    | H3    | def/1/1   | def/1/1   | def/1/1     | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8443"          |
| selector          | v1    | H2    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8444"          |
| selector          | v1    | H3    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8444"          |
| selector+chain    | v1    | H2    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8445"          |
| selector+chain    | v1    | H3    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8445"          |
| tlscb-ctx         | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8446"          |
| tlscb-ctx         | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8446"          |
| tlscb-shared      | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8447"          |
| tlscb-shared      | v1    | H3    | ERR       | ERR       | ERR         | ERR          | ERR       | -          | -                   |
| tlscb-shared-alpn | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8448"          |
| tlscb-shared-alpn | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8448"          |
| tlscb-cert        | v1    | H2    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8449"          |
| tlscb-cert        | v1    | H3    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8449"          |
| tlscb-selcb       | v1    | H2    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8450"          |
| tlscb-selcb       | v1    | H3    | a1/1/1    | b1/1/1    | w1/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8450"          |
| optsel-cb         | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8451"          |
| optsel-cb         | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8451"          |
| split             | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| split             | v1    | H3    | h3fix/1/1 | h3fix/1/1 | h3fix/1/1   | h3fix/1/1    | h3fix/1/1 | HTTP/3.0   | (none)              |
| split-sni         | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| split-sni         | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | (none)              |
| reject-throw      | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | ERR          | ERR       | HTTP/2.0   | h3=":443"           |
| reject-throw      | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | ERR          | ERR       | HTTP/3.0   | (none)              |
| reject-null       | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | ERR          | ERR       | HTTP/2.0   | h3=":443"           |
| reject-null       | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | ERR          | ERR       | HTTP/3.0   | (none)              |
| altsvc-custom     | v1    | H2    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| altsvc-custom     | v1    | H3    | a1/2/2    | b1/2/2    | w1/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | (none)              |
| static-cert       | v2    | H2    | def/1/1   | def/1/1   | def/1/1     | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8443"          |
| static-cert       | v2    | H3    | def/1/1   | def/1/1   | def/1/1     | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8443"          |
| selector          | v2    | H2    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8444"          |
| selector          | v2    | H3    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8444"          |
| selector+chain    | v2    | H2    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8445"          |
| selector+chain    | v2    | H3    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8445"          |
| tlscb-ctx         | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8446"          |
| tlscb-ctx         | v2    | H3    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8446"          |
| tlscb-shared      | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8447"          |
| tlscb-shared      | v2    | H3    | ERR       | ERR       | ERR         | ERR          | ERR       | -          | -                   |
| tlscb-shared-alpn | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8448"          |
| tlscb-shared-alpn | v2    | H3    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8448"          |
| tlscb-cert        | v2    | H2    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8449"          |
| tlscb-cert        | v2    | H3    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8449"          |
| tlscb-selcb       | v2    | H2    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/2.0   | h3=":8450"          |
| tlscb-selcb       | v2    | H3    | a2/1/1    | def/1/1   | w2/1/1      | def/1/1      | def/1/1   | HTTP/3.0   | h3=":8450"          |
| optsel-cb         | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":8451"          |
| optsel-cb         | v2    | H3    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | h3=":8451"          |
| split             | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| split             | v2    | H3    | h3fix/1/1 | h3fix/1/1 | h3fix/1/1   | h3fix/1/1    | h3fix/1/1 | HTTP/3.0   | (none)              |
| split-sni         | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| split-sni         | v2    | H3    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | (none)              |
| reject-throw      | v2    | H2    | a2/2/2    | ERR       | w2/2/2      | ERR          | ERR       | HTTP/2.0   | h3=":443"           |
| reject-throw      | v2    | H3    | a2/2/2    | ERR       | w2/2/2      | ERR          | ERR       | HTTP/3.0   | (none)              |
| reject-null       | v2    | H2    | a2/2/2    | ERR       | w2/2/2      | ERR          | ERR       | HTTP/2.0   | h3=":443"           |
| reject-null       | v2    | H3    | a2/2/2    | ERR       | w2/2/2      | ERR          | ERR       | HTTP/3.0   | (none)              |
| altsvc-custom     | v2    | H2    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/2.0   | h3=":443"           |
| altsvc-custom     | v2    | H3    | a2/2/2    | def/2/2   | w2/2/2      | def/2/2      | def/2/2   | HTTP/3.0   | (none)              |

== Probe errors (condensed) ==
  [tlscb-shared v1|v2 H3 <every SNI>]          AuthenticationException: Application layer protocol negotiation error was encountered.
  [reject-* v1|v2 H2 unknown.test|127.0.0.1]    IOException: Received an unexpected EOF or 0 bytes from the transport stream.
  [reject-* v1|v2 H3 unknown.test|127.0.0.1]    AuthenticationException: Authentication failed because the remote party sent a TLS alert: 'UserCanceled'.
  [reject-* v2 H2|H3 b.test]                    same as above (b.test was removed by the hot swap => now unknown)

== Extra probes: IP gate and IPv6 dual-mode ==
| mechanism       | proto | target     | result                                                                                                        |
|-----------------|-------|------------|---------------------------------------------------------------------------------------------------------------|
| quic-gate       | H2    | 127.0.0.1  | ERR SocketException: Connection reset by peer                                                                 |
| quic-gate       | H2    | 172.17.0.2 | def/2/2 HTTP/2.0                                                                                              |
| quic-gate       | H3    | 127.0.0.1  | ERR AuthenticationException: Authentication failed because the remote party sent a TLS alert: 'UserCanceled'. |
| quic-gate       | H3    | 172.17.0.2 | def/2/2 HTTP/3.0                                                                                              |
| split-sni-v6any | H2    | a.test     | a2/2/2 HTTP/2.0                                                                                               |
| split-sni-v6any | H2    | [::1]      | def/2/2 HTTP/2.0                                                                                              |
| split-sni-v6any | H2    | 172.17.0.2 | def/2/2 HTTP/2.0                                                                                              |
| split-sni-v6any | H3    | a.test     | a2/2/2 HTTP/3.0                                                                                               |
| split-sni-v6any | H3    | [::1]      | def/2/2 HTTP/3.0                                                                                              |
| split-sni-v6any | H3    | 172.17.0.2 | def/2/2 HTTP/3.0                                                                                              |

== Health after all probes (incl. rejections) ==
  every server answers HTTP/2.0 and HTTP/3.0 (reject-throw, reject-null, quic-gate included),
  except tlscb-shared, whose H3 stays broken (ALPN trap below).

== tlscb-shared: ApplicationProtocols on the shared a.test v1 options after probing = [h2, http/1.1] (was null when created) ==
== tlscb-shared-alpn: ApplicationProtocols after probing = [h3, h2, http/1.1] (preset h3, h2, http/1.1) ==
```

Callback invocations, condensed from the run log. Every callback ran once per connection, on both transports:

| mechanism | what the callback saw on TCP | what it saw on QUIC |
|---|---|---|
| `selector` (`ServerCertificateSelector`) | `conn=SocketConnection`, SNI | **`conn=null`**, SNI |
| `tlscb-*` (`TlsHandshakeCallbackOptions`) | `ctx.Connection = SocketConnection` | `ctx.Connection = ConnectionContextAdapter` (internal type), **`RemoteEndPoint` populated** |
| `tlscb-selcb` (`ServerCertificateSelectionCallback`) | `sender = SslStream` | `sender = SslServerAuthenticationOptions` |
| `optsel-cb` (`ServerOptionsSelectionCallback`) | `stream = SslStream` | `stream = null` |
| `quic-gate` | `tcp-gate DENY remote=127.0.0.1` (in `ListenOptions.Use`, pre-TLS); `remote=172.17.0.2` | `quic-gate DENY remote=127.0.0.1`; `remote=172.17.0.2` |
| `split-sni-v6any` | `remote=::ffff:127.0.0.1`, `::1`, `::ffff:172.17.0.2` | `remote=127.0.0.1`, `::1`, `172.17.0.2` |

With no SNI (IP literal), `ServerName` is `""` (empty), not `null`, on both transports.

Mechanisms:

| name | configuration |
|---|---|
| `static-cert` | `UseHttps(X509Certificate2)` (baseline) |
| `selector` | `HttpsConnectionAdapterOptions.ServerCertificateSelector` |
| `selector+chain` | the same, plus `ServerCertificateChain = [intermediate]` |
| `tlscb-ctx` | `UseHttps(TlsHandshakeCallbackOptions)` → a **new** `SslServerAuthenticationOptions { ServerCertificateContext }` per handshake |
| `tlscb-shared` | the same, but returns a **prebuilt, shared** options instance per cert with `ApplicationProtocols = null` |
| `tlscb-shared-alpn` | prebuilt, shared, with `ApplicationProtocols = [h3, h2, http/1.1]` preset |
| `tlscb-cert` | callback → `{ ServerCertificate = cert }` (no context) |
| `tlscb-selcb` | callback → `{ ServerCertificateSelectionCallback = ... }` |
| `optsel-cb` | `UseHttps(ServerOptionsSelectionCallback, state)` |
| `split` | two `Listen` calls on the **same port**: TCP `Http1AndHttp2` per-SNI (callback) + QUIC `Http3` with one fixed cert (`HttpsConnectionAdapterOptions{ServerCertificate, ServerCertificateChain}`). This is the [hot-path.md, HTTP/3 strategy](../design/hot-path.md#http3-strategy) fallback. |
| `split-sni` | two `Listen` calls on the same port, **both** per-SNI via `TlsHandshakeCallbackOptions`, each returning prebuilt per-transport options (TCP: ALPN `h2, http/1.1`; QUIC: ALPN `h3`). TCP has `DisableAltSvcHeader = true`, and middleware emits `h3=":443"`. |
| `reject-throw` / `reject-null` | `split-sni` with no default cert: for an unknown or absent SNI the callback **throws** `AuthenticationException` / **returns null**. Kestrel logs captured at Debug. |
| `quic-gate` | `split-sni` on `0.0.0.0` with an IP deny list `{127.0.0.1}`. TCP: `ListenOptions.Use` connection middleware (`Abort()`). QUIC: the check happens inside the QUIC `TlsHandshakeCallbackOptions` callback, using `ctx.Connection.RemoteEndPoint`, which throws when denied |
| `split-sni-v6any` | `split-sni` with both `Listen` calls on `[::]` (IPv6Any, dual-mode) |
| `altsvc-custom` | `tlscb-ctx` on one H1/H2/H3 listener + `DisableAltSvcHeader = true` + middleware emitting `Alt-Svc: h3=":443"; ma=86400` on non-H3 responses |

Source citations below are against the `v10.0.14` tags of dotnet/aspnetcore and dotnet/runtime.

## Answers

### Q1. Does per-SNI selection work on QUIC, and through which mechanisms?

**Yes, through every mechanism tested.** Kestrel invokes the selector or callback per QUIC connection with the
client's SNI, and the right leaf is served over HTTP/3. Results:

- (a) `HttpsConnectionAdapterOptions.ServerCertificateSelector`: **works on QUIC**. The selector gets
  `ConnectionContext = null` on QUIC. `CreateHttp3Options` wraps it as `ServerCertificateSelector(null, host)`
  (aspnetcore `HttpsConnectionMiddleware.cs:562-575`).
- (b) `UseHttps(TlsHandshakeCallbackOptions)`: **works on QUIC**. `ctx.ClientHelloInfo.ServerName` is populated.
- (c) `ServerCertificateSelectionCallback` on the options returned by the callback: **works on QUIC**.
  `UseHttps(ServerOptionsSelectionCallback, state)` also works; `stream` is `null` on QUIC.

**Nothing is rejected at startup**, with one condition:

- With `WebApplication.CreateSlimBuilder()`, **every** `UseHttps(HttpsConnectionAdapterOptions | TlsHandshakeCallbackOptions | ServerOptionsSelectionCallback)`
  on an H3-enabled listener fails startup with
  `InvalidOperationException: Call UseKestrelHttpsConfiguration() on IWebHostBuilder to enable transport layer security for HTTP/3.`
  Only `UseHttps(X509Certificate2)` started. Reproduce with `run.sh --no-https-config`. The fix is to call
  `builder.WebHost.UseKestrelHttpsConfiguration()`. `CreateBuilder()` includes it already. The slim builder also needs
  `builder.WebHost.UseQuic()`.

**No mechanism silently fell back to a different cert.** There are two traps, though:

1. **Both transports write ALPN into the `SslServerAuthenticationOptions` instance you return, if you left it null.**
   - TCP: `HttpsConnectionMiddleware.ServerOptionsCallback` calls `ConfigureAlpn` when `sslOptions.ApplicationProtocols == null`
     (`HttpsConnectionMiddleware.cs:470-473`). That assigns a new `[h2, http/1.1]` list (`:370-385`).
   - QUIC: `QuicConnectionListener` does `serverAuthenticationOptions.ApplicationProtocols ??= [h3]`
     (`Transport.Quic/src/Internal/QuicConnectionListener.cs:79`).

   So with a shared instance, **whichever transport handshakes first fixes the ALPN for the other**. In this run TCP
   went first, so the object ended up as `[h2, http/1.1]` and every later H3 handshake failed with
   `Application layer protocol negotiation error` (`tlscb-shared`). The other order would break H2/H1 instead. It's
   also an unsynchronised write to an object shared across connection threads.

   Kestrel doesn't make this visible at Warning or above. Handshake failures are logged at **Debug**
   (`HttpsConnectionMiddleware` `AuthenticationFailed`, EventId 1, on TCP). `QuicConnectionListener` also has a
   `ConnectionListenerApplicationProtocolsNotSpecified` check, but the `??=` fills the list in before that check runs.

   Prebuilt options are fine **if `ApplicationProtocols` is set explicitly**: `tlscb-shared-alpn` stayed unmutated
   and worked on both transports.
2. **Only a `SslStreamCertificateContext` reliably sends the intermediate.** Our test certs have no AIA extension,
   and the intermediate isn't in any OS store. Under those conditions, everything except a context sent **only the
   leaf** (`/1/1`), on both transports:
   - `ServerCertificateSelector` (even with `ServerCertificateChain` set)
   - `{ ServerCertificate = cert }`
   - `{ ServerCertificateSelectionCallback }`
   - the `HttpsConnectionAdapterOptions{ServerCertificate, ServerCertificateChain}` path used for QUIC in `split`

   Why, from the source:
   - `ServerCertificateChain` is only used in the fixed-cert branch of the middleware constructor
     (`HttpsConnectionMiddleware.cs:112`, `SslStreamCertificateContext.Create(certificate, additionalCertificates: options.ServerCertificateChain)`).
     That branch is skipped entirely when `ServerCertificateSelector` is set (`:90-94`).
   - `CreateHttp3Options` copies only `ServerCertificate` (`:547-558`), so the chain never reaches QUIC.
   - For a bare cert, the transports then build the chain themselves:
     - **TCP/SslStream:** `SslStreamCertificateContext.Create(certificate, null, offline: false, ...)` (runtime
       `SslAuthenticationOptions.cs:196`). With a real CA whose leaf has an AIA URL (e.g. Let's Encrypt), this **may
       fetch the intermediate over the network during the handshake**. The chain would usually come out complete,
       but the handshake would depend on outbound HTTP to the CA.
     - **QUIC/MsQuic:** `Create(cert, null, offline: true)` from local stores only (runtime
       `MsQuicConfiguration.cs:205-212`). That means leaf-only unless the intermediate happens to be in an OS store.

   Neither is acceptable for a proxy: network I/O in the handshake on TCP, a possibly incomplete chain on QUIC. The
   recommendation is unchanged: always hand Kestrel a prebuilt context. `ServerCertificateContext` sent the
   intermediate (`/2/2`) on both transports.

### Q2. Hot-swap

**Works for TCP and QUIC, with no listener restart.** After `store.Swap(mapV2)`, which is one volatile write, every
new connection on every per-SNI mechanism (H2 and H3) served `a2`/`w2`. `b.test` fell back to the default cert,
or was rejected in `reject-*`. QUIC does not cache the selection: the callback runs on every new QUIC connection,
so swapping certs doesn't defeat the System.Net.Quic MsQuic configuration cache. Existing connections keep the
cert they negotiated, which is normal TLS behaviour and wasn't probed further.

### Q3. Alt-Svc

- By default, Kestrel adds `alt-svc: h3=":<bound port>"` to every response on a listener that has H3 enabled,
  including the H3 responses themselves. The port is the **bound** listener port (`h3=":8446"` etc.). It knows nothing
  about the public port, so behind a port mapping (e.g. `-p 443:8443`) it advertises the wrong port. No `ma`
  parameter is emitted.
- `ListenOptions.DisableAltSvcHeader = true` fully suppresses it (`altsvc-custom`: only our value is present).
- Our own `Alt-Svc: h3=":443"; ma=86400`, set in middleware on non-H3 responses, arrives intact over H2.
- A listener whose `Protocols` don't include H3 (the TCP side of `split` / `split-sni`) never emits Kestrel's header.

### Q4. Fallback (single H3 cert on QUIC, per-SNI on TCP, same port)

**Not needed, but validated.** One `Listen` can't take a different cert source for QUIC and TCP. The callback could
tell them apart by `ctx.Connection`'s runtime type, but that's an internal type (`ConnectionContextAdapter`), so that
approach is fragile. **Two `Listen` calls on the same IP:port work**: one `HttpProtocols.Http1AndHttp2` (TCP) and one
`HttpProtocols.Http3` (UDP). Kestrel binds both without complaint, and each gets its own `UseHttps(...)`. In `split`,
TCP served per-SNI and QUIC served `h3fix` for every name. Its QUIC side used
`HttpsConnectionAdapterOptions{ServerCertificate, ServerCertificateChain}` and **did not send the intermediate**
(Q1, trap 2). If the fallback is ever used, give it a `TlsHandshakeCallbackOptions` that returns a
`ServerCertificateContext` instead.

### Q5. `SslStreamCertificateContext.Create(cert, chain, offline: true)` on TCP and QUIC

**Supported on both**, and it is the only tested way that sends the intermediate on both transports (`/2/2` for H2
and H3 in `tlscb-ctx`, `tlscb-shared-alpn`, `optsel-cb`, `split-sni`, `altsvc-custom`). On QUIC, System.Net.Quic
takes `ServerCertificateContext.IntermediateCertificates` directly (`MsQuicConfiguration.cs:109`). Contexts are built
once at snapshot compile time and reused across connections and transports. `offline: true` means no AIA/OCSP
fetches while building them.

### Q6. Unknown-SNI rejection (throw vs return null)

Policy under test: no exact or wildcard match, or no SNI at all → reject.

| | client sees (TCP / H2) | client sees (QUIC / H3) | server log | server afterwards |
|---|---|---|---|---|
| **throw** `AuthenticationException` | `IOException: Received an unexpected EOF or 0 bytes from the transport stream` (connection closed, no TLS alert) | `AuthenticationException: ... remote party sent a TLS alert: 'UserCanceled'` | TCP: **Debug** `Failed to authenticate HTTPS connection. [AuthenticationException: unknown SNI]`. QUIC: **nothing** from Kestrel at any level (the connection is never "accepted") | healthy: known names keep working on both transports, including after the swap and in the final health probe |
| **return null** | same EOF | same `UserCanceled` alert | TCP: **Error** `Unhandled exception while processing <conn>. [NullReferenceException]`. QUIC: nothing | healthy |

Returning null "works", but only by crashing into a `NullReferenceException` on TCP, and on QUIC via the `??=` at
`QuicConnectionListener.cs:79`. **Throw** is the right way to reject. Kestrel logs QUIC rejections nowhere, so
Paduraksa must count rejections itself in the callback: a `tls_rejected{reason=unknown_sni|ip_denied}` counter, plus
the access log if desired.

### Q7. QUIC connection-stage gating (stage A for H3)

`ListenOptions.Use` connection middleware runs on TCP only, so for H3 the stage-A checks (IP deny, per-IP cap,
ClientInfo) must live in the QUIC listener's `TlsHandshakeCallbackOptions.OnConnection`. Verified:

- **`ctx.Connection.RemoteEndPoint` is populated on QUIC** (an `IPEndPoint`: `127.0.0.1`, `172.17.0.2`, `::1`).
- **Throwing from the QUIC callback rejects the connection.** The client gets the `UserCanceled` TLS alert. The
  server stays healthy: an allowed source (`172.17.0.2`) is served over H3 right after the denied ones.
- The TCP equivalent (`ListenOptions.Use` + `ConnectionContext.Abort()`, pre-TLS) produces `Connection reset by peer`
  on the client. It's logged at Debug as `sending RST because: The connection was aborted by the application`.
- Cost difference: the TCP gate runs before any TLS work. The QUIC gate runs after MsQuic has accepted the Initial
  packet and parsed the ClientHello, but before any certificate or signature work. That's cheaper than a full
  handshake, but it's not a free drop. Volumetric UDP floods remain a firewall/host concern.
- **Dual-mode trap:** on a `[::]` listener, TCP reports IPv4 clients as **IPv4-mapped** (`::ffff:127.0.0.1`), while
  QUIC reports plain IPv4 (`127.0.0.1`). The gate must normalise with `IsIPv4MappedToIPv6 ? MapToIPv4() : addr`
  before the CIDR-trie lookup (the spike does), or deny rules silently miss on one transport.

### IPv6 dual-mode

**Works.** Two `Listen(IPAddress.IPv6Any, port)` calls (TCP H1/H2 + UDP H3) accept IPv4 (`127.0.0.1`,
`172.17.0.2`) and IPv6 (`::1`) clients on both transports, with per-SNI selection intact (`split-sni-v6any`). The
only caveat is the mapped-address difference above. This was tested only on a container without a routable IPv6
network (loopback `::1` only).

## Recommendation for Paduraksa

1. **Use per-SNI on QUIC.** Drop the "single H3 certificate" fallback from [hot-path.md, HTTP/3 strategy](../design/hot-path.md#http3-strategy) as the default plan. The per-host
   "H3 eligible" logic reduces to: H3 is enabled globally (libmsquic present), and the host hasn't opted out.
   Keep the fallback only as a documented contingency.
2. **Listener layout per HTTPS endpoint (e.g. :443): two `Listen` calls on the same address and port.** Use
   `IPAddress.IPv6Any` for dual-stack.
   ```csharp
   builder.WebHost.UseKestrelHttpsConfiguration();     // mandatory with CreateSlimBuilder (H3 + UseHttps(options))
   builder.WebHost.UseQuic();                          // slim builder only
   o.Listen(addr, 443, lo => {                         // TCP
       lo.Protocols = HttpProtocols.Http1AndHttp2;
       lo.DisableAltSvcHeader = true;                  // belt and braces; H1/H2-only listeners don't emit it anyway
       lo.Use(stageA.TcpMiddleware);                   // PROXY protocol, IP deny, per-IP cap, ClientInfo (pre-TLS)
       lo.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = tls.SelectTcp });
   });
   if (QuicListener.IsSupported)
       o.Listen(addr, 443, lo => {                     // UDP
           lo.Protocols = HttpProtocols.Http3;
           lo.UseHttps(new TlsHandshakeCallbackOptions { OnConnection = tls.SelectQuic });
       });

   // TlsSelector (hot path: one volatile read, no allocation, no await)
   ValueTask<SslServerAuthenticationOptions> SelectQuic(TlsHandshakeCallbackContext ctx)
   {
       var snap = SnapshotStore.Current;
       // Stage A for QUIC: ListenOptions.Use is TCP-only, so gate here, before cert selection.
       var ip = ((IPEndPoint)ctx.Connection!.RemoteEndPoint!).Address;
       if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();  // TCP on [::] reports ::ffff:a.b.c.d, QUIC reports a.b.c.d
       if (snap.IpDeny.Contains(ip) || !connLimiter.TryAcquire(ip))
       {
           Metrics.TlsRejected(Reason.IpDenied);         // Kestrel logs nothing for QUIC rejections
           throw new AuthenticationException("denied");  // client: TLS alert UserCanceled
       }
       // ClientInfo for H3 gets computed here and stashed, e.g. in ctx.Connection.Features
       // (wiring to the request's connection features: verify in M1).
       var entry = snap.Tls.Lookup(ctx.ClientHelloInfo.ServerName);  // exact → wildcard → default | null
       if (entry is null) { Metrics.TlsRejected(Reason.UnknownSni); throw new AuthenticationException("unknown SNI"); }
       return ValueTask.FromResult(entry.QuicOptions);
   }
   // SelectTcp: same lookup (the IP gate already ran in TcpMiddleware), returns entry.TcpOptions.
   ```
   **Options cache key.** Prebuild `SslServerAuthenticationOptions` per **(resolved host TLS policy, transport)**,
   deduplicated on **(cert, TLS policy, transport)**, at snapshot compile time. Keying per (cert, transport) is not
   enough. [hot-path.md, B. TLS handshake](../design/hot-path.md#b-tls-handshake) allows per-host min TLS version and per-host ALPN, and several hosts can share one wildcard or the
   default cert while having different policies. Each SNI table entry points at the deduplicated instance for its
   host:
   - **TCP:** `ServerCertificateContext`, `EnabledSslProtocols` = the host's minimum and up, and an **explicit**
     `ApplicationProtocols` (`[h2, http/1.1]`, or the host's restricted list).
   - **QUIC:** the same context, and an **explicit** `ApplicationProtocols = [h3]`. QUIC is always TLS 1.3, so the
     min-version policy is moot there. A host that has opted out of H3 maps to no QUIC entry, i.e. it is rejected or
     falls to the default, and gets no `Alt-Svc`.

   The instances are immutable after compile. **Never** emit one with `ApplicationProtocols == null` (Q1, trap 1).
   Add a compiler unit test asserting non-null, non-empty ALPN on every prebuilt options object. This is the
   `split-sni` mechanism, and it allocates nothing per handshake. It also lets H3 be dropped by simply not adding the
   UDP listener.
3. **Certificates:** always `ServerCertificateContext` from `SslStreamCertificateContext.Create(leaf, intermediates, offline: true)`.
   Don't use `ServerCertificateSelector`, `ServerCertificate` or `ServerCertificateChain` (Q1, trap 2).
4. **Unknown SNI / rejection:** reject by **throwing** from the callback, never by returning null (Q6). An empty
   `ServerName` means no SNI. Emit our own counters, because QUIC rejections are invisible in Kestrel logs.
5. **Alt-Svc:** emitted by the gate from the precomputed per-host bytes, using the configured **public** port and
   `ma=86400`, only on non-H3 responses of H3-enabled hosts. Kestrel's header never appears with this layout (the
   TCP listener has no H3, and `DisableAltSvcHeader` is set). That's as planned in [hot-path.md, HTTP/3 strategy](../design/hot-path.md#http3-strategy).
6. **Docker image ([deployment.md, Overview](../design/deployment.md#overview)):** the stock `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04 noble) **does not include
   libmsquic**.
   - Add the `packages.microsoft.com` repo (`/config/ubuntu/24.04/packages-microsoft-prod.deb`) and install a
     pinned `libmsquic=<ver>` (2.6.3 tested). It depends on `libnuma1` and the system `libcrypto.so.3`, and added
     about 21 MB.
   - Pin the base image tags. Set `ASPNETCORE_HTTP_PORTS=` (empty) to avoid Kestrel's "Overriding address(es)"
     warning.
   - Publish `443/udp` as well as `443/tcp`.
   - See `spikes/S1-http3-sni/Dockerfile`.
   - Not verified: **arm64** availability of the package (needed for the multi-arch image), and **chiseled** bases,
     which have no apt, so `libmsquic.so.2` + `libnuma.so.1` would have to be copied in from a build stage.

## Not covered / follow-ups

- **Real browser upgrade to H3** is still open, and it's what keeps S1 only partially closed. It wasn't tested: no
  browser in the container, and the host has no libmsquic. The pieces it depends on were verified: correct cert over
  QUIC, intermediate sent, and a correct `Alt-Svc` with the public port. Verify against Chrome/Firefox with the M1
  build, using a publicly trusted or locally trusted CA, since browsers won't use H3 with an untrusted cert or a
  user-accepted exception.
- How H3 requests reach the ClientInfo computed in the QUIC callback (feature plumbing from the QUIC connection to
  `HttpContext`). The `RemoteEndPoint` part is verified; the plumbing is untested.
- IPv6 dual-mode on a host with a routable IPv6 network (only `::1` was tested). arm64.
- Behaviour with a real AIA-bearing chain (Let's Encrypt staging) on the TCP bare-cert path. That's moot if
  contexts are always used, as recommended.
