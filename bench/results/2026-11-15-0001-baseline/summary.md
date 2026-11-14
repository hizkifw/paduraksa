### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 166,986 | **327,129** | 52,138 | 225,244 | 98,133 | 94,980 | 163,559 | 0.51 |
| HTTPS HTTP/1.1, proxied | 135,991 | **285,727** | 59,890 | 119,443 | 93,609 | 100,384 | 130,454 | 0.48 |
| HTTPS HTTP/2, proxied | 145,731 | **241,924** | 40,762 | 123,344 | 57,309 | 104,549 | 147,755 | 0.60 |
| HTTPS HTTP/2, browser-like headers, proxied | 90,706 | **161,995** | 24,699 | 116,756 | 35,991 | 67,266 | 92,601 | 0.56 |
| HTTP/1.1 plain, 64 KiB body, proxied | 69,663 | 71,148 | 36,718 | **83,141** | 49,457 | 56,254 | 61,326 | 0.98 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 63,169 | **115,264** | 26,820 | 69,634 | 39,854 | 61,869 | 59,505 | 0.55 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 9,920 | 8,880 | 9,981 | 7,296 | 10,591 | **12,722** | 9,724 | 1.12 |
| WebSocket echo, proxied (echo round trips) | 221,915 | **429,982** | 232,586 | 213,958 | 236,709 | 249,720 | 201,592 | 0.52 |
| HTTP/1.1 plain, answered by the proxy | 378,387 | 418,539 | 119,654 | **524,295** | - | 175,468 | 372,011 | 0.90 |
| HTTP/1.1 plain, access log on, proxied | 138,861 | **286,561** | 47,421 | 183,564 | 61,273 | 94,659 | 140,793 | 0.48 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 163,235 | **297,845** | - | 224,256 | 87,558 | 98,195 | 156,548 | 0.55 |
| HTTP/1.1 plain, every request refused (429) | 370,912 | 437,769 | - | **440,164** | 219,259 | 233,545 | 369,057 | 0.85 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 147,701 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 16,394 | **201,099** | 31,591 | 175,432 | 44,969 | 82,342 | 20,092 | 0.08 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 62,086 | 27,152 | 23,641 | **73,192** | 29,640 | 42,268 | 61,383 | 2.29 |
| HTTP/1.1 plain, request and response header rules, proxied | 145,969 | **289,417** | 45,001 | 215,231 | 80,752 | 89,686 | 144,294 | 0.50 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 154,962 | **287,992** | 45,272 | 224,578 | 83,223 | 96,869 | 157,432 | 0.54 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 144,582 | **273,207** | 46,870 | 212,912 | - | - | - | 0.53 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 148,919 | **276,581** | 43,052 | 214,250 | - | 90,427 | 147,301 | 0.54 |

Upstream alone (HTTP/1.1, same concurrency): 988,062 requests/s.

WebSocket echo server alone (same connections and message size): 546,708 echo round trips/s.

For `websocket`, a request is one echo round trip (a message each way on one of the open connections), so the
throughput is echoes/s and the CPU per request is per echo; the status breakdown counts the upgrades (101), one per
connection.

