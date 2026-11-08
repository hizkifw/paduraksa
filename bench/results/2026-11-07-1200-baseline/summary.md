### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | Paduraksa ÷ nginx | Paduraksa ÷ Caddy |
|---|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 178,734 | 314,137 | 70,271 | 0.57 | 2.54 |
| HTTPS HTTP/1.1, proxied | 142,310 | 220,311 | 68,090 | 0.65 | 2.09 |
| HTTPS HTTP/2, proxied | 164,249 | 230,465 | 45,242 | 0.71 | 3.63 |
| HTTPS HTTP/2, browser-like headers, proxied | 134,975 | 206,793 | 37,037 | 0.65 | 3.64 |
| HTTP/1.1 plain, answered by the proxy | 419,452 | 780,784 | 204,095 | 0.54 | 2.06 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 163,176 | - | - | - | - |

Upstream alone (HTTP/1.1, same concurrency): 955,017 requests/s.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy |
|---|---:|---:|---:|
| HTTP/1.1 plain, proxied | 18.1 (3.24) | 9.6 (3.01) | 51.0 (3.58) |
| HTTPS HTTP/1.1, proxied | 23.0 (3.27) | 14.8 (3.25) | 52.4 (3.57) |
| HTTPS HTTP/2, proxied | 21.3 (3.50) | 14.0 (3.22) | 79.9 (3.62) |
| HTTPS HTTP/2, browser-like headers, proxied | 26.1 (3.52) | 16.0 (3.31) | 98.4 (3.65) |
| HTTP/1.1 plain, answered by the proxy | 6.6 (2.78) | 3.7 (2.88) | 17.2 (3.50) |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 20.0 (3.26) | - | - |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 178,734 | 0.34 | 0.40 | 0.73 | 1.99 | 0 |
| HTTP/1.1 plain, proxied | nginx | 314,137 | 0.20 | 0.23 | 0.25 | 0.43 | 0 |
| HTTP/1.1 plain, proxied | Caddy | 70,271 | 0.69 | 2.00 | 3.24 | 4.34 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 142,310 | 0.43 | 0.50 | 0.89 | 2.24 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | 220,311 | 0.28 | 0.38 | 0.40 | 0.43 | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 68,090 | 0.71 | 2.07 | 3.44 | 4.81 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 164,249 | 0.37 | 0.46 | 0.97 | 1.41 | 0 |
| HTTPS HTTP/2, proxied | nginx | 230,465 | 0.23 | 0.52 | 0.58 | 0.64 | 0 |
| HTTPS HTTP/2, proxied | Caddy | 45,242 | 1.22 | 2.64 | 4.06 | 5.30 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 134,975 | 0.45 | 0.55 | 1.08 | 1.73 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | 206,793 | 0.31 | 0.43 | 0.49 | 0.52 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 37,037 | 1.49 | 3.24 | 4.83 | 6.14 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 419,452 | 0.14 | 0.17 | 0.46 | 2.22 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 780,784 | 0.08 | 0.11 | 0.13 | 0.17 | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 204,095 | 0.21 | 0.65 | 1.89 | 2.97 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 163,176 | 0.37 | 0.44 | 0.89 | 2.21 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,998 | 0.38 | 0.61 | 0.67 | 1.15 | 3.30 | 138.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,997 | 0.33 | 0.55 | 0.64 | 0.81 | 1.54 | 60.3 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,997 | 0.39 | 1.78 | 2.94 | 5.10 | 12.36 | 159.4 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,992 | 0.46 | 0.82 | 1.13 | 2.75 | 16.86 | 48.9 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,993 | 0.52 | 0.85 | 0.99 | 1.42 | 5.78 | 41.9 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,994 | 0.42 | 1.64 | 2.20 | 3.18 | 11.23 | 61.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,998 | 0.49 | 0.78 | 0.86 | 1.99 | 3.40 | 158.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,999 | 0.37 | 0.60 | 0.68 | 0.85 | 2.21 | 66.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,998 | 0.46 | 1.29 | 2.23 | 3.80 | 7.15 | 164.0 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,995 | 0.56 | 0.97 | 1.32 | 2.67 | 8.68 | 56.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,996 | 0.42 | 0.88 | 1.06 | 1.59 | 3.82 | 33.9 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,997 | 0.79 | 2.07 | 2.59 | 3.92 | 17.39 | 81.3 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).
6 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,573 | 80.7 | 0.0 | 0 | 24.38 | 0.37 | 34 | 11 |
| HTTPS HTTP/1.1, proxied | 5,128 | 72.6 | 0.0 | 0 | 24.44 | 0.42 | 25 | 6 |
| HTTPS HTTP/2, proxied | 5,069 | 64.1 | 0.0 | 0 | 31.27 | 0.59 | 25 | 6 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,885 | 60.3 | 0.0 | 0 | 29.51 | 0.61 | 37 | 6 |
| HTTP/1.1 plain, answered by the proxy | 822 | 67.0 | 0.1 | 0 | 16.19 | 0.49 | 50 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 5,244 | 81.1 | 0.0 | 0 | 32.51 | 0.51 | 32 | 9 |
| h1 at 10,000 req/s (run 1) | 4,585 | 5.7 | 0.3 | 1 | 6.75 | 1.23 | 7 | 7 |
| h1 at 30,000 req/s (run 1) | 4,584 | 26.3 | 0.0 | 0 | 24.44 | 1.15 | 14 | 7 |
| h2-tls at 10,000 req/s (run 1) | 5,059 | 7.1 | 0.1 | 1 | 9.90 | 1.92 | 8 | 6 |
| h2-tls at 30,000 req/s (run 1) | 5,054 | 21.6 | 0.1 | 1 | 22.83 | 1.33 | 13 | 6 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 57 | 19 |
| nginx | 59 | 116 |
| Caddy | 80 | 115 |
