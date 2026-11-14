# Soak test

Does one Paduraksa stay healthy for an hour of realistic traffic and change? The [S7 baseline](../README.md) measures peak throughput in ten-second runs. This runs a
moderate, fixed load for as long as it takes memory, descriptors, threads and latency to show a trend, while the config changes, an upstream flaps and the server restarts.
It is the M11 "load tests" item, and the S6 exit criterion at load (publishes every 100 ms: continuous 429s, monotonic counters, destination health kept).

```sh
bench/soak/run-soak.sh --quick              # 5 minutes: every event once, scaled down; checks the harness, and gives no leak figures
bench/soak/run-soak.sh                      # 60 minutes
bench/soak/run-soak.sh --duration 1800 --rate 3000 --out /tmp/soak
bench/soak/run-soak.sh --analyze-only DIR   # judge a finished run again, after changing a threshold
```

Exit 0 when every check passed, 1 when one failed, 3 when the harness itself failed. It needs the .NET SDK, `python3` and `ss`. `taskset` pins the server and the load to separate
CPUs (`--server-cpus 0-3`, `--load-cpus 4-19`). Chromium (`PADURAKSA_E2E_CHROMIUM`, else `/usr/bin/chromium`) gives it two dashboard tabs. libmsquic turns HTTP/3 on. Run it on a quiet
machine: another heavy process moves the latency and the CPU figures.

## What runs

**The server** is the Release build of `src/Paduraksa.Server`, started as `dotnet paduraksa.dll` with its settings in environment variables. It is the real entry point
(vault, config database, three listeners, admin API, dashboard, MCP, analytics), not the bench runner's in-memory host, but not the container image either: building and running the image needs
Docker, host networking and libmsquic from Microsoft's feed, and adds nothing that makes this test's findings different. What the image has and this does not: its cgroup limits and its read-only
root. A [startup hook](../Paduraksa.Bench.Soak.Probe) loaded through `DOTNET_STARTUP_HOOKS` writes the process's GC and thread-pool figures to a file every five seconds, because neither
`/proc` nor `/metrics` has them. The hook is a thread of its own and no change to the product.

