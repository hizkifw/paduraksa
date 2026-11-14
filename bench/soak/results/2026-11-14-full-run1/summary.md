# Soak result: 60 min at 4,000 req/s, 51 hosts, 7 upstreams (19 destinations)

| Group | Check | Result | Value | Limit | Note |
|---|---|---|---|---|---|
| Errors | no unexpected client errors | FAIL | 15 | 0 | timeout=15; 14,880,729 requests |
| Errors | no unexpected server log errors | FAIL | 3 | 0 | t=665s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ1VBKAV686" application never completed.; t=2477s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ1VSPFI6PQ" application never completed.; t=3047s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ1VSPFI9QD" application never completed. |
| Errors | server log warnings outside the expected ones | INFO | 0 |  |  |
| Errors | generator kept up (no skipped requests) | PASS | 0 | 0 |  |
| Load | the generator held the rate | PASS | 4,134 req/s | >= 3,800 | server CPU median 162% of 4 cores' worth |
| No leak | RSS growth | FAIL | +333.4 MiB/h | <= 64 MiB/h | process 1: +106.1/h, spread 67, over 1480 s of quiet samples; process 2: +333.4/h, spread 129, over 1160 s of quiet samples |
| No leak | anonymous RSS growth (heap and native, not mapped files) | SKIP |  |  | the run is too short for a fit (needs 600 s of quiet samples) |
| No leak | GC heap retained after collections | FAIL | +48.3 MiB/h | <= 16 MiB/h | process 1: +48.3/h, spread 52, over 1480 s of quiet samples; process 2: +42.9/h, spread 46, over 1160 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): slope | PASS | +0.0/h | <= 30/h | process 1: +0.0/h, spread 27, over 1480 s of quiet samples; process 2: +0.0/h, spread 24, over 1160 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): band (p99 - p1) | PASS | 27 | <= 100 |  |
| No leak | open descriptors, raw | INFO | 1269 to 2651 |  | follows the connection count |
| No leak | OS threads: band (p99 - p1) | PASS | 4 | <= 16 | process 1: +0.0/h, spread 1, over 1480 s of quiet samples; process 2: +10.3/h, spread 4, over 1160 s of quiet samples |
| No leak | thread-pool threads stay put | PASS | max 7 | <= 23 | warm-up max 15; queue max 24 |
| No leak | gen2 collections / GC pause total | INFO | 40 / 14215 ms |  | after the last restart; for the record |
| No leak | RSS range over the run | INFO | 402 to 738 MiB |  | the burst costs memory the collector gives back minutes later |
| No latency drift | small requests: p99, last window vs baseline | PASS | 10.12 ms vs 10.74 ms (0.94x) | <= 1.5x + 0.25 ms | minutes 5-15 vs 50-60 |
| No latency drift | small requests: p50 | INFO | 1.769 vs 1.769 ms |  | minutes 5-15 vs 50-60 |
| No latency drift | small requests: p99.9 | INFO | 16.728 vs 19.392 ms |  | minutes 5-15 vs 50-60 |
| No latency drift | 64 KiB transfers: p99, last window vs baseline | PASS | 12.09 ms vs 12.45 ms | <= 1.5x | minutes 5-15 vs 50-60 |
| Publishes | all publishes succeeded | PASS | 0 of 955 failed | 0 |  |
| Publishes | cycle publishes (every 10 s) | INFO | 359 of about 360 |  |  |
| Publishes | publish latency p99 | PASS | 40 ms | <= 500 ms | p50 16 ms, max 311 ms |
| Publishes | publish latency does not trend up | PASS | 18.7 ms vs 18.0 ms (median, last vs first third) | <= 1.5x + 5 ms |  |
| Publishes | command to live (canary host) | PASS | p50 21 ms, p99 69 ms | p99 <= 1000 ms | 45 checked |
| Publishes | clients never saw the canary go backwards | PASS | highest seen 641 of 641 | monotonic |  |
| Publishes | the last published revision is the one served | PASS | 641 vs 641 | within 3 |  |
| S6 continuity | the rate limit holds under the mixed load (limited host, 200s per second) | PASS | 19.7/s | <= 33/s (policy 20/s) | 100 requests a second are sent; a bucket evicted by other hosts' buckets starts full and lets them through |
| S6 continuity | 429s continue through the publish burst | PASS | 0 seconds without a 429 of 56 | 0 |  |
| S6 continuity | the limit holds through the burst (200s per second, burst vs baseline) | PASS | 19.7/s vs 19.7/s (limit 20/s) | <= 1.25x + 2 | max in one second 20; a publish that reset the bucket would show as extra 200s |
| S6 continuity | counters are monotonic across publishes | PASS | 0 regressions in 361 scrapes | 0 |  |
| S6 continuity | no scrape failed outside the restart | PASS | 0 | restart window only |  |
| S6 continuity | a destination marked down stays down across publishes | PASS | 537 publishes while it was down | 0 recoveries before it was started again | 0s healthy; 1207s unhealthy; 1324s healthy |
| S6 continuity | no healthy destination flaps | PASS | 0 down verdicts, 0 flapping flags on live destinations | 0 | 7194 health polls |
| Flap | the stopped destination was marked down | PASS | 6.5 s | <= 20 s | active probe: connection refused |
| Flap | it was marked healthy again and got traffic | FAIL | 63.5 s, 19 requests since | <= 20 s |  |
| Flap | the notification outbox recorded down and up | FAIL | 1 down, 0 up of 1 events | >= 1 each | destination.down |
| Flap | passive health reactivation, as the server logged it | INFO | 00:01:00 |  | the upstreams are configured with a 10 s reactivation period; see README |
| Restart | the server stopped cleanly on SIGTERM | PASS | exit 0 after 31.11 s | exit 0 | with WebSockets open the drain runs to the ShutdownTimeout (30 s); see README |
| Restart | healthy again within N seconds of the stop signal | PASS | 34.37 s | <= 50 s | /readyz |
| Restart | first proxied 200 | PASS | 34.38 s | <= 55 s |  |
| Restart | every destination healthy again | PASS | 34.43 s | <= 75 s |  |
| Restart | Prometheus counters (process-local) | INFO | 7,428,510 -> 28,109 |  | they start again from zero with the process, as Prometheus expects |
| Restart | analytics continue (the total carried across) | FAIL | 7,426,965 -> 7,291,409 requests (hour) | no loss |  |
| Restart | metrics overview continues, with the downtime shown as a gap | PASS | 30 busy buckets before, 3 gap or dip buckets, 8 after | all three > 0 | downtime 34.37 s; the hour's series comes back from the persisted rollups |
| Long-lived and slow clients | WebSockets chatted | PASS | 26,519 echoes over 610 sockets | >= 300 sockets |  |
| Long-lived and slow clients | silent WebSockets are cut by the idle timeout | SKIP |  |  | none reached the timeout in this run |
| Long-lived and slow clients | slowloris connections are cut by the header timeout | PASS | 4424 cut, max 17.0 s, 0 survived | <= 25 s |  |
| Long-lived and slow clients | slow request bodies are cut by the minimum rate | PASS | 1284 cut, max 11.0 s | <= 30 s |  |
| Admin, dashboard and MCP | MCP calls succeeded | PASS | 59 calls, 0 failed | about 60, none failed |  |
| Admin, dashboard and MCP | admin API polling had no errors | FAIL | 2,633 calls, 1 errors | 0 |  |
| Admin, dashboard and MCP | dashboard tabs (overview, analytics) stayed live | PASS | 240 checks, 0 stale, 0 console problems | 0 |  |

**32 passed, 8 failed, 2 skipped**, 9 informational.
