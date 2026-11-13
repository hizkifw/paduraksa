# Benchmarks

Two kinds of measurement live here:

- **Micro-benchmarks** (`Paduraksa.Benchmarks`, BenchmarkDotNet): the hot-path building blocks, in ns and bytes per
  operation. They back the [hot-path.md, Budget and rules](../docs/design/hot-path.md#budget-and-rules) rule that Paduraksa's own per-request code allocates 0 B.
- **End-to-end baseline** (`run-baseline.sh`, spike S7 in [milestones.md, Risks and spikes](../docs/design/milestones.md#risks-and-spikes)): Paduraksa vs nginx, Caddy, HAProxy, Traefik,
  Envoy and a bare Kestrel + YARP reference as reverse proxies in front of the same upstream on the same machine,
  measured with `oha`. For Paduraksa it also tracks the whole process's allocated bytes per request (Kestrel, routing
  and YARP included), GC activity and thread-pool state.

Recorded runs are kept as a history, with generated tables in [`bench/RESULTS.md`](RESULTS.md). The raw output of a
recorded run goes in `bench/results/`, one directory per run.

## Micro-benchmarks

```sh
# Everything, short job (~8 min on the reference box):
dotnet run -c Release --project bench/Paduraksa.Benchmarks -- --filter '*' --job short
# One class, full accuracy:
dotnet run -c Release --project bench/Paduraksa.Benchmarks -- --filter '*GateBenchmarks*'
# List the benchmarks:
dotnet run -c Release --project bench/Paduraksa.Benchmarks -- --list flat
```

Pin to a quiet core with `taskset -c N` if the machine is busy. The program exits non-zero when the filter matches
nothing or any benchmark fails.

| Class | What it measures |
|---|---|
| `IpParserBenchmarks` | `IpParser.TryParse` from chars and UTF-8 (v4, v6, v4-mapped v6) vs `IPAddress.TryParse` |
| `CidrSetBenchmarks` | `CidrSet.Contains` over 20 and 2,000 ranges (half v4 /24, half v6 /48): v4 hit, v6 hit, v4 miss |
| `ClientIpResolverBenchmarks` | `ClientIpResolver.Resolve` in each mode: off, generic (untrusted peer, 3-hop `X-Forwarded-For`), Cloudflare proxy (v4/v6 `CF-Connecting-IP`, missing header), Cloudflare Tunnel |
| `HostTableBenchmarks` | `HostTable.Find` over ~500 hosts: exact, upper case, with port, wildcard, miss |
| `CertificateTableBenchmarks` | `CertificateTable.Find` (per-SNI selection) over ~500 names: exact, upper case + trailing dot, wildcard, Unicode IDN, miss → default |
| `RouteMatchBenchmarks` | `RoutePath.IsCanonical`, and the canonical view + route pick over 9 routes (2nd and 5th in evaluation order, catch-all, a path needing rewriting) |
| `GateBenchmarks` | `GateMiddleware.Decide`: forward (canonical and rewritten path, and behind a trusted Cloudflare Tunnel with `CF-Connecting-IP` and `CF-Visitor`), Respond host, Redirect host, HTTP→HTTPS redirect, unknown host |
| `ProxyProtocolBenchmarks` | `ProxyProtocolParser.TryParse`: v1 TCP4/TCP6, v2 IPv4, v2 IPv6 with 40 B of TLVs, the same over a split `ReadOnlySequence` |
| `MixedInputBenchmarks` | `HostTable.Find` and IP parsing (ours and the BCL's) cycling through 256 mixed inputs per invocation, so branch prediction can't learn one value |

**GC settings, and why.** The project runs **workstation, non-concurrent GC** (`Paduraksa.Benchmarks.csproj`), and
`Program.cs` always runs BenchmarkDotNet **in-process** (it adds `--inProcess` unless `--inProcess` or `-i` is given)
and refuses to start under Server GC:

- In-process means the benchmarks run under this project's own runtimeconfig. BenchmarkDotNet doesn't generate and
  build a separate project, which would also inherit the repo's warnings-as-errors and analyzers.
- BenchmarkDotNet's `Allocated` column is wrong under Server GC with the in-process toolchain (spike S3, [hot-path.md, Budget and rules](../docs/design/hot-path.md#budget-and-rules)).
  Workstation GC gives correct per-operation bytes.
- Non-concurrent GC keeps the background GC thread out of the timings.

The proxy itself runs Server GC. That changes throughput, not what our code allocates.

**Internals.** `Paduraksa.Proxy` exposes its internals to `Paduraksa.Benchmarks` (`InternalsVisibleTo`) for
`GateMiddleware.Decide` and `RoutePath`.

**Reading the numbers.**
- **Bytes per operation** are reproducible, but BenchmarkDotNet averages them over many operations and rounds. So
  `-` (0 B) means "less than about one byte per operation", not "never allocates". The exact guarantee is
  `AllocAssert` in `tests/Paduraksa.Tests.Proxy`: it measures every call separately and fails on any byte.
- **Nanoseconds** from `--job short` on a shared machine are indicative only. Compare within one run, not across runs.
- **Gate benchmarks** run on a `DefaultHttpContext`, whose feature lookups go through a dictionary. On Kestrel those
  lookups are field-backed, so the real gate is a little faster than measured here.

## End-to-end baseline (S7)

```sh
bench/run-baseline.sh                                     # all proxies and scenarios, the full profile
PROFILE=quick bench/run-baseline.sh                       # short runs for iterating on the harness
PROXIES=paduraksa SCENARIOS="h1 h2-tls" bench/run-baseline.sh
PROFILE=quick SCENARIOS="access-log rate-limit rate-limited error-page cf" bench/run-baseline.sh
PROFILE=quick RATES="" SCENARIOS="h1 compressed headers rewrite rewrite-redirect" bench/run-baseline.sh
RATES="" bench/run-baseline.sh                            # no fixed-rate runs
REPEATS=3 RATE_DURATION=30s RATES=30000 bench/run-baseline.sh   # tail latency: 3 x 30 s, medians reported
OHA_DB_DIR=/tmp/oha-db PROXIES=paduraksa SCENARIOS=h1 RATES=30000 bench/run-baseline.sh
python3 -I bench/baseline/stalls.py /tmp/oha-db/*.db      # are slow requests clustered into stalls?
DOTNET_GCDynamicAdaptationMode=0 PROXIES=paduraksa bench/run-baseline.sh   # DOTNET_GC* reach the runner
RECORD=1 bench/run-baseline.sh                            # full profile, then append to the history
```

**Requirements:** Docker with host networking, the .NET SDK from `global.json`, and `curl`, `openssl`, `python3`,
`taskset`, `ss`, `git`, `getconf` and `sha256sum`, plus `socat` when the `access-log` scenario runs against HAProxy (its
syslog sink). The script checks for them before it starts.

**What the script does:**
- pulls the pinned `nginx`, `caddy`, `haproxy`, `traefik`, `envoy` and `oha` images (by digest) if they are missing;
- builds the Paduraksa runner, the bare YARP reference and the WebSocket tool;
- checks the CPU sets and ports before it starts, and prints an estimated wall time;
- runs everything;
- removes its containers (oha's included) and stops the Paduraksa and YARP processes on exit, including on Ctrl+C or
  a failure.

**Output:** it writes to `bench/results/<date>-<time>-baseline/` (or `$OUT`) and refuses to overwrite an existing
directory.
- `raw/<run>.json`: oha's output for every measured run.
- `raw/<run>.meta.json`: the run's time window, the proxy's CPU time, and the proxy↔upstream TCP states at its end.
- `paduraksa.log` (and `paduraksa-cf.log` for the `cf` runner), `yarp.log` and `echo.log`: the .NET processes' logs; the
  Paduraksa logs carry one `METRICS` line per second.
- `env.txt`: the machine and versions, plus:
  - the image digests and CPU sets;
  - the negotiated TLS parameters per proxy;
  - `git status --porcelain` (with `bench/results/` excluded, since it is the harness's own output) and a `dirty`
    flag, plus a SHA-256 of `git diff HEAD`, so a run from a dirty tree is visible.
- `summary.md`: the tables (`baseline/summarize.py`).
- `summary.json`: the same results in machine-readable form, for `bench/history.py`.
- `bench/results/throughput-latest.svg`, `bench/results/paduraksa-trend.svg` and
  `bench/results/fixed-rate-p99-latest.svg`: the report's charts (`bench/history.py report`), written next to the
  history and embedded in `bench/RESULTS.md` with relative links. They are plain string-built SVG with no
  dependencies, deterministic for the same history, and switch to dark colours through
  `@media (prefers-color-scheme: dark)`.

**Knobs:**

| Variable | Default | Meaning |
|---|---|---|
| `PROFILE` | `full` | `full` (the defaults below) or `quick` (3 s runs, 2 s warm-ups, one rate, `REPEATS=1`) |
| `RECORD` | `0` | `1` appends a successful full run to the history and regenerates `bench/RESULTS.md` |
| `PROXIES` / `SCENARIOS` | all | which proxies and scenarios run |
| `DURATION` | `10s` | each fixed-concurrency run |
| `WARMUP` | `5s` | each scenario's discarded warm-up |
| `PROXY_WARMUP` | `10s` | each proxy's warm-up before its first scenario |
| `HANDSHAKE_CONNS` | `16` | requests in flight for `handshake` (the `quick` profile uses 8) |
| `WS_CONNS` / `WS_MESSAGE` | `$CONNS` / `1024` | connections and message size in bytes for `websocket` |
| `RATES` | `10000 30000` | fixed-rate points (req/s) for `h1` and `h2-tls`; empty disables them |
| `RATE_DURATION` | `$DURATION` | each fixed-rate run |
| `REPEATS` | `1` | fixed-rate runs per point; summaries report the median |
| `UPSTREAM_DIRECT` | `1` | `0` skips the upstream-alone run |
| `OHA_DB_DIR` | unset | keep oha's per-request SQLite for fixed-rate runs (for `stalls.py`) |
| `PROXY_CPUS` / `LOAD_CPUS` / `UPSTREAM_CPUS` / `LOAD_THREADS` | see below | CPU pinning |

Explicit variables override either profile. `PROFILE=quick` can never be recorded: `RECORD=1` with `quick` fails
before anything starts.

### Setup

```
oha (Docker, CPUs 4-11) ──► one proxy under test (CPUs 0-3) ──► upstream: nginx (Docker, CPUs 12-19)
WebSocket client (CPUs 4-11)                                 └──► WebSocket echo server (CPUs 12-19), ws.test only
                              Paduraksa  :18081 / :18443
                              nginx      :18082 / :18444
                              Caddy      :18083 / :18445
                              HAProxy    :18084 / :18446
                              Traefik    :18085 / :18447
                              Envoy      :18086 / :18448
                              YARP       :18087 / :18449
```

- **Network:** every container uses `--network host`, so there is no NAT or bridge. All traffic is over loopback.
  nginx, Caddy, HAProxy, Traefik, Envoy, YARP and the upstream listen on 127.0.0.1 only. Paduraksa's public listeners
  bind every address (`ListenAnyIP` in `PublicApp`), which the runner can't change; the load still arrives over
  loopback.
- **Upstream:** the same for every proxy, an nginx container returning a static 12-byte `200` at `/`
  (`baseline/upstream.nginx.conf`). It also serves a fixed 64 KiB body at `/64k` from a file the script generates in
  its work directory and mounts read-only, for the `body-64k` scenario. A few more paths serve the harness itself:
  - `/upload` reads a request body before it answers (a content handler behind a `mirror` with
    `mirror_request_body`; a plain `return` would answer first and only discard the body), for `upload-64k`.
  - `/xff` echoes the `X-Forwarded-For` it received in a response header, so the `cf` check can prove a proxy forwards
    the resolved client address.
  - `/error-502.html` is the 502 page Traefik's `errors` middleware fetches.
  - `/hdr` is the `headers` scenario's page: the same 12-byte answer plus an `X-Powered-By` and a `SAMEORIGIN`
    `X-Frame-Options` that the proxy's response rules must remove and replace.
  - `/api/...` is the only path the `rewrite` scenarios' upstream answers 200 for; `/app/...` and the paths a strip alone
    would leave (`/users/...`) are 404, so a missed rewrite shows in the status breakdown. `/api/redirect/...` answers
    `302` with `Location: /api/login`.
  - **Probe echo.** A request with `X-Bench-Probe: 1` makes the upstream echo what it received in `X-Seen-*` response
    headers (the request headers the `headers` rules touch, with a removed one read as `(absent)`, and the URI). Without
    that header the values are empty, which nginx doesn't send, so measured requests carry none of it.
- **WebSocket echo server** (`Paduraksa.Bench.WebSocket echo`, :18090) is the upstream for `ws.test` on every proxy: a
  Kestrel app that echoes every frame, natively on the upstream CPUs. oha has no WebSocket mode and the nginx upstream
  can't echo one without a third-party module, so `Paduraksa.Bench.WebSocket load` is the load generator (below).
- **Paduraksa** runs **outside Docker**, in Release, through the real `PaduraksaHost` (core, public and admin hosts).
  `Paduraksa.Bench.Runner` boots it with an in-memory config on :18081 (HTTP) and :18443 (HTTPS), with HTTP/3 off.
  - **Process settings:** Server GC, tiered PGO, the shipped `appsettings.json` and production logging, like
    `paduraksa` itself.
  - `bench.test` is a proxy host with one catch-all route to the upstream (gate → routing → YARP).
  - `respond.test` is a Respond host answering `200 ok` from the gate.
  - `ws.test` forwards to the WebSocket echo server.
  - `access-log.test`, `rate-limit.test`, `rate-limited.test`, `browser-check.test`, `error-page.test`,
    `compressed.test`, `headers.test`, `rewrite.test` and `rewrite-redirect.test` are the extra hosts the comparison
    scenarios use. They share the one runner process, so none of the scenarios restarts it
    (`cf` is the one that does, and it starts a second process).
    - `access-log.test` turns the access log on; every other host keeps it off.
    - `rate-limit.test` and `rate-limited.test` name a rate-limit policy each: one far above the load, one at 1
      request per hour with a burst of 1.
    - `browser-check.test` has the proof-of-work gate on at 8 bits. The runner solves one challenge at startup, the
      way the page's JavaScript would, and prints `POW_COOKIE=paduraksa_pow=...` before `READY`; the harness reads
      that line and sends the cookie on every `browser-check` request. A failed solve exits non-zero instead of
      letting the run measure 403s.
    - `error-page.test` routes to `http://127.0.0.1:18079`, where nothing listens.
  - **Runtime counters:** every second the runner prints
    - GC counts per generation;
    - total and largest GC pause;
    - `GC.GetTotalAllocatedBytes(precise: true)`;
    - the thread-pool queue length and thread count.

    `summarize.py` turns these into per-run rates. It drops samples where the allocation counter went backwards,
    which happens under Server GC.
- **nginx** (`baseline/proxy.nginx.conf`, :18082/:18444), **Caddy** (`baseline/Caddyfile`, :18083/:18445), **HAProxy**
  (`baseline/haproxy.cfg`, :18084/:18446), **Traefik** (`baseline/traefik.yml` + `baseline/traefik-dynamic.yml`,
  :18085/:18447) and **Envoy** (`baseline/envoy.yaml`, :18086/:18448) run in their official images, with the same two
  hosts plus `ws.test` and the extra hosts of the comparison scenarios below.
- **YARP** (`Paduraksa.Bench.Yarp`, :18087/:18449) is a bare Kestrel + YARP `WebApplication`, outside Docker like
  Paduraksa, with the same `Yarp.ReverseProxy` version as `Directory.Packages.props`: one catch-all route to the
  upstream, a minimal endpoint answering `respond.test`, and the same extra hosts. The gap between it and Paduraksa is Paduraksa's own cost
  (gate, routing, policies); the gap between it and nginx is Kestrel + YARP.
- **Fairness:**
  - Every proxy keeps upstream connections alive and adds `X-Forwarded-For`/`-Proto`/`-Host`. nginx and HAProxy are
    configured to match YARP's and Caddy's defaults; Traefik adds all three by default; Envoy sets `-For` and `-Proto`
    itself and gets an explicit `X-Forwarded-Host`, which is not in its default set.
  - Upstream pools are large enough not to churn. Caddy's idle connections per host are raised from its default of 32
    to 256 (checked with `ss`: at the default Caddy left ~880 TIME_WAIT sockets after a 10 s run, about 0.1 % of its
    requests, with no throughput change). nginx keeps 256 idle upstream connections, HAProxy uses `http-reuse always`
    with `pool-max-conn 256`, Traefik raises `maxIdleConnsPerHost` to 256, and Envoy pools HTTP/1.1 connections with a
    120 s idle timeout. YARP keeps Kestrel's default pool.
  - nginx's 1,000-requests-per-connection cap is lifted, because Kestrel, Caddy, HAProxy, Traefik and Envoy have none.
  - nginx gets `reuseport` so its 4 workers share the connections. Without it nginx sometimes used ~1 of its 4 CPUs.
  - Access logs are off for every host but the `access-log` scenario's own: nginx and Caddy log per host, HAProxy
    silences its logging for every other host, Envoy filters its file log to that host's authority, YARP's W3C
    logging runs in a branch for that host, Traefik's static log is only on in the second instance started for that
    scenario, and Paduraksa's runner turns the host access log off everywhere else (its built-in default is on).
    `bench.test` itself stays off, so `h1` and the rest keep the numbers they had.
  - Worker counts follow the CPU set: nginx `worker_processes auto`, `GOMAXPROCS=4` for Caddy and Traefik, `nbthread 4`
    for HAProxy, `--concurrency 4` for Envoy, and `Environment.ProcessorCount=4` for .NET (4 Server GC heaps unless
    DATAS shrinks them).
  - Upstream and client timeouts are not tighter than the others': 60 s to connect and 120 s idle (Envoy's 15 s route
    timeout is raised to 120 s).
  - **Traefik has no built-in static response.** Its routers only point at services, redirects or error pages, so
    `respond.test` is not configured for it and `answered` is skipped. The tables show `-` for that cell, and
    `check_proxy` skips the answered probe for a proxy that declares it can't answer, rather than inventing a number.
  - **Envoy's X-Forwarded-For value on loopback.** Envoy deliberately appends its own local address when the peer is
    a loopback address, so the upstream sees the host's LAN address (`192.168.x.x`) instead of `127.0.0.1`, which is
    what the other proxies send. The header is still there, and this is the one place where the forwarded value
    differs; it doesn't affect the request path or the measurement.
- **TLS:**
  - ECDSA P-256 everywhere. Every proxy that isn't Paduraksa gets a throwaway self-signed certificate that the script
    generates; HAProxy gets the certificate and key concatenated into one PEM file.
  - All HTTPS listeners offer HTTP/2 through ALPN and were checked with `curl --http2` before a run.
  - Paduraksa goes through its per-SNI certificate table and TLS policy like production. Both hosts name the
    in-memory placeholder certificate as their own, so `bench.test` takes the exact-name path.
  - `env.txt` records the TLS version, group and cipher an OpenSSL client negotiates with each proxy. oha uses
    rustls and may pick a different TLS 1.3 AEAD.
  - oha uses `--insecure`, and `--connect-to` so the SNI and `Host` are `bench.test` while it connects to 127.0.0.1.
- **CPU pinning (i5-13600K):** CPUs 0-11 are the six P-cores with their SMT siblings, and 12-19 are the eight E-cores.
  - **Proxy under test:** two P-cores (CPUs 0-3), via `taskset` for Paduraksa and YARP and `--cpuset-cpus` for the
    containers. The script checks that each CPU set exists on this machine before it starts and names the variable to
    override on other hardware.
  - **oha:** the other four P-cores (CPUs 4-11), with 8 worker threads. On two P-cores it tops out near 580k req/s,
    below what nginx answers by itself.
  - **Upstream:** the eight E-cores. It needs throughput, not latency fidelity, and it is the same for every proxy.
    On one P-core (CPUs 4-5) it served 389k req/s and capped nginx's proxied HTTP/1.1. On the E-cores it serves
    >800k req/s, more than twice what any proxy asks of it.
  - **Cost:** the E-cores are where the rest of the shared machine runs, so the upstream sees that noise.
- **CPU per request:** the proxy's user + system CPU time over each measured run, from `/proc/<pid>/stat` for
  Paduraksa and YARP and the container's cgroup `cpu.stat` for the others, divided by requests. The tables also show
  the average number of busy CPUs out of 4; a proxy near 4 is CPU-bound.

### Scenarios

**Warm-up:**
- Every scenario runs **10 s** after a discarded **5 s** warm-up. The `quick` profile uses 3 s runs and 2 s warm-ups.
- Before its first scenario, each proxy also gets 10 s of HTTP/1.1 load and 10 s of HTTP/2 load. .NET's tiered JIT
  and PGO need several seconds under load to reach steady state: a 5 s run straight after start measured about half
  of Paduraksa's steady-state throughput.
- Every proxy gets the same warm-up.

**Fixed concurrency:** throughput and latency are measured first at **64 requests in flight**.
- HTTP/1.1: 64 connections.
- HTTP/2: 16 connections x 4 streams.
- `handshake`: 16 connections (8 in the `quick` profile), each request on a new connection.

At fixed concurrency, mean latency is concurrency ÷ throughput (Little's law), so the latency medians only restate
throughput. Compare latency at the fixed rates below.

| Scenario | Request |
|---|---|
| `h1` | plain HTTP/1.1 `GET /` to `bench.test`, proxied |
| `h1-tls` | HTTPS, HTTP/1.1, proxied |
| `h2-tls` | HTTPS, HTTP/2, proxied |
| `browser` | HTTPS, HTTP/2, proxied, with the 13 Chrome-like headers from spike S3 (`sec-ch-ua*`, `Sec-Fetch-*`, `Referer`, `Cookie`, `priority`, ...) and random paths with query strings (`/static/abcdef.js?v=123`, ...) |
| `body-64k` | plain HTTP/1.1 `GET /64k` to `bench.test`, proxied; the upstream answers a fixed 64 KiB body, so this measures body streaming instead of per-request overhead |
| `handshake` | HTTPS, HTTP/1.1, proxied, with oha's `--disable-keepalive`: every request is a new TCP connection plus a TLS handshake. This is the cost a real browser pays on its first visit, and it is why it gets its own lower concurrency |
| `answered` | plain HTTP/1.1 `GET /` to `respond.test`, answered by the proxy itself (Paduraksa: a Respond host in the gate; Traefik: skipped, no built-in static response) |
| `upload-64k` | plain HTTP/1.1 `POST /upload` with a 64 KiB body to `bench.test`, proxied; the upstream reads the whole body and answers `upstream ok`. The mirror of `body-64k`: it measures request-body streaming. oha sends the body from a file (`-D`) with a `Content-Length`. |
| `websocket` | WebSocket echo through the proxy (`ws.test`, plain `ws://`): `$WS_CONNS` connections (64) each with one binary message of `$WS_MESSAGE` bytes (1,024) in flight, sent and echoed back, repeated for the run. The unit is the echo round trip (one message each way, so twice as many messages cross the proxy); the CPU column is per round trip. The upstream is the echo server, and an `upstream.websocket` run measures the client and echo server alone, as `upstream.h1` does for HTTP. The 64 connections are all opened (and upgraded) before the clock starts. |
| `access-log` | plain HTTP/1.1 like `h1`, to `access-log.test`, whose proxy writes an access log to a file (the production default for Paduraksa; every other host keeps it off). Per proxy below. |
| `rate-limit` | plain HTTP/1.1 like `h1`, to `rate-limit.test` under a per-client-address limit of 1,000,000 req/s with a burst of about a million. Every request comes from 127.0.0.1, so all of them take a token from one hot bucket: the worst case for Paduraksa's table, and the same single entry for the others. The budget is far above the load, so nothing is refused. |
| `rate-limited` | plain HTTP/1.1 like `h1`, to `rate-limited.test` under one request per hour (per minute on nginx) with a burst of one. The pre-run probe spends the one token, so the measured run is all 429s. This is the refusal path: how much abuse the proxy absorbs. |
| `browser-check` | Paduraksa only: plain HTTP/1.1 like `h1`, to `browser-check.test`, which has the proof-of-work gate on at 8 bits. Every request carries a valid `paduraksa_pow` cookie and `Accept: text/html`, so the token is verified (HMAC over the host and client IP) and the request forwarded. The runner solves one challenge at startup; see the runner's hosts above. No other proxy here has a proof-of-work gate, so nothing is run for them. |
| `error-page` | plain HTTP/1.1 with `Accept: text/html`, to `error-page.test`, whose upstream is a closed loopback port (18079). Every forward fails and the proxy writes a 502 HTML page: Paduraksa's built-in page, and for the others a ~900-byte static page (`baseline/error-502.html`: Paduraksa's page without its request ID, logo and animation). This is the upstream-failure path. |
| `compressed` | plain HTTP/1.1 `GET /text` with `Accept-Encoding: gzip` to `compressed.test`, whose proxy compresses the upstream's 16 KiB HTML page (generated per run from a fixed-seed word list, so it compresses like text; nginx's own gzip stays off upstream). Paduraksa: host compression on (Brotli preferred, gzip here as asked); nginx `gzip on` at level 1 from 1 KiB with `gzip_proxied any`; Caddy `encode gzip`; HAProxy `compression algo gzip` on a backend of its own; Traefik the `compress` middleware from 1 KiB. Envoy `compressor` filter with gzip at `BEST_SPEED` from 1 KiB, on for this route only; YARP ASP.NET Core's `ResponseCompression` (gzip at its fastest level) in a branch for the host. The pre-run check asserts `Content-Encoding: gzip` |
| `headers` | plain HTTP/1.1 like `h1`, to `headers.test`, path `/hdr`, through a header rule set on each proxy. Request side: set `X-Bench-Replace` (the load sends `client`, the rule makes it `replaced`), set `X-Bench-Tenant`, add `X-Bench-Added`, remove `X-Bench-Drop` (the load sends it). Response side: set `X-Frame-Options: DENY` over the upstream's `SAMEORIGIN`, set `X-Bench-Policy: max-age=31536000`, remove the upstream's `X-Powered-By`. The pre-run probe checks the upstream received each request change (its echo) and the client saw each response change, with exactly one `X-Frame-Options` |
| `rewrite` | plain HTTP/1.1 like `h1`, to `rewrite.test`, route `/app` stripping its prefix and adding `/api`, on varied paths with a query (`/app/users/abcdef/123?x=4`; oha's regex URLs, as in `browser`). The upstream answers 200 only under `/api/`; the pre-run probe reads back the URI it received, `/api/users/abcdef/123?x=1` for `/app/users/abcdef/123?x=1` |
| `rewrite-redirect` | the same route on `rewrite-redirect.test`, load on `/app/redirect/<random>?x=<digit>`, where the upstream answers `302` with `Location: /api/login` and the proxy must give the client `Location: /app/login`. oha doesn't follow redirects (`--redirect 0`), so the run is one 302 per request, and the throughput counts every response. Paduraksa (built in), nginx, Caddy and HAProxy only; the pre-run probe checks the status, the URI the upstream saw and the mapped `Location` |
| `cf` | plain HTTP/1.1 like `h1`, from a peer the proxy trusts as Cloudflare Tunnel (loopback), carrying `CF-Connecting-IP` (IPv6), `CF-Visitor` and `CF-Ray`. The proxy resolves the client address from `CF-Connecting-IP` and forwards it as `X-Forwarded-For` (checked before the run through the upstream's `/xff`). Paduraksa also resolves the scheme from `CF-Visitor` and forwards `X-Forwarded-Proto`; the others only resolve the address. Paduraksa runs it on `bench.test` in a second runner process (`--cloudflare-tunnel true`); the others on `cf.test`. Each gets its own proxy warm-up. |

**The comparison scenarios.** `access-log`, `rate-limit`, `rate-limited`, `error-page`, `compressed`, `headers`, `rewrite`, `rewrite-redirect` and `cf` run against every proxy
that has a stock equivalent, each configured with the closest feature its pinned image ships. No third-party module or
plugin build is used; the table says what was ruled out. A skip is shown as `-` in the tables and has a reason here.

| | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---|---|---|---|---|---|
| `access-log` | `access_log` in the host's `server`, `combined` format, `buffer=64k flush=1s` | `log { output file }` on the site; JSON, one entry per request | `log` to a UDP syslog sink (below) with a combined-style `log-format`; silenced per request for other hosts | `accessLog` to a file, common log format, `bufferingSize: 100`; static config, so a second instance | file access logger with a combined-style format; a header filter on the authority limits it to the host | ASP.NET Core W3C logger to a file (fields: date, time, client, method, path, query, status, host, user agent, referer), flushed every millisecond (its default second caps it near 1k lines/s); branch for the host |
| `rate-limit`, `rate-limited` | `limit_req` zone on `$binary_remote_addr`; leaky bucket; 1,000,000r/s with `burst=1000000 nodelay`; `rate=1r/m` with no burst | skipped | stick table, `http_req_rate` per source address, `deny_status 429`; window of 1 s and 1 h | `rateLimit` middleware; token bucket per source address; 1,000,000 per 1 s burst 1,048,575; 1 per 1 h burst 1 | `local_ratelimit` token bucket on the route; 1,048,575 tokens, 1,000,000 per 1 s; 1 token, 1 per 3,600 s | ASP.NET Core token bucket limiter per client address, rejecting with 429; same sizes as Traefik |
| `error-page` | `error_page 502` with a static file; the log level for the host is `crit` | `handle_errors` with `file_server { status {err.status_code} }` | `errorfile 503` holding a 502 response, `retries 0` | `errors` middleware with the page from a service (the shared upstream) | `local_reply_config` mapper on response flag `UF` (502 and the page) | a middleware writing the page when the forward ended in 502 |
| `cf` | `set_real_ip_from 127.0.0.1` and `real_ip_header CF-Connecting-IP` on the host; `$proxy_add_x_forwarded_for` | `trusted_proxies` and `client_ip_headers` (server-wide, so a second instance) and `header_up X-Forwarded-For {client_ip}` | `set-src hdr(CF-Connecting-IP)` for the host when the source is loopback; `option forwardfor` | skipped | original-IP detection extension on `CF-Connecting-IP` (replaces `use_remote_address`, so a second instance) and `X-Forwarded-For` from the detected address | `UseForwardedHeaders` with `ForwardedForHeaderName = CF-Connecting-IP` (a second process, like Paduraksa) |
| `compressed` | `gzip on` at level 1 from 1 KiB, `gzip_proxied any` | `encode gzip` | `compression algo gzip` on a backend of its own | `compress` middleware from 1 KiB | `compressor` filter, gzip `BEST_SPEED`, 1 KiB minimum; switched off by its runtime flag in the shared chain and on for the route by a `CompressorPerRoute` override | ASP.NET Core `ResponseCompression`, gzip `Fastest`, no minimum size, in a branch for the host |
| `headers` | `proxy_set_header` (an empty value drops a request header); `proxy_hide_header` plus `add_header ... always` for the response | `header_up` (`Name value` sets, `+Name` appends, `-Name` removes) and `header_down` on the proxy handler | `http-request` and `http-response` `set-header`, `add-header`, `del-header`, in a backend of their own | `headers` middleware, `customRequestHeaders` and `customResponseHeaders` (an empty value removes) | the route's `request_headers_to_add`/`_to_remove` and `response_headers_to_add`/`_to_remove`, with `OVERWRITE_IF_EXISTS_OR_ADD` and `APPEND_IF_EXISTS_OR_ADD` | transforms `RequestHeader` (`Set`, `Append`), `RequestHeaderRemove`, `ResponseHeader` (`Set`, `When: Always`), `ResponseHeaderRemove` |
| `rewrite` | `rewrite ^/app(/.*)$ /api$1 break;` in `location /app/` | `handle_path /app/*` and `rewrite * /api{uri}` | `http-request replace-path ^/app(/.*)$ /api\1` | `stripPrefix` then `addPrefix` middlewares on a `Host() && PathPrefix(/app/)` router | route `prefix: /app/` with `prefix_rewrite: /api/` | transforms `PathRemovePrefix: /app` and `PathPrefix: /api` on a route `/app/{**rest}` |
| `rewrite-redirect` | `proxy_redirect /api/ /app/;` with `absolute_redirect off` | `header_down Location ^/api/ /app/` | `http-response replace-header Location ^/api/(.*)$ /app/\1` | skipped | skipped | skipped |

Skipped, and why:
- **Caddy `rate-limit` and `rate-limited`:** rate limiting is the third-party `caddy-ratelimit` module, which the pinned
  `caddy:2.11.7-alpine` image doesn't contain. An `xcaddy` build would be a different binary from the other scenarios',
  so the two rows are `-` for Caddy.
- **Traefik `cf`:** `forwardedHeaders.trustedIPs` makes Traefik trust an `X-Forwarded-For` header from a listed peer, not
  `CF-Connecting-IP`; reading that header takes a plugin (for example Traefik's own `real-ip` plugin), which isn't in the
  image. Feeding it the CF address in `X-Forwarded-For` would be a different request from the other proxies' and from
  Paduraksa's.
- **`browser-check`:** Paduraksa's own proof-of-work gate; none of the others has an equivalent.
- **Traefik, Envoy and YARP `rewrite-redirect`:** none of them maps a `Location` header with stock configuration.
  Traefik's `headers` middleware sets whole header values and has no search-and-replace (a regex rewrite of response
  headers is the third-party `rewriteHeaders` plugin); Envoy's route `prefix_rewrite` acts on the request only, and mapping a
  response `Location` takes a Lua or Wasm filter or an internal redirect (which re-dispatches the request, so it isn't
  the same behaviour); YARP's transforms can set, append or remove a response header but not edit its value by pattern.
  Their `rewrite` still runs.

**The extra-host scenarios.** `access-log`, `rate-limit`, `rate-limited`, `browser-check`, `error-page`, `compressed`, `headers`, `rewrite`,
`rewrite-redirect` and `cf` all run
at `c=$CONNS` with plain HTTP/1.1, like `h1`. Each proxy serves them from extra hosts of the one instance that serves
everything else, so nothing restarts between them, with these exceptions, where the setting can't be limited to a host
and the proxy is started a second time with it, for that scenario only (a proxy warm-up first, as for `cf` on
Paduraksa): Traefik for `access-log` (the access log is static configuration), and Caddy, Envoy and YARP for `cf`
(trusted proxies, original-IP detection and the forwarded-headers middleware are server-wide). The other scenarios'
configuration, and so their history, is the one they always had, apart from the extra hosts themselves.
- **Status codes:** `summarize.py` prints a status breakdown for these paths and for `upload-64k` and `websocket`, for
  every proxy that ran them. `rate-limit` must show no 429s anywhere; if it does, that policy is below the load and the
  run no longer measures the check alone. `rate-limited` must show 429s, `error-page` 502s and `rewrite-redirect` 302s, which is
  what their throughput columns count: for those three rows the number is every response, not only 2xx, because counting only
  successes would report zero for the path being measured. `headers`, `rewrite` and `compressed` must be all 200. `upload-64k` is 200s, `websocket` is one 101 per
  connection. The latency tables' `failed` column includes the expected non-2xx answers.
- **Per-scenario checks:** before the measured runs, `check_scenario_hosts` makes each proxy prove the scenario's own
  point: the host answers, the second `rate-limited` request is a 429, `error-page` is a 502 with a page, `compressed` is gzip, `headers` shows each rule's effect at the upstream and the client, `rewrite` the URI the upstream received, `rewrite-redirect` the mapped `Location`, and for
  `cf` the upstream's `/xff` shows the `CF-Connecting-IP` address in `X-Forwarded-For`.
- **`error-page` and passive health:** the host's upstream has no `PassiveHealth` configured, so YARP retries the
  closed destination on every request and the steady state is a 502 with the built-in HTML page (the smoke run's
  status breakdown was 502 only). With passive health on, YARP's transport-failure-rate policy would take the
  destination out after the first failures and answer 503 "no healthy destination" instead, which the breakdown would
  say. A refused loopback connect is immediate, but each failure still throws and handles an exception and writes the
  page: the quick smoke runs measured 12-20k req/s and about 315 µs of proxy CPU per request, CPU-bound on the four
  proxy cores. That is the price of the 502 path, not of a connect timeout. The runner sets `Yarp.ReverseProxy.Forwarder` to
  `None`: at Warning, YARP logs a stack trace for every failed forward, which would otherwise put a several-hundred-MB
  log file and the formatter's work into the same run.
- **`browser-check` scope:** the token is bound to this host and the client IP /32 (`127.0.0.1`), and lives for an
  hour, so it outlasts the run. Solve failure stops the runner before `READY`.
- **`access-log` disk:** at `h1` rates the writer sees several hundred MB in one run. Paduraksa rotates and trims by
  production's limits (64 MiB a file, 1 GiB total) inside the throwaway data directory, which the runner deletes on
  exit. The containers write inside their own (throwaway) filesystem: nginx, Caddy, Traefik and Envoy to `/tmp`, HAProxy
  through the UDP sink to the harness's temporary directory, YARP's W3C logs (10 MB files, four kept) to a directory
  there too. None of it is committed or kept.
- **HAProxy's access-log sink:** HAProxy only logs to syslog, stdout or stderr, so a `socat` on the upstream CPUs
  receives the UDP datagrams and appends them to a file. A datagram the sink can't keep up with is dropped, which costs
  HAProxy nothing, so the file may be shorter than the request count; HAProxy still formats and sends every line.

**Fixed rates:** `h1` and `h2-tls` are then run at fixed request rates (`RATES`, default 10,000 and 30,000 req/s).
- Each rate is warmed up for 5 s at that rate first.
- oha's `--latency-correction` corrects for coordinated omission.
- These runs show latency below saturation, which is where real deployments run.

### Recording a run

1. Start from a clean tree, so the run's `commit` means something (`env.txt` records the dirty state).
2. `RECORD=1 bench/run-baseline.sh` (the full profile; `quick` refuses to record).
3. Commit the run directory (`summary.md`, `summary.json` and `env.txt`; `raw/` and `*.log` are ignored), plus
   `bench/results/history.jsonl`, the regenerated `bench/RESULTS.md` and the SVG charts under `bench/results/`.

`RECORD=1` calls `bench/history.py append` and then `bench/history.py report`. `append` refuses a dirty run unless
given `--allow-dirty`, and a run from a different machine than the previous entry unless given `--new-machine`.
`report` can be rerun at any time; it writes the latest tables, the SVG charts, Paduraksa's trend per commit, and a
delta against the previous entry, marking a Paduraksa scenario that lost more than 10 % throughput or more than 25 %
at the fixed-rate p99 as a possible regression.

### Caveats

- **Shared machine.** Other agents and services run on the same box. CPU pinning limits the interference but doesn't
  remove it: the cores share L3, memory bandwidth and the package power budget, and the upstream shares the E-cores
  with everything else. Expect run-to-run differences of ±5-10 % in throughput and much more at p99 and beyond.
  Tail numbers from a single 10 s run are anecdotes; use `REPEATS=3 RATE_DURATION=30s` and the median.
- **Loopback, one box.** There's no real network, so latencies are microseconds plus scheduling. The load generator
  shares the machine. The `upstream direct` line shows the ceiling of the load generator plus the upstream.
- **Tiny, static responses.** A 12-byte body with no upstream work measures per-request proxy overhead only.
  `body-64k` adds one body-streaming point: 64 KiB per request, so it reports much lower request rates and much more
  CPU per request everywhere. Throughput with real pages and real upstreams is lower than `h1`, and the gaps shrink.
- **`handshake` measures a connection, not a request.** With `--disable-keepalive`, oha opens a TCP connection and
  completes a TLS handshake for every request; at 16 in flight the run is usually CPU-bound in the proxies and the
  load generator. It is there to compare handshake cost between proxies, not to predict browser throughput.
  `env.txt` records the negotiated TLS parameters, and all proxies use the same P-256 certificate.
- **oha's browser mode is a lower bound for header variety.** oha sends the same header values on every request; only
  the path varies. S3's `browser` shape also varied `Accept`, `Sec-Fetch-*` and `Referer`. Kestrel allocates a string
  for each value that differs from the previous request on the connection, so real browser traffic costs Paduraksa a
  little more than this scenario.
- **Different defaults.**
  - Caddy adds a `Via` header.
  - Request bodies (`upload-64k`): nginx buffers a request body before connecting upstream; the others stream it.
  - Traefik also adds `X-Forwarded-Port` and `X-Real-Ip`, and Envoy generates an `x-request-id` (a UUID) per request,
    all by default.
  - nginx buffers responses; Envoy, HAProxy and Traefik stream them. For `body-64k` that includes nginx's default
    proxy buffers (8 x 4 KiB), so a body the client side can't keep up with spills to a temp file.
  - Paduraksa forwards the raw path and runs its gate (host lookup, route match, policies) on every request.
  - Paduraksa's `answered` path writes `Cache-Control: no-store` (precomputed); nginx, Caddy, HAProxy and Envoy don't.
  - HAProxy and Envoy answer `respond.test` from a built-in static response; Traefik has none and skips `answered`.
- **Docker vs native.** With host networking the container overhead for nginx, Caddy, HAProxy, Traefik and Envoy is
  small, but it isn't zero. Paduraksa and the bare YARP reference run natively.
- **HTTP/3 isn't measured yet** (see Future work).
- **CPU per request at fixed rates** includes the proxy's idle time around each run (oha's container start and stop)
  and .NET's thread-pool spin-waiting at low load. Compare it between proxies at the same rate, not with the
  fixed-concurrency figures.
- **The comparison scenarios compare features that aren't identical.** Read each row with the matrix above; what
  differs:
  - `browser-check` has one bar (Paduraksa only). `rate-limit` and `rate-limited` have none for Caddy, and `cf` none
    for Traefik, so those panels have fewer bars than the others. `rewrite-redirect` has bars for Paduraksa,
    nginx, Caddy and HAProxy only.
  - **Access log:** the line formats differ in content and size: Paduraksa writes a 13-field plain line (with request
    ID, upstream and timings), nginx the `combined` format, Caddy JSON (the largest), HAProxy a combined-style line
    over syslog, Traefik common log format plus router, service and duration, Envoy and YARP a combined-style or W3C
    subset. Buffering differs too: Paduraksa's ring and background writer, nginx's explicit `buffer=64k` (an unbuffered
    nginx writes every line with its own syscall and costs more), Traefik's 100-line queue, Envoy's file logger, YARP's
    channel and 1 ms flush interval, Caddy's own writer, and for HAProxy a `sendto` per line to the sink instead of a file write. nginx, Caddy and the .NET apps log on their own host only; HAProxy and Envoy
    evaluate a small per-request condition for every host to scope their log; Traefik is a second instance.
  - **Rate limit:** the algorithms differ: nginx's `limit_req` is a leaky bucket with millisecond accounting, HAProxy's
    stick table counts a request rate over a sliding window, and Traefik, Envoy, YARP and Paduraksa are token buckets
    (Paduraksa's state is a packed word updated by a CAS loop; the others have their own implementations and locking).
    Keying differs: nginx,
    HAProxy, Traefik, YARP and Paduraksa key on the client address; Envoy's `local_ratelimit` is on the route and shared
    by all clients (per-client buckets need descriptors and a `rate_limits` action), which is the same single bucket
    for this one-client run. `rate-limit` is one hot entry, not a distribution: every client is `127.0.0.1`, so it is the
    worst case for contention on one counter, not the average over many keys. nginx has no per-hour unit, so `rate-limited` is one request per minute there;
    the run lasts well under a minute, so it is equivalent. YARP's partition key is the client address as a string, which
    allocates per request, as a stock `AddPolicy` does.
  - **`rate-limited`'s one allowed request** is the pre-run probe's (the warm-up would spend it too); the measured run
    should be all 429s. If the breakdown shows more than a handful of 2xx answers there, the policy isn't being applied
    (or the bucket was swept) and the run is wrong.
  - **Error page:** the page comes from a different place on each: Paduraksa's precomputed built-in page, a static file
    read by nginx (`root`) and Caddy (`file_server`), HAProxy's and Envoy's page held in
    memory from the config, YARP's byte array, and for Traefik a proxied request to the shared upstream for every error
    (the `errors` middleware has no other source), so Traefik pays an extra hop. HAProxy and Envoy would answer 503 for
    a refused connect, so both are told to say 502, like the others. Passive health is off everywhere (nginx never marks
    a single-server upstream down) and every proxy's per-failure logging is switched off or filtered (nginx's
    error_log for that host at `crit`, Caddy's default logger at `PANIC`, YARP's forwarder logger off).
  - **Cloudflare IP:** nginx, HAProxy and the .NET proxies take the address when the peer is trusted (loopback); Envoy's
    custom-header extension takes it from any peer, because Envoy offers no trusted-peer list for it (it also has no
    `use_remote_address` then, so the `cf.test` route writes `X-Forwarded-For` itself). Only Paduraksa resolves the
    scheme from `CF-Visitor`.
  - **Header rules** (`headers`): what differs.
    - **Add.** Paduraksa, Caddy, HAProxy, Envoy and YARP have a real append for a request header; nginx and Traefik only
      have a set. The load doesn't send `X-Bench-Added`, so the upstream sees one value either way, and the probe checks it.
    - **Response set.** Caddy, HAProxy, Traefik, Envoy, YARP and Paduraksa replace the header. nginx's `add_header` only
      adds, so its rule is `proxy_hide_header X-Frame-Options` and then `add_header X-Frame-Options DENY always`: two steps, and
      the probe requires exactly one `X-Frame-Options` line at the client. `always` is needed or nginx drops the header on
      non-2xx answers.
    - **Response remove** is a true remove on every proxy (nginx's `proxy_hide_header`, HAProxy's `del-header`, Traefik's empty
      value, Envoy's `response_headers_to_remove`, Caddy's `-Name`, YARP's `ResponseHeaderRemove`, Paduraksa's `remove`); the probe
      requires it absent at the client.
    - **Request remove** is a true remove everywhere except nginx, where an empty `proxy_set_header` is the stock way to keep
      a header from being passed upstream (the upstream sees it absent, which is what the probe reads).
    - **Cost.** Paduraksa writes precomputed values into the request and response header stores; nginx and HAProxy
      evaluate their directives per request on already-parsed headers; YARP's transforms run per request as delegates over
      its header copy. The `/hdr` page carries two more upstream headers than `h1`'s page, so comparing with the proxy's
      own `h1` includes copying them (and an `X-Powered-By` that the proxy then drops).
  - **Path rewrite** (`rewrite`, `rewrite-redirect`): what differs.
    - **Matching.** nginx, Caddy, Envoy and Traefik match the literal prefix `/app/`; HAProxy's `replace-path` regex and
      YARP's route (`/app/{**rest}`) do the same. Paduraksa's route is `/app` (a prefix that also matches `/app` itself),
      matched on the canonical view; it forwards the raw path, answers 404 for a dot segment or an unstrippable spelling (a
      per-request check the others don't make), and sends `X-Forwarded-Prefix: /app` upstream, a header none of the others adds.
    - **Query.** Kept by every proxy (the probe sends `?x=1`).
    - **Location mapping** (`rewrite-redirect`):
      - Paduraksa maps a path-absolute `Location` (or an absolute URL on the request's own host) that points under what the
        route sends: it takes the strip and add off and puts the client's prefix back, and leaves a `Location` that already
        starts with the client's prefix alone. `rewrite.test` has the mapping on too (its default) but its upstream sends no `Location`.
      - nginx's `proxy_redirect /api/ /app/` is a string-prefix replacement of the header text: it maps what starts with
        `/api/` and nothing else, and for a path-only replacement it needs `absolute_redirect off` or it writes an absolute URL.
      - HAProxy's `replace-header` and Caddy's `header_down Location` are regular-expression replacements on the header
        value, anchored by us at `^/api/`; they are configured on `rewrite-redirect.test` only, so `rewrite.test` pays nothing for them.
      - The scenario's upstream answers a path-absolute `Location`, which is what all four handle; an absolute one on the
        proxy's own host is something only Paduraksa maps.
    - **Redirect answers are 302s.** The throughput of `rewrite-redirect` counts them (oha doesn't follow); a 200 in its
      status breakdown is a miss.
- **WebSocket numbers are for one small shape.** 64 connections each with one 1 KiB message in flight is a latency-bound
  ping-pong; the proxies' CPU per frame (a read and a write each way) dominates. The client and the echo server are
  .NET, on the load and upstream CPUs; `upstream.websocket` shows what they do without a proxy in between. Paduraksa
  and the bare YARP forward the stream with YARP; the others use their own. Message size
  and connection count are knobs (`WS_MESSAGE`, `WS_CONNS`); the other directions of load (one large message, many idle
  connections) aren't measured.
- **Upload buffering differs.** nginx reads the whole request body before it connects upstream (its default
  `proxy_request_buffering on`); the others stream it. nginx's body buffer is raised to 128 KiB so a 64 KiB body stays
  in memory (at its default of 8 or 16 KiB it spilled to a temp file and warned per request: the first quick smoke gave
  1.5k req/s against 80k with the larger buffer).
- **`rate-limited`, `error-page` and `rewrite-redirect` report every response** in `summarize.py` and `bench/history.py`, because their
  useful answer is a 429, a 502 or a 302. Do not compare those three rows with the successful-requests rows as if they were
  the same metric; read the status breakdown beside them.

### Future work

- **HTTP/3:** add an `h3` scenario (`oha --http-version 3`, with `Paduraksa:Listen:Http3` on and libmsquic present) for
  Paduraksa and Caddy, and nginx if its image has QUIC enabled.
- **Cloudflare proxy mode:** the `cf` scenario uses Tunnel mode, because the edge ranges proxy mode trusts can only be
  injected through `SnapshotCompilerOptions`, which the server builds internally. Both modes take the same resolver
  path.
- **Placeholders:** the runner names `PlaceholderCertificates.Id` as each host's certificate (its config goes in through
  the config service, as one command after the start). Switch to the vault's certificates when M3 replaces it.