**The config** (`SoakConfig.cs`, written out as `config.yaml` in the run's directory) is 51 hosts over seven upstreams of two or three destinations each, two profiles, and what a homelab turns on:

| Feature | Where |
|---|---|
| Access log and analytics | every host (the global default) |
| HSTS, response compression, custom response header, custom error pages (404, 502, 429) | the `public` profile (`app01` to `app20`, the flap and rewrite hosts) |
| Access list `lan` and a rate limit `mid` (600/s) | the `internal` profile (`app21` to `app30`), `limit01` to `limit05`, `lan` |
| A rule set that refuses scanner paths and `sqlmap` | `rules` |
| Forward auth against a fake provider (cached 30 s) | `auth` |
| Proof of work, 8 bits | `pow` |
| Custom request headers (set, add, remove), response headers, compression | `headers` |
| Strip-prefix `/app` and add-prefix `/api` | `rewrite` |
| Force HTTPS, a redirect host, a respond host, a request body limit of 1 MiB | `https`, `redirect`, `respond`, `upload` |
| A rate limit of 20/s, burst 20, for the 429 client | `limited` |
| An upstream with a 120 s idle timeout, for the WebSockets | `ws` |
| A global deny list and 20 000 connections per IP (every client is loopback) | settings |

`route-transforms` is on `main`, so the headers, compression and rewrite hosts are in. HTTP/3 is on when libmsquic is present; without it the run says so in `plan.json` and the report, and the
H3 share of the mix goes to H2 (this machine has none, so the H3 request path is written but has not been exercised).

**The traffic** is an open loop at a fixed rate (`--rate`, default 4 000 requests a second): a request is due at its scheduled time whether or not the earlier ones are done, and its latency runs
from then, so a stall shows up as latency and not as a quieter generator. The mix, by weight:

- small GETs on HTTP/1.1 and HTTP/2 over TLS, and a little plain HTTP, over the 30 app hosts (more traffic to the first ones) and many paths;
- 64 KiB downloads (checked byte for byte) and uploads (the upstream reports how many bytes it got), and one upload in fifty over the body limit that must be a 413;
- compressed text, the headers, rewrite, forward-auth, rule-set, access-list, redirect, respond and HTTPS-redirect hosts, each checking the answer and not only the status;
- the PoW host with a solved cookie, solved again when the gate stops accepting it;
- the canary host, whose response header carries a revision that only goes up;
- requests for unknown hosts (404);
- the `flap` hosts, which sit on the upstream that flaps;
- a separate 429 client: 100 requests a second at the `limited` host, which lets 20 through;
- 300 WebSockets, each sending a message every 20 to 60 seconds and checking the echo, and five that stay silent and must be cut by the 120-second idle timeout;
- 20 slowloris connections (a header trickled a byte every 3 s, to be cut by the 15 s header timeout) and four slow request bodies (10 bytes a second, to be cut by the minimum body rate).

The default rate is **not** 30 to 50 % of the machine's bench throughput (the bench's `oha` pushes 100 000 or more a second through four CPUs, and a .NET `HttpClient` generator on the
other CPUs can't, with TLS, 51 host names and per-request checks). It is the rate this generator holds without skipping a request, which uses about 40 % of the server's four CPUs (the `Load` row
says how much). `--rate` goes up until the generator reports skipped requests.

**The changes**, while it runs:

- **Publishes.** Every 10 s one edit through the admin API (`PUT` on one object, with `If-Match`), cycling a host (the canary's header, or an ordinary host's notes and tags), an upstream
  (`web-b` gains and loses a third destination and changes its balancing), the `mid` rate-limit policy and the `lan` access list. Each is checked to be a real change. A canary edit also measures
  command to live: the time until the canary answers with the new revision. The **burst** is an edit of the canary every 100 ms for 60 s.
- **Flap.** One destination of `flappy` stops for 60 s, then starts again. Its health, polled every 500 ms, must go down and come back; the outbox must hold both events.
- **Admin.** An API client polls the metrics overview (3 s), analytics, revisions, audit, logs and more at the dashboard's rates; two Chromium tabs, on the overview and on Analytics, stay open the
  whole run (a Blazor circuit each, polling the server) and are checked every 30 s to have a live circuit and no console error.
- **MCP.** A call a minute, `search` and `execute` alternating (the `execute` reads the host list through the sandbox).
- **Restart.** At the halfway point the server gets SIGTERM and is started again on the same data directory. The tabs reload.

Events are scaled to the length of the run (`SoakPlan`): the 60-minute plan warms up for 5 min, flaps at 20:00 for 60 s, bursts from 20:05 for 60 s, restarts at 30:00, compares minutes 5 to 15 with
50 to 60. The 5-minute plan warms up for 60 s, flaps at 1:40 for 30 s, bursts from 1:45 for 30 s, restarts at 2:30.

## What it writes

`bench/soak/results/<date>-<time>-soak/` (or `--out`):

| File | Holds |
|---|---|
| `summary.md`, `results.json` | the table below and the same as data, with the thresholds used |
| `samples.csv` | one row per 10 s: RSS, GC heap and retained size, gen 0/1/2 counts, GC pause, thread-pool threads and queue, descriptors, OS threads, CPU, connections from clients and to upstreams, the clients' requests, errors by kind and p50/p99/p99.9, publish latency, the server's own request and 429 counters |
| `facts.json` | what the checks read: error kinds and the first few errors, latency windows, the limited host's 200s and 429s per second, counter regressions, health timeline, flap and restart facts, WebSocket and slow-client results, MCP calls |
| `publishes.json`, `events.jsonl` | every publish with its time and latency; every phase and notable event, in seconds from the load's start |
| `plan.json`, `env.txt`, `config.yaml` | the plan, the machine and versions, the config that was imported |
| `server.log`, `probe.log`, `driver.log`, `server-log-notable.txt` | the server's output (10 MB and more, not committed), the startup hook's lines, the driver's output, and the few server log lines worth keeping (errors, health verdicts, lifecycle) |
| `server-logs/` | with `--keep-logs`: the server's own log directory, the access log of every request (not committed; for a short run) |

## The checks and their thresholds

All of them are in `THRESHOLDS` at the top of `analyze.py`. The reasoning:

| Check | Limit | Why |
|---|---|---|
| Unexpected client errors | 0 | A 5xx, a connection error, a timeout, a wrong body, a WebSocket closed by the server, an admin or MCP failure. Tolerated: errors in the restart (from the stop signal to ready, plus 15 s) and, on the flap hosts only, the flap's window plus 30 s. YARP does not retry, so the requests in flight to a destination as it stops get 502 until the check takes it out. |
| Unexpected server log `fail`/`crit` | 0 | Outside the same windows, the slow clients' own warnings and the setup's startup notes. Other warnings are listed, not failed. |
| RSS growth | 64 MiB/h | Theil-Sen slope (the median of pairwise slopes; a GC or a hiccup does not move it) over each post-warm-up stretch of one process: before the restart, and after it from 120 s on. Server GC's RSS wanders by tens of MiB, so a tighter bound would fail on noise; a real leak at this rate fills 1 GiB in a few hours. |
| GC heap retained after collections | 16 MiB/h | What survived the last collection of each generation, large and pinned heaps included. Far less noisy than RSS, so the bound is tighter. |
| Descriptors, OS threads | slope 30/h and 4/h; spread (p99 minus p1) 100 and 16 | The count follows the connections, which come and go (HttpClient recycles every 10 min), so a band as well as a slope. |
| Thread-pool threads | at most the warm-up's maximum + 8 | The pool must not creep. |
| Segments shorter than 600 s | skipped | A slope over a few minutes says nothing. `--quick` therefore reports no leak figures. |
| p99 of the last 10 min against minutes 5 to 15 | 1.5x + 0.25 ms | The brief's 1.5x. The quarter millisecond is the histogram's 3 % bucket error and the clock's grain, so a 0.3 ms p99 is not a failure at 0.5. Computed from merged histograms of the whole window, not an average of p99s. |
| Publishes | all succeed; p99 under 500 ms; the last third's median not above 1.5x + 5 ms the first third's | A 51-host config publishes in about 15 ms; the bounds are wide enough for a busy machine and tight enough to catch a publish that grows with history. |
| Command to live | p99 under 1 s | Measured on the canary. |
| Canary | never goes back; the last revision served within 3 of the last published | S6: a snapshot only moves forward. Three allows the requests that were in flight when the burst ended. |
| S6, 429s | none of the burst's seconds without a 429; 200s per second within 1.25x + 2 of the baseline's; the baseline's within 1.5x + 3 of the policy (20/s) | A publish that reset a bucket would show as extra 200s every 100 ms. The absolute bound is what found the table's eviction bug (below). |
| S6, counters | 0 regressions | Every counter and histogram series in `/metrics`, between scrapes; the one comparison skipped is the first after the restart. |
| S6, health | down stays down across the publishes, no live destination reported down or flapping | The health board is polled every 500 ms, and the burst overlaps the flap. |
| Flap | down within 20 s, up within 20 s and traffic again, outbox has `destination.down` and `destination.up` | Active probe every 3 s, 2 s timeout, 10 s debounce on the notification. |
| Restart | exit 0; `/readyz` within 50 s of the signal; first proxied 200 within 55 s; every destination healthy within 75 s | 50 s is the 30 s `ShutdownTimeout` a drain with open WebSockets uses, plus the start. Analytics' total does not shrink; the overview's hour has busy buckets before and after and the downtime as a coverage gap or a dip. |
| Slow clients | slowloris cut within 25 s (limit 15 s); slow bodies within 30 s (grace 10 s); silent WebSockets cut at 120 s, -10 s and +30 s | The listener's own limits, with the client's cadence as margin. |
| MCP, admin, dashboard | no failed call; no stale tab or console error | |
| Generator | the rate achieved is at least 95 % of the one asked for; no request skipped | Otherwise the latency measures the generator. |

The Prometheus counters are the process's own, so they start again from zero with the restart (the table shows the totals before and after). What carries across is the persisted rollups: the
metrics overview and analytics.

## What the first full runs found

Two 60-minute runs on 2026-11-14 (`results/2026-11-14-full-run1`, `results/2026-11-14-full`; the second used a harness with the diagnostics the first run showed it needed). They agree
closely, which is itself a finding: the problems below are not noise. Both ran 14.9 million requests at 4 134 requests a second with no skipped request, the server using about 40 % of its four CPUs.

**Passed:** no latency drift (p99 of minutes 50 to 60 was 0.94x and 1.09x that of minutes 5 to 15), every publish succeeded (957; p50 17 ms, p99 49 ms, command to live 22 ms at p50),
the canary never went backwards, the counters never regressed in 361 scrapes, the limit held through the burst (19.7 a second against a policy of 20), a destination marked down stayed down through
542 publishes, no live destination was reported down, descriptors less sockets flat, OS threads flat (30 to 34), thread-pool threads at most 7, MCP, the admin API and the dashboard tabs clean.

**Failed, each a finding:**

1. **Rate limits stopped limiting once a client met more than eight rate-limited hosts** (found in a first 5-minute run, fixed in `RateLimitTable`: see the waf.md note). A 20/s policy let 88/s through under the
   mixed load; a bucket evicted from the probe window starts full. The check `the rate limit holds under the mixed load` is what keeps it fixed.
2. **A restart with WebSockets open takes the whole `ShutdownTimeout` (30.1 s) and loses the analytics and metrics buckets that were still in memory.** The restart reached `/readyz` after 33 s (3 s with no
   WebSocket open, where the same stop took 0.11 s). The hour's analytics total fell from 7 427 229 to 6 906 897 requests in the full run (and from 618 000 to 152 000 in the 5-minute runs, whose
   whole history was the open bucket), where the control run without WebSockets carried all of it (618 854 to 767 901). `PaduraksaHost.StopHostsAsync` gives the public drain, admin and core one shared
   budget; once the drain has spent it, core stops "without waiting", and `BackgroundService.StopAsync` given a cancelled token returns before the rollup jobs' final flush (which uses `CancellationToken.None`) has run.
   Not fixed here: closing upgraded connections when the drain starts, or giving core a budget of its own, are both small and the manager's call.