For `rate-limited` and `error-page` and `rewrite-redirect`, the number is every response, 429s, 502s or 302s included: those scenarios measure what the proxy
answers when it refuses, fails or redirects, so counting only 2xx would report zero.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 20.0 (3.34) | **9.4 (3.06)** | 69.5 (3.62) | 13.8 (3.11) | 36.7 (3.61) | 38.3 (3.63) | 20.3 (3.31) |
| HTTPS HTTP/1.1, proxied | 25.0 (3.40) | **11.1 (3.16)** | 60.3 (3.61) | 25.9 (3.09) | 38.3 (3.59) | 36.4 (3.66) | 25.4 (3.32) |
| HTTPS HTTP/2, proxied | 24.2 (3.53) | **13.4 (3.24)** | 89.4 (3.64) | 25.1 (3.09) | 62.5 (3.58) | 35.2 (3.68) | 23.8 (3.51) |
| HTTPS HTTP/2, browser-like headers, proxied | 29.9 (3.62) | **15.2 (3.28)** | 110.8 (3.65) | 19.8 (3.09) | 76.1 (3.65) | 40.7 (3.65) | 29.0 (3.58) |
| HTTP/1.1 plain, 64 KiB body, proxied | 49.1 (3.42) | 42.0 (2.99) | 95.5 (3.51) | **38.1 (3.17)** | 69.6 (3.44) | 62.1 (3.50) | 55.9 (3.43) |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 53.7 (3.39) | **28.7 (3.30)** | 129.8 (3.48) | 43.0 (3.00) | 87.1 (3.47) | 58.2 (3.60) | 57.1 (3.39) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 377.8 (3.75) | 371.0 (3.29) | 368.2 (3.68) | 502.0 (3.66) | 350.6 (3.71) | **241.2 (3.07)** | 378.9 (3.68) |
| WebSocket echo, proxied (echo round trips) | 12.4 (2.75) | **4.5 (1.93)** | 6.7 (1.55) | 6.9 (1.47) | 6.7 (1.59) | 9.1 (2.26) | 13.2 (2.65) |
| HTTP/1.1 plain, answered by the proxy | 7.9 (2.98) | 7.5 (3.15) | 29.6 (3.54) | **6.0 (3.17)** | - | 15.3 (2.68) | 8.0 (2.98) |
| HTTP/1.1 plain, access log on, proxied | 24.4 (3.39) | **10.1 (2.90)** | 76.3 (3.62) | 17.1 (3.15) | 59.9 (3.67) | 38.3 (3.62) | 24.6 (3.46) |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 20.6 (3.36) | **9.8 (2.93)** | - | 14.3 (3.22) | 41.1 (3.60) | 36.9 (3.62) | 21.3 (3.34) |
| HTTP/1.1 plain, every request refused (429) | 8.1 (3.02) | **7.6 (3.32)** | - | **7.6 (3.35)** | 16.1 (3.53) | 15.1 (3.53) | 8.3 (3.08) |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 22.7 (3.35) | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 232.1 (3.81) | **15.2 (3.07)** | 114.4 (3.61) | 17.8 (3.13) | 78.7 (3.54) | 42.9 (3.53) | 188.7 (3.79) |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 60.1 (3.73) | 140.3 (3.81) | 160.7 (3.80) | **49.9 (3.65)** | 126.0 (3.73) | 88.4 (3.74) | 59.6 (3.66) |
| HTTP/1.1 plain, request and response header rules, proxied | 23.1 (3.37) | **10.2 (2.96)** | 81.4 (3.66) | 14.8 (3.18) | 43.8 (3.53) | 40.6 (3.64) | 23.2 (3.35) |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 21.5 (3.33) | **10.2 (2.95)** | 78.3 (3.54) | 14.1 (3.17) | 42.9 (3.57) | 37.3 (3.61) | 21.2 (3.34) |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 22.6 (3.27) | **10.5 (2.88)** | 76.8 (3.60) | 15.0 (3.20) | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 22.7 (3.38) | **10.7 (2.97)** | 82.6 (3.55) | 15.1 (3.24) | - | 40.6 (3.67) | 22.8 (3.36) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 166,986 | 0.37 | 0.43 | 0.80 | 1.90 | 0 |
| HTTP/1.1 plain, proxied | nginx | **327,129** | **0.19** | **0.25** | **0.29** | **0.67** | 0 |
| HTTP/1.1 plain, proxied | Caddy | 52,138 | 0.92 | 2.73 | 4.36 | 6.12 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 225,244 | 0.25 | 0.36 | 0.97 | 1.88 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 98,133 | 0.53 | 1.12 | 3.12 | 4.35 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 94,980 | 0.63 | 0.92 | 1.33 | 1.86 | 0 |
| HTTP/1.1 plain, proxied | YARP | 163,559 | 0.37 | 0.44 | 0.89 | 1.90 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 135,991 | 0.45 | 0.52 | 1.00 | 1.99 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | **285,727** | **0.22** | **0.26** | **0.30** | **0.66** | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 59,890 | 0.80 | 2.39 | 3.94 | 5.30 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | 119,443 | 0.50 | 0.72 | 1.41 | 2.28 | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 93,609 | 0.56 | 1.17 | 3.26 | 4.63 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 100,384 | 0.61 | 0.87 | 1.09 | 1.27 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 130,454 | 0.46 | 0.57 | 1.07 | 2.09 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 145,731 | 0.42 | 0.51 | 1.12 | 1.79 | 0 |
| HTTPS HTTP/2, proxied | nginx | **241,924** | **0.23** | **0.39** | **0.44** | **0.81** | 0 |
| HTTPS HTTP/2, proxied | Caddy | 40,762 | 1.33 | 3.00 | 4.59 | 6.96 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 123,344 | 0.47 | 0.71 | 1.44 | 2.28 | 0 |
| HTTPS HTTP/2, proxied | Traefik | 57,309 | 0.98 | 1.85 | 3.86 | 5.36 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 104,549 | 0.59 | 0.93 | 1.16 | 1.66 | 0 |
| HTTPS HTTP/2, proxied | YARP | 147,755 | 0.41 | 0.51 | 1.07 | 1.80 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 90,706 | 0.50 | 0.61 | 1.33 | 2.10 | 302321 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | **161,995** | **0.18** | 0.66 | **0.73** | **0.96** | 541191 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 24,699 | 1.64 | 3.70 | 5.50 | 8.31 | 82438 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 116,756 | 0.36 | **0.58** | 1.45 | 2.56 | 388851 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 35,991 | 1.16 | 2.22 | 4.59 | 6.12 | 120312 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 67,266 | 0.77 | 0.93 | 1.30 | 1.84 | 223724 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 92,601 | 0.49 | 0.62 | 1.17 | 1.76 | 309202 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 69,663 | 0.89 | 1.15 | **1.77** | **3.00** | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 71,148 | 0.82 | **1.09** | 2.74 | 3.72 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 36,718 | 1.34 | 3.76 | 6.39 | 8.33 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | **83,141** | **0.68** | 1.17 | 2.14 | 3.15 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 49,457 | 1.11 | 2.26 | 4.61 | 6.69 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 56,254 | 1.05 | 1.58 | 2.37 | 3.29 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 61,326 | 1.01 | 1.33 | 1.99 | 3.02 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 63,169 | 0.99 | 1.18 | 1.82 | 2.94 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | **115,264** | **0.54** | **0.73** | **1.20** | 2.12 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 26,820 | 1.90 | 4.84 | 7.35 | 10.82 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 69,634 | 0.83 | 1.32 | 2.45 | 4.02 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 39,854 | 1.24 | 3.34 | 5.86 | 7.75 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 61,869 | 1.01 | 1.27 | 1.52 | **1.85** | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 59,505 | 1.05 | 1.29 | 1.89 | 3.55 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 9,920 | 1.56 | **1.94** | **2.89** | 4.03 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 8,880 | 1.69 | 3.02 | 4.23 | 5.16 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 9,981 | 1.46 | 2.47 | 4.17 | 5.52 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 7,296 | 2.12 | 2.64 | 4.01 | 6.02 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 10,591 | 1.37 | 2.30 | 4.27 | 6.13 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | **12,722** | **1.11** | 2.26 | 3.01 | **3.51** | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 9,724 | 1.58 | 2.02 | 2.98 | 4.24 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 221,915 | 0.25 | 0.44 | 0.78 | 1.66 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | **429,982** | **0.13** | **0.17** | **0.77** | 2.47 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 232,586 | 0.21 | 0.51 | 1.12 | 1.78 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 213,958 | 0.17 | 0.61 | 1.75 | 2.89 | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 236,709 | 0.21 | 0.51 | 1.04 | 1.67 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 249,720 | 0.21 | 0.39 | 1.04 | 2.20 | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 201,592 | 0.27 | 0.51 | 0.80 | **1.22** | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 378,387 | 0.16 | 0.20 | 0.52 | 2.22 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 418,539 | 0.14 | **0.17** | 0.54 | 2.09 | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 119,654 | 0.35 | 1.19 | 3.04 | 4.34 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | **524,295** | **0.10** | 0.19 | **0.47** | **1.02** | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 175,468 | 0.27 | 0.38 | 2.14 | 2.67 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 372,011 | 0.16 | 0.21 | 0.54 | 2.07 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 138,861 | 0.40 | 0.62 | 1.43 | 2.84 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | **286,561** | **0.20** | **0.29** | 0.93 | 1.97 | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 47,421 | 1.03 | 2.95 | 4.90 | 6.97 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 183,564 | 0.33 | 0.43 | **0.85** | 1.73 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 61,273 | 0.76 | 2.14 | 4.87 | 7.04 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 94,659 | 0.63 | 0.89 | 1.12 | **1.30** | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 140,793 | 0.42 | 0.55 | 1.02 | 2.03 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 163,235 | 0.38 | 0.44 | 0.86 | 1.88 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | **297,845** | **0.20** | **0.29** | 0.76 | 1.60 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 224,256 | 0.26 | 0.36 | **0.68** | **1.21** | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 87,558 | 0.59 | 1.29 | 3.51 | 4.89 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 98,195 | 0.67 | 0.86 | 1.01 | 1.43 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 156,548 | 0.39 | 0.46 | 0.91 | 1.92 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 370,912 | 0.16 | 0.20 | 0.60 | 2.44 | 3709896 |
| HTTP/1.1 plain, every request refused (429) | nginx | 437,769 | 0.15 | **0.17** | 0.34 | 0.76 | 4378840 |
| HTTP/1.1 plain, every request refused (429) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, every request refused (429) | HAProxy | **440,164** | **0.13** | 0.23 | **0.32** | **0.68** | 4402535 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 219,259 | 0.20 | 0.59 | 1.53 | 4.18 | 2193251 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 233,545 | 0.24 | 0.41 | 0.59 | 2.06 | 2335867 |
| HTTP/1.1 plain, every request refused (429) | YARP | 369,057 | 0.16 | 0.21 | 0.49 | 1.97 | 3691244 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 147,701 | 0.42 | 0.48 | 0.89 | 2.13 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | nginx | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | HAProxy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Envoy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | YARP | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 16,394 | 3.68 | 5.80 | 9.08 | 13.01 | 163976 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | **201,099** | **0.31** | **0.38** | **0.63** | **1.02** | 2011662 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 31,591 | 1.61 | 4.24 | 6.83 | 8.96 | 316015 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 175,432 | 0.35 | 0.42 | 0.75 | 1.18 | 1754905 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 44,969 | 1.20 | 2.42 | 5.12 | 6.73 | 449778 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 82,342 | 0.81 | 0.95 | 1.15 | 1.92 | 823629 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 20,092 | 3.04 | 4.44 | 7.23 | 9.95 | 200962 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Paduraksa | 62,086 | 1.00 | 1.28 | 2.06 | 2.94 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | nginx | 27,152 | 2.38 | 2.58 | 3.07 | 3.78 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Caddy | 23,641 | 2.33 | 5.00 | 8.51 | 12.08 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | HAProxy | **73,192** | **0.84** | **1.00** | **1.40** | **1.86** | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Traefik | 29,640 | 1.91 | 3.88 | 6.56 | 9.29 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Envoy | 42,268 | 1.39 | 2.17 | 2.57 | 2.93 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | YARP | 61,383 | 1.02 | 1.30 | 1.84 | 2.55 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Paduraksa | 145,969 | 0.42 | 0.49 | 1.17 | 2.02 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | nginx | **289,417** | **0.20** | **0.27** | 0.85 | 1.69 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Caddy | 45,001 | 1.08 | 3.16 | 5.14 | 6.49 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | HAProxy | 215,231 | 0.27 | 0.37 | **0.77** | **1.31** | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Traefik | 80,752 | 0.64 | 1.40 | 3.69 | 5.07 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Envoy | 89,686 | 0.69 | 0.86 | 1.26 | 1.53 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | YARP | 144,294 | 0.42 | 0.50 | 0.96 | 1.97 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Paduraksa | 154,962 | 0.39 | 0.46 | 1.16 | 2.25 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | nginx | **287,992** | **0.19** | **0.30** | 0.85 | 1.78 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Caddy | 45,272 | 1.04 | 3.17 | 5.47 | 7.02 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | HAProxy | 224,578 | 0.26 | 0.36 | **0.74** | **1.20** | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Traefik | 83,223 | 0.61 | 1.37 | 3.63 | 5.01 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Envoy | 96,869 | 0.64 | 0.81 | 1.14 | 1.55 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | YARP | 157,432 | 0.38 | 0.46 | 0.90 | 1.89 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Paduraksa | 144,582 | 0.41 | 0.49 | 1.29 | 2.28 | 1446166 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | nginx | **273,207** | **0.20** | **0.29** | 1.13 | 2.08 | 2732604 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Caddy | 46,870 | 1.02 | 3.06 | 5.13 | 6.44 | 468822 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | HAProxy | 212,912 | 0.27 | 0.38 | **0.80** | **1.41** | 2129718 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Envoy | - | - | - | - | - | - |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | YARP | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 148,919 | 0.41 | 0.49 | 1.02 | 2.06 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | **276,581** | **0.21** | **0.28** | 0.85 | 1.75 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 43,052 | 1.16 | 3.14 | 4.74 | 6.11 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 214,250 | 0.27 | 0.38 | **0.77** | 1.55 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 90,427 | 0.69 | 0.91 | 1.04 | **1.30** | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 147,301 | 0.41 | 0.49 | 0.97 | 2.06 | 0 |

