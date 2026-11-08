### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa |
|---|---:|
| HTTP/1.1 plain, proxied | 182,697 |

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa |
|---|---:|
| HTTP/1.1 plain, proxied | 17.6 (3.22) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 182,697 | 0.34 | 0.39 | 0.55 | 2.01 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

Median of 3 runs per row; the p99 column also shows the lowest and highest run.

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,994 | 0.57 | 0.94 | 1.08 | 2.13 (1.69-2.48) | 8.00 | 58.1 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,580 | 13.9 | 0.0 | 0 | 9.33 | 0.74 | 22 | 13 |
| h1 at 30,000 req/s (run 1) | 4,585 | 2.1 | 0.0 | 0 | 3.69 | 2.20 | 11 | 7 |
| h1 at 30,000 req/s (run 2) | 4,586 | 2.0 | 0.0 | 0 | 4.08 | 2.90 | 18 | 7 |
| h1 at 30,000 req/s (run 3) | 4,585 | 1.6 | 0.0 | 0 | 3.29 | 2.33 | 53 | 7 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 58 | 64 |
