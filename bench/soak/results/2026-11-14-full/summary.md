# Soak result: 60 min at 4,000 req/s, 51 hosts, 7 upstreams (19 destinations)

| Group | Check | Result | Value | Limit | Note |
|---|---|---|---|---|---|
| Errors | no unexpected client errors | FAIL | 15 | 0 | timeout=15; 14,880,562 requests |
| Errors | no unexpected server log errors | FAIL | 7 | 0 | t=677s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ20JI9A8CA" application never completed.; t=677s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ20JI9A8CG" application never completed.; t=677s crit Microsoft.AspNetCore.Server.Kestrel[23]: app=public Connection id "0HNQ20JI9A8DI" application never completed. |
| Errors | server log warnings outside the expected ones | INFO | 0 |  |  |
| Errors | generator kept up (no skipped requests) | PASS | 0 | 0 |  |
| Load | the generator held the rate | PASS | 4,134 req/s | >= 3,800 | server CPU median 158% of 4 cores' worth |
| No leak | RSS growth | FAIL | +234.6 MiB/h | <= 64 MiB/h | process 1: +234.6/h, spread 117, over 1480 s of quiet samples; process 2: +99.8/h, spread 140, over 1160 s of quiet samples |
| No leak | anonymous RSS growth (heap and native, not mapped files) | FAIL | +217.0 MiB/h | <= 64 MiB/h | process 1: +217.0/h, spread 110, over 1480 s of quiet samples; process 2: +92.5/h, spread 139, over 1160 s of quiet samples |
| No leak | GC heap retained after collections | FAIL | +32.5 MiB/h | <= 16 MiB/h | process 1: +17.8/h, spread 66, over 1480 s of quiet samples; process 2: +32.5/h, spread 44, over 1160 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): slope | PASS | +0.0/h | <= 30/h | process 1: +0.0/h, spread 23, over 1480 s of quiet samples; process 2: +0.0/h, spread 26, over 1160 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): band (p99 - p1) | PASS | 26 | <= 100 |  |
| No leak | open descriptors, raw | INFO | 1128 to 2757 |  | follows the connection count |
| No leak | OS threads: band (p99 - p1) | PASS | 3 | <= 16 | process 1: +0.0/h, spread 3, over 1480 s of quiet samples; process 2: +0.0/h, spread 1, over 1160 s of quiet samples |
| No leak | thread-pool threads stay put | PASS | max 7 | <= 19 | warm-up max 11; queue max 22 |
| No leak | gen2 collections / GC pause total | INFO | 42 / 12879 ms |  | after the last restart; for the record |
| No leak | RSS range over the run | INFO | 411 to 783 MiB |  | the burst costs memory the collector gives back minutes later |
| No latency drift | small requests: p99, last window vs baseline | PASS | 11.39 ms vs 10.42 ms (1.09x) | <= 1.5x + 0.25 ms | minutes 5-15 vs 50-60 |
| No latency drift | small requests: p50 | INFO | 1.439 vs 1.769 ms |  | minutes 5-15 vs 50-60 |
| No latency drift | small requests: p99.9 | INFO | 63.259 vs 18.279 ms |  | minutes 5-15 vs 50-60 |
| No latency drift | 64 KiB transfers: p99, last window vs baseline | PASS | 13.60 ms vs 12.09 ms | <= 1.5x | minutes 5-15 vs 50-60 |
| Publishes | all publishes succeeded | PASS | 0 of 957 failed | 0 |  |
| Publishes | cycle publishes (every 10 s) | INFO | 359 of about 360 |  |  |
| Publishes | publish latency p99 | PASS | 49 ms | <= 500 ms | p50 17 ms, max 340 ms |
| Publishes | publish latency does not trend up | PASS | 20.3 ms vs 17.6 ms (median, last vs first third) | <= 1.5x + 5 ms |  |
| Publishes | command to live (canary host) | PASS | p50 22 ms, p99 53 ms | p99 <= 1000 ms | 45 checked |
| Publishes | clients never saw the canary go backwards | PASS | highest seen 643 of 643 | monotonic |  |
| Publishes | the last published revision is the one served | PASS | 643 vs 643 | within 3 |  |
| S6 continuity | the rate limit holds under the mixed load (limited host, 200s per second) | PASS | 19.7/s | <= 33/s (policy 20/s) | 100 requests a second are sent; a bucket evicted by other hosts' buckets starts full and lets them through |
| S6 continuity | 429s continue through the publish burst | PASS | 0 seconds without a 429 of 56 | 0 |  |
| S6 continuity | the limit holds through the burst (200s per second, burst vs baseline) | PASS | 19.7/s vs 19.7/s (limit 20/s) | <= 1.25x + 2 | max in one second 20; a publish that reset the bucket would show as extra 200s |
| S6 continuity | counters are monotonic across publishes | PASS | 0 regressions in 361 scrapes | 0 |  |
| S6 continuity | no scrape failed outside the restart | PASS | 0 | restart window only |  |
| S6 continuity | a destination marked down stays down across publishes | PASS | 542 publishes while it was down | 0 recoveries before it was started again | 1s healthy; 1206s unhealthy; 1323s healthy |
| S6 continuity | no healthy destination flaps | PASS | 0 down verdicts, 0 flapping flags on live destinations | 0 | 7194 health polls |
| Flap | the stopped destination was marked down | PASS | 6.0 s | <= 20 s | active probe: connection refused |
| Flap | it was marked healthy again and got traffic | FAIL | 63.0 s, 20 requests since | <= 20 s |  |
| Flap | the notification outbox recorded down and up | PASS | 1 down, 1 up of 2 events | >= 1 each | destination.down, destination.up |
| Flap | passive health reactivation, as the server logged it | INFO | 00:01:00 |  | the upstreams are configured with a 10 s reactivation period; see README |
| Restart | the server stopped cleanly on SIGTERM | PASS | exit 0 after 30.16 s | exit 0 | with WebSockets open the drain runs to the ShutdownTimeout (30 s); see README |
| Restart | healthy again within N seconds of the stop signal | PASS | 33.34 s | <= 50 s | /readyz |
| Restart | first proxied 200 | PASS | 33.35 s | <= 55 s |  |
| Restart | every destination healthy again | PASS | 33.4 s | <= 75 s |  |
| Restart | Prometheus counters (process-local) | INFO | 7,428,569 -> 32,064 |  | they start again from zero with the process, as Prometheus expects |
| Restart | analytics continue (the total carried across) | FAIL | 7,427,229 -> 6,906,897 requests (hour) | no loss |  |
| Restart | metrics overview continues, with the downtime shown as a gap | PASS | 30 busy buckets before, 3 gap or dip buckets, 7 after | all three > 0 | downtime 33.34 s; the hour's series comes back from the persisted rollups |
| Long-lived and slow clients | WebSockets chatted | PASS | 26,526 echoes over 610 sockets | >= 300 sockets |  |
| Long-lived and slow clients | silent WebSockets are cut by the idle timeout | SKIP |  |  | none reached the timeout in this run |
| Long-lived and slow clients | slowloris connections are cut by the header timeout | PASS | 4431 cut, max 17.0 s, 0 survived | <= 25 s |  |
| Long-lived and slow clients | slow request bodies are cut by the minimum rate | PASS | 1284 cut, max 11.1 s | <= 30 s |  |
| Admin, dashboard and MCP | MCP calls succeeded | PASS | 59 calls, 0 failed | about 60, none failed |  |
| Admin, dashboard and MCP | admin API polling had no errors | PASS | 2,624 calls, 0 errors | 0 |  |
| Admin, dashboard and MCP | dashboard tabs (overview, analytics) stayed live | PASS | 240 checks, 0 stale, 0 console problems | 0 |  |

**34 passed, 7 failed, 1 skipped**, 9 informational.