### Response status codes at fixed concurrency

A pair is listed when it isn't all 200, and so are the upload, WebSocket and extra-host scenarios even when
they are (101 is one per upgraded WebSocket connection).

| Scenario | Proxy | 101 | 200 | 302 | 404 | 429 | 502 |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 0 | 907,306 | 0 | 302,321 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | 0 | 1,620,419 | 0 | 541,191 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 0 | 247,078 | 0 | 82,438 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 0 | 1,167,840 | 0 | 388,851 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 0 | 359,987 | 0 | 120,312 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 0 | 672,840 | 0 | 223,724 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 0 | 926,226 | 0 | 309,202 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 0 | 631,820 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | 0 | 1,152,837 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 0 | 268,247 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 0 | 696,546 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 0 | 398,593 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 0 | 618,829 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 0 | 595,245 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 64 | 0 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 0 | 1,388,978 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | 0 | 2,866,318 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 0 | 474,362 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 0 | 1,836,116 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 0 | 612,864 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 0 | 946,805 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 0 | 1,408,170 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 0 | 1,632,782 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | 0 | 2,979,042 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 0 | 2,243,039 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 0 | 875,701 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 0 | 982,133 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 0 | 1,565,923 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 0 | 0 | 0 | 0 | 3,709,896 | 0 |
| HTTP/1.1 plain, every request refused (429) | nginx | 0 | 0 | 0 | 0 | 4,378,840 | 0 |
| HTTP/1.1 plain, every request refused (429) | HAProxy | 0 | 0 | 0 | 0 | 4,402,535 | 0 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 0 | 0 | 0 | 0 | 2,193,251 | 0 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 0 | 0 | 0 | 0 | 2,335,867 | 0 |
| HTTP/1.1 plain, every request refused (429) | YARP | 0 | 0 | 0 | 0 | 3,691,244 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 0 | 1,477,506 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 0 | 0 | 0 | 0 | 0 | 163,976 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | 0 | 0 | 0 | 0 | 0 | 2,011,662 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 0 | 0 | 0 | 0 | 0 | 316,015 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 0 | 0 | 0 | 0 | 0 | 1,754,905 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 0 | 0 | 0 | 0 | 0 | 449,778 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 0 | 0 | 0 | 0 | 0 | 823,629 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 0 | 0 | 0 | 0 | 0 | 200,962 |
| HTTP/1.1 plain, request and response header rules, proxied | Paduraksa | 0 | 1,460,191 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | nginx | 0 | 2,894,863 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Caddy | 0 | 450,108 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | HAProxy | 0 | 2,152,817 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Traefik | 0 | 807,710 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Envoy | 0 | 896,979 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | YARP | 0 | 1,443,195 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Paduraksa | 0 | 1,549,961 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | nginx | 0 | 2,880,347 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Caddy | 0 | 452,794 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | HAProxy | 0 | 2,246,204 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Traefik | 0 | 832,423 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Envoy | 0 | 968,996 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | YARP | 0 | 1,574,616 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Paduraksa | 0 | 0 | 1,446,166 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | nginx | 0 | 0 | 2,732,604 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Caddy | 0 | 0 | 468,822 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | HAProxy | 0 | 0 | 2,129,718 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 0 | 1,489,415 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | 0 | 2,766,395 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 0 | 430,634 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 0 | 2,142,952 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 0 | 904,490 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 0 | 1,473,346 | 0 | 0 | 0 | 0 |

