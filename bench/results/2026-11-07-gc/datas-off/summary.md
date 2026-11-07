### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa |
|---|---:|
| HTTP/1.1 plain, proxied | 182,693 |

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa |
|---|---:|
| HTTP/1.1 plain, proxied | 17.7 (3.23) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 182,693 | 0.34 | 0.39 | 0.55 | 2.06 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

Median of 3 runs per row; the p99 column also shows the lowest and highest run.

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,995 | 0.49 | 0.72 | 0.83 | 1.27 (1.08–1.79) | 5.92 | 51.7 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,583 | 14.0 | 0.0 | 0 | 10.30 | 0.86 | 41 | 9 |
| h1 at 30,000 req/s (run 1) | 4,585 | 1.9 | 0.0 | 0 | 3.02 | 2.06 | 5 | 7 |
| h1 at 30,000 req/s (run 2) | 4,585 | 1.8 | 0.0 | 0 | 3.07 | 2.04 | 19 | 6 |
| h1 at 30,000 req/s (run 3) | 4,584 | 1.6 | 0.0 | 0 | 2.91 | 2.34 | 23 | 6 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 48 | 105 |