3. **A destination that recovers after a flap stays out for a minute.** The 60 s outage was seen by the active check at 6 s and the destination was healthy again to the active check 3 s after it came
   back, but it was out of rotation for 63 s. The first request it got after recovery evaluated YARP's passive transport-failure window, which still held the outage's failures, marked it unhealthy again, and
   YARP logged "scheduled for a reactivation in 00:01:00" although the upstream's `passiveHealth.reactivationPeriod` is 10 s. The period reaches YARP's cluster model (`SnapshotPublisherTests` builds it) and does not
   change what YARP does: a unit test with a 2 s period still had the destination passively unhealthy after 5 s and healthy only after about a minute. The notification outbox's `destination.up` therefore arrives a minute late.
4. **About 15 HTTP/2 requests an hour never get a response** (15 of 14.9 million in each run; none on HTTP/1.1 or plain HTTP): the client gives up after 30 s. In 13 of the 15 the upstream had answered. In a 5-minute
   run that kept the access log, the proxy's own log has the hung request as a normal completed `200 420 B` in 0.27 ms. So the response was lost between Kestrel's HTTP/2 write and the .NET client, or is a client-side
   fault; the soak can't tell which. Kestrel also logged `crit ... application never completed` for 9 connections, in groups of two or three, a few minutes apart. Unresolved: it needs a packet capture or a second client (`h2load`) to place it.
