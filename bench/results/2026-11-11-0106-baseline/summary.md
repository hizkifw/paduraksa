### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 178,206 | **323,238** | 72,351 | 284,142 | 105,427 | 113,233 | 177,749 | 0.55 |
| HTTPS HTTP/1.1, proxied | 142,414 | 229,379 | 70,324 | **232,269** | 101,761 | 105,236 | 142,539 | 0.62 |
| HTTPS HTTP/2, proxied | 157,976 | **246,909** | 46,481 | 234,545 | 62,580 | 112,919 | 163,556 | 0.64 |
| HTTPS HTTP/2, browser-like headers, proxied | 132,620 | **218,491** | 37,903 | 214,260 | 53,720 | 102,765 | 135,374 | 0.61 |
| HTTP/1.1 plain, 64 KiB body, proxied | 75,757 | 78,634 | 51,338 | **106,793** | 60,974 | 71,220 | 74,684 | 0.96 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 10,025 | 9,062 | 11,248 | 8,059 | 11,312 | **14,185** | 10,207 | 1.11 |
| HTTP/1.1 plain, answered by the proxy | 401,129 | **842,593** | 210,495 | 590,669 | - | 269,604 | 380,674 | 0.48 |
| HTTP/1.1 plain, access log on, proxied | 163,485 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 175,950 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, every request refused (429) | 395,616 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 151,991 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the built-in 502 page | 19,962 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 162,153 | - | - | - | - | - | - | - |

Upstream alone (HTTP/1.1, same concurrency): 1,020,760 requests/s.

