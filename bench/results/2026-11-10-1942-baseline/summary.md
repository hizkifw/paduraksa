### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 178,585 | 332,262 | 72,767 | 285,429 | 105,685 | 112,755 | 177,220 | 0.54 |
| HTTPS HTTP/1.1, proxied | 143,121 | 231,925 | 70,424 | 235,184 | 101,995 | 107,063 | 143,142 | 0.62 |
| HTTPS HTTP/2, proxied | 157,865 | 251,012 | 46,468 | 237,263 | 62,876 | 113,565 | 162,887 | 0.63 |
| HTTPS HTTP/2, browser-like headers, proxied | 132,806 | 212,001 | 38,051 | 216,411 | 53,678 | 102,956 | 134,098 | 0.63 |
| HTTP/1.1 plain, 64 KiB body, proxied | 75,033 | 78,831 | 51,389 | 106,731 | 61,613 | 72,091 | 74,971 | 0.95 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 10,208 | 9,095 | 11,210 | 8,050 | 11,726 | 14,193 | 10,195 | 1.12 |
| HTTP/1.1 plain, answered by the proxy | 404,741 | 836,510 | 209,428 | 606,102 | - | 273,501 | 377,956 | 0.48 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 163,412 | - | - | - | - | - | - | - |

Upstream alone (HTTP/1.1, same concurrency): 997,413 requests/s.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 18.5 (3.31) | 9.3 (3.09) | 50.9 (3.70) | 11.2 (3.20) | 34.2 (3.61) | 32.4 (3.65) | 18.6 (3.29) |
| HTTPS HTTP/1.1, proxied | 23.6 (3.37) | 14.3 (3.31) | 52.5 (3.69) | 14.1 (3.31) | 35.4 (3.61) | 34.1 (3.65) | 23.5 (3.36) |
| HTTPS HTTP/2, proxied | 22.6 (3.57) | 12.9 (3.24) | 79.8 (3.71) | 13.8 (3.28) | 58.4 (3.67) | 32.4 (3.68) | 21.6 (3.52) |
| HTTPS HTTP/2, browser-like headers, proxied | 27.2 (3.61) | 15.8 (3.36) | 98.1 (3.73) | 15.5 (3.35) | 69.1 (3.71) | 36.3 (3.74) | 26.3 (3.53) |
| HTTP/1.1 plain, 64 KiB body, proxied | 46.0 (3.45) | 40.5 (3.19) | 68.8 (3.53) | 31.8 (3.39) | 55.6 (3.43) | 49.8 (3.59) | 46.1 (3.45) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 364.6 (3.72) | 373.2 (3.39) | 336.4 (3.77) | 472.3 (3.80) | 324.3 (3.80) | 222.2 (3.15) | 363.7 (3.71) |
| HTTP/1.1 plain, answered by the proxy | 7.1 (2.89) | 3.5 (2.95) | 17.1 (3.58) | 5.3 (3.20) | - | 13.2 (3.61) | 7.4 (2.80) |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 20.6 (3.37) | - | - | - | - | - | - |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 178,585 | 0.35 | 0.40 | 0.73 | 1.84 | 0 |
| HTTP/1.1 plain, proxied | nginx | 332,262 | 0.19 | 0.22 | 0.24 | 0.27 | 0 |
| HTTP/1.1 plain, proxied | Caddy | 72,767 | 0.68 | 1.94 | 2.99 | 3.76 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 285,429 | 0.21 | 0.27 | 0.31 | 0.32 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 105,685 | 0.49 | 1.05 | 2.92 | 4.10 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 112,755 | 0.55 | 0.67 | 0.75 | 0.78 | 0 |
| HTTP/1.1 plain, proxied | YARP | 177,220 | 0.34 | 0.40 | 0.76 | 2.20 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 143,121 | 0.43 | 0.50 | 0.90 | 2.03 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | 231,925 | 0.28 | 0.39 | 0.40 | 0.43 | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 70,424 | 0.70 | 1.98 | 3.04 | 3.97 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | 235,184 | 0.27 | 0.31 | 0.34 | 0.39 | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 101,995 | 0.51 | 1.08 | 3.01 | 4.33 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 107,063 | 0.58 | 0.78 | 0.99 | 1.03 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 143,142 | 0.43 | 0.50 | 0.92 | 2.14 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 157,865 | 0.39 | 0.48 | 0.95 | 1.24 | 0 |
| HTTPS HTTP/2, proxied | nginx | 251,012 | 0.25 | 0.31 | 0.36 | 0.41 | 0 |
| HTTPS HTTP/2, proxied | Caddy | 46,468 | 1.21 | 2.54 | 3.71 | 4.75 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 237,263 | 0.26 | 0.31 | 0.37 | 0.41 | 0 |
| HTTPS HTTP/2, proxied | Traefik | 62,876 | 0.90 | 1.69 | 3.56 | 4.82 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 113,565 | 0.66 | 0.72 | 0.91 | 0.94 | 0 |
| HTTPS HTTP/2, proxied | YARP | 162,887 | 0.37 | 0.46 | 0.94 | 1.43 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 132,806 | 0.46 | 0.57 | 1.09 | 1.51 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | 212,001 | 0.30 | 0.37 | 0.43 | 0.46 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 38,051 | 1.47 | 3.12 | 4.46 | 5.62 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 216,411 | 0.29 | 0.34 | 0.40 | 0.62 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 53,678 | 1.03 | 2.02 | 4.12 | 5.44 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 102,956 | 0.47 | 0.95 | 1.17 | 1.26 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 134,098 | 0.45 | 0.56 | 1.08 | 1.76 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 75,033 | 0.84 | 1.03 | 1.50 | 2.51 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 78,831 | 0.82 | 0.97 | 1.04 | 1.11 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 51,389 | 1.01 | 2.51 | 4.05 | 5.18 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | 106,731 | 0.59 | 0.65 | 0.75 | 2.68 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 61,613 | 0.89 | 1.80 | 3.86 | 5.74 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 72,091 | 0.81 | 1.27 | 1.46 | 1.65 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 74,971 | 0.83 | 1.03 | 1.57 | 2.34 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 10,208 | 1.53 | 1.88 | 2.52 | 3.75 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 9,095 | 1.67 | 2.98 | 4.16 | 4.91 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 11,210 | 1.31 | 2.22 | 3.41 | 4.37 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 8,050 | 1.98 | 2.30 | 2.56 | 2.85 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 11,726 | 1.25 | 2.08 | 3.87 | 5.34 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | 14,193 | 0.99 | 2.04 | 2.68 | 3.01 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 10,195 | 1.51 | 1.91 | 2.85 | 3.99 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 404,741 | 0.15 | 0.18 | 0.48 | 2.18 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 836,510 | 0.07 | 0.11 | 0.12 | 0.14 | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 209,428 | 0.20 | 0.65 | 1.76 | 2.57 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | 606,102 | 0.10 | 0.11 | 0.20 | 0.21 | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 273,501 | 0.23 | 0.30 | 0.36 | 0.42 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 377,956 | 0.16 | 0.19 | 0.46 | 2.38 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 163,412 | 0.37 | 0.44 | 0.94 | 2.26 | 0 |

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,998 | 0.43 | 0.66 | 0.72 | 1.37 | 3.89 | 156.6 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,997 | 0.36 | 0.61 | 0.69 | 0.83 | 1.92 | 69.2 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,996 | 0.41 | 1.87 | 3.02 | 4.83 | 6.68 | 167.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,998 | 0.33 | 0.53 | 0.58 | 0.66 | 1.72 | 66.5 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,997 | 0.48 | 0.88 | 1.56 | 5.46 | 8.24 | 156.4 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,998 | 0.70 | 1.14 | 1.31 | 1.62 | 5.51 | 122.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,998 | 0.40 | 0.64 | 0.70 | 1.58 | 3.42 | 145.8 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,996 | 0.54 | 0.96 | 1.16 | 5.35 | 22.94 | 57.9 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,993 | 0.62 | 0.87 | 0.95 | 1.26 | 4.83 | 45.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,992 | 0.41 | 1.62 | 2.17 | 3.18 | 9.25 | 61.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,995 | 0.74 | 1.13 | 1.23 | 1.63 | 6.90 | 53.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,994 | 0.47 | 1.18 | 2.50 | 4.59 | 9.19 | 60.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,994 | 0.65 | 1.03 | 1.14 | 1.32 | 11.62 | 47.3 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,994 | 0.55 | 1.04 | 1.29 | 2.43 | 6.12 | 59.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,998 | 0.50 | 0.75 | 0.84 | 2.23 | 3.56 | 169.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,998 | 0.40 | 0.60 | 0.68 | 0.81 | 2.75 | 87.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,998 | 0.46 | 1.47 | 2.32 | 3.82 | 5.20 | 172.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,998 | 0.41 | 0.59 | 0.67 | 0.80 | 3.14 | 82.4 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,998 | 0.58 | 0.98 | 1.66 | 4.73 | 6.90 | 183.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,998 | 0.71 | 1.14 | 1.30 | 1.60 | 3.37 | 111.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | 0.47 | 0.68 | 0.79 | 2.21 | 5.24 | 158.2 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,996 | 0.52 | 0.88 | 1.09 | 2.34 | 5.33 | 56.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,993 | 0.58 | 1.21 | 1.54 | 3.92 | 6.15 | 50.3 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,991 | 0.77 | 2.01 | 2.50 | 4.08 | 18.42 | 80.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,996 | 0.69 | 1.11 | 1.36 | 4.79 | 14.30 | 51.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,995 | 0.51 | 1.06 | 1.99 | 3.79 | 11.82 | 63.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,991 | 0.63 | 1.17 | 1.28 | 1.48 | 8.19 | 44.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,994 | 0.59 | 1.07 | 1.26 | 2.34 | 6.20 | 62.0 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).
5 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,056 | 72.9 | 0.0 | 0 | 22.33 | 0.43 | 38 | 14 |
| HTTPS HTTP/1.1, proxied | 4,621 | 65.6 | 0.0 | 0 | 23.75 | 0.48 | 51 | 7 |
| HTTPS HTTP/2, proxied | 4,516 | 61.3 | 0.0 | 0 | 28.07 | 0.59 | 32 | 6 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,064 | 55.8 | 0.0 | 0 | 28.00 | 0.64 | 33 | 6 |
| HTTP/1.1 plain, 64 KiB body, proxied | 5,462 | 37.8 | 0.0 | 0 | 16.70 | 0.52 | 54 | 6 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 18,430 | 23.7 | 0.0 | 0 | 13.79 | 0.73 | 17 | 6 |
| HTTP/1.1 plain, answered by the proxy | 900 | 71.0 | 0.0 | 0 | 20.34 | 0.30 | 49 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 4,433 | 64.0 | 0.0 | 0 | 28.19 | 0.49 | 39 | 23 |
| h1 at 10,000 req/s (run 1) | 4,050 | 4.1 | 0.1 | 1 | 6.86 | 1.99 | 15 | 8 |
| h1 at 30,000 req/s (run 1) | 4,049 | 15.9 | 0.0 | 0 | 14.82 | 1.11 | 2 | 8 |
| h2-tls at 10,000 req/s (run 1) | 4,524 | 5.8 | 0.0 | 0 | 9.26 | 1.81 | 1 | 6 |
| h2-tls at 30,000 req/s (run 1) | 4,525 | 18.9 | 0.0 | 0 | 23.94 | 1.64 | 46 | 6 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 64 | 30 |
| nginx | 55 | 86 |
| Caddy | 128 | 81 |
| HAProxy | 59 | 53 |
| Traefik | 42 | 54 |
| Envoy | 40 | 14 |
| YARP | 22 | 49 |