5. **RSS grows while the managed heap does not.** The anonymous RSS of the first process rose about 217 MiB/h over its quiet minutes and the second's about 93 MiB/h; GC heap retained after collections stayed near 150 MiB and
   the managed heap's committed size rose from about 240 to 300 MiB. RSS moves in steps (522 to 571 MiB in the first six minutes, 580 to 675 MiB at 50 minutes in the first run) and the burst costs about
   150 MiB that the collector gives back minutes later, so one hour cannot tell a plateau from a leak. The thresholds make this a failure on purpose: the second process's slope is measured over only 19 minutes. A two-hour run, or one with a
   restart at a third, would settle it.

The silent WebSocket check was skipped in both runs: `ClientWebSocket` pings every 30 s by default, a ping is traffic, and so the 120-second idle timeout never ran out. The harness now disables the pings for those
five sockets; 5-minute runs after the change show them cut at exactly 120 s.

## Known limits of the test

- One client IP: every client is loopback, so per-IP limits (the rate limits, the connection cap) see one client. The deny list and the cap are configured high for that reason.
- The upstreams are in the driver's process (one Kestrel app per destination), on the load CPUs.
- The upstream flap is a stopped listener; a destination that hangs or answers slowly is not tested.
- HTTP/3 needs libmsquic; without it that path is not exercised, and the report says so.
- Nothing here is a security test: the slow clients are the only hostile traffic.