For `rate-limited` and `error-page`, the number is every response, 429s or 502s included: those scenarios measure what the proxy
answers when it refuses or fails, so counting only 2xx would report zero.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 18.5 (3.29) | **9.6 (3.11)** | 51.1 (3.69) | 11.3 (3.21) | 34.3 (3.61) | 32.2 (3.65) | 18.4 (3.27) |
| HTTPS HTTP/1.1, proxied | 23.5 (3.34) | 14.5 (3.32) | 52.6 (3.70) | **14.2 (3.30)** | 35.4 (3.60) | 34.8 (3.66) | 23.7 (3.37) |
| HTTPS HTTP/2, proxied | 22.3 (3.53) | **13.2 (3.26)** | 79.9 (3.71) | 14.0 (3.28) | 58.5 (3.66) | 32.7 (3.69) | 21.6 (3.54) |
| HTTPS HTTP/2, browser-like headers, proxied | 27.1 (3.59) | **15.3 (3.34)** | 98.6 (3.74) | 15.6 (3.35) | 69.0 (3.71) | 36.5 (3.75) | 26.7 (3.61) |
| HTTP/1.1 plain, 64 KiB body, proxied | 45.6 (3.45) | 40.5 (3.19) | 68.8 (3.53) | **31.7 (3.39)** | 55.8 (3.40) | 50.5 (3.59) | 45.6 (3.41) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 372.3 (3.73) | 373.3 (3.38) | 336.1 (3.78) | 471.4 (3.80) | 335.5 (3.79) | **222.0 (3.15)** | 365.5 (3.73) |
| HTTP/1.1 plain, answered by the proxy | 7.2 (2.87) | **3.5 (2.91)** | 17.0 (3.59) | 5.4 (3.22) | - | 13.4 (3.62) | 7.4 (2.83) |
| HTTP/1.1 plain, access log on, proxied | 20.5 (3.35) | - | - | - | - | - | - |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 18.6 (3.27) | - | - | - | - | - | - |
| HTTP/1.1 plain, every request refused (429) | 7.3 (2.91) | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 20.8 (3.16) | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the built-in 502 page | 191.6 (3.83) | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 20.7 (3.35) | - | - | - | - | - | - |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 178,206 | 0.34 | 0.40 | 0.80 | 2.02 | 0 |
| HTTP/1.1 plain, proxied | nginx | **323,238** | **0.20** | **0.23** | **0.24** | **0.27** | 0 |
| HTTP/1.1 plain, proxied | Caddy | 72,351 | 0.68 | 1.94 | 2.99 | 3.78 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 284,142 | 0.21 | 0.27 | 0.31 | 0.33 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 105,427 | 0.49 | 1.05 | 2.96 | 4.15 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 113,233 | 0.53 | 0.75 | 0.95 | 0.99 | 0 |
| HTTP/1.1 plain, proxied | YARP | 177,749 | 0.34 | 0.40 | 0.75 | 2.08 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 142,414 | 0.43 | 0.50 | 0.96 | 2.20 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | 229,379 | 0.29 | 0.37 | 0.38 | 0.40 | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 70,324 | 0.70 | 1.98 | 3.05 | 3.92 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | **232,269** | **0.27** | **0.31** | **0.34** | **0.37** | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 101,761 | 0.51 | 1.09 | 3.02 | 4.27 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 105,236 | 0.58 | 0.73 | 0.92 | 0.95 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 142,539 | 0.43 | 0.50 | 0.94 | 2.07 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 157,976 | 0.39 | 0.47 | 1.05 | 1.49 | 0 |
| HTTPS HTTP/2, proxied | nginx | **246,909** | **0.21** | 0.46 | 0.53 | 0.56 | 0 |
| HTTPS HTTP/2, proxied | Caddy | 46,481 | 1.21 | 2.54 | 3.73 | 4.64 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 234,545 | 0.27 | **0.31** | **0.37** | **0.40** | 0 |
| HTTPS HTTP/2, proxied | Traefik | 62,580 | 0.90 | 1.70 | 3.57 | 4.91 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 112,919 | 0.61 | 0.98 | 1.17 | 1.27 | 0 |
| HTTPS HTTP/2, proxied | YARP | 163,556 | 0.37 | 0.46 | 0.96 | 1.37 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 132,620 | 0.46 | 0.56 | 1.10 | 1.74 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | **218,491** | **0.29** | 0.41 | 0.47 | 0.50 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 37,903 | 1.47 | 3.13 | 4.52 | 5.68 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 214,260 | **0.29** | **0.35** | **0.41** | **0.44** | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 53,720 | 1.04 | 2.01 | 4.10 | 5.40 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 102,765 | 0.61 | 0.96 | 1.18 | 1.28 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 135,374 | 0.44 | 0.56 | 1.07 | 1.46 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 75,757 | 0.83 | 1.02 | 1.56 | 2.26 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 78,634 | 0.80 | 1.03 | 1.13 | 1.25 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 51,338 | 1.01 | 2.50 | 4.09 | 5.16 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | **106,793** | **0.59** | **0.64** | **0.71** | **0.88** | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 60,974 | 0.90 | 1.83 | 3.93 | 5.85 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 71,220 | 0.89 | 1.19 | 1.31 | 1.44 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 74,684 | 0.83 | 1.04 | 1.66 | 2.83 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 10,025 | 1.55 | 1.91 | 2.70 | 3.86 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 9,062 | 1.67 | 3.02 | 4.19 | 4.96 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 11,248 | 1.31 | 2.20 | 3.50 | 4.55 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 8,059 | 1.98 | 2.30 | **2.56** | **2.84** | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 11,312 | 1.28 | 2.18 | 4.08 | 5.42 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | **14,185** | **0.99** | 2.04 | 2.67 | 3.04 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 10,207 | 1.52 | **1.88** | **2.56** | 3.70 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 401,129 | 0.15 | 0.18 | 0.45 | 2.25 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | **842,593** | **0.07** | **0.11** | **0.12** | **0.15** | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 210,495 | 0.20 | 0.64 | 1.81 | 2.77 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | 590,669 | 0.10 | 0.12 | 0.21 | 0.23 | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 269,604 | 0.25 | 0.32 | 0.37 | 0.44 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 380,674 | 0.16 | 0.19 | 0.49 | 2.37 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 163,485 | 0.36 | 0.46 | 1.12 | 2.25 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 175,950 | 0.35 | 0.40 | 0.82 | 2.10 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 395,616 | 0.15 | 0.18 | 0.50 | 2.21 | 3956654 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 151,991 | 0.40 | 0.47 | 0.88 | 2.48 | 0 |
| HTTP/1.1 plain, closed upstream, the built-in 502 page | Paduraksa | 19,962 | 3.07 | 4.41 | 6.78 | 9.92 | 199646 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 162,153 | 0.37 | 0.45 | 0.88 | 2.27 | 0 |

### Response status codes at fixed concurrency

A pair is listed when it isn't all 200, and the Paduraksa-only paths are listed even when they are.

| Scenario | Proxy | 200 | 429 | 502 |
|---|---|---:|---:|---:|
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 1,635,081 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 1,759,954 | 0 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 1 | 3,956,654 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 1,520,310 | 0 | 0 |
| HTTP/1.1 plain, closed upstream, the built-in 502 page | Paduraksa | 0 | 0 | 199,646 |

