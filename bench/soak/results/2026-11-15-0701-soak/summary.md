# Soak result: 120 min at 4,000 req/s, 51 hosts, 7 upstreams (19 destinations)

| Group | Check | Result | Value | Limit | Note |
|---|---|---|---|---|---|
| Errors | no unexpected client errors | PASS | 0 | 0 | 78232 tolerated in the flap and restart windows; 29,763,625 requests |
| Errors | no unexpected server log errors | PASS | 0 | 0 |  |
| Errors | server log warnings outside the expected ones | INFO | 280 |  | t=123s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2KDBERMNU:00000001 => app=public UpgradeActivityTimeout: The WebSocket; t=125s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2KDBERMS5:00000001 => app=public UpgradeActivityTimeout: The WebSocket; t=130s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2KDBERN2O:00000001 => app=public UpgradeActivityTimeout: The WebSocket |
| Errors | generator kept up (no skipped requests) | PASS | 0 | 0 |  |
| Load | the generator held the rate | PASS | 4,134 req/s | >= 3,800 | server CPU median 163% of 4 cores' worth |
| No leak | RSS growth | PASS | +42.2 MiB/h | <= 64 MiB/h | process 1: +42.2/h, spread 208, over 6080 s of quiet samples; process 2: too short |
| No leak | anonymous RSS growth (heap and native, not mapped files) | PASS | +38.7 MiB/h | <= 64 MiB/h | process 1: +38.7/h, spread 200, over 6080 s of quiet samples; process 2: too short |
| No leak | GC heap retained after collections | PASS | +8.5 MiB/h | <= 16 MiB/h | process 1: +8.5/h, spread 85, over 6080 s of quiet samples; process 2: too short |
| No leak | open descriptors (less client and upstream sockets): slope | PASS | +0.0/h | <= 30/h | process 1: +0.0/h, spread 11, over 6080 s of quiet samples; process 2: too short |
| No leak | open descriptors (less client and upstream sockets): band (p99 - p1) | PASS | 11 | <= 100 |  |
| No leak | open descriptors, raw | INFO | 535 to 2775 |  | follows the connection count |
| No leak | OS threads: band (p99 - p1) | PASS | 4 | <= 16 | process 1: +0.0/h, spread 4, over 6080 s of quiet samples; process 2: too short |
| No leak | thread-pool threads stay put | PASS | max 7 | <= 24 | warm-up max 16; queue max 32 |
| No leak | gen2 collections / GC pause total | INFO | 21 / 43833 ms |  | after the last restart; for the record |
| No leak | RSS range over the run | INFO | 334 to 973 MiB |  | the burst costs memory the collector gives back minutes later |
| No latency drift | small requests: p99, last window vs baseline | PASS | 9.54 ms vs 10.12 ms (0.94x) | <= 1.5x + 0.25 ms | minutes 5-15 vs 110-120 |
| No latency drift | small requests: p50 | INFO | 1.668 vs 1.718 ms |  | minutes 5-15 vs 110-120 |
| No latency drift | small requests: p99.9 | INFO | 17.23 vs 18.828 ms |  | minutes 5-15 vs 110-120 |
| No latency drift | 64 KiB transfers: p99, last window vs baseline | PASS | 11.06 ms vs 11.73 ms | <= 1.5x | minutes 5-15 vs 110-120 |
| Publishes | all publishes succeeded | PASS | 0 of 1313 failed | 0 |  |
| Publishes | cycle publishes (every 10 s) | INFO | 719 of about 720 |  |  |
| Publishes | publish latency p99 | PASS | 49 ms | <= 500 ms | p50 16 ms, max 780 ms |
| Publishes | publish latency does not trend up | PASS | 18.9 ms vs 18.7 ms (median, last vs first third) | <= 1.5x + 5 ms |  |
| Publishes | command to live (canary host) | PASS | p50 21 ms, p99 61 ms | p99 <= 1000 ms | 89 checked |
| Publishes | clients never saw the canary go backwards | PASS | highest seen 684 of 684 | monotonic |  |
| Publishes | the last published revision is the one served | PASS | 684 vs 684 | within 3 |  |
| S6 continuity | the rate limit holds under the mixed load (limited host, 200s per second) | PASS | 19.7/s | <= 33/s (policy 20/s) | 100 requests a second are sent; a bucket evicted by other hosts' buckets starts full and lets them through |
| S6 continuity | 429s continue through the publish burst | PASS | 0 seconds without a 429 of 56 | 0 |  |
| S6 continuity | the limit holds through the burst (200s per second, burst vs baseline) | PASS | 19.8/s vs 19.7/s (limit 20/s) | <= 1.25x + 2 | max in one second 20; a publish that reset the bucket would show as extra 200s |
| S6 continuity | counters are monotonic across publishes | PASS | 0 regressions in 721 scrapes | 0 |  |
| S6 continuity | no scrape failed outside the restart | PASS | 0 | restart window only |  |
| S6 continuity | a destination marked down stays down across publishes | PASS | 544 publishes while it was down | 0 recoveries before it was started again | 1s healthy; 2406s unhealthy; 2463s healthy |
| S6 continuity | no healthy destination flaps | PASS | 0 down verdicts, 0 flapping flags on live destinations | 0 | 14393 health polls |
| Flap | the stopped destination was marked down | PASS | 5.5 s | <= 20 s | active probe: connection refused |
| Flap | it was marked healthy again and got traffic | PASS | 2.5 s, 2419 requests since | <= 20 s |  |
| Flap | the notification outbox recorded down and up | PASS | 1 down, 1 up of 2 events | >= 1 each | destination.down, destination.up |
| Restart | the server stopped cleanly on SIGTERM | PASS | exit 0 after 16.18 s | exit 0 | with WebSockets open the drain runs to the ShutdownTimeout (30 s); see README |
| Restart | healthy again within N seconds of the stop signal | PASS | 19.55 s | <= 50 s | /readyz |
| Restart | first proxied 200 | PASS | 19.56 s | <= 55 s |  |
| Restart | every destination healthy again | PASS | 19.61 s | <= 75 s |  |
| Restart | Prometheus counters (process-local) | INFO | 26,410,344 -> 6,604 |  | they start again from zero with the process, as Prometheus expects |
| Restart | analytics continue (no bucket lost requests) | PASS | 0 of 12 buckets shrank | 0 | the bucket open at the restart 1,033,534 -> 1,163,242; hour total 15,888,642 -> 14,968,553 |
| Restart | metrics overview continues, with the downtime shown as a gap | PASS | 59 busy buckets before, 2 gap or dip buckets, 7 after | all three > 0 | downtime 19.55 s; the hour's series comes back from the persisted rollups |
| Long-lived and slow clients | WebSockets chatted | PASS | 53,485 echoes over 900 sockets | >= 300 sockets |  |
| Long-lived and slow clients | silent WebSockets are cut by the idle timeout | PASS | 290 cut, 120-120 s | 120 s +30 |  |
| Long-lived and slow clients | slowloris connections are cut by the header timeout | PASS | 8928 cut, max 16.9 s, 0 survived | <= 25 s |  |
| Long-lived and slow clients | slow request bodies are cut by the minimum rate | PASS | 2594 cut, max 11.0 s | <= 30 s |  |
| Admin, dashboard and MCP | MCP calls succeeded | PASS | 118 calls, 0 failed | about 120, none failed |  |
| Admin, dashboard and MCP | admin API polling had no errors | PASS | 5,256 calls, 0 errors | 0 |  |
| Admin, dashboard and MCP | dashboard tabs (overview, analytics) stayed live | FAIL | 480 checks, 1 stale, 0 console problems | 0 |  |

**41 passed, 1 failed, 0 skipped**, 8 informational.
