# Soak result: 120 min at 4,000 req/s, 51 hosts, 7 upstreams (19 destinations)

| Group | Check | Result | Value | Limit | Note |
|---|---|---|---|---|---|
| Errors | no unexpected client errors | PASS | 0 | 0 | 78118 tolerated in the flap and restart windows; 29,763,621 requests |
| Errors | no unexpected server log errors | PASS | 0 | 0 |  |
| Errors | server log warnings outside the expected ones | INFO | 187 |  | t=122s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2I56HDIOP:00000001 => app=public UpgradeActivityTimeout: The WebSocket; t=124s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2I56HDIS4:00000001 => app=public UpgradeActivityTimeout: The WebSocket; t=127s warn Yarp.ReverseProxy.Forwarder.HttpForwarder[48]: RequestPath:/ws RequestId:0HNQ2I56HDIVP:00000001 => app=public UpgradeActivityTimeout: The WebSocket |
| Errors | generator kept up (no skipped requests) | PASS | 0 | 0 |  |
| Load | the generator held the rate | PASS | 4,134 req/s | >= 3,800 | server CPU median 163% of 4 cores' worth |
| No leak | RSS growth | FAIL | +138.4 MiB/h | <= 64 MiB/h | process 1: +138.4/h, spread 212, over 3280 s of quiet samples; process 2: +58.4/h, spread 142, over 2980 s of quiet samples |
| No leak | anonymous RSS growth (heap and native, not mapped files) | FAIL | +130.4 MiB/h | <= 64 MiB/h | process 1: +130.4/h, spread 206, over 3280 s of quiet samples; process 2: +52.4/h, spread 138, over 2980 s of quiet samples |
| No leak | GC heap retained after collections | FAIL | +44.6 MiB/h | <= 16 MiB/h | process 1: +2.5/h, spread 64, over 3280 s of quiet samples; process 2: +44.6/h, spread 98, over 2980 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): slope | PASS | +0.0/h | <= 30/h | process 1: +0.0/h, spread 19, over 3280 s of quiet samples; process 2: +0.0/h, spread 10, over 2980 s of quiet samples |
| No leak | open descriptors (less client and upstream sockets): band (p99 - p1) | PASS | 19 | <= 100 |  |
| No leak | open descriptors, raw | INFO | 744 to 2699 |  | follows the connection count |
| No leak | OS threads: band (p99 - p1) | PASS | 4 | <= 16 | process 1: +2.4/h, spread 4, over 3280 s of quiet samples; process 2: +0.0/h, spread 3, over 2980 s of quiet samples |
| No leak | thread-pool threads stay put | PASS | max 7 | <= 17 | warm-up max 9; queue max 24 |
| No leak | gen2 collections / GC pause total | INFO | 241 / 21212 ms |  | after the last restart; for the record |
| No leak | RSS range over the run | INFO | 328 to 928 MiB |  | the burst costs memory the collector gives back minutes later |
| No latency drift | small requests: p99, last window vs baseline | PASS | 10.12 ms vs 10.12 ms (1.00x) | <= 1.5x + 0.25 ms | minutes 5-15 vs 110-120 |
| No latency drift | small requests: p50 | INFO | 1.668 vs 1.718 ms |  | minutes 5-15 vs 110-120 |
| No latency drift | small requests: p99.9 | INFO | 18.279 vs 19.974 ms |  | minutes 5-15 vs 110-120 |
| No latency drift | 64 KiB transfers: p99, last window vs baseline | PASS | 11.39 ms vs 11.73 ms | <= 1.5x | minutes 5-15 vs 110-120 |
| Publishes | all publishes succeeded | PASS | 0 of 1313 failed | 0 |  |
| Publishes | cycle publishes (every 10 s) | INFO | 719 of about 720 |  |  |
| Publishes | publish latency p99 | PASS | 49 ms | <= 500 ms | p50 17 ms, max 418 ms |
| Publishes | publish latency does not trend up | PASS | 18.3 ms vs 18.2 ms (median, last vs first third) | <= 1.5x + 5 ms |  |
| Publishes | command to live (canary host) | PASS | p50 21 ms, p99 78 ms | p99 <= 1000 ms | 89 checked |
| Publishes | clients never saw the canary go backwards | PASS | highest seen 684 of 684 | monotonic |  |
| Publishes | the last published revision is the one served | PASS | 684 vs 684 | within 3 |  |
| S6 continuity | the rate limit holds under the mixed load (limited host, 200s per second) | PASS | 19.7/s | <= 33/s (policy 20/s) | 100 requests a second are sent; a bucket evicted by other hosts' buckets starts full and lets them through |
| S6 continuity | 429s continue through the publish burst | PASS | 0 seconds without a 429 of 56 | 0 |  |
| S6 continuity | the limit holds through the burst (200s per second, burst vs baseline) | PASS | 19.7/s vs 19.7/s (limit 20/s) | <= 1.25x + 2 | max in one second 20; a publish that reset the bucket would show as extra 200s |
| S6 continuity | counters are monotonic across publishes | PASS | 0 regressions in 721 scrapes | 0 |  |
| S6 continuity | no scrape failed outside the restart | PASS | 0 | restart window only |  |
| S6 continuity | a destination marked down stays down across publishes | PASS | 545 publishes while it was down | 0 recoveries before it was started again | 1s healthy; 2406s unhealthy; 2463s healthy |
| S6 continuity | no healthy destination flaps | PASS | 0 down verdicts, 0 flapping flags on live destinations | 0 | 14393 health polls |
| Flap | the stopped destination was marked down | PASS | 5.5 s | <= 20 s | active probe: connection refused |
| Flap | it was marked healthy again and got traffic | PASS | 2.5 s, 2382 requests since | <= 20 s |  |
| Flap | the notification outbox recorded down and up | PASS | 1 down, 1 up of 2 events | >= 1 each | destination.down, destination.up |
| Restart | the server stopped cleanly on SIGTERM | PASS | exit 0 after 16.2 s | exit 0 | with WebSockets open the drain runs to the ShutdownTimeout (30 s); see README |
| Restart | healthy again within N seconds of the stop signal | PASS | 19.75 s | <= 50 s | /readyz |
| Restart | first proxied 200 | PASS | 19.75 s | <= 55 s |  |
| Restart | every destination healthy again | PASS | 19.81 s | <= 75 s |  |
| Restart | Prometheus counters (process-local) | INFO | 14,856,957 -> 6,034 |  | they start again from zero with the process, as Prometheus expects |
| Restart | analytics continue (no bucket lost requests) | PASS | 0 of 13 buckets shrank | 0 | the bucket open at the restart 838,909 -> 1,158,762; hour total 14,854,882 -> 15,174,735 |
| Restart | metrics overview continues, with the downtime shown as a gap | PASS | 59 busy buckets before, 2 gap or dip buckets, 7 after | all three > 0 | downtime 19.75 s; the hour's series comes back from the persisted rollups |
| Long-lived and slow clients | WebSockets chatted | PASS | 53,498 echoes over 807 sockets | >= 300 sockets |  |
| Long-lived and slow clients | silent WebSockets are cut by the idle timeout | FAIL | 197 cut, 120-840 s | 120 s +30 |  |
| Long-lived and slow clients | slowloris connections are cut by the header timeout | PASS | 8939 cut, max 17.0 s, 0 survived | <= 25 s |  |
| Long-lived and slow clients | slow request bodies are cut by the minimum rate | PASS | 2599 cut, max 11.1 s | <= 30 s |  |
| Admin, dashboard and MCP | MCP calls succeeded | PASS | 119 calls, 0 failed | about 120, none failed |  |
| Admin, dashboard and MCP | admin API polling had no errors | PASS | 5,257 calls, 0 errors | 0 |  |
| Admin, dashboard and MCP | dashboard tabs (overview, analytics) stayed live | PASS | 480 checks, 0 stale, 0 console problems | 0 |  |

**38 passed, 4 failed, 0 skipped**, 8 informational.