`rate-limit` answered no 429s: the policy stayed inside its budget, so the run measures the
token take on one hot bucket, as intended.

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,998 | 0.41 | 0.64 | 0.70 | 1.82 | 5.02 | 158.7 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,998 | **0.33** | 0.55 | 0.63 | 0.80 | 2.87 | 64.8 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,997 | 0.44 | 2.12 | 3.31 | 5.19 | 7.52 | 174.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,998 | **0.33** | **0.51** | **0.55** | **0.66** | **1.25** | **63.5** | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,998 | 0.49 | 0.87 | 1.62 | 6.19 | 9.86 | 157.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,998 | 0.72 | 1.19 | 1.38 | 1.70 | 5.66 | 125.3 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,997 | 0.42 | 0.64 | 0.69 | 1.93 | 3.07 | 152.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,994 | 0.55 | 0.95 | 1.14 | 2.48 | **5.00** | 58.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,995 | 0.65 | 0.89 | 1.00 | 2.16 | 10.49 | **49.9** | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,995 | **0.41** | 1.71 | 2.29 | 3.26 | 5.50 | 62.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,995 | 0.73 | 1.11 | 1.21 | 1.55 | 6.05 | 52.3 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,992 | 0.45 | 1.25 | 2.56 | 4.79 | 8.52 | 59.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,993 | 0.72 | 1.06 | 1.14 | **1.32** | 6.75 | 53.1 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,994 | 0.50 | **0.83** | **0.95** | 1.92 | 7.89 | 55.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,998 | 0.48 | 0.70 | 0.80 | 2.03 | 3.68 | 164.7 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,998 | **0.43** | 0.69 | 0.76 | 0.91 | **2.14** | **77.0** | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,999 | 0.49 | 1.73 | 2.68 | 4.21 | 6.39 | 182.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,997 | **0.43** | **0.65** | **0.72** | **0.86** | 3.29 | 85.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,998 | 0.56 | 0.94 | 1.45 | 4.63 | 7.03 | 176.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,998 | 0.74 | 1.19 | 1.37 | 1.66 | 2.32 | 120.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | 0.52 | 0.83 | 0.92 | 2.39 | 3.89 | 174.1 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,994 | 0.60 | **1.04** | **1.20** | 2.49 | 5.80 | 62.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,997 | 0.69 | 1.10 | 1.24 | 2.24 | **5.18** | 54.6 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,991 | 0.76 | 1.99 | 2.49 | 3.80 | 16.18 | 80.6 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,993 | 0.71 | 1.08 | **1.20** | 2.15 | 6.47 | 51.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,995 | **0.56** | 1.25 | 2.31 | 4.13 | 13.50 | 66.6 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,992 | 0.64 | 1.14 | 1.24 | **1.42** | 7.51 | **44.2** | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,993 | 0.61 | 1.10 | 1.34 | 2.48 | 6.16 | 63.5 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).
8 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,054 | 71.2 | 0.0 | 0 | 26.24 | 0.53 | 30 | 17 |
| HTTPS HTTP/1.1, proxied | 4,620 | 64.4 | 0.0 | 0 | 26.99 | 0.42 | 37 | 7 |
| HTTPS HTTP/2, proxied | 4,514 | 51.5 | 0.0 | 0 | 28.66 | 0.68 | 26 | 7 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,070 | 56.4 | 0.0 | 0 | 28.77 | 0.85 | 31 | 7 |
| HTTP/1.1 plain, 64 KiB body, proxied | 5,464 | 32.2 | 0.0 | 0 | 17.56 | 0.63 | 43 | 7 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 18,421 | 18.3 | 0.0 | 0 | 11.40 | 0.72 | 18 | 7 |
| HTTP/1.1 plain, answered by the proxy | 897 | 70.6 | 0.0 | 0 | 17.13 | 0.25 | 41 | 7 |
| HTTP/1.1 plain, access log on, proxied | 4,528 | 62.0 | 9.9 | 5 | 33.80 | 0.94 | 22 | 6 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 4,068 | 70.6 | 0.0 | 0 | 26.61 | 0.48 | 49 | 6 |
| HTTP/1.1 plain, every request refused (429) | 898 | 69.8 | 0.0 | 0 | 20.38 | 0.32 | 53 | 6 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 4,067 | 78.4 | 0.0 | 0 | 26.13 | 0.42 | 36 | 6 |
| HTTP/1.1 plain, closed upstream, the built-in 502 page | 87,070 | 95.1 | 20.6 | 0 | 38.49 | 0.51 | 57 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 4,464 | 69.5 | 0.0 | 0 | 25.29 | 0.51 | 60 | 24 |
| h1 at 10,000 req/s (run 1) | 4,049 | 5.3 | 0.0 | 0 | 8.33 | 1.71 | 3 | 9 |
| h1 at 30,000 req/s (run 1) | 4,049 | 10.9 | 0.0 | 0 | 15.96 | 1.78 | 7 | 7 |
| h2-tls at 10,000 req/s (run 1) | 4,519 | 6.0 | 0.0 | 0 | 9.75 | 1.82 | 6 | 7 |
| h2-tls at 30,000 req/s (run 1) | 4,521 | 18.6 | 0.0 | 0 | 23.34 | 1.45 | 20 | 7 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 64 | 26 |
| nginx | 57 | 76 |
| Caddy | 74 | 107 |
| HAProxy | 59 | 87 |
| Traefik | 30 | 60 |
| Envoy | 38 | 17 |
| YARP | 49 | 47 |
