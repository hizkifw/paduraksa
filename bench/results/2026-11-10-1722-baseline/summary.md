### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 161,923 | 326,585 | 70,352 | 274,335 | 104,540 | 106,963 | 176,149 | 0.50 |
| HTTPS HTTP/1.1, proxied | 134,197 | 222,796 | 68,340 | 229,380 | 101,427 | 105,186 | 140,511 | 0.60 |
| HTTPS HTTP/2, proxied | 146,099 | 240,047 | 45,136 | 232,446 | 60,706 | 116,386 | 158,823 | 0.61 |
| HTTPS HTTP/2, browser-like headers, proxied | 121,776 | 206,545 | 37,719 | 211,637 | 49,456 | 105,787 | 132,434 | 0.59 |
| HTTP/1.1 plain, 64 KiB body, proxied | 69,491 | 76,682 | 50,255 | 104,022 | 38,797 | 70,155 | 69,921 | 0.91 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 7,187 | 8,977 | 11,169 | 7,978 | 9,159 | 13,854 | 10,056 | 0.80 |
| HTTP/1.1 plain, answered by the proxy | 406,828 | 803,824 | 207,097 | 594,317 | - | 267,982 | 384,421 | 0.51 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 150,940 | - | - | - | - | - | - | - |

Upstream alone (HTTP/1.1, same concurrency): 1,010,985 requests/s.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 20.6 (3.34) | 9.5 (3.10) | 51.6 (3.63) | 11.7 (3.20) | 34.5 (3.60) | 33.6 (3.60) | 18.7 (3.29) |
| HTTPS HTTP/1.1, proxied | 25.5 (3.42) | 14.8 (3.31) | 53.3 (3.64) | 14.4 (3.30) | 35.5 (3.61) | 34.7 (3.65) | 23.9 (3.36) |
| HTTPS HTTP/2, proxied | 24.5 (3.58) | 13.6 (3.26) | 81.0 (3.66) | 14.1 (3.28) | 59.4 (3.61) | 32.1 (3.74) | 22.0 (3.49) |
| HTTPS HTTP/2, browser-like headers, proxied | 29.7 (3.61) | 16.0 (3.31) | 98.6 (3.72) | 15.7 (3.32) | 70.8 (3.50) | 35.4 (3.75) | 27.3 (3.62) |
| HTTP/1.1 plain, 64 KiB body, proxied | 49.9 (3.47) | 41.6 (3.19) | 70.3 (3.53) | 32.2 (3.34) | 66.9 (2.60) | 51.1 (3.59) | 49.1 (3.43) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 524.4 (3.77) | 376.2 (3.38) | 337.6 (3.77) | 470.9 (3.76) | 341.5 (3.13) | 225.1 (3.12) | 369.6 (3.72) |
| HTTP/1.1 plain, answered by the proxy | 7.1 (2.87) | 3.7 (2.97) | 17.2 (3.56) | 5.4 (3.20) | - | 13.5 (3.62) | 7.5 (2.87) |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 22.4 (3.38) | - | - | - | - | - | - |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 161,923 | 0.37 | 0.44 | 0.90 | 1.99 | 0 |
| HTTP/1.1 plain, proxied | nginx | 326,585 | 0.19 | 0.26 | 0.28 | 0.73 | 0 |
| HTTP/1.1 plain, proxied | Caddy | 70,352 | 0.70 | 2.01 | 3.14 | 4.13 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 274,335 | 0.22 | 0.28 | 0.32 | 0.58 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 104,540 | 0.50 | 1.05 | 2.99 | 4.16 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 106,963 | 0.64 | 0.88 | 1.04 | 2.85 | 0 |
| HTTP/1.1 plain, proxied | YARP | 176,149 | 0.35 | 0.41 | 0.78 | 1.94 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 134,197 | 0.45 | 0.53 | 1.01 | 1.94 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | 222,796 | 0.29 | 0.36 | 0.40 | 0.94 | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 68,340 | 0.72 | 2.05 | 3.19 | 4.13 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | 229,380 | 0.27 | 0.31 | 0.34 | 0.47 | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 101,427 | 0.51 | 1.09 | 3.06 | 4.28 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 105,186 | 0.57 | 0.75 | 0.92 | 1.00 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 140,511 | 0.43 | 0.51 | 0.97 | 1.92 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 146,099 | 0.42 | 0.51 | 1.07 | 1.42 | 0 |
| HTTPS HTTP/2, proxied | nginx | 240,047 | 0.27 | 0.46 | 0.53 | 0.75 | 0 |
| HTTPS HTTP/2, proxied | Caddy | 45,136 | 1.24 | 2.63 | 3.91 | 5.01 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 232,446 | 0.27 | 0.32 | 0.38 | 0.50 | 0 |
| HTTPS HTTP/2, proxied | Traefik | 60,706 | 0.91 | 1.77 | 3.76 | 5.40 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 116,386 | 0.55 | 0.84 | 1.02 | 1.12 | 0 |
| HTTPS HTTP/2, proxied | YARP | 158,823 | 0.38 | 0.48 | 0.99 | 1.43 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 121,776 | 0.50 | 0.62 | 1.21 | 1.86 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | 206,545 | 0.33 | 0.44 | 0.53 | 0.96 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 37,719 | 1.48 | 3.16 | 4.54 | 5.65 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 211,637 | 0.29 | 0.35 | 0.43 | 0.74 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 49,456 | 1.08 | 2.26 | 4.71 | 7.81 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 105,787 | 0.59 | 0.78 | 0.88 | 1.01 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 132,434 | 0.45 | 0.58 | 1.09 | 1.88 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 69,491 | 0.89 | 1.12 | 1.75 | 3.06 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 76,682 | 0.81 | 1.29 | 1.43 | 1.93 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 50,255 | 1.03 | 2.56 | 4.15 | 5.32 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | 104,022 | 0.59 | 0.67 | 1.09 | 2.62 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 38,797 | 1.19 | 3.40 | 7.54 | 13.98 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 70,155 | 0.99 | 1.28 | 1.48 | 1.77 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 69,921 | 0.88 | 1.14 | 1.74 | 2.78 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 7,187 | 2.15 | 2.74 | 3.53 | 4.73 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 8,977 | 1.68 | 3.05 | 4.25 | 4.97 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 11,169 | 1.32 | 2.22 | 3.51 | 4.46 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 7,978 | 1.98 | 2.35 | 2.77 | 3.33 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 9,159 | 1.41 | 3.06 | 6.37 | 11.21 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | 13,854 | 1.01 | 2.10 | 2.76 | 3.22 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 10,056 | 1.54 | 1.90 | 2.73 | 5.99 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 406,828 | 0.15 | 0.18 | 0.49 | 2.16 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 803,824 | 0.08 | 0.10 | 0.11 | 0.14 | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 207,097 | 0.21 | 0.66 | 1.81 | 2.69 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | 594,317 | 0.10 | 0.14 | 0.20 | 0.25 | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 267,982 | 0.24 | 0.29 | 0.37 | 0.41 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 384,421 | 0.15 | 0.19 | 0.49 | 2.34 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 150,940 | 0.40 | 0.48 | 1.01 | 2.28 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,997 | 0.43 | 0.69 | 0.79 | 2.04 | 4.83 | 150.3 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,998 | 0.32 | 0.56 | 0.65 | 0.94 | 2.69 | 61.6 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,998 | 0.39 | 1.98 | 3.14 | 5.46 | 17.16 | 157.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,998 | 0.34 | 0.58 | 0.66 | 0.82 | 1.46 | 63.1 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,998 | 0.49 | 0.92 | 1.67 | 5.73 | 8.40 | 156.5 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,998 | 0.54 | 1.04 | 1.21 | 1.59 | 2.39 | 95.1 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,998 | 0.39 | 0.64 | 0.72 | 1.68 | 3.23 | 138.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,993 | 0.47 | 0.81 | 0.99 | 2.07 | 8.99 | 52.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,994 | 0.50 | 0.91 | 1.10 | 1.62 | 7.59 | 40.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,992 | 0.39 | 1.49 | 2.03 | 2.92 | 4.75 | 58.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,993 | 0.56 | 0.93 | 1.15 | 2.23 | 10.39 | 45.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,993 | 0.40 | 0.94 | 2.09 | 4.27 | 6.09 | 55.1 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,994 | 0.60 | 0.96 | 1.10 | 6.02 | 13.95 | 46.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,994 | 0.45 | 0.80 | 1.04 | 2.13 | 7.95 | 49.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,998 | 0.49 | 0.82 | 0.92 | 2.39 | 3.73 | 161.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,997 | 0.29 | 0.59 | 0.70 | 0.99 | 2.15 | 55.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,999 | 0.48 | 1.58 | 2.49 | 4.10 | 8.90 | 171.4 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,998 | 0.45 | 0.69 | 0.78 | 1.00 | 3.41 | 85.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,999 | 0.21 | 0.84 | 1.78 | 3.80 | 8.49 | 71.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,997 | 0.64 | 1.05 | 1.21 | 1.51 | 2.21 | 103.8 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | 0.39 | 0.72 | 0.85 | 1.79 | 3.30 | 127.8 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,991 | 0.51 | 0.88 | 1.16 | 2.52 | 6.63 | 55.0 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,991 | 0.61 | 1.12 | 1.37 | 6.30 | 11.95 | 47.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,997 | 0.80 | 2.13 | 2.65 | 4.08 | 16.96 | 82.2 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,997 | 0.73 | 1.21 | 1.43 | 3.78 | 7.98 | 54.8 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,995 | 0.59 | 1.78 | 2.78 | 5.83 | 20.07 | 62.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,994 | 0.68 | 1.26 | 1.39 | 4.85 | 11.13 | 48.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,994 | 0.49 | 1.05 | 1.68 | 4.47 | 6.71 | 53.0 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).
5 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 5,452 | 77.6 | 0.0 | 0 | 31.91 | 0.50 | 46 | 17 |
| HTTPS HTTP/1.1, proxied | 6,065 | 70.4 | 0.0 | 0 | 30.28 | 0.44 | 39 | 6 |
| HTTPS HTTP/2, proxied | 5,942 | 53.0 | 0.0 | 0 | 28.66 | 0.63 | 44 | 7 |
| HTTPS HTTP/2, browser-like headers, proxied | 7,545 | 54.6 | 0.0 | 0 | 30.78 | 0.63 | 24 | 6 |
| HTTP/1.1 plain, 64 KiB body, proxied | 7,014 | 35.0 | 0.0 | 0 | 20.38 | 0.61 | 40 | 6 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 19,618 | 21.0 | 0.0 | 0 | 12.66 | 0.69 | 19 | 6 |
| HTTP/1.1 plain, answered by the proxy | 898 | 71.3 | 0.0 | 0 | 20.66 | 0.41 | 51 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 5,862 | 70.6 | 0.0 | 0 | 32.72 | 0.56 | 42 | 23 |
| h1 at 10,000 req/s (run 1) | 5,474 | 6.1 | 0.2 | 1 | 9.09 | 1.79 | 5 | 9 |
| h1 at 30,000 req/s (run 1) | 5,474 | 19.5 | 0.0 | 0 | 17.88 | 1.04 | 15 | 8 |
| h2-tls at 10,000 req/s (run 1) | 5,947 | 7.4 | 0.1 | 1 | 11.35 | 1.97 | 7 | 7 |
| h2-tls at 30,000 req/s (run 1) | 5,947 | 16.4 | 0.0 | 0 | 23.07 | 1.61 | 18 | 7 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 42 | 20 |
| nginx | 64 | 73 |
| Caddy | 90 | 88 |
| HAProxy | 59 | 54 |
| Traefik | 27 | 70 |
| Envoy | 68 | 28 |
| YARP | 64 | 45 |
