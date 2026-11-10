# Benchmarks

Two kinds of measurement live here:

- **Micro-benchmarks** (`Paduraksa.Benchmarks`, BenchmarkDotNet): the hot-path building blocks, in ns and bytes per
  operation. They back the PLAN §7.1 rule that Paduraksa's own per-request code allocates 0 B.
- **End-to-end baseline** (`run-baseline.sh`, spike S7 in PLAN §13): Paduraksa vs nginx, Caddy, HAProxy, Traefik,
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
| `RouteMatchBenchmarks` | `RoutePath.IsCanonical`, and the canonical view + route pick over 9 routes (first route, 8th route, catch-all, a path needing rewriting) |
| `GateBenchmarks` | `GateMiddleware.Decide`: forward (canonical and rewritten path, and behind a trusted Cloudflare Tunnel with `CF-Connecting-IP` and `CF-Visitor`), Respond host, Redirect host, HTTP→HTTPS redirect, unknown host |
| `ProxyProtocolBenchmarks` | `ProxyProtocolParser.TryParse`: v1 TCP4/TCP6, v2 IPv4, v2 IPv6 with 40 B of TLVs, the same over a split `ReadOnlySequence` |
| `MixedInputBenchmarks` | `HostTable.Find` and IP parsing (ours and the BCL's) cycling through 256 mixed inputs per invocation, so branch prediction can't learn one value |

**GC settings, and why.** The project runs **workstation, non-concurrent GC** (`Paduraksa.Benchmarks.csproj`), and
`Program.cs` always runs BenchmarkDotNet **in-process** (it adds `--inProcess` unless `--inProcess` or `-i` is given)
and refuses to start under Server GC:

- In-process means the benchmarks run under this project's own runtimeconfig. BenchmarkDotNet doesn't generate and
  build a separate project, which would also inherit the repo's warnings-as-errors and analyzers.
- BenchmarkDotNet's `Allocated` column is wrong under Server GC with the in-process toolchain (spike S3, PLAN §7.1).
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
RATES="" bench/run-baseline.sh                            # no fixed-rate runs
REPEATS=3 RATE_DURATION=30s RATES=30000 bench/run-baseline.sh   # tail latency: 3 x 30 s, medians reported
OHA_DB_DIR=/tmp/oha-db PROXIES=paduraksa SCENARIOS=h1 RATES=30000 bench/run-baseline.sh
python3 -I bench/baseline/stalls.py /tmp/oha-db/*.db      # are slow requests clustered into stalls?
DOTNET_GCDynamicAdaptationMode=0 PROXIES=paduraksa bench/run-baseline.sh   # DOTNET_GC* reach the runner
RECORD=1 bench/run-baseline.sh                            # full profile, then append to the history
```

**Requirements:** Docker with host networking, the .NET SDK from `global.json`, and `curl`, `openssl`, `python3`,
`taskset`, `ss`, `git`, `getconf` and `sha256sum`. The script checks for them before it starts.

**What the script does:**
- pulls the pinned `nginx`, `caddy`, `haproxy`, `traefik`, `envoy` and `oha` images (by digest) if they are missing;
- builds the Paduraksa runner and the bare YARP reference;
- checks the CPU sets and ports before it starts, and prints an estimated wall time;
- runs everything;
- removes its containers (oha's included) and stops the Paduraksa and YARP processes on exit, including on Ctrl+C or
  a failure.

**Output:** it writes to `bench/results/<date>-<time>-baseline/` (or `$OUT`) and refuses to overwrite an existing
directory.
- `raw/<run>.json`: oha's output for every measured run.
- `raw/<run>.meta.json`: the run's time window, the proxy's CPU time, and the proxy↔upstream TCP states at its end.
- `paduraksa.log` (and `paduraksa-cf.log` for the `cf` runner) and `yarp.log`: the .NET processes' logs; the Paduraksa
  logs carry one `METRICS` line per second.
- `env.txt`: the machine and versions, plus:
  - the image digests and CPU sets;
  - the negotiated TLS parameters per proxy;
  - `git status --porcelain` (with `bench/results/` excluded, since it is the harness's own output) and a `dirty`
    flag, plus a SHA-256 of `git diff HEAD`, so a run from a dirty tree is visible.
- `summary.md`: the tables (`baseline/summarize.py`).
- `summary.json`: the same results in machine-readable form, for `bench/history.py`.

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
  its work directory and mounts read-only, for the `body-64k` scenario.
- **Paduraksa** runs **outside Docker**, in Release, through the real `PaduraksaHost` (core, public and admin hosts).
  `Paduraksa.Bench.Runner` boots it with an in-memory config on :18081 (HTTP) and :18443 (HTTPS), with HTTP/3 off.
  - **Process settings:** Server GC, tiered PGO, the shipped `appsettings.json` and production logging, like
    `paduraksa` itself.
  - `bench.test` is a proxy host with one catch-all route to the upstream (gate → routing → YARP).
  - `respond.test` is a Respond host answering `200 ok` from the gate.
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
  hosts.
- **YARP** (`Paduraksa.Bench.Yarp`, :18087/:18449) is a bare Kestrel + YARP `WebApplication`, outside Docker like
  Paduraksa, with the same `Yarp.ReverseProxy` version as `Directory.Packages.props`: one catch-all route to the
  upstream and a minimal endpoint answering `respond.test`. The gap between it and Paduraksa is Paduraksa's own cost
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
  - Access logs are off everywhere: nginx and Caddy are configured off, HAProxy has no `log` directive, Traefik has no
    `accessLog`, Envoy has `access_log: []`, YARP drops its logging below Information (Kestrel's request diagnostics),
    and Paduraksa's runner turns the host access log off explicitly (its built-in default is on).
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
| `cf` | Paduraksa only: plain HTTP/1.1 like `h1`, through a second runner process that trusts loopback as a Cloudflare Tunnel (`--cloudflare-tunnel true`). Each request carries `CF-Connecting-IP` (IPv6), `CF-Visitor` and `CF-Ray`; the client address and scheme are resolved per request and forwarded as `X-Forwarded-For`/`-Proto`. It gets its own 10 s warm-up. |

**Fixed rates:** `h1` and `h2-tls` are then run at fixed request rates (`RATES`, default 10,000 and 30,000 req/s).
- Each rate is warmed up for 5 s at that rate first.
- oha's `--latency-correction` corrects for coordinated omission.
- These runs show latency below saturation, which is where real deployments run.

### Recording a run

1. Start from a clean tree, so the run's `commit` means something (`env.txt` records the dirty state).
2. `RECORD=1 bench/run-baseline.sh` (the full profile; `quick` refuses to record).
3. Commit the run directory (`summary.md`, `summary.json` and `env.txt`; `raw/` and `*.log` are ignored), plus
   `bench/results/history.jsonl` and the regenerated `bench/RESULTS.md`.

`RECORD=1` calls `bench/history.py append` and then `bench/history.py report`. `append` refuses a dirty run unless
given `--allow-dirty`, and a run from a different machine than the previous entry unless given `--new-machine`.
`report` can be rerun at any time; it writes the latest tables, Paduraksa's trend per commit, and a delta against the
previous entry, marking a Paduraksa scenario that lost more than 10 % throughput or more than 25 % at the fixed-rate
p99 as a possible regression.

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

### Future work

- **HTTP/3:** add an `h3` scenario (`oha --http-version 3`, with `Paduraksa:Listen:Http3` on and libmsquic present) for
  Paduraksa and Caddy, and nginx if its image has QUIC enabled.
- **Cloudflare proxy mode:** the `cf` scenario uses Tunnel mode, because the edge ranges proxy mode trusts can only be
  injected through `SnapshotCompilerOptions`, which the server builds internally. Both modes take the same resolver
  path.
- **Placeholders:** the runner names `PlaceholderCertificates.Id` as each host's certificate (its config goes in through
  the config service, as one command after the start). Switch to the vault's certificates when M3 replaces it.
