### Throughput at fixed concurrency (successful requests/s)

| Scenario | nginx | Caddy |
|---|---:|---:|
| HTTP/1.1 plain, proxied | 308,744 | 70,261 |

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | nginx | Caddy |
|---|---:|---:|
| HTTP/1.1 plain, proxied | 9.8 (3.02) | 50.9 (3.58) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | nginx | 308,744 | 0.21 | 0.26 | 0.28 | 0.77 | 0 |
| HTTP/1.1 plain, proxied | Caddy | 70,261 | 0.69 | 2.01 | 3.27 | 4.37 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

Median of 2 runs per row; the p99 column also shows the lowest and highest run.

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,995 | 0.64 | 1.04 | 1.15 | 1.52 (1.49–1.55) | 3.44 | 49.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,993 | 0.43 | 1.67 | 2.24 | 3.25 (3.17–3.32) | 7.35 | 62.5 | 0 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| nginx | 54 | 8 |
| Caddy | 68 | 138 |