`rate-limit` answered no 429s anywhere: every policy stayed inside its budget, so the runs measure
the cost of the check on one hot client, as intended.

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,998 | 0.32 | 0.65 | 0.78 | 1.60 | 5.07 | 115.3 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,998 | 0.17 | 0.46 | 0.64 | 0.96 | 1.66 | 41.8 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,997 | 0.25 | 0.53 | 0.71 | 1.34 | 2.65 | 81.8 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,997 | **0.14** | 0.33 | 0.48 | 0.80 | **1.23** | **22.3** | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,998 | 0.16 | **0.30** | **0.37** | **0.66** | 1.47 | 49.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,997 | 0.27 | 0.46 | 0.53 | 0.76 | 1.33 | 49.5 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,998 | 0.20 | 0.62 | 0.84 | 1.29 | 2.71 | 94.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,995 | 0.43 | 0.81 | 1.11 | 3.87 | 10.43 | 48.5 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,992 | 0.35 | 1.30 | 1.66 | 3.27 | 7.76 | 40.1 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,995 | 0.68 | 2.27 | 2.89 | 4.15 | 5.81 | 73.4 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,994 | **0.25** | **0.59** | **0.77** | **1.22** | **1.91** | **16.5** | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,994 | 0.35 | 0.76 | 1.19 | 2.85 | 5.05 | 41.1 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,993 | 0.49 | 0.78 | 0.93 | 1.38 | 2.35 | 38.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,992 | 0.37 | 1.74 | 2.85 | 4.73 | 6.54 | 48.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,997 | 0.45 | 0.68 | 0.80 | 2.15 | 7.68 | 153.3 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,999 | 0.20 | 0.48 | 0.66 | 1.14 | 2.80 | 38.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,998 | 0.37 | 0.78 | 1.06 | 2.11 | 7.66 | 104.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,998 | **0.18** | **0.39** | 0.56 | 1.02 | 4.02 | **28.5** | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,997 | 0.30 | 0.61 | 0.78 | 1.31 | 3.64 | 78.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,998 | 0.26 | 0.42 | **0.49** | **0.69** | **2.38** | 47.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | 0.27 | 0.82 | 1.13 | 2.41 | 4.93 | 111.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,995 | 0.53 | 0.97 | 1.20 | 4.57 | 9.63 | 55.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,997 | 0.39 | 1.33 | 2.21 | 4.15 | 6.94 | 35.8 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,997 | 1.24 | 3.62 | 4.94 | 16.04 | 26.56 | 91.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,994 | **0.31** | **0.69** | **0.88** | 1.44 | 4.98 | **19.5** | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,994 | 0.76 | 1.92 | 2.82 | 4.92 | 11.55 | 66.6 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,997 | 0.49 | 0.81 | 0.95 | **1.36** | **4.52** | 36.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,992 | 0.48 | 2.30 | 3.64 | 6.38 | 9.59 | 56.8 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (docs/design/hot-path.md, Budget and rules, tracks these here; the gate's own share is asserted 0 B in tests).
8 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,064 | 64.6 | 0.0 | 0 | 21.90 | 0.37 | 39 | 14 |
| HTTPS HTTP/1.1, proxied | 4,619 | 60.9 | 0.0 | 0 | 26.50 | 0.46 | 46 | 7 |
| HTTPS HTTP/2, proxied | 4,531 | 48.1 | 0.0 | 0 | 28.55 | 0.77 | 24 | 7 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,108 | 37.0 | 0.0 | 0 | 27.04 | 0.75 | 38 | 7 |
| HTTP/1.1 plain, 64 KiB body, proxied | 5,494 | 35.1 | 0.0 | 0 | 16.89 | 0.54 | 59 | 7 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 4,725 | 42.8 | 0.0 | 0 | 25.40 | 0.59 | 48 | 7 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 19,301 | 18.4 | 0.0 | 0 | 15.30 | 0.94 | 19 | 7 |
| WebSocket echo, proxied (echo round trips) | 0 | 0.0 | 0.0 | 0 | 0.00 | 0.00 | 46 | 7 |
| HTTP/1.1 plain, answered by the proxy | 894 | 66.8 | 0.0 | 0 | 20.09 | 0.40 | 61 | 7 |
| HTTP/1.1 plain, access log on, proxied | 4,564 | 54.0 | 12.3 | 4 | 38.87 | 1.91 | 27 | 7 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 4,073 | 66.4 | 0.0 | 0 | 25.95 | 0.51 | 34 | 7 |
| HTTP/1.1 plain, every request refused (429) | 894 | 65.4 | 0.0 | 0 | 23.30 | 0.51 | 60 | 7 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 4,065 | 58.4 | 0.0 | 0 | 22.03 | 0.41 | 43 | 7 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 100,707 | 108.3 | 28.7 | 1 | 105.22 | 2.63 | 58 | 7 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 5,777 | 33.8 | 0.1 | 0 | 24.57 | 0.78 | 53 | 7 |
| HTTP/1.1 plain, request and response header rules, proxied | 3,119 | 26.5 | 0.0 | 0 | 16.97 | 0.80 | 41 | 7 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 4,312 | 33.6 | 0.0 | 0 | 21.90 | 0.74 | 34 | 7 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 4,576 | 50.8 | 0.0 | 0 | 46.29 | 1.16 | 33 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 4,469 | 56.1 | 0.0 | 0 | 26.60 | 0.54 | 38 | 14 |
| h1 at 10,000 req/s (run 1) | 4,050 | 5.2 | 0.0 | 0 | 4.85 | 1.42 | 9 | 8 |
| h1 at 30,000 req/s (run 1) | 4,049 | 22.9 | 0.0 | 0 | 21.42 | 1.21 | 31 | 7 |
| h2-tls at 10,000 req/s (run 1) | 4,528 | 4.3 | 0.0 | 0 | 6.53 | 1.72 | 2 | 7 |
| h2-tls at 30,000 req/s (run 1) | 4,523 | 13.6 | 0.1 | 1 | 16.97 | 1.77 | 3 | 7 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 51 | 9 |
| nginx | 62 | 71 |
| Caddy | 104 | 124 |
| HAProxy | 63 | 315 |
| Traefik | 51 | 176 |
| Envoy | 51 | 118 |
| YARP | 64 | 97 |
