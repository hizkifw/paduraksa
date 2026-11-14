# Observability, logs, analytics and notifications

Nothing in this area runs on the request path except a few constant-time writes into fixed buffers. The gate and the access-log middleware record into a ring and into striped counters, and background services read them. This page covers the access and error logs, the request ID, counted body bytes, metrics and `/metrics`, the live tail, traffic analytics, upstream health, notifications and their outbox.

Contents:
- [Overview](#overview)
- [The log files](#the-log-files-m6)
- [The access log pipeline](#the-access-log-pipeline-m6)
- [Counted body bytes](#counted-body-bytes)
- [Request timing](#request-timing)
- [Metrics](#metrics-m9)
- [The live tail](#the-live-tail-m9)
- [Traffic analytics](#traffic-analytics)
- [Health state and notifications](#health-state-and-notifications-m9-part-2a)
- [Durable delivery and history](#durable-delivery-and-history-notify-outbox)

Everything here sits behind the request path. A request records into fixed-size, lock-free structures (a ring buffer, striped counters, histograms), and background services read them. No request ever waits on a log write, a database or a reader.

## Overview

- **Counters.** A `long[]` indexed by `metricSlot * K + metric`, using stable slots (never the snapshot's host index; see [config.md](config.md)). They are striped per core (cache-line padded) to avoid contention and summed only when read.
- **Latency.** Fixed log-linear histogram buckets (about 1 us to 60 s, around 120 buckets), updated with `Interlocked.Increment`. Two histograms are kept, total time and upstream time, so the proxy's own overhead is visible.
- **Exposed via `System.Diagnostics.Metrics`.** `ObservableCounter` and `ObservableGauge` callbacks read the arrays, so OpenTelemetry and Prometheus `/metrics` (admin port) work out of the box.
- **Dashboard live data.** A 1 s sampler writes into in-memory ring buffers (last hour). A background job persists 1-minute rollups to SQLite **keyed by typed ID** (`HostId`, `UpstreamId`, ...), with configurable retention.
- **Upstream health.** YARP active checks (HTTP probe, interval, path, expected status) and passive checks (failure rate). State changes go to the audit log and notifications, and appear as dashboard status dots.
- **Access log and error log (M6).** The hot path fills a fixed-size ring buffer of structs referencing existing strings (no copies). A background writer formats plaintext lines to `<data dir>/logs/access.log` (one per request, per-host on or off). The process's own Warning-and-above logs go to `<data dir>/logs/error.log`.
  - Files rotate by size and daily. Rotated files are gzip-compressed in the background. Retention is by count, age and total size.
  - A full ring drops lines and counts the drops. It never blocks a request.
  - The same drain feeds the dashboard's live tail and the analytics.
- **Request ID.** `X-Request-Id`: honour an incoming one from trusted proxies, else generate one. It is shown on error pages so users can report it.

## The log files (M6)

`<data dir>/logs/` holds two logs. Each has a live file (`access.log`, `error.log`) and any number of rotated ones (`error-20261109T120000Z.log`, gzip-compressed to `....log.gz`).

**Names.** The names, their parsing and the "is this one of ours?" test are `Paduraksa.Core.Logging.LogFileNames`. The writer and the reader share them, so a name that is not one of ours is never opened.

**The writer** is `RotatingLogFile` (`Paduraksa.Server/Logging`).
- Every call site formats a whole line and offers it to a bounded queue (8192 lines, dropped and counted when full), so nothing on the request path waits for a disk.
- One background thread appends to a buffered `FileStream` (append, `FileShare.Read`, 0600). The directory is 0700, since the access log holds client IPs. It flushes once a second.
- It rotates when the next line would pass `Logs:MaxFileSize` or the UTC date has changed. The live file is renamed aside (a `-2`, `-3`, ... suffix when two rotations land in the same second, checked and then renamed without overwriting).
- A second thread gzips the rotated file (to a `.gz.tmp`, renamed into place, the `.log` deleted only afterwards) and trims the directory.

**Retention** deletes rotated files oldest first until each log holds at most `Logs:RetainedFiles`, none is older than `Logs:MaxAge`, and the total is at most `Logs:MaxTotalSize`. The live file is never a candidate. The four limits (with `Logs:MaxFileSize`) are server settings: `RotatingLogFile.Retain` takes new ones live and queues a trim at once, so a lowered limit deletes without waiting for a rotation.

**Startup.** The same thread removes stale `*.log.gz.tmp`, compresses rotated files a crash left uncompressed, and trims. A process that never logs creates no directory.

**Failures.**
- An I/O failure is counted (`Failures`), reported to the console at most once a minute, and retried. The file is closed at once, so the next line opens a fresh handle (a full disk, or a directory that vanished and came back). A rotation that could not rename keeps appending until a later one can.
- No log call ever throws. One `BestEffort` helper holds the general suppression of "a logger's own failure must not propagate" and reports through `Failed`. The filtered catches that tell an expected failure apart (a file that cannot be opened or read, the compressor's temp file) keep their own, and report the same way.

**Flushing.**
- `Flush()` (and disposal, after the hosts have stopped) writes out and flushes what is buffered. A `Flush` racing with the shutdown returns instead of waiting on a writer that has gone.
- `Flush()` wakes the idle writer at once, by offering the one item in the queue that is not a line (recognised by identity, never written). Before that it set a flag and waited for the writer's own one-second poll to time out, so every flush (and therefore every host stop, two writers per host) could wait up to that long.
- A full queue refuses the wake-up item like any line, and the flag still ends the flush at the next poll, so a request is never lost.

**Composition.** `SharedServices` builds one `ServerLogFiles`, registers the error log's provider in the one logger factory, and exposes both writers. The access-log code only has to feed `WriteLine(ReadOnlySpan<char>)`.

**The error log.**
- `ErrorLogProvider` keeps every entry at or above `Paduraksa:Logs:ErrorLevel` (default Warning) through a filter rule for its own provider type. So `Logging:LogLevel` still decides what the console shows, and nothing else.
- Entries render as `2026-11-09T12:00:00.000Z warn: Category[12] => app=core: message`. Scopes are included (which is where the role scope's `app` comes from). An exception's text and stack are indented on the lines below.
- Entries from `Paduraksa.Server.Logging` are skipped, so the log files' own reports cannot loop back into them.

**Options** are validated at startup: size 1 KiB to 16 GiB, files 1 to 10 000, age 1 minute to 10 years, total size 1 MiB to 1 TiB. `Logs:Directory` is empty (meaning `<data dir>/logs`) or resolves against the data directory.

**The CLI** commands (`vault`, `setup-token`, `admin`, `doctor`) build no shared services and so create no log files. Only a `serve` does, and even then only once something is logged.

The setup-token banner's Warning entry keeps its parity with the console. That is the one documented exception (see the "No secret in a log" rule in [../PLAN.md](../PLAN.md)). Nothing else new reaches either log.

### A client going away is not an error

Kestrel logs a client that vanished mid-write (a phone off the network, a laptop asleep, a NAT entry gone) at Error with a full stack, twice per connection: when the HTTP/2 GOAWAY cannot be flushed and when the TLS stream's dispose flushes again. Production saw a few an hour (`SSL_ERROR_SYSCALL` over `ETIMEDOUT`), and they buried real errors in `error.log`.

`PeerGoneLogFilter` (`Paduraksa.Server.Logging`) is applied in `RoleLogger` before the level check.
- It writes such an entry at Debug as one line ("... The client went away: <cause>") with no stack.
- It counts it (`paduraksa_connection_errors_total{kind="peer_gone"}`), even with Debug off.
- It is narrow on purpose. It takes only Kestrel's categories at Warning or above, and only an exception chain of `IOException` wrappers ending in:
  - a reset, aborted, timed-out, shut-down or unreachable socket;
  - Kestrel's `ConnectionResetException` or `ConnectionAbortedException`;
  - OpenSSL's `SSL_ERROR_SYSCALL` (error 5, the socket under TLS failing; the runtime's type is internal, so it is matched by name).
- Any other exception, from Kestrel or from Paduraksa's own code, keeps its level and its stack.

## The access log pipeline (M6)

Built in `m6-access-log`: request ID, access-log pipeline, formatter, redaction and `access.log`.

**The sink.** The drain writes through an `IAccessLogSink` (`void WriteLine(ReadOnlySpan<char>)`, `void Flush()`). The registered one is `AccessLogFileSink` (`Paduraksa.Server.Logging`), a thin adapter over `ServerLogFiles.Access`.
- The lines land in `<data dir>/logs/access.log` and rotate with it. The writer owns the newline; the formatter produces none, and the line is passed through unchanged.
- The writer is built before the sink and disposed after every host has stopped, so the drain's last flush and the writer's buffer both reach the disk before the file closes. A test stops a host right after a request and reads the line from the file.
- A test app swaps in its own sink (or `NullAccessLogSink`) through `SharedServices.Builder.Replace`.

### Request ID

`Paduraksa.Proxy.Logging.RequestIds`. The header is `X-Request-Id`.
- A value is accepted when it is one header line of 1 to 128 visible ASCII characters (0x21 to 0x7E).
- It is honoured only from a peer the snapshot's trusted-proxy policy trusts: the same `ClientIpResolution.PeerTrusted` decision that makes `X-Forwarded-For` or `CF-Connecting-IP` count. From anyone else, and from a trusted peer who sends nothing usable, it is replaced.
- A generated ID is 22 characters: an 8-character per-process prefix from `RandomNumberGenerator` and 13 base32 characters of a per-process 64-bit counter (`Interlocked`). Two processes behind one log do not collide either.
- The gate chooses it in `Decide` as a string reference (adopted) or a counter (generated) at **0 B**. `GateMiddleware.InvokeAsync` materialises the text once. **One string per request is the only allocation a request accepts**, because an HTTP header value can only be a `string`.
- The response header, the upstream header and the access log all share that one string. `GateRequestState.RequestIdText()`, `HasClientRequestId` and the state itself are what the error pages read.

**Where the ID travels** is per host: `HostSettings.RequestIdPropagation`.

| Mode | Meaning |
|---|---|
| `upstreamAndResponse` (default) | Sent to the upstream and on the response. |
| `upstreamOnly` | Sent to the upstream only. |
| `responseOnly` | On the response only. |
| `off` | Not sent anywhere. |

- It resolves through built-in defaults, global defaults, profile and host, and compiles to `CompiledHost.RequestIdPropagation` (and `RuntimeSnapshot.RequestIdPropagation` for an unknown host).
- When it is not sent upstream, an incoming `X-Request-Id` is **removed** rather than passed through (also from a trusted peer). When it is, ours always overwrites.
- On the response, ours replaces whatever the upstream set, or the upstream's is stripped when the setting says it is not sent.
- The access log records the ID in every mode.
- The dashboard offers it in the same three layers as the TLS fields (`HostSettingsFields`).

### The ring

`AccessLogRing` (`Paduraksa.Proxy.Logging`) is Vyukov's bounded multi-producer, single-consumer queue over `AccessLogEntry` structs.
- It is a power-of-two slot array with one sequence number per slot. A CAS on the tail claims a position, and a release store of the slot's sequence publishes it. A producer can never overwrite what the consumer is reading.
- When full, the line is dropped and `Interlocked.Increment`ed into a drop counter. No producer ever waits. A producer stalled past a whole lap drops its line and counts it, even if the ring has room again by the time it runs.
- A slot is padded to three 64-byte cache lines (192 bytes: the sequence number, the entry and padding), so neighbouring slots never share one and producers writing different slots do not ping-pong a line between cores. The default 16 384 slots are about 3 MiB.
- `Paduraksa:AccessLog:RingSize` must be a power of two from 1 024 to 1 048 576.

### The entry

It references only strings the request already holds:
- the resolved client address (the gate's per-connection cached text);
- `Host` as received, method, path, raw query and protocol;
- status;
- the response and request body bytes (counted, see [Counted body bytes](#counted-body-bytes));
- the total and upstream durations, and YARP's destination address;
- the stages (see [Request timing](#request-timing)): the connection's TLS handshake and the request's index on its connection, the body read, the gate, the upstream connect and the response send;
- the request ID;
- `CF-Ray` (only when `CloudflareHeadersTrusted`, so a client cannot forge it);
- referer and user agent.

It never holds the `HttpContext`, a pooled object, a cookie or `Authorization`.

**Recording** is `AccessLogMiddleware`, the public pipeline's outermost middleware.
1. It acquires the gate's pooled state first (the gate then reuses it).
2. It stores the ring and the request's start timestamp there.
3. It registers a static completion callback on `HttpResponse.OnCompleted` whose only state is the `HttpContext`.
4. It returns `next(context)`.

Kestrel keeps the callback stack per connection (or pooled H2/H3 stream) and clears it between requests. So there is no closure, and registration is 0 B in steady state. Gate refusals, the HTTPS redirect, Redirect/Respond answers, YARP forwards and exceptions all get a line. In-process Kestrel tests keep the gate, the callback and the reapply hook at 0 B with logging on.

**Duration and status.**
- The total duration is from the top of the pipeline to the end of the response.
- The status is what the client got, as Kestrel has settled it by the time the callback runs: an unhandled exception before anything was sent is 500, a connection that ended without a response is 499, and a request body Kestrel rejected mid-read is the 4xx Kestrel answers with. Kestrel settles that status only after the callback, so the callback reads it from `IBadRequestExceptionFeature`.
- The callback then clears the start timestamp, so the gate's skipped reset stays request-scoped.
- A request the gate dropped without a response is recorded as no status at all, written `-`. An aborted connection has no status, and the 200 a response object would still report is not invented.
- A request that never reaches the gate (a test app's `WithoutGate`) has no snapshot and logs under the default rule, with no invented request ID.

**Upstream duration** comes from YARP's own transforms (`AccessLogTransform`): a `Stopwatch` timestamp in the first request transform, then the elapsed time and `IReverseProxyFeature.ProxiedDestination` in the response transform. That is the cheapest source that covers the forward and the upstream's answer.
- Load balancing picks the destination just before the request transforms run, so that one step sits outside the measured window.
- A forward that ends without a response (YARP's own 502) is measured up to the failure, in the middleware, from the same timestamp, and still names the destination YARP had chosen.
- It is `-` when nothing was forwarded at all (an answered request).

**Bytes** are the response body bytes written to the client, counted as they passed (see [Counted body bytes](#counted-body-bytes)). Until then the field was the declared `Content-Length`, `-` without one. The request's body bytes reach the live tail, the counters and the analytics, but not the file.

### The line (`AccessLogFormatter`)

It runs off the request path, into a reused 8 KiB buffer:

```
time client "host" "METHOD path[?query] protocol" status bytes total upstream "upstream" "request-id" "cf-ray" "referer" "user-agent" tls conn-request body gate connect send
```

That is 19 space-separated fields. The first 13 are the line as it was before the stages; the six stages are appended after them (see [Request timing](#request-timing)), so a reader of the first 13 reads them as before.
- Time is ISO-8601 UTC with milliseconds. Durations are milliseconds with three decimals (microsecond resolution).
- Missing values are a bare `-`, the status included when the gate dropped the request.
- **Escaping.** Inside a quoted field `"` becomes `\"`, `\` becomes `\\`, and a C0 control, 0x7F or a C1 control (NEL among them) becomes `\xNN`. U+2028 and U+2029 become `\u2028` and `\u2029`. A hostile path, user agent or referer cannot forge or break a line. Non-ASCII stays readable (the sink writes UTF-8).
- **Output caps.** A capped field ends with `...`.

| Field | Cap |
|---|---|
| path | 1 024 |
| query | 1 024 |
| request target | 2 048 |
| user agent, referer | 512 |
| host | 255 |
| upstream | 256 |
| request ID | 128 |
| `CF-Ray` | 64 |

- **Redaction.** The query's sensitive parameters become `name=REDACTED`, including a bare `?token`. Names are case-insensitive, matched as written (not percent-decoded). The default set is `token`, `access_token`, `refresh_token`, `id_token`, `code`, `password`, `passwd`, `pwd`, `secret`, `client_secret`, `key`, `api_key`, `apikey`, `signature`, `sig`, `auth`, `session`. `Paduraksa:AccessLog:SensitiveQueryKeys` replaces it. `Paduraksa:AccessLog:LogQueryString=false` leaves the query out entirely.
- Cookies and `Authorization` are never part of an entry.
- The sink owns the newline: `WriteLine` receives one line without a trailing `\n`.

### The drain

`AccessLogDrainService` is a hosted service of the core host, the one reader.
- It drains every 200 ms and on shutdown (then flushes the sink and so the file). It formats each entry into its one buffer and hands it over.
- A sink that throws is reported at Warning (at most once a minute) and the line is skipped. A whole pass that throws (a formatter bug) is reported the same way so the next pass carries on: a faulted hosted service would stop the core host. Dropped lines are reported the same way, with the count since the last report.
- `SharedServices` creates the one ring, the formatter's options and the sink (the process's `AccessLogFileSink`) for all three hosts. `SharedServices.Builder.Replace` is how a test swaps it.

**Options** (`Paduraksa:AccessLog`): `RingSize`, `SensitiveQueryKeys` (a list, comma-separated or indexed) and `LogQueryString`. They are validated in `PaduraksaOptionsValidator` before anything is built, and documented in `docs/install.md`.

**Not built there:** the dashboard's live tail (below). The simulator reports the response `X-Request-Id` in its response-headers step. Where the ID goes *upstream* is a request transform, so the trace says nothing about that leg.

## Counted body bytes

Every byte figure used to be the response's declared `Content-Length`. A chunked, streamed or upstream-compressed response added nothing, and an upload was not seen at all. Now the bytes are counted where the body passes.

**Where.** `BodyCounter` (`Paduraksa.Proxy.Http`) stands in for:
- the request's `IHttpResponseBodyFeature` (its `Stream` and `PipeWriter` both count);
- the request body stream (`IHttpRequestFeature.Body`);
- on a request that can be upgraded, `IHttpUpgradeFeature` (HTTP/1.1) or `IHttpExtendedConnectFeature` (HTTP/2).

Everything is passed through, and the bytes written to the client and read from it are added up.
- `AccessLogMiddleware.Begin`, the public pipeline's outermost step, installs it before anything has touched a body.
- So YARP's forward and every answer the gate writes itself (error pages, maintenance, redirects, robots.txt, the proof-of-work page, refusals, the page that replaces an upstream's error) are counted, on H1, H2 and H3 alike. The features are Kestrel's `HttpProtocol`'s on all three.
- What is counted is what went to the client after any transformation: an upstream's gzip counts as the compressed bytes.
- A `HEAD` answer counts 0 (Kestrel discards its body).
- `SendFileAsync` goes through `SendFileFallback` over the counting stream: a user-space copy with a `FileStream` per call, instead of the OS's sendfile, so that its bytes are counted too. Nothing on the public listeners sends files today. A feature that does should weigh that copy.

**0 B.**
- One counter (four small objects) hangs on the gate's pooled `GateRequestState`. It is created once per H1 connection or pooled H2/H3 stream and re-pointed each request: a few feature reads and sets, which Kestrel keeps in fields.
- A write is counted before it is handed on (no continuation). A read is counted when it completes, and one that goes asynchronous awaits through `PoolingAsyncValueTaskMethodBuilder`, so a warm process allocates nothing for it either.
- The counts are plain `long`s. Each direction has one writer at a time (a stream or pipe is never used concurrently), and the completion callback reads them after the request is over. No lock, no I/O.
- `ProxyTestServer`'s measure mode now adds `AccessLogMiddleware.Begin` to the gate's measured bytes, so every `GateAllocationTests` and `AccessLogAllocationTests` case covers it.
- `BodyBytesTests.CountingAllocatesNothingOnKestrel` (H1 and h2c, a chunked and a declared response and an upload) and `BodyCounterTests.APendingReadAndAWriteAllocateNothing` (an optimised build; a debug build compiles every async state machine as a class) prove the rest.

**Upgraded connections.** A WebSocket's frames do not pass through the body features but through the stream the upgrade returns. The counter wraps that stream too (reads are bytes in, writes bytes out). The request is counted when it ends, so a WebSocket's bytes land when it closes. Accepting an upgrade costs Kestrel's own task once, never anything per message.

**What "written" means.** The bytes handed to the server for the client. A client that leaves mid-body has been counted what the proxy wrote before it noticed. That can include what still sat in Kestrel's and the kernel's send buffers (a test reads 256 KiB of a 64 MiB body and requires a count at least that and well under half the body). Nothing more exact is visible to a process. A request that reads `HttpRequest.BodyReader` gets Kestrel's pipe reader over the counting stream, which allocates. Nothing on the public pipeline reads that way.

**Where the numbers go.**
- The access log's bytes field. Request bytes reach the live tail's entry, whose drawer shows the request size when there was a body.
- The counters: `RequestMetric.BytesOut` and the new `BytesIn` for hosts and routes, and `UpstreamMetric.BytesOut` and `BytesIn` for upstreams.
- The sampler's points and the minute rollups (`MetricRollups.BytesIn`, migration `MetricRollupBytesIn`).
- Prometheus: `paduraksa_response_size_bytes_total` keeps its name with a corrected HELP, plus `paduraksa_request_size_bytes_total` and the route's and the upstream's pairs. OpenTelemetry: `paduraksa.request.size`.
- The analytics: `TrafficBucket.BytesIn` and `AnalyticsRollups.BytesIn` (migration `AnalyticsRollupBytesIn`). The stored bucket's format 2 adds the figure, and format 1 still reads as a bucket with none.
- The Analytics page's Bandwidth tile says the figure was sent and, when anything was uploaded, how much was received. Its chart gains a bytes-received line then.

**Decision: old rows are not rewritten.** Rollups stored before this hold declared `Content-Length`s and no request bytes. They mix with the counted ones in a range that spans the change. There is nothing to recompute them from, and the difference fades out with retention.

**Tests.**
- `BodyBytesTests` (real Kestrel and YARP, H1 and h2c): a chunked and a declared response counted exactly, an upstream's gzip counted compressed, an upload (declared and chunked) as bytes in, the gate's own 404 in text and as the HTML page, a `HEAD`, a client leaving mid-body, a WebSocket both ways, and three requests on one HTTP/1.1 connection (upgradable, plain, upgradable) each counted on its own features. Each is checked on the access-log entry and on the host's, route's and upstream's counters.
- `BodyCounterTests` covers every write and read overload (the array-based async ones over a body that refuses synchronous I/O, as Kestrel's does: `Stream`'s own fall back to it, so the counter sends them to its Memory overloads), a server without a response body feature (bytes unknown, `-`, not 0), `SendFileAsync`, a second `Begin`, re-pointing for the next request, the upgrade and extended-`CONNECT` streams, and a request that cannot be upgraded.
- QUIC-specific paths are untouched, so `run-quic-tests.sh` was not extended.

**Cost.** Bench (`bench/run-baseline.sh`, Paduraksa only, fixed concurrency, h1, h2-tls, body-64k, answered, access-log and error-page, two runs each of `main` and this branch, alternated, with the proxy on 3.3 to 3.5 of its 4 CPUs in every run). Bytes per request were unchanged within the runs' own spread, so the counter costs no allocation in a real process:

| Scenario | Bytes per request, `main` | Bytes per request, branch |
|---|---|---|
| h1 | 4 065 / 4 076 | 4 122 / 4 071 |
| h2-tls | 4 504 / 4 517 | 4 527 / 4 523 |
| answered | 895 / 898 | 894 / 899 |
| body-64k | 5 461 / 5 471 | 5 470 / 5 488 |

Throughput, mean of the two runs:

| Scenario | `main` | Branch | Change |
|---|---|---|---|
| h1 | 171.0k req/s | 172.1k | -0.6 % |
| h2-tls | 150.1k | 153.2k | -2.0 % |
| body-64k | 73.5k | 73.8k | -0.4 % |
| answered | 386.5k | 392.3k | -1.5 % |
| access-log | 150.1k | 152.4k | -1.5 % |
| error-page | 19.9k | 20.0k | -0.6 % |

- Proxy CPU per request rose 1 to 1.5 % (h1 19.2 vs 19.5 microseconds, h2-tls 23.05 vs 23.4).
- The cost is one more virtual call on every body write, read and flush, and the feature swaps at the start of a request. HTTP/2 pays the most because its responses are more, smaller writes and flushes.
- A second set an hour later, against the rebased `main`, was taken while another session's builds and tests ran on the same cores (some runs had the proxy on one CPU). It is not used.

## Request timing

Built in `request-timing`. Until then a request had two times: the total and the upstream leg. Now each stage is timed, so a slow request says where its time went. Every figure is a pair of monotonic `Stopwatch` timestamps, kept on the pooled request state (`GateRequestState`), the body counter or a per-connection object, and turned into `TimeSpan` ticks on `AccessLogEntry` when the request ends.

### The stages

| Stage | Starts | Ends | Not known (`-`) |
|---|---|---|---|
| TLS handshake | TCP: the connection middleware just before Kestrel's `UseHttps` (after PROXY protocol, the lockdown and the connection limits). QUIC: the TLS callback, once the ClientHello is in (`TlsHandshakeSelector.SelectQuic`, after the connection gate). | TCP: the middleware just after `UseHttps`. QUIC: the multiplexed middleware, which Kestrel runs once the connection is established. | Plain HTTP, and every request but a connection's first. |
| Request index | A count, not a time: the access log's start counts the request on its connection (`ConnectionTimings`, an atomic shared by an H2/H3 connection's streams), from 1. | | A connection without the feature (a test app). |
| Request body | The first read of the request body that returned bytes completed. | The last read that returned bytes completed (the read that finds the end of the body is not counted, so a reader that comes back for it late does not stretch the figure). | 0 when no byte was read; `-` when the body was not counted. |
| Gate | The top of the public pipeline (the access log's start, the same timestamp as the total). | The gate hands the request on: `GateMiddleware.FinishAsync`, after the decision and its last header stage, to the forward or to its own answer (a refusal, a redirect, the proof-of-work page, robots.txt). A deferred policy's wait (a password check) is inside it. | No gate ran. |
| Upstream connect | The handler's connect callback, for the request whose wait made the pool open a connection. | The handler's plaintext-stream filter, which runs once TLS to the upstream is up and before the first byte of HTTP. So it is DNS, TCP and TLS. | Not forwarded. 0 on a pooled connection. |
| Upstream wait | The connect's end (the forward's start, on a pooled connection). | YARP's response transform: the response headers are in. | Not forwarded. Derived (the upstream leg minus the connect), not stored. |
| Response send | Kestrel's response-start callback (the headers go out). | The completion callback, the same timestamp as the total's end. | No response (a dropped request). |

**The stages overlap and don't add up to the total.** A body is read while the forward is under way, and a streamed answer is sent while the upstream is still writing it, so its send includes the upstream's own streaming. A WebSocket's send runs from the 101 to the close. "Sent" means handed to Kestrel and flushed past its output buffer: up to about 64 KiB, and the kernel's socket buffers, can still be on their way to a slow reader when the callback runs.

**Request headers are left out.** Kestrel exposes no timestamp of a request's first byte. The connection's own start would include an idle keep-alive's wait, and a parser hook would mean replacing Kestrel's. So the time from the first header byte to the top of the pipeline is not measured.

### How each is taken

- **TLS** (`Net.ConnectionTimings`): one small object per connection, set as a connection feature by `UseConnectionTimings` (plain HTTP), `UseTimedHttps` (TCP, the two middlewares around `UseHttps`) or the QUIC callback and `UseQuicHandshakeTiming`. The handshake is written once, before the connection's first request can start, and read by the request whose index is 1. The pooled request state caches the feature, since the state never leaves its connection (it lives on the H1 connection, or on an H2/H3 stream Kestrel pools per connection).
- **Body** (`Http.BodyCounter`): the request stream it already wraps takes a timestamp per read that returned bytes. The upgraded stream's reads are a WebSocket's frames, not the body, and are not timed.
- **Gate**: one timestamp in `FinishAsync`.
- **Upstream connect** (`Yarp.UpstreamConnects`): the forwarder's handlers get a connect callback (the handler's own connect, or the admin cluster's, wrapped) and a plaintext-stream filter. Both get the request message that made the pool open the connection; a `ConditionalWeakTable` keyed by it holds the start and the end. The request's message is kept on the pooled state by the access-log transform, and its completion looks it up.
- **Send**: a response-start callback, registered next to the completion one, whose state is the pooled request state.

### Decisions

- **Reused, not repeated.** A later request on a TLS connection reports no handshake: the entry says "reused" (`AccessLogEntry.HandshakeReused`), the file writes `-` and its request index tells the reader which request it was. The file writes no word in a number field, so a reader parses each stage field as a number or `-`.
- **QUIC is measured.** Its start is the ClientHello rather than the first packet, which on QUIC carry the same flight; the end is the established connection. The HTTP/3 end-to-end test in Docker checks both transports report one.
- **The connect is the pool's, attributed to a request.** The pool opens a connection on behalf of a waiting request, but hands that request whichever connection frees up first. When a pooled one wins, the request that triggered the connect is still reported with it though it didn't wait for all of it, and the request that later gets the new connection reports none. The figure is capped at the request's upstream time. Under steady keep-alive traffic both are rare. A connect that never finished is the whole forward when the forward failed (refused, timed out), and 0 when the forward was answered anyway: the pool served it on a connection that freed up first. HTTP/3 upstream connects are not timed: the pool runs the plaintext filter, which stops the clock, only for HTTP/1.1 and HTTP/2, and no cluster asks for HTTP/3 to its upstream today.
- **The default connect is ours now.** A connect callback replaces the handler's own TCP connect, so `UpstreamConnects` does what the handler did without one: a dual-mode socket, Nagle off, `ConnectAsync` over the name's addresses in turn. The admin cluster's callback is wrapped, not replaced.
- **YARP's and HttpClient's telemetry are not used.** Their `EventSource` and `Activity` paths allocate per request and need a listener; the two callbacks run only when a connection is opened.
- **Histograms, not rollups.** The TLS handshake and the response send are histograms per host on `/metrics` (`paduraksa_tls_handshake_duration_seconds`, `paduraksa_response_send_duration_seconds`), in a table of their own beside the latency one, plus their sums in the host's counters. They are not sampled into the 10-second points or the minute rollups: that would widen the stored point and the rollup rows (a migration) for figures nothing on the dashboard draws over time yet. The handshake counts once per connection, against the host of its first request. OpenTelemetry does not offer them.
- **The log's six fields are appended.** The 13 fields before them keep their order and format.

### Cost

- On the request path: an atomic and a field read per request on the connection object, three more `Stopwatch` reads (the gate's end, the response's start, the end now shared by the total, the send and the counters, so the recorder no longer reads its own), one per body read that returned bytes, one more callback registration, one `ConditionalWeakTable` lookup at the end of a forward, and two more interlocked adds for the send's histogram and sum (four on a TLS connection's first request, with the handshake's). All 0 B, no lock.
- Per connection: one 40-byte object; per upstream connection: one more and the table's lock to add it, on a path that waits on the network anyway.
- The ring's slot stays 192 bytes: the entry grew from 136 to 176 bytes and the padding shrank to 8.
- **Tests.** `GateAllocationTests` (h1, h2c, TLS h1 and h2), `BodyBytesTests.CountingAllocatesNothingOnKestrel` (TLS uploads included) and `AccessLogAllocationTests` (the completion, response-start and reapply callbacks on h1, h2c and TLS) hold the request at 0 B; `UpstreamConnectsTests` and `ConnectionTimingsTests` measure the lookup and the count alone. `RequestTimingTests` checks each stage against a known delay on a real listener: a client that waits 300 ms before its ClientHello (h1 and h2 over TLS), a body trickled over 300 ms, an upstream whose TLS handshake a relay holds for 300 ms (and a second request on the pooled connection with no connect), an upstream that waits 300 ms before its first byte, and a client that stops reading a 16 MiB answer for 600 ms. The bench numbers are in [hot-path.md](hot-path.md#request-timing).

## Metrics (M9)

Built in `m9-metrics`: counters, histograms, `/metrics`, the sampler, rollups and the live overview. Health checks and notifications came next, and the live tail has its own section (both below).

### Slots

`MetricsRegistry` (`Paduraksa.Proxy.Metrics`) owns four append-only `Guid`-to-slot registries (`MetricSlotRegistry`: host, route, upstream, destination; the key is the typed ID's value) and one striped table set per subject. It copies the rate-limit slots:
- A *publishing* compile calls `SlotFor`. A draft, a dry run or a simulation only `FindSlot`s, so `SnapshotCompilerOptions.WillBePublished` (formerly `RateLimitsWillBePublished`) governs both.
- The publisher calls `Publish` just before the swap. It re-asserts the snapshot's objects and retires every other live one, so a slot a failed compile took is reclaimed at the next publish too.
- A retired slot is rented to another object only after the grace period (one minute, `MetricsRegistry.DefaultGracePeriod`), with its counters cleared first. Retired slots are reused *before* the table grows.

**Slot versions.**
- Every slot has a version, even while stable. A rent makes it odd, clears the counters, sets the owner and makes it even again. A compiled `MetricSlot` carries the version it was handed out under.
- `RecordRequest` drops a record whose slot version is no longer current (one compare per slot, still 0 B). A WebSocket, SSE stream or slow upload on a deleted host that finishes after its slot went to another host is therefore never counted into the new owner.
- A request that is mid-record at the very instant of a rent (nanoseconds, once per rent) can still land one stale increment.
- A publish that cannot reclaim an object's slot (another object holds it by then) is logged at Warning, as the rate-limit case is, *and* made harmless. That object's compiled slot is stale, so it records nothing (never into the other object), and the exporters leave it out until the next compile gives it a fresh slot.

**Who gets slots.** Only enabled hosts, their routes, the upstreams they route to and those upstreams' destinations. The admin host's implicit route has none.

Host slot 0 is reserved for requests no host owns, shown as `(unmatched)`. Scanners and typos show up as traffic that belongs to nobody.

`CompiledHost.MetricSlot`, `CompiledRoute.MetricSlot` and `CompiledRoute.Upstream` (`UpstreamMetrics`: the upstream's slot and its destinations', found by YARP's destination key with a short scan) carry them. `RuntimeSnapshot.MetricRegistrations` is what the publisher hands the registry.

S6's metric part is `MetricsEndToEndTests`. Under a publish every 100 ms that adds and drops a host and an upstream, counters never decrease, stay on the same slots, and every answered request is counted once.

### Storage and the hot path

`MetricTable` is a `long[]` per chunk of 16 slots holding every stripe's block, indexed `(stripe * 16 + slot % 16) * stride + metric`.
- The stride is a multiple of 8 longs, so no two slots share a cache line, and stripes are kilobytes apart.
- Stripes are the core count rounded up to a power of two, at most 16. A request uses `Thread.GetCurrentProcessorId() & mask`. Counters are summed only when read.
- Chunks are created under a lock before a slot is given to a snapshot.

**Layouts** (`RequestMetric`, `UpstreamMetric`, `DestinationMetric`):
- A host and a route count requests by status class (`1xx` to `5xx`, and `none` for a dropped connection) and by `GateOutcome`. They count the response and request body bytes (`BytesOut`, and `BytesIn` placed after the older counters so their offsets stay). A host also sums the total and upstream microseconds and keeps two histograms.
- An upstream counts forwards, YARP `ForwarderError`s by kind, its upstream-time histogram and sum, and the body bytes both ways of what was forwarded to it (stride 32).
- A destination counts forwards and the errors on its side. `ForwarderErrors.IsUpstreamFault`: a client that hung up is not a destination's error, though the upstream's kind counter still records it.

**Histograms.** 101 fixed log-linear buckets (`LatencyBuckets`): 0 to 3 microseconds one each, then four per octave up to 2^26 microseconds (67 s). The last bucket is the overflow. No bucket is more than 25 % wide. Each request does `Interlocked.Increment`, and quantiles are interpolated inside the bucket.

**Gate outcomes** (`GateOutcome`):
- `Forwarded`.
- Each refusal kind the security page tells apart: `RefusedAccessList`, `RefusedRuleSet`, `RefusedRateLimit`, `RefusedProofOfWork`, `RefusedForwardAuth`, `RefusedRequestLimits`, `RefusedConnection`, `NoRoute`, `Unavailable`, `Dropped`, `UnknownHost` and `RefusedOther`.
- `Answered` for the host's own answers: redirect, robots.txt, Redirect/Respond hosts, ACME and the verify endpoint.

How an outcome is chosen: the policy that refuses records its kind on the pooled request state (`IRequestPolicy.Outcome`, a default member each policy class overrides). The gate sets `Forwarded` where it picks the endpoint. The rest is classified from the facts at the end (`GateOutcomes.Classify`: aborted, no host, host kind, 421, 413, 414, 431, 404, 503).

**Recording** is `RequestMetricsRecorder`, called from `AccessLogMiddleware`'s completion callback for every request whether or not the host logs it. It reads the same two clocks the access log reads (the request's start timestamp and `AccessLogTransform`, which now also keeps YARP's destination key). The process's `RequestMetrics` struct goes in by `in`.
- It is 0 B. `RecordRequest` has an allocation test, the in-process Kestrel test measures the recorder next to the registration, recording and reapply callbacks, and the gate test runs with metrics on.
- The existing process totals (`ProofOfWorkStats`, `RateLimitStats`, `AccessListStats`) are not counted a second time. `ProcessCounters` exposes them where they live, and the sampler keeps them as series (see "Security counters" below).

### Cost

- `RecordRequest` is about 14 atomic increments on lines the core owns, plus two feature lookups and one more stopwatch read in the completion. Measured alone it is 75 ns a call on one thread.
- Measured on the Kestrel test context, the whole recorder was about 330 ns before the trims. The forwarder's error is looked up only for a 4xx/5xx or an aborted request (the lookup alone was 93 ns), and the two "timed" counters are derived from the histograms.
- **Bench** (`bench/run-baseline.sh`, Paduraksa only, h1 and h2-tls, this branch against `main`, one box shared with other work, so only quiet pairs count):
  - at a fixed concurrency of 64: h1 176.4k vs 172.3k requests/s (-2.3 %), h2-tls about -2 % in the quiet pairs, proxy CPU 18.8 vs 19.1 microseconds a request (+1.6 %);
  - an earlier set before the trims gave h1 -2.3 % and CPU +3 % over the median of four pairs.
- A single 30 s fixed-rate run at 30 000 requests/s, three repeats (medians): h1 p99 2.13 ms vs main's 220 ms (main's was a machine stall; its best repeat was 203 ms in a run the box was busy for), h2 p99 2.21 ms vs 2.48 ms. Those tails are noise at this length, not a result.
- The loss is inside the 3 % the brief allows. The next saving would be sharing the access log's `Content-Length` and elapsed-time reads with the recorder (about 70 ns).

### Exposure

`PrometheusWriter` writes text format 0.0.4 straight from the registry, with no exporter package.

**Families.**
- Counters per host: `paduraksa_requests_total{host,host_id,status_class}`, `paduraksa_gate_outcomes_total`, `paduraksa_response_size_bytes_total`, `paduraksa_request_size_bytes_total`.
- Per route: the same two byte counters.
- Per upstream: `_errors_total{kind}`, `_response_size_bytes_total`, `_request_size_bytes_total`. And per destination.
- Real histograms: `paduraksa_request_duration_seconds`, `paduraksa_host_upstream_duration_seconds` and `paduraksa_upstream_duration_seconds`, with `_sum` and `_count`. Per host also `paduraksa_tls_handshake_duration_seconds` and `paduraksa_response_send_duration_seconds` (see [Request timing](#request-timing)).
- The process counters, `paduraksa_config_generation` and `paduraksa_hosts`.

**Histogram edges.** They are cumulative at the octave edges, which are bucket edges, so the counts are exact. `le` is 2^e - 1 microseconds (`0.000015` to `67.108863`), since Prometheus's `le` means "at most" and the counts are of observations below 2^e.

**Labels.** They come from the *live snapshot* (`MetricInventory`), so a deleted host's series end with it. Label values are escaped (`\\`, `\"`, `\n`). A test feeds a host name that tries to close its label and start a new series.

**OpenTelemetry.** `PaduraksaMeter` (meter `Paduraksa`) offers the same counters as `ObservableCounter`s (`paduraksa.requests`, `paduraksa.gate.outcomes`, `paduraksa.response.size`, `paduraksa.request.size`, `paduraksa.upstream.errors`, ...) and two gauges, with the same tags. The meter API has no observable histogram, so a latency there is a sum in seconds. Both exporters are read-only on demand, so the request path never touches them.

**`/metrics`** on the admin port is `GET /api/v1/metrics` (`metrics:read`, in OpenAPI), with `/metrics` rewritten to it by `ApiPipelineMiddleware`. A scraper gets the API's bearer-token authentication, permission check and problem bodies instead of a copy. `Cache-Control: no-store`.

`metrics:read` (a new `Permission`, scope `metrics:read`) is held by every role. The numbers say no more than the config the same roles read, and the live overview is for all of them. `GET /api/v1/metrics/overview?range=hour|day|week|month` returns the dashboard's data. Scrape config and the families are in `docs/install.md`.

### Sampler and rings

`MetricSampler` (`MetricSamplerService`, a hosted service of the core host) reads each host's and upstream's counters and histograms, takes the gain since the tick before, and files it in a ten-second bucket and a minute window. A finished bucket goes into the series' ring: **360 buckets (an hour)**. A finished minute goes into `MetricRollupBacklog`.

**Decision: 10 s buckets, not 1 s samples, in the hour.**
- 1 s samples would cost 3 600 points of about 256 bytes (about 0.9 MB) per series.
- The sampler still reads every second, and the headline is the last 30 s. But a ring holds the hour at 10 s resolution: 92 KB a series, hosts and upstreams only (58 KB before a point carried the refusals by kind, `MetricPoint.Refusals`, twelve longs that an upstream's point leaves at 0). Routes and destinations keep counters but no history, since nothing draws them over time.
- A bucket's percentiles come from the histogram of its own requests. A longer stretch combines them weighted by requests (`MetricPoints.Combine`): exact under steady load and an approximation otherwise, which the dashboard does not label.

**Series follow the slot's owner.** When a slot is rented to another object, the old series' unfinished minute is flushed under its own typed ID, and the new owner starts at zero. A grace period of many ticks makes it unlikely that a count is lost. A tick that overlaps a rent is skipped: the sampler reads the slot's version before and after the owner and the counters, and files nothing if it changed, so a half-cleared read is never taken for a gain.

**The totals series** is the sum of the hosts' gains, the unmatched slot included, so a deleted host's traffic stays in it.

### Coverage, alignment and no straddling (timeline honesty)

**Coverage.**
- Every window (a ring bucket, a minute, a rollup row) records how many of its seconds the sampler observed: `MetricPoint.CoveredSeconds` next to `Width` (the bucket's length).
- Every rate (requests a second, 5xx a second, the tiles) divides by the covered seconds and never by the width, so a half-observed minute is not diluted.
- 0 means nobody was looking. A covered window that saw no request has coverage and zero counts. The two are never the same thing.
- The sampler measures from its own construction (the registry has counted since then), and each tick covers the time observed since the one before it.

**Monotonic time.**
- Coverage and splitting come from monotonic time (`TimeProvider.GetTimestamp`, the elapsed time between ticks). The wall clock only says which bucket and minute a moment belongs to.
- A wall clock stepped backwards therefore never reopens a closed bucket or minute and never doubles a second. The labels run on at the monotonic rate until the wall clock catches up, each real second is counted once, and a window's coverage cannot exceed its width.
- The clock stepping forwards is a gap in the labels: the second before the new time is observed, the time skipped is not.

**Alignment.** Ticks are aligned to whole seconds of the clock (`MetricSamplerService` waits `UntilNextSecond` on the injected `TimeProvider`). An interval crosses a ten-second or minute boundary only by the few milliseconds a tick is late.

**Splitting.** A tick that does cross a boundary **splits its gain in proportion to the time on each side** (`TickSplit`).
- Every counter and every latency histogram count is divided by rounding the cumulative share to the nearest whole count at each piece's end (the piece's share is the difference). The pieces are whole numbers, add up to the gain exactly, and none is more than one count off its proportion.
- A tick 4 ms late keeps its three requests in the window they happened in (0.012 of one belongs after the boundary). A tick 5 s late over a boundary splits 4 s to 1 s.

**Decision: a gap longer than 5 seconds (`MetricSampler.MaxTickGap`) is judged by what was counted in it.**
- *Nothing counted* means a suspended process, a VM pause or an outage. The seconds are not observed, and the buckets in between are in the ring as buckets with no coverage. Only the hour before now is filled, so a day-long stall leaves 360 of them ending at the tick, not a ring of stale ones. The chart draws that as a gap.
- *Requests counted* means the process was running and serving and only the sampler was late (thread-pool starvation, a long GC). The gap counts as observed, and its gain is split in proportion across every bucket and minute it spans, up to the ring's hour, like any tick. Filing that gain at the tick's end instead would have shown a gap, or a spike of five times the rate, for time that was served.
- The accepted cost: a process suspended with requests in flight that complete on resume gets the suspension reported as covered time, with those few requests spread over it.

### Shutdown and the seed

**Shutdown.** When the sampler's service stops, it takes a final reading and calls `MetricSampler.Flush`. The open bucket and the open minute of every series are closed as partial, with the seconds they observed up to now, and the minutes queued.
- The rollup job is registered before the sampler's service, so it stops after it and its last write carries them.
- The core host stops last of the three, after the public and admin hosts have drained, so the final reading sees the last requests.
- A crash loses what the process held in memory: the minute in progress and any minute closed in the last write interval (15 s by default). The time after it shows as a gap, not as zeros.

**The hour is seeded from the rollups.**
- Before the sampler's first tick, the job (`MetricRollupJob.StartAsync`) reads the last hour (and the minute before, for the window's edge) of rollups, every subject, once, and hands it to `MetricSampler.Seed`.
- It is a snapshot held in memory (`MetricHistory`, minute points keyed by object, trimmed as they leave the hour) and never reread. That is what makes the boot minute right: its first half is the database row from before the stop, its second half is the ring. When the job later merges the live half into the row, the snapshot does not see it, so nothing is counted twice.
- The busiest-hosts and upstream tables for the hour sum the seed and the live series. The tiles ("last 30 s") read the ring alone and, right after boot, divide by the time actually observed.
- An unreadable database at startup logs a warning and leaves the view starting at boot (as before).
- A seed is at most an hour of minute rows of the series that existed: a few thousand points.

### Rollups

`MetricRollupJob` (core host, registered before the sampler so it starts first and stops after it) writes the backlog every `Paduraksa:Metrics:WriteInterval` (15 s) into `MetricRollups` (migrations `MetricRollups` and `MetricRollupCoverage`).

**A row.**
- An Int64Ordered `MetricRollupId` (`mrl_`).
- The subject (`Total`, `Host` or `Upstream`: the `MetricRollupSubject` enum in Core).
- The object's typed-ID string (empty for the totals and for the unmatched host).
- The minute as whole Unix minutes, `CoveredSeconds`, the counters and the six percentiles.
- A unique index on (subject, ID, minute).

**The totals series has a row for every minute the process observed, idle ones included**, with its covered seconds: a partial first minute after boot, a partial last minute at shutdown.
- That makes it the record of when the process was up: a minute with no totals row is a minute nobody observed.
- Host and upstream rows stay sparse. They are written for a minute only when they counted something. A missing row inside an observed minute means zero for that object.
- One totals row a minute is 1 440 a day, 43 200 a month of running and 20 160 held at the 14-day default. The per-host and per-upstream rows a busy install already writes are far more (at about 150 series, up to 150 a minute). The totals rows are noise in the table, so there is no separate presence table.
- The migration sets `CoveredSeconds` to 60 for the rows already stored (they were minutes with traffic, which the process was there for). **Idle minutes from before the upgrade were never stored, so they show as gaps** in the history they leave. Only minutes from the upgrade on tell idle from down.

**Decision: a minute written twice is merged into its row, not kept as a second row.**
- A graceful stop writes the partial minute, and the next start goes on to fill the same minute. So `MetricRollupStore.WriteAsync` loads the stored rows of the written range and combines each collision with `MetricPoints.Combine`.
- Counts and sums add. Observed seconds add (capped at 60: the two halves never overlap in time). Percentiles are the mean of the two weighted by their requests (the same approximation as a longer range's).
- Two rows per minute would break the unique key that every read relies on (one row, one minute) and push the merge into every reader, where it would be paid on every poll instead of once per restart.

**Failure and retention.**
- A failed write puts the minutes back and logs a warning. The backlog is bounded (9 000 minutes), so a database that stays down loses the oldest, never memory.
- Retention (`Paduraksa:Metrics:Retention`, default 90 days since `server-settings` (was 14), a server setting read at every prune pass, range 1 hour to 5 years, validated like `Paduraksa:Logs:*`) deletes by age hourly.

**The refusals by kind** (`security-history`, migration `MetricRollupRefusals`).
- A row keeps `Refused` and, next to it, one column per gate outcome that turns a request away: the eight refusal kinds (`RefusedAccessList`, `RefusedRuleSet`, `RefusedRateLimit`, `RefusedProofOfWork`, `RefusedForwardAuth`, `RefusedRequestLimits`, `RefusedConnection`, `RefusedOther`) and the four answers the gate gives without a policy (`NoRoute`, `Unavailable`, `Dropped`, `UnknownHost`). Total and host rows fill them; upstream rows leave them at 0.
- In memory they are `RefusalCounts` on `MetricPoint`, built from the slot's outcome counters when a window becomes a point, and added by `MetricPoints.Combine`, so a merge on collision, a grouped step and the hour's seed all carry them like any count.
- **Decision: a column per kind, not a compact encoding.** The range reads are `SUM`s grouped in SQL; twelve more `SUM`s cost nothing there, where a blob or a packed integer would have to be read back and decoded per row. SQLite stores a 0 in no payload bytes, so the columns cost a header byte each on rows that have none.
- **Rows from before the breakdown** get 0 in each column from the migration. Their `Refused` is then all of an unknown reason: `MetricPoint.UnknownRefused` is `Refused` less the kinds' sum, never negative, and the API and the chart show it as "unknown reason". Nothing guesses a split. A minute merged across the upgrade keeps the old half's refusals as unknown and the new half's by kind.

**Decision: no hourly downsampling in v0.1.**
- The longer ranges group minutes in memory (6 minutes a step for a day, 42 for a week, 3 hours for a month, 240 points each) from at most 43 200 rows of one series. At about 150 series a day is a few tens of MiB, and the retention default keeps the table in the low hundreds of MiB for a busy install.
- A second, coarser table would add a job and a second query path for a saving nobody has asked for yet. The unique key and the typed-ID subject leave room for it.

**Known approximations.**
- A latency percentile of a merged or grouped minute is the requests-weighted mean of its parts, not the percentile of the union.
- A stall's gain is spread evenly over the stall, not placed where within it the requests were.
- A suspension with requests completing on resume is reported as covered (above).

The longer ranges are summed in SQL (`GROUP BY` the object or the step, `SUM`s only, the observed seconds included: nothing of the range becomes an entity). The seed is one read of the `Minute` index range. A range's rollup read is kept for 30 s, since the page polls every twenty.

### The overview

`MetricsService` (Application) is the one read side for the API and the dashboard. `PrometheusText()` and `ReadOverviewAsync(range)` return `MetricsOverview` (Contracts): the pace over the last 30 s (requests, 5xx share, refused, bytes, p50/p95/p99 total and upstream), a chart series, the eight busiest hosts and the upstreams ordered by errors.

**The hour** is the seeded minutes up to where this run's ring begins (a bucket a minute, the boot minute's first half cut short where the ring starts), then the ring's ten-second buckets. A day, week or month reads the rollups, so they lag by up to a minute plus the write interval.

**Gaps are in the data, not in the drawing** (`MetricTimeline`).
- The series runs from the first step the server observed to the last. Every step between them is present.
- A step the server was not running for has `coveredSeconds` 0 (and every figure 0) next to `widthSeconds`. That is distinct from a covered step that saw no requests (a rate of 0, no latency).
- A step has its own width, because the hour mixes minute-wide steps with ten-second ones. A longer range's step covers the sum of its minutes' coverage, and its rates are over those seconds.

**Decision: the part of the range before the first observed step, and any step after the last, are not gaps** and are left out. A fresh install's 30-day view starts where its data does, and the minute the rollups have not been written for yet is not drawn as downtime.

**Decision: a step observed in part is an ordinary point.** The server started or stopped inside it. Its rate is over what was observed, and its tooltip says "Observed 20 s of 1 min". It is not a lighter segment: only a step with no coverage at all breaks the line.

**Where the dashboard shows it.** The traffic over a range is the **Traffic tab of Analytics** (`TrafficStatistics`, under the page's one range picker; see dashboard.md, "The Analytics page"), and the home page keeps a strip of four live tiles (`TrafficStrip`). Both read `MetricsService.ReadOverviewAsync`.
- **The Traffic tab** shows:
  - a requests chart (area, with 5xx) and a latency chart (p50, p95, p99 and the upstream's p95), as inline SVG (`TimeSeriesChart`: no dependency, a script-driven tooltip per point (see dashboard.md, "Charts"), a round-number scale);
  - the busiest hosts (linked; the requests no host owns, unlinked);
  - the upstream errors (named from the config).
- **The strip** shows requests per second, the 5xx share, refused per second and latency p95 over the last 30 s (`Now`), each with a sparkline of the last hour from the ring (10 s steps). It is always the live hour.
- **The hour on the Visitors tab** is live to within a few seconds: `AnalyticsService` merges the stored buckets, the closed buckets waiting in the backlog and the open bucket the drain feeds every 200 ms, and keeps a result for 5 s.

**Chart rules.**
- The axis is time (points sit at their start times, so a minute-wide step and ten-second steps share it).
- A line is **one polyline per unbroken run of values**. A lone point is a mark the width of its step. The latency lines also break where a covered step timed nothing.
- A stretch with no data is a quiet shaded band whose tooltip says "No data: Paduraksa was not running" (`pdk-chart-gap`, a 6 % tint of the text colour, so it reads in both themes and at phone width).

**Polling.** On the Traffic and Security tabs the hour polls every two seconds while the browser tab is visible (the `paduraksa.pageVisibility` pause, as the security page), the strip every five, and the Visitors tab every ten. Only the open tab polls. The rollup ranges poll a tenth as often (a sixth on Visitors). A range that cannot be read shows an alert with "Try again". The pages need `metrics:read`.

**Not built:** the per-host page's own traffic (the Traffic tab's table links to it), which would be one more call to the same service.

**Left out, with the seams.**
- *Health checks and notifications:* the registry's per-upstream error counters are the input. YARP's `IUpstreamHealth` is already read by the simulator.
- *Route and destination history:* add a `MetricRollupSubject` value and a series kind in the sampler.
- *Downsampling:* above.

### Security counters

Built in `security-history`. Before it, the security overview read the gate's process counters live, so every restart reset it to zero.

**What is sampled** (`SecuritySources`, read by `MetricSampler.Tick` after the slots):
- the process stats: `ProofOfWorkStats`, `RateLimitStats`, `AccessListStats`, `ConnectionStats` (not `Open`, which is a level);
- every auth provider's `ForwardAuthCounters` and every rule set's `RuleSetCounters`. The runtimes now keep an append-only array of them (`ForwardAuthRuntime.AllCounters`, `RuleSetRuntime.All`), replaced under a lock when an ID is first seen, so the tick walks them with no enumerator and an entry keeps its index for good. Production compiles with `RuleSetRuntime.Default`, so that is the one the sources read.
- The 32 counters are named by `SecurityCounter`; a `SecurityPoint` holds them, the start, the width and the seconds observed.

**Series** (`SecuritySeries`):
- one for the totals (the process stats plus every provider and set, so a deleted provider's counts stay in them), one per provider and one per rule set, keyed by the typed ID.
- A provider's or set's counters never reset and are never handed to another ID, so a gain is a plain difference: there is no slot, no owner and no version to follow.
- Gains are filed like the request series': the same tick interval, split in proportion at minute boundaries (`TickSplit` with 60 s buckets), the same coverage, and the same judgement of a long gap. A connection refused during a gap counts as having been served through it, like a request.
- **Decision: minutes only, no ten-second ring.** Nothing draws the security counters at ten-second steps: the refusals chart is the request series' breakdown above. A series keeps the minutes it closed in the last hour (61 points) and the open one. An idle provider's or set's minute is not kept and allocates nothing.
- The totals have a row for every minute the process observed, idle ones included (their coverage); a provider's and a set's rows are sparse, as hosts' are.

**Storage** (`SecurityRollups`, migration `SecurityRollups`, Int64Ordered `SecurityRollupId`, `srl_`):
- subject (`SecurityRollupSubject`: Total, AuthProvider, RuleSet), the typed-ID string, the minute, `CoveredSeconds` and a column per `SecurityCounter`. A unique index on (subject, ID, minute) and one on the minute, as for the request rollups.
- **Decision: a table of its own, not a new `MetricRollupSubject`.** The two have no column in common beyond the key and the coverage. One table would make every request row carry 32 empty columns, every security row 20 unused ones, and the request aggregates read past them.
- `SecurityRollupStore` mirrors `MetricRollupStore`: a minute written twice is merged (counts and seconds add, capped at 60), the seed reads the Minute index, and a range is `SUM`s grouped by the key in SQL (an `EXPLAIN QUERY PLAN` test pins it to the index).
- The sampler queues finished minutes in `MetricSampler.SecurityRollups` (a second `RollupBacklog<T>`, bounded the same way). `MetricRollupJob` seeds both histories at startup, writes both queues every interval and at shutdown, and prunes both with `Paduraksa:Metrics:Retention`. A failed write puts that queue's minutes back without holding up the other.
- `MetricSampler.Flush` closes the open security minutes with the request ones at shutdown, and `SeedSecurity` gives the sampler the hour before the boot, as `Seed` does.

**Cost.** The tick reads about 32 counters per provider and set and diffs them into fixed arrays: 0 B in steady state (`ASteadyTickAllocatesNothingWithEverySecuritySourceAndAProviderAndASet`, ticks inside one minute with every source, a provider and a set). A closed minute allocates its point and its queue entry, once a minute per series that counted something.

**Read** (`SecurityOverviewService.ReadAsync(range)`, the ranges of `MetricsService`):
- The hour is the sampler's: the request series' timeline (`MetricTimeline.Hour`, the seed and the ring) for the refusals, `SummariseSecurity` (seeded minutes, this run's minutes, the open one) for the counters. It touches no store.
- A longer range reads the rollups: the request series and host summaries through `MetricsService.ReadRangeAsync` (the overview's own read, which shares its 30 s cache), and the three security summaries, kept 30 s per range.
- **Decision: a longer range's figures take their last hour from memory.** Rollups lag a minute plus the write interval, so a request made a moment ago (the auth-provider E2E asks for one on the security page) would show only later. `MetricsService.RecentFrom(at)` is the first whole minute of the last hour: from there the hosts', upstreams' and security summaries come from the sampler (seed, this run's minutes, the open one), and the rollups are read only to the minute before, so no minute is read twice. A cached rollup read is kept only while that split minute holds. The overview's busiest hosts and upstream errors get the same splice.
- The chart's steps stay the rollups', so their last minute or two lag; a longer range's refusal totals are the hosts' (spliced), the hour's are its chart's. `From` is the first step observed inside the range; absent when none was.
- A host deleted in the last hour drops out of the memory half once its slot goes to another host (its series is flushed and dropped); its minutes are in the rollups, which that half doesn't read. A rig that writes rollups for the last hour without seeding the sampler (a test) sees nothing of them in a longer range.

## The live tail (M9)

Built in `m9-live-tail`. The ring keeps its one reader. The request path is untouched (no new field, no new work, still 0 B). The drain, which already formats every entry, offers it to a second place once the file's line is written.

### The hub

`AccessLogTail` (`Paduraksa.Proxy.Logging`) holds the newest `Paduraksa:AccessLog:TailSize` entries (default 4 096, range 64 to 65 536, validated and documented in [the user guide](../guide/observability.md)) and a bounded channel per subscriber.
- `AccessLogDrainService` calls `Offer` after `sink.WriteLine`, in its own `try`. A failure in the tail is reported like a failing sink and never costs the file a line.
- **Per-host on or off needs no code of its own.** A host with its access log off never reaches the ring (`AccessLogMiddleware.Record` returns first), so it is in neither the file nor the tail.
- **Decision, recorded because privacy depends on it:** "off" means not recorded anywhere. A test proves it through a real listener.
- The tail is memory only (a restart empties it) and holds what the file holds, no more.

### Copied, and shown under the file's rules

The ring's slot is reused, so `Offer` copies the struct into the buffer (`AccessLogTailEntry.Bound`).
- Any string longer than a character past its cap is cut. The raw query is cut at the file's own query cap of 1 024 characters, so the tail never holds more of a query than the file could show. Nothing is allocated unless a string is cut.
- Cutting the query before redaction is safe. A cut only removes the end of the last parameter, so a sensitive one that straddles it is a name with a partial value (written `name=REDACTED`) or a partial name (no value at all). A test puts a sensitive key at the cut in three positions.

**Memory, stated as it is.**
- An entry is typically a few hundred bytes to a KiB, so 2 to 4 MiB for a full default buffer.
- The worst case is every field at its cap: 3 897 characters (about 7.6 KiB) plus string headers and the slot. That is **about 8 KiB an entry, about 32 MiB at the default 4 096 and about 512 MiB at the largest size**. It is reachable only by a client that sends kilobytes in every field of every request.
- A subscriber's channel adds at most 2 048 converted entries, which share their strings with the buffer while the entries are still in it.

**Decision: nothing is converted until someone looks.** A subscriber's entries and a backlog become `AccessLogTailEntry`s when they are read:
- capped at the file's caps;
- escaped by the one escaper the file's quoted fields use (`LogEscaping`: `\"` and `\\`, C0 and C1 controls including NEL as `\xNN`, U+2028 and U+2029 as `\u2028` and `\u2029`);
- the query redacted by the formatter's own routine (`AccessLogFormatter.AppendQuery`, which `BuildRequestTarget` now calls too). The tail and the file therefore cannot disagree on which parameters are sensitive, `LogQueryString=false` included.

A parity test formats an entry both ways and compares each field with the file line's quoted text as written, with no unescaping on either side. It covers quotes, backslashes, CR/LF, NEL, C1 controls, U+2028 and U+2029 in the path, query, host, user agent, referer, upstream, request ID and `CF-Ray`, the redaction list, and a replaced list.

The alternative, converting every entry as the drain sees it, measured 296 B and about 300 ns an entry with nobody watching, so it was dropped for the lazy copy.

### Hosts

The entry carries only the `Host` header. The host that owned the request is resolved against the live snapshot (`HostTable.Find`) when the entry is converted: `MatchedHostNames` (all of the host's names) or none.
- A backlog entry of a host deleted since reads as "no host matched".
- A streamed entry is resolved as the drain offers it, within 200 ms of the request.
- The alternative, a field on `AccessLogEntry` set by the request path, was refused: the brief keeps the request path as it is.

### Subscribers

`Subscribe(filter, backlog)` takes the hub's one lock to copy the buffer's structs and to register the subscriber in the same step. It then converts and filters the copy outside the lock, so the backlog and the stream neither skip nor repeat. A concurrent test checks the sequence is gapless over 32 subscribers racing a producer.

- A subscriber's channel is bounded (2 048) and drops its oldest entry when full, counting it (`TakeSkipped`). The drain never waits for a reader's channel.
- The subscribers are bounded too: `MaxSubscribers` is 32, dashboard tabs and API streams together. The 33rd is refused.
- The filter runs before the channel: host name, "no host matched", status class or status, client address prefix, method, path contains. A narrow filter therefore does not fill the channel with lines it will never show.
- **The lock** covers the drain's store and the hand-over to the channels (a write to a bounded channel never waits) and a reader's plain array copy of the buffer, about a millisecond at the largest size. The drain waits for a reader's call only for that copy, and converts the entry before taking the lock. The request path never takes it.

### Cost, measured

`bench/Paduraksa.Benchmarks/AccessLogDrainBenchmarks`, per entry through `AccessLogDrainService.Drain`, in-process, medium job, workstation GC. The box was noisy, so read the differences only roughly.

| Case | Time per entry | Extra allocation |
|---|---|---|
| File only | 670 ns | |
| Tail, nobody watching | 825 ns | **0 B** (a struct copy, ten length checks and an uncontended lock, off the request path) |
| A subscriber that keeps up | 1 020 ns | 296 B (one entry object shared by every subscriber) |
| A subscriber that never reads | 1 060 ns | its channel overflows on every entry |

- `TheGateAllocatesNothingWhileTheTailHasASubscriber` runs the real Kestrel pipeline with a running drain and an overflowing subscriber, and asserts the gate at 0 B a request.
- It is "free" in the sense that matters (the drain is not the request path, and at the bench's 170 000 requests/s it is a fifth of a core more), not in the sense of unmeasurable. A tail that converted lazily only when read was the way to get it that low.

### The API

- `GET /api/v1/logs/tail` (`logs:read`, in OpenAPI as `StreamAccessLog`, `text/event-stream`). The wire format is in [dashboard.md](dashboard.md).
  - It lives in `LogEndpoints` beside the file endpoints, reads through `LogTailService` (Application: opens a subscription, maps `AccessLogTailEntry` to the contract's `LogTailEntry`), and is bounded like a dashboard subscriber (the same channel and the same 32).
- `GET /api/v1/logs/recent` (M10, for MCP; see [admin-surfaces.md](admin-surfaces.md)) is the same buffer as one JSON array: the newest `limit` entries (1 to 1000, default 100) the filters keep, oldest first, with no stream.
- The Application layer is the only place that sees the hub besides the drain. Admin and Dashboard get contract types.

### Tests

- Unit (`AccessLogTailTests`, 40): ring semantics and wrap, drop counting, the subscriber bound, atomic subscribe, every filter, wildcard and multi-name hosts, redaction and caps parity with the file formatter, what the buffer keeps of a long field, the drain offering after a failing sink, and a tail that throws never costing the file a line (`AccessLogDrainServiceTests`).
- Proxy (`AccessLogTailEndToEndTests`, `AccessLogAllocationTests`): requests through a real Kestrel listener appear with the right fields and redaction, a logging-off host never does, the gate at 0 B with a subscriber.
- Admin (`LogTailApiTests`): scope per role, entries with sequence and redaction, filters and a backlog, heartbeat, the skipped event, the stream bound with a 503, a disconnect freeing its place, 400s, the OpenAPI entry.
- Dashboard: see [dashboard.md](dashboard.md).
- Run locally, a developer machine's port prober sends `GET /` to every new listener, and it shows up in a tail like any request. The tests that count entries filter to their own client address.

### Left out

- Persistence across restarts.
- Regex or CIDR filters (a prefix of the address text is what the filter takes).
- Saved filters.
- Export (the file is the record).
- Other nodes (the fleet design already names the pull over the sync stream; see [deployment.md](deployment.md)).
- Both a field on the entry for the matched host and a per-request sampling are seams: the first needs the request path, the second the drain.

## Traffic analytics

Built in the `analytics` branch: visitors and content, clients and bots.

Nothing is new on the request path. The ring entry already carries the host, path, method, protocol, status, body bytes both ways, durations, user agent, referer and client address. So analytics is a **third consumer in the drain** (`Paduraksa.Proxy.Analytics.TrafficAnalytics`), offered each entry after the file's line and the tail, in a `try` of its own.
- A failure in it is reported like a failing sink and never costs the file a line.
- A host with its access log off never reaches the ring, so **it is in neither the file nor the analytics** (a test proves it through a real listener, as for the tail).
- `TrafficAnalyticsDrainTests` and `AccessLogAllocationTests.TheGateAllocatesNothingWhileTheAnalyticsAreFedByTheDrain` keep the gate at 0 B with the analytics fed by a running drain.
- Query strings are never counted: a path is the decoded path without its query.

### Buckets and memory

- A bucket is five minutes wide: one `TrafficBucket` per host that had traffic, plus one for the requests no host owns. Hosts are resolved against the live snapshot when the drain reaches the entry, like the tail. At most 1 024 per bucket; the rest are counted with the unowned.
- A bucket closes five seconds after its end (so entries still in the ring land in it) into `AnalyticsBacklog`, bounded to 4 096 rollups. A database that stays down loses the oldest, never memory. A shutdown closes the open bucket as partial.
- **Memory is fixed by construction:** the open buckets (one or two) times the hosts with traffic. A bucket is:
  - 4 KiB of visitor registers;
  - four Space-Saving sketches (paths 64 keys, referers 32, user agents 32, largest responses 16);
  - 64 latency histograms of 808 bytes (one per tracked path);
  - a few bounded counters (status codes 64, methods 16, browsers 64, systems 32, bot names 32).
  That is about 100 KiB for a busy host and a few KiB for a quiet one. The user-agent cache is 4 096 strings (LRU).
- A late entry (its bucket closed already) is filed in the oldest open bucket, since dropping a request that was served would lose it.
- A bucket's stored form is 2 to 20 KiB: Brotli of varints, the visitors sketch on its own, sparse while it holds few registers.

### What is counted, and the bounds

**Exact** (up to their table sizes, the rest in "other"): requests, response and request body bytes (counted since [Counted body bytes](#counted-body-bytes); before it, the declared `Content-Length`s, a floor), status classes and codes, methods, HTTP versions, and the device, bot-kind, browser, system and bot-name distributions.

**Unique visitors** are a HyperLogLog: precision 12, 4 096 registers, standard error 1.04/sqrt(4096), about 1.6 %. They are merged by the register-wise maximum, so a range's visitors are the union of its buckets' (a visitor on two hosts or in two hours is one).

**The top tables** (paths, referers, raw user agents, and paths by bytes) are **Space-Saving** sketches.
- Every tracked key has a count and an error. Its true weight is between `count - error` and `count`.
- A key that is not tracked weighs at most the sketch's floor, and a key heavier than the floor is always tracked. In a bucket the overshoot is at most (requests / capacity).
- Merging (Agarwal et al.) takes a key one side lacks at that side's floor, in its count and its error. So **a range's bound is the sum of its buckets' errors** and stays a true bound.
- A range is merged into a 16 times larger sketch so a month rarely drops a key that matters, and an hourly roll-up into one twice the size.
- A path's payload (bytes, 5xx, latency histogram, so the p95) covers the requests seen *since the path was tracked* (an evicted path starts over). The slowest-paths table needs five.

**Decision: error rate is the share of 5xx**, as in the metrics overview. 4xx is the status breakdown's.

**Decision: the largest responses are their own sketch weighted by bytes**, not a column of the paths sketch. A large file downloaded three times is exactly what a sketch ranked by requests would evict.

### User-agent classification

**Decision: our own table, not a library.** `UserAgentClassifier` is one file of rules. The answer is a handful of families. A library brings thousands of regexes, which the rest of the proxy avoids, and a dependency to track.

Rules are tried in order:
1. AI crawlers (taken from `RobotsText.AiCrawlers`, so the two lists cannot drift);
2. search engines;
3. social previews;
4. monitors and uptime checkers;
5. scanners;
6. named SEO crawlers;
7. HTTP libraries;
8. anything calling itself a bot, crawler, spider, scraper or fetcher (named by its own product token);
9. then browsers, from the most specific token (Edge's `Edg/` is in a Chrome agent) to the least, an operating system and a device (desktop, mobile, tablet, bot, other: a television or console).

It reads the first 1 024 characters for a bot's token (bots name themselves late) and the first 400 for the browser, system and device, case-insensitively.

**A miss is cheap on purpose.** The drain is one thread, and an attacker chooses user agents.
- A bitmap of the first three characters of every token is tested at each position in about a nanosecond. Only a position that passes is compared with the rules that start there, the lowest rule index winning, capped at 48 matches per agent.
- A 1 KiB agent with no token in it takes about 3 us, and one made of nothing but tokens about 5 us. The first version took 34 us to 400 us.
- The cache (`UserAgentCache`, 4 096, LRU) is keyed on the capped copy, so a 32 KB agent pins 1 KiB.

Honest limits: Brave and most forks are Chrome, an iPad on iPadOS 13+ sends a Mac's agent, and a scanner that sends a browser's agent is a browser. It is tested against about 90 real agent strings. A `BotKey` (`SearchEngine|Googlebot`) is the counted label, built once with the cached classification.

### The drain never pays for analytics with a file line

**Rule:** when the ring is more than half full, an agent the cache does not know is not classified. It is counted as "Unclassified", device other, not a bot, and the robots check is skipped. The entry is still counted, with its path, status, bytes and visitor.

The drain passes `behind` to the analytics from `AccessLogRing.Pending` (`AccessLogDrainService.BehindDivisor`).

Tests: one streams 20 000 unique 1 KiB agents at 20 000 a second through a running drain and requires every line in the file and none dropped. Another fills a ring past half and checks the first entries are the unclassified ones.

### Bots ignoring robots.txt

For a host that serves a managed robots.txt (any mode but passthrough), a bot of a kind that is supposed to read it that fetches a path the file disallows *for that agent* is counted by name. The kinds are search engine, AI crawler, social preview, scanner and other bot, **not** monitors and HTTP libraries.

- `RobotsRules` reads the compiled body back (RFC 9309). The group with the longest agent name inside the crawler's agent applies, else `*`. The longest rule wins, and Allow wins ties. `*` and `$` work. Matching is without backtracking, since a custom file is operator text. `/robots.txt` is always allowed.
- It is cached per host and per compiled response object, so a publish invalidates it.
- The group that applies to an agent is resolved once per agent (256 per host). The rules are matched against the path cut at 256 characters, with each rule's literal prefix and pinned end worked out once, so a rule that cannot match costs one comparison. 1 000 wildcard rules that all share the path's prefix cost about 25 us for a bot's entry, the extreme. 2 048 rules is the cap.
- It runs in the drain against the live snapshot, and not while the drain is behind.

### Visitors and privacy

**Decision: a random salt per process, in memory only, never stored.**
- The client address is hashed (SHA-256 of the 16-byte salt and the address) into the sketch. The address is an IPv4 address or an IPv6 address's /64, with v4-mapped IPv6 counted as the IPv4 address, parsed by the gate's allocation-free `IpParser`.
- The sketch keeps only the longest run of zeros per register. Nothing stored can be turned back into an address, even by someone holding the database and the binary.
- A per-day salt would not change that (a sketch is already one-way) and would cut every visitor at midnight in a range that spans it. A salt that is stable across restarts would have to be stored, which is what we avoid.
- **The cost is accepted and stated: a visitor active across a restart is counted once on each side of it.**

What else is kept:
- A referer is reduced to scheme and host (no port, path, query or credentials). It is `direct` when absent and `(same site)` when it is the host the request was for.
- User agents are kept as sent, cut at 256 characters, control characters replaced.
- Paths are kept as the access log keeps them, so a secret that lives in a path is in both.

### TLS version left out

The brief allowed a field on the entry only if the gate already had the value at 0 B. It does not. The TLS version is a connection feature Kestrel exposes behind a lookup (`ITlsHandshakeFeature`), which the gate does not read, so adding it would put new work on the request path. The seam is one field on `AccessLogEntry` filled where the gate already has the connection, with an allocation test.

### Rollups

`AnalyticsRollups` (migration `AnalyticsRollups`; Int64Ordered `AnalyticsRollupId`, `anr_`) holds:
- a **tier** (0 five minutes, 1 an hour, 2 a day);
- a **subject** (coverage, a host, or the requests no host owns);
- the host's typed-ID text (never the in-memory slot);
- the start of the stretch (Unix seconds), `CoveredSeconds`, `Requests`, `BytesOut`, `ServerErrors`;
- two blobs (`Visitors`, `Data`).

There is a unique index on (tier, subject, host, start) and one on (tier, start).

**Coverage honours the M9 timeline semantics.**
- Every tick the drain makes credits the time it observed to the buckets it fell in. A tick later than five seconds counts only if it brought entries: a suspended process has an empty ring.
- A closed bucket writes a coverage row even when nothing happened. So a stretch with no coverage row inside the observed span is a **gap, not zeros**, and a quiet bucket is a bucket with coverage and no host rows.

**`AnalyticsRollupJob`** is registered before the drain, so it stops after it and carries the bucket the drain closes on its way out.
- It writes every 15 s (`Paduraksa:Analytics:WriteInterval`).
- It merges a bucket written twice into its row (a restart inside it): counts add, visitors union, coverage adds up to 300.
- It **rolls an hour up ten minutes after it ends** into one row per host (sketches merged at twice the size, one transaction per hour, idempotent).
- It then rolls **a day up once all its hours are rolled** (four times the size).
- It prunes hourly.

**Retention** is `Paduraksa:Analytics:Retention` (default 90 days since `server-settings` (was 30), a server setting read at every prune pass, range 1 hour to 5 years, validated, in [the user guide](../guide/observability.md#analytics)). Five-minute rows are kept two days once their hour is rolled up. Hourly and daily rows are kept for the whole retention.

**Decision: reads take the stored rows and the backlog's unconfirmed buckets under one gate with the writer.** A bucket is therefore in exactly one of the two: never missing, never doubled.

### Reads, cost and the API

`AnalyticsService` reads:
- **daily rows** for the whole rolled-up days inside the range (only the month, whose step is a day);
- **hourly rows** for the whole rolled-up hours of what is left (the day and the week);
- **five-minute rows** for the edges and the hours not rolled up yet (the hour reads only these, since its step is five minutes).

The steps are 5 minutes (hour), 1 hour (day), 3 hours (week) and 1 day (month).

`EXPLAIN QUERY PLAN` tests pin every read to an index: all hosts use the (tier, start) walk, one host a seek on (tier, subject, host, start), and coverage the same.

**Measured on a Release build** (20 busy hosts, 30 days of hourly rows: 15 120 rows of about 2.7 KB of data and 4 KB of visitors sketch):

| Read | Time |
|---|---|
| A month for all hosts, without the daily tier | 1.2 s (0.4 s of it reading, the rest decoding and merging 14 400 blobs) |
| A month for all hosts, with the daily tier | **75 to 85 ms** |
| A month for one host | 5 ms |
| A week for all hosts | 0.23 s |
| Rolling the 30 days up once, as a catch-up | 1.7 s (a day a day in normal running) |

The daily tier was added because of the first row.

- A result is kept 5 s (30 s beyond the hour), at most 128 of them (expired first, then the oldest). The API accepts any well-formed host ID, so a deleted host's history stays readable.
- A step with requests and no coverage row is drawn as data (observed for the moment it is known to have had), so the chart and the totals agree. A step with neither is the gap.
- A dropped connection is the status "none" ("No response" on the page), the API's class name.
- `GET /api/v1/analytics?range=hour|day|week|month&host=host_...|unmatched` (`metrics:read`, in OpenAPI) returns `AnalyticsOverview`: totals, the series (with `coveredSeconds`), the status classes, codes, methods and protocols, the tables with each row's error bound, the browsers, systems and devices, the bot kinds, top bots and bots ignoring robots.txt. It is a **read-only** endpoint (for the MCP classification at merge).

### Bench

`bench/Paduraksa.Benchmarks`, `AccessLogDrainBenchmarks`, per entry, one pinned core:
- The file's line alone is 0.93 to 1.0 us.
- The analytics add about 0.8 to 0.9 us an entry (1.7 to 1.9 us in all). They make about 190 B of garbage an entry on the drain's thread (the referer's normalised text, mostly) for the same entry, and about 700 B for 256 varied ones.
- The live tail's reader costs about 0.4 us for comparison.
- **The worst case, every entry a unique 1 KiB agent (the cache always missing): 6.6 us an entry in all (about 150 000 entries a second), 800 B an entry.** Before the classifier was made cheap it was about 400 us (2 500 a second), which is where the ring would have dropped lines.

End-to-end (`run-baseline.sh`, h1 and h2-tls, three alternating rounds against the merge base):

| Measure | Branch | Merge base |
|---|---|---|
| h1 median throughput | 174.1k req/s (rounds 174.1, 172.3, 174.6) | 173.3k (173.2, 173.3, 174.1) |
| h1 proxy CPU | 18.9 to 19.3 us | 19.0 to 19.1 us |
| h2-tls throughput | 153.5k | 152.7k |
| h2-tls proxy CPU | 22.9 to 23.7 us | 23.0 to 23.3 us |
| p99 h1 | 0.74 to 0.79 ms | 0.79 to 0.82 ms |
| p99 h2-tls | 1.01 to 1.06 ms | 1.02 to 1.07 ms |

**The request path is unchanged, inside the run-to-run noise**, as it should be. The runner's hosts have the access log off by default, and the analytics live entirely behind the ring either way. The cost is the drain's, above.

### Later (not built)

- Geo and a country map (after v0.1, needing a local country database).
- Protection-over-time series (the gate's refusals by kind per step).
- Top client IPs. They need a place to keep addresses, against the privacy rule above, so they would be a separate, opt-in decision.

## Health state and notifications (M9 part 2a)

Built in `m9-health-notify`. Active and passive checks themselves were built in M1. This adds what is done with their verdicts. The live tail (part 2b) is separate.

### Reading YARP

- `IUpstreamHealth.ReadAll` returns, per upstream, each destination's active, passive and combined verdict (the combined one is computed: YARP's own is internal) and the last active probe in words ("HTTP 503", "timed out", "connection refused": never the exception's text).
- YARP does not keep a probe's result. So `ProbeRecordingPolicy` wraps its registered active policy (same name, the verdict logic stays YARP's) and notes each result in a `ProbeLog` held weakly by destination state.
- `Lookup` (the simulator's) is left as it was and **cannot say an upstream is down**. YARP's available-destinations list falls back to every destination when none is healthy (pinned by a test), which is why `ReadAll` reads verdicts.

### Passive health

The soak's flap (bench/soak/README.md, `soak-fixes`) found that a destination back from an outage stayed out about a minute, with `destination.up` as late. Two things in YARP's `TransportFailureRate` policy caused it:
- **The period was raised.** YARP uses `max(reactivationPeriod, DetectionWindowSize)`, and the window is 60 s by default. So any shorter `passiveHealth.reactivationPeriod` was ignored: the period reached YARP's model and changed nothing.
- **The history outlived the verdict.** YARP raises the period so that the failures still in its 60 s window cannot take the destination out again on its first request back. When the active check had the destination back first, they did exactly that.

As built, `PassiveFailureRatePolicy` replaces YARP's policy under the same name, so the compiled config doesn't change. YARP's registration is removed, and a YARP that registers it differently fails at startup rather than running both.
- **The verdict is YARP's.** It is the share of transport failures over the 60 s window, after 10 requests, against 30 %, read from the same `TransportFailureRateHealthPolicyOptions`. It uses the same failure classification, and a client that went away is not a failure.
- **The upstream's period holds as configured.** An unset period is YARP's default, 60 s.
- **The history is cleared when it takes a destination out.** Without the raise, that is what stops old failures from counting twice.
- **Requests that end while the destination is out are ignored.** They were in flight when it was taken out, and recording them would carry the outage into the next window.
- **The active check seeing a destination back is a recovery.** `ProbeRecordingPolicy` notes each destination's active verdict before YARP's policy runs. When one goes from Unhealthy to anything else, it clears the passive history and lifts a passive verdict from the same outage (Unknown, so it routes at once). So with probes on, a recovered destination is back within one probe interval whatever its period, and `destination.up` follows after the notification hold.
- **The request path is lock-free and 0 B.** YARP's policy takes a lock per request. Here each destination has a fixed array of one-second buckets, one `long` each (the second in 28 bits, then requests and failures in 18 bits each, saturating), updated by compare-and-swap. Only a failure sums the window. Only a change of verdict calls YARP's updater, which allocates to refresh the cluster.
- **Tests.** `PassiveFailureRatePolicyTests` checks the period, the clears, the window and the allocation on a clock the test moves. `PassiveHealthRecoveryTests` checks a 2 s period and a flap with probes through real YARP. Both fail on YARP's own policy.

### The watcher

`HealthMonitor` (a core-host hosted service, never the request path) reads `ReadAll` every second into the pure `HealthTracker` and publishes the result to the `UpstreamHealthBoard`. The dashboard and `GET /api/v1/upstreams/health` (and `/upstreams/{id}/health`, `config:read`) read it.

**Debounce.** A new state must hold `Paduraksa:Notifications:HealthHold` (10 s) before it is reported.

**Flap guard.** An object (a destination, or an upstream as a whole) that changes 4 times in 10 minutes is reported once as flapping and then silent until it has been still for 10 minutes, when its state is reported once more.

**What is not a change** (see the state table in [config.md](config.md)):
- a destination first seen healthy or Unknown;
- an Unknown that follows a lapsed passive verdict;
- a destination removed by a publish (forgotten, so re-adding it cannot report a recovery);
- an upstream with no check configured;
- a publish that keeps the IDs keeps YARP's state, so reports nothing.

A destination first seen unhealthy is down. An upstream's change replaces its destinations' changes of the same direction in the same tick (one message names the destinations).

**Audit and events.** Each reported change is a **system audit entry** (`upstream.down`, `upstream.up`, `upstream.destination.down` and `.up`, `*.flapping`; target the upstream, details the destination, address, reason, since) and an event.

**Proof.** `UpstreamHealthNotificationTests` runs a real probe against a switched listener on a real host with the dispatcher and a fake webhook. There is exactly one audit row and one message per transition, none for publishes. A restart forgets "last change" (the audit has the history).

### Channels

Channels are an **operational table** (`NotificationChannels`, migration `NotificationChannels`), the DNS-provider decision.
- They hold delivery state outside the document and secrets that must never be in one. So there are no revisions or review-before-apply.
- Each change is audited (`notification.channel.create`, `.update`, `.delete`, `.test`).
- Permissions are **`providers:read` and `providers:write`**, shared with ACME accounts and DNS providers (they hold credentials the same way). No new permission, role or token scope.

Kinds:

| Kind | How it sends |
|---|---|
| ntfy | JSON publish to the server root, optional bearer token. |
| Gotify | `POST /message`, token in `X-Gotify-Key`. |
| Discord | An embed, `allowed_mentions.parse` empty, markdown in names escaped. |
| Generic webhook | JSON. With a signing key: `X-Paduraksa-Timestamp` and `X-Paduraksa-Signature: sha256=hex(HMAC-SHA-256("<timestamp>." + body))`. |
| Email | MailKit. |

**Email is MailKit 5**, not `System.Net.Mail.SmtpClient` (Microsoft discourages it, and it has no modern TLS or SASL).
- MIT licensed and maintained.
- STARTTLS can be *required*: a server that does not offer it is refused, never sent the password in clear. Or implicit TLS.
- The certificate is checked against the system roots.
- Plain text with `Auto-Submitted`. Recipients and the subject are sanitised.

The senders live in a new project, `Paduraksa.Notifications` (Core only), so MailKit stays out of Application.

### Secrets

A webhook URL, token and SMTP password are vault `NotificationSecret`s. A webhook's signing key is an `HmacSigningKey` read through `IMacKeySource` (HMAC stays banned outside the vault).
- Migration `NotificationSecrets` rebuilds `Secrets` to widen its kind check. A test pins that `TotpStates` survives and that its Down deletes the notification secrets first (the narrower check would refuse them), keeping every other row. EF's non-transactional-PRAGMA warning is ignored for the vault only.
- **The vault grows its fourth sanctioned egress**, the narrowest: `INotificationSecretSource.UseAsync` lends a `NotificationSecret`'s value to a callback as a view of the pinned buffer, zeroed when the callback returns. A webhook URL, token or password has to be used by the sender, which no handler or MAC handle can do. Every other kind answers as absent, nothing returns the value, and the arch test lists the position.
- A secret is in no config document, response, OpenAPI document, audit entry or log. Tests walk them all.
- A replacement keeps, replaces in place or removes (empty) a secret. A failed change takes back what it stored.
- Failures read as fixed lines ("The server refused the credentials (HTTP 401).", "Could not connect to the server.") and say whether a retry can help.
- **No TLS with a login is refused.** A channel with `smtpSecurity: none` may carry neither a login nor a password (`notification.smtp.insecureLogin`), since both would cross the network in clear. A trusted-network relay takes neither.
- **SSRF, decided.** A channel's URL or SMTP host may be any address, loopback, link-local and the admin port included. That is accepted because only `providers:write` holds it (as with DNS providers), responses are never echoed (only the fixed lines above), redirects are refused, and the test endpoint grants nothing the delivery loop does not.

### Events

- **Upstream down and up, and destination down and up** (above). New channels start without the destination pair, which a one-destination upstream says twice.
- **Certificate expiry.** `CertificateExpiryMonitor` looks hourly at every active certificate whatever its source (ACME, uploaded, self-signed, local-CA leaves and the root). It sends the smallest crossed threshold of 30/14/7/1 days **once per chain and threshold**.
  - `ExpiryNotices` (certificate, `NotAfter`, days; cascade on delete) records it, so a restart does not repeat it. A renewal (a new `NotAfter`) starts over. Several crossed thresholds are one message with all marked.
  - **Nothing is marked while no enabled channel subscribes**, so a channel added at 5 days still hears the next one.
- **ACME.** The first failure of a streak (not every retry; the expiry notices cover a certificate that stays unrenewed), and the recovery after it.
- **Sign-in anomalies, defined:**
  1. An account locked by failed sign-ins (the lockout the identity core decides).
  2. An Owner or Admin signing in from an address with **no completed sign-in as that user in the last 30 days**, for a user who has signed in before. A password-only step does not count until the second factor completes.
  The answer comes from the audit log, so a restart forgets nothing.
- **The lookup is cheap by construction.**
  - Two seeks on the `AuditEvents (ActorUserId, Action, Timestamp)` index (migration `AuditSignInIndex`), bounded by time ("has signed in before" looks back a year, "known address" 30 days). They run before the sign-in's write transaction opens.
  - The `Details` LIKE that tells a completed password-only sign-in from a pending one runs only over that user's rows in the window. A test pins the query plan (no scan of the table).
  - A user whose last sign-in was over a year ago is treated as signing in for the first time.
- **Not notified:** the per-address rate limit (`AdminThrottle`). It fires on any attacker's burst, and the lockout already covers guessing at an account. The seam is the refusal in `AdminThrottle.TryEnter`.
- **Test.** The "send test" button sends one message without retries.

### Dashboard

- **Where.** The Upstreams page, upstream drawer, hosts list and overview show dots per destination and per upstream with the reason, a "last change" line, an "Upstream down/degraded" chip beside a host's name, and "N down" on the overview's tile.
- **Polling.** `UpstreamHealthFeed` reads the board every 3 s and renders only when the reading differs. Unlike the security page it does not pause on a hidden tab: that JS hook has one slot and the overview already uses it, and a memory read that renders nothing unless something changed costs a hidden tab nothing.
- **Settings, Notifications** (`/settings/notifications`, `providers:read`):
  - A row per channel with its target, event count, last sent or last error, and send test with its verdict inline.
  - A drawer whose fields follow the kind. **Adding a channel sends it one test message at once**, so the operator learns whether it works without a second click. Saving an edit does not, and a test pins both.
  - Secrets are password fields emptied as the form is sent. Events are grouped checkboxes.
  - The list re-reads every 10 s for the delivery state the dispatcher changes.
- **Cost.** Nothing on the request path changed (the allocation tests are untouched). The watcher and the probe recorder run off timers.

### Left out, with the seams

- The per-IP throttle notice (above).
- A "since the process started" limit on the last-change line (a persisted history would read the audit).
- Per-channel quiet hours or severity filters.
- Pushover, Telegram, Slack (an `INotificationSender` and a kind each).
- Email as HTML.
- Sign-in anomaly rules beyond the two.

## Durable delivery and history (`notify-outbox`)

Before this, an event waited in memory, and a stop or a crash lost whatever was queued. Now it is a row, and the channels' workers send from the rows. Nothing is on the request path.

The first dispatcher (in memory) had the same retry schedule, coalescing and digest wording as now, with these properties: `INotificationEvents.Publish` only queues (bounded 1000, oldest dropped and counted, never blocks). A loop addressed each event to every enabled channel subscribed to it, each channel had a queue (bounded 100) and a loop, so a dead channel never held up another. Settings are read again for every attempt.

### Publish never waits for the database

- `INotificationEvents.Publish` is a `TryWrite` into a bounded in-memory buffer (`EventCapacity`, 1000). The oldest is dropped and counted (`paduraksa_notification_buffer_dropped_total`).
- The **flusher** (inside `NotificationDispatcher`, a hosted service) writes the buffer in one transaction:
  - every `FlushInterval` (250 ms, real time: a throughput knob, not a schedule, so a test that holds the clock still does not hold it up);
  - at once when `FlushBatchSize` (100) events are waiting;
  - on a graceful stop.
- **That buffer is the only window in which a crash loses an event: at most about one flush interval of events.** While the database cannot be written to, add what the bounded buffer holds. A failed write keeps its batch and tries again, and what arrives meanwhile drops oldest-first and is counted.
- A test proves `Publish` returns at once with the database held and that the buffer then drops and counts.

### Tables

Config database, migration `NotificationOutbox`, `Int64Ordered` IDs `nev_`, `ndl_` and `ndg_`.

**`NotificationEvents`:** the kind, the subject (the upstream, destination, certificate or user ID it is about, as text), the title, body, severity, dashboard link path and the webhook's facts (a JSON array of pairs), when it happened and when it was published. It holds what a sender needs to build the message again after a restart, and it is the history's backbone.

**`NotificationDeliveries`:** the event, the channel, the state, attempts, next attempt, last attempt, sent at, the last error (the senders' fixed, secret-free text) and the digest ID.
- The channel has **no foreign key**, so the history outlives a deleted channel.
- The state is `pending`, `sent`, `failed`, `expired` or `dropped`, by name with a check.

**Indexes:**
- **(channel, state, next attempt)** for the worker's claim and for "is a retry waiting".
- **(event, channel), unique**, so an event is addressed to a channel once, and the history's deliveries, its channel and outcome filters and retention's "no pending delivery" are probes.
- **(channel, ID)** for a channel's latest deliveries.
- **(created)** on events for the history's range and retention.

`EXPLAIN QUERY PLAN` tests pin the claim, the retry look, the recovery, the retention reads and deletes, the history page, a channel's recent deliveries and the seven-day figures to those indexes. The one statement that scans, a start dropping the pending deliveries of channels that no longer exist, is named in its test.

### Routing at flush time

- One read of the enabled channels per flush (not one per event; a test counts the statements).
- Then for each event, one `pending` delivery per enabled channel subscribed to its kind, in the same transaction as the event.
- An event nothing subscribes to is stored with no delivery, so the history shows it ("No channel was subscribed").

### Workers

One per channel: a task, created by the first wake or at start for a channel with pending rows. The flusher wakes it in process. A slow poll (`PollInterval`, 30 s) is the safety net, and a retry's own time is the usual wake.

**The first send goes at once** (the agreed design; an earlier build waited the window before every first send). A channel that has not sent within the last `CoalesceWindow` (3 s) claims what is due the moment it is woken. Only a channel that has (a burst) waits out the rest of the window.

It then **claims the due `pending` rows**: `next attempt <= now`, in due order, at most `ClaimBatchSize` (500) a pass, going on at once when a pass was full.

**Coalescing.**
- The first event of a burst goes alone. The second and later ones, which arrive inside the window, go together.
- Events of one digestible kind (health, expiry, ACME) that are several go out as one digest with the same text (title "12 notifications: upstreams down", ten lines and "and 2 more"). **A storm of more than 500 is one digest per claim batch**, not one message.
- The rows of a digest get its **digest ID before anything is sent and keep it through retries**. A resend after a failure or a crash is the same message with the same ID.
- Sign-in events and tests always go singly.
- "One notification per event per channel" holds: each event is delivered once per channel, as itself or inside a digest.
- Messages are short and name the object, since when, the reason and what to do. A dashboard link is added only when **`Paduraksa:Notifications:AdminUrl`** is set (the process cannot know which name or port its admin is reached by).

**Retry** schedule: 15 s, 1, 4 and 16 minutes after a transient failure. A transient failure is a network error, 408, 425, 429, 5xx, or a 4xx mail reply. A permanent one (401, 403, other 4xx, a refused login, a missing secret) is final at once and recorded. The schedule is kept in the row (attempts, next attempt), so it survives a restart. The channel keeps **last sent** and **last error** for the dashboard.

**Ordering.** While a channel has a retry waiting, its newer deliveries wait behind it (order is kept: an "up" never overtakes its "down") and are digested with it when it comes due.

**Concurrency rules.**
- One process, one worker per channel, one send at a time. A row cannot be sent twice in process.
- Every row update says `State = pending`, so a delivery dropped meanwhile stays dropped.
- A channel's settings are read again before each group (an edit, a deletion and a pause take effect).
- A message already in flight when its channel is paused or deleted is sent, and its row stays `dropped`. The pause or the delete marks every pending row, in flight or not, and recording a send touches only rows still pending. So the history can show a dropped delivery that did reach the service, which is the safe side of at-least-once.
- A worker whose channel is gone marks its pending rows `dropped` ("The channel was deleted.") and leaves. A paused one marks them "The channel is paused.". `NotificationService` does the same in the transaction of the delete or the pause, so the history is right at once.

### Restart and outages

- At start, pending rows resume. The pending rows of channels that no longer exist are dropped.
- **Rows that have been due for longer than `Paduraksa:Notifications:MaxDeliveryAge` (1 hour; 1 minute to 7 days, validated) are marked `expired` instead of sent.** A long outage does not end in a burst of stale "upstream down" messages.
- **The same rule is applied when a worker claims.** Events that waited out a database outage in the flusher's buffer (they keep their publish time) are expired when the database returns, not sent.
- "Due for" is the row's due time. A new row is due from the moment its event was published (the event's age). A manual retry is due from the retry, so an old event sent again on purpose is not expired again.
- A row whose event is missing (retention keeps an event with a pending delivery, so it should not happen) is marked `dropped` ("The event is gone.") at claim, instead of being found every pass.
- **Recovery heals itself.** A start whose recovery failed is retried by every upkeep tick. Each tick wakes every channel with a due `pending` row (one seek per channel on the claim index), so a channel is never left without a worker whatever happened at boot.
- The certificate-expiry monitor's own dedup (`ExpiryNotices`) is unchanged.

### Guarantee: at least once

- A crash between a successful send and the row update sends again at the next start. A test simulates it: the process is stopped in the middle of a send.
- Writing a delivered message down is outside the send's error handling. The rows are updated first, then the channel's last-sent state. A failure of either is logged and is never counted as a send failure (a test makes the channel-state write throw a non-database exception).
- A **webhook** body carries `eventId`, and the request carries `X-Paduraksa-Event-Id` (both signed with the rest when the channel has a key). It is the event's ID, or the digest's when several events went out as one message, **the same on every resend**, so a receiver drops the repeat. Other kinds have no such key.
- The "send test" button does not go through the outbox and carries no event ID.
- Documented in [the user guide](../guide/observability.md#notifications).

### Retention

`Paduraksa:Notifications:HistoryRetention` (14 days; 1 hour to 5 years, validated; a server setting read at every prune) prunes events **with** their deliveries once the event is older and **no delivery is pending**. Every other state is final, `failed` included, since it was given up.
- It runs hourly, `PruneBatchSize` (500) events at a time, each batch in its own transaction, with indexed reads and deletes.
- A pending delivery keeps its event whatever its age, until a claim or the next start expires it.
- **The prune is safe against a manual retry.** It chooses the events outside its transaction. Inside it deletes only deliveries that are not pending, and then only events left with no delivery at all. A delivery put back to pending in between, and its event, are never touched (a test races them).
- The retry itself answers 422 if retention removed the row under it.

### History API

`GET /api/v1/notifications/history` (`providers:read`, the permission of the channels) returns events newest first, each with its deliveries: the channel's name and kind (absent for a deleted one), state, attempts, the next try of a pending one, the last attempt, when it was sent, and the last error.
- Filters: `channel`, `kind`, `outcome` (an event with a delivery in that state, to that channel when one is given), and `from` and `to` (when it was published). Paging uses `limit` and `cursor`, as the audit log's.

**Retry** (`providers:write`):
- `POST /api/v1/notifications/deliveries/{id}/retry`.
- `POST /api/v1/notifications/events/{id}/retry` (every failed or expired delivery of it).
- The row goes back to `pending`, due now, with a fresh schedule and no digest ID, and the channel's worker is woken.
- It is refused with 422 for a delivery that is not failed or expired, and for one whose channel is deleted or paused (it would only be dropped again).
- Audited as `notification.delivery.retry` and `notification.event.retry`: who, which delivery and event and channel, or how many.

**The channel list** (`GET /notification-channels`) gains `delivery`. One channel's `GET` does not, because the figures read every channel's deliveries. It holds the deliveries queued now, and the sent and failed (failed or expired) counts of the last seven days, by when each ended. A final row keeps its end time in `NextAttemptAt`, so the figures are a covering range scan of the claim index with no join.

Contracts `<summary>` docs are in OpenAPI.

**MCP.** The history GET is a reviewed read (`EndpointRules.Reviewed`: our own messages and the senders' fixed lines, no secret and no channel setting). Each retry is a write that needs `confirm`. The OpenAPI walk test passes with them listed.

### Metrics

Through the existing `ProcessCounters` path, so `/metrics` and the OpenTelemetry meter both show them (`ProcessCounter` gained a `Gauge` flag):
- `paduraksa_notification_queue_depth`: pending deliveries, a gauge. It is counted in memory as rows are added and ended, and counted again from the table on every poll, so a delivery dropped by a channel change cannot make it drift.
- `paduraksa_notification_sent_total`, `_failed_total`, `_expired_total` and `_dropped_total`, each with a `kind` label (ntfy, gotify, discord, webhook, email). The label is **the kind of channel, never its name**, so it does not grow with the config.
- `paduraksa_notification_buffer_dropped_total`.

### Dashboard

**Settings, Notification history** (`/settings/notifications/history`, `providers:read`) has its own navigation entry after Notifications, and a History button on that page.

**Decision: under Settings, not under Insight.** Insight's pages are read-only logs behind their own permissions (metrics, audit, logs). This one is read with the channels' permission, carries the retry the way the channels page carries "Send test", and is linked from the channel list and the channel drawer. So it sits with them, and its address agrees with its navigation entry.

**The page.**
- A filter card: the shared `SegmentedControl` for the time range (1 h / 24 h / 7 d / All) and for the outcome (any / sent / failed / retrying / expired / dropped), then a channel and an event select, and "Clear filters".
- A list, newest first, 50 at a time with "Load older". Each event row is its time, its title with a kind badge and its subject, and a **chip per channel**:
  - sent (a tick);
  - failed ("Mail failed", the error and the attempts in its tooltip);
  - queued, or "retrying, next 14:03 UTC" (the zone is the browser's);
  - expired;
  - dropped (a deleted channel is named "Deleted Webhook channel").
- **Retry:** a 36 px refresh button beside each failed or expired chip (`providers:write` only), and "Send all again" on an event with several. A refusal is said in a snackbar in the API's words.
- The first page is read again every 8 s (a retrying delivery changes by itself) and replaces the list when nothing older was opened. Otherwise it offers "Show the latest".
- The channel links (`?channel=` and `&outcome=`) set the filters.

**The channel list** adds, on each row, "N queued", "N failed" (a link to those in the history) and "98.5% delivered, 7 d". That is sent out of sent and failed, rounded down, never 100 while something failed.

**The channel drawer** adds "Recent deliveries": its latest eight, with a link to all of them.

Phone width: the row is one column.

**Tests.** bUnit (`NotificationHistoryTests`, 14): the chips and tooltips, the filters and the query, retry and "send all", a refusal, the poll and the older pages, the empty and failed states, the figures and the drawer's list, the rate and the wording. Playwright (`NotificationHistoryEndToEndTests`): a refused delivery shows failed with its error, a click delivers it with the same event ID twice at the receiver, and the channel row and drawer show the outcome.

### Left out

- Counts on the outcome segments (a count per outcome is a query per segment; the filter finds them).
- A delivery's detail drawer (the chip's tooltip carries what a person acts on).
- Per-channel quiet hours and severity filters (as before).
- A cross-process claim. One process, one worker per channel is the rule, and a second process on the same data directory is already refused by the run lock.
- Exporting the history (the API pages it).

