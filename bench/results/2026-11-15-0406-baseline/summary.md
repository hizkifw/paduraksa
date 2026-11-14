### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 168,782 | **322,706** | 60,152 | 245,055 | 103,102 | 108,897 | 171,595 | 0.52 |
| HTTPS HTTP/1.1, proxied | 133,543 | **304,431** | 68,819 | 138,756 | 101,424 | 101,467 | 139,298 | 0.44 |
| HTTPS HTTP/2, proxied | 148,311 | **249,543** | 45,703 | 148,457 | 62,255 | 108,374 | 156,991 | 0.59 |
| HTTPS HTTP/2, browser-like headers, proxied | 91,616 | **168,252** | 27,990 | 97,052 | 39,701 | 69,780 | 97,156 | 0.54 |
| HTTP/1.1 plain, 64 KiB body, proxied | 73,795 | 77,446 | 44,996 | **98,931** | 59,521 | 67,895 | 74,094 | 0.95 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 64,174 | **121,629** | 33,317 | 78,759 | 48,413 | 67,749 | 63,828 | 0.53 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 10,028 | 9,405 | 11,131 | 7,793 | 11,553 | **13,796** | 10,058 | 1.07 |
| WebSocket echo, proxied (echo round trips) | 260,131 | **507,318** | 387,463 | 414,735 | 394,679 | 331,975 | 214,590 | 0.51 |
| HTTP/1.1 plain, answered by the proxy | 383,897 | 472,334 | 138,689 | **548,025** | - | 239,975 | 386,949 | 0.81 |
| HTTP/1.1 plain, access log on, proxied | 144,775 | **320,174** | 55,271 | 193,301 | 67,822 | 100,960 | 148,147 | 0.45 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 168,281 | **322,888** | - | 233,219 | 97,150 | 105,625 | 166,449 | 0.52 |
| HTTP/1.1 plain, every request refused (429) | 383,363 | **461,236** | - | 446,066 | 240,292 | 246,774 | 380,846 | 0.83 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 151,798 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 20,101 | **212,454** | 37,289 | 188,347 | 52,137 | 84,685 | 21,502 | 0.09 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 63,067 | 28,641 | 26,586 | **77,352** | 33,225 | 46,845 | 63,806 | 2.20 |
| HTTP/1.1 plain, request and response header rules, proxied | 152,442 | **306,031** | 51,791 | 225,059 | 92,132 | 96,984 | 152,894 | 0.50 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 160,689 | **311,378** | 56,145 | 237,028 | 95,021 | 105,197 | 163,912 | 0.52 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 157,341 | **305,107** | 55,179 | 228,797 | - | - | - | 0.52 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 154,205 | **315,477** | 50,885 | 225,981 | - | 97,184 | 156,466 | 0.49 |

Upstream alone (HTTP/1.1, same concurrency): 973,600 requests/s.

WebSocket echo server alone (same connections and message size): 560,802 echo round trips/s.

For `websocket`, a request is one echo round trip (a message each way on one of the open connections), so the
throughput is echoes/s and the CPU per request is per echo; the status breakdown counts the upgrades (101), one per
connection.

For `rate-limited` and `error-page` and `rewrite-redirect`, the number is every response, 429s, 502s or 302s included: those scenarios measure what the proxy
answers when it refuses, fails or redirects, so counting only 2xx would report zero.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 19.8 (3.34) | **9.7 (3.12)** | 62.0 (3.73) | 13.4 (3.29) | 34.9 (3.60) | 33.5 (3.65) | 19.1 (3.28) |
| HTTPS HTTP/1.1, proxied | 24.6 (3.28) | **10.3 (3.15)** | 53.7 (3.69) | 23.4 (3.25) | 35.5 (3.60) | 36.1 (3.66) | 24.2 (3.37) |
| HTTPS HTTP/2, proxied | 23.8 (3.53) | **13.1 (3.26)** | 81.2 (3.71) | 21.9 (3.25) | 58.8 (3.66) | 34.6 (3.75) | 22.5 (3.53) |
| HTTPS HTTP/2, browser-like headers, proxied | 29.9 (3.65) | **14.7 (3.29)** | 100.6 (3.75) | 26.2 (3.39) | 69.5 (3.68) | 39.4 (3.66) | 27.6 (3.57) |
| HTTP/1.1 plain, 64 KiB body, proxied | 47.1 (3.48) | 41.4 (3.20) | 79.0 (3.56) | **34.5 (3.41)** | 57.3 (3.41) | 53.0 (3.60) | 46.8 (3.47) |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 52.9 (3.39) | **27.3 (3.32)** | 109.1 (3.64) | 40.9 (3.22) | 72.6 (3.52) | 53.4 (3.62) | 52.4 (3.35) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 375.2 (3.76) | 358.6 (3.37) | 340.2 (3.79) | 485.3 (3.78) | 329.0 (3.80) | **225.9 (3.12)** | 374.6 (3.77) |
| WebSocket echo, proxied (echo round trips) | 11.4 (2.97) | **4.3 (2.17)** | 5.6 (2.18) | 5.9 (2.46) | 5.5 (2.19) | 8.2 (2.72) | 12.6 (2.71) |
| HTTP/1.1 plain, answered by the proxy | 7.9 (3.02) | 7.1 (3.36) | 26.7 (3.71) | **5.9 (3.26)** | - | 15.2 (3.64) | 7.6 (2.93) |
| HTTP/1.1 plain, access log on, proxied | 24.2 (3.51) | **9.8 (3.12)** | 67.2 (3.72) | 16.7 (3.23) | 54.8 (3.72) | 36.4 (3.67) | 22.8 (3.38) |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 20.2 (3.40) | **9.6 (3.11)** | - | 14.2 (3.31) | 37.3 (3.63) | 34.7 (3.66) | 20.0 (3.33) |
| HTTP/1.1 plain, every request refused (429) | 8.2 (3.15) | **7.3 (3.36)** | - | 7.6 (3.37) | 14.9 (3.59) | 14.7 (3.63) | 7.9 (3.01) |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 22.6 (3.43) | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 190.6 (3.83) | **14.6 (3.10)** | 99.3 (3.70) | 17.0 (3.21) | 68.1 (3.55) | 42.5 (3.60) | 178.1 (3.83) |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 59.1 (3.73) | 136.0 (3.90) | 144.9 (3.85) | **48.2 (3.73)** | 115.5 (3.84) | 81.3 (3.81) | 57.9 (3.69) |
| HTTP/1.1 plain, request and response header rules, proxied | 22.0 (3.35) | **10.3 (3.15)** | 72.0 (3.73) | 14.9 (3.35) | 39.5 (3.64) | 38.1 (3.69) | 21.7 (3.32) |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 20.7 (3.32) | **10.1 (3.13)** | 66.4 (3.73) | 13.9 (3.30) | 38.3 (3.64) | 34.9 (3.67) | 20.1 (3.29) |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 21.3 (3.34) | **10.3 (3.16)** | 67.5 (3.72) | 14.6 (3.33) | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 21.9 (3.38) | **10.0 (3.14)** | 73.8 (3.75) | 14.8 (3.35) | - | 38.0 (3.69) | 21.5 (3.37) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 168,782 | 0.37 | 0.42 | 0.78 | 1.87 | 0 |
| HTTP/1.1 plain, proxied | nginx | **322,706** | **0.21** | **0.25** | **0.27** | **0.30** | 0 |
| HTTP/1.1 plain, proxied | Caddy | 60,152 | 0.81 | 2.36 | 3.70 | 4.60 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 245,055 | 0.25 | 0.32 | 0.38 | 0.39 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 103,102 | 0.50 | 1.08 | 3.01 | 4.22 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 108,897 | 0.58 | 0.70 | 0.76 | 0.79 | 0 |
| HTTP/1.1 plain, proxied | YARP | 171,595 | 0.36 | 0.41 | 0.78 | 2.16 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 133,543 | 0.46 | 0.53 | 1.08 | 2.66 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | **304,431** | **0.19** | **0.27** | **0.28** | **0.30** | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 68,819 | 0.71 | 2.06 | 3.33 | 4.37 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | 138,756 | 0.46 | 0.53 | 0.60 | 0.66 | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 101,424 | 0.51 | 1.09 | 3.04 | 4.31 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 101,467 | 0.62 | 0.76 | 0.97 | 1.00 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 139,298 | 0.44 | 0.51 | 0.94 | 2.20 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 148,311 | 0.41 | 0.50 | 1.01 | 1.47 | 0 |
| HTTPS HTTP/2, proxied | nginx | **249,543** | **0.28** | **0.34** | **0.37** | **0.40** | 0 |
| HTTPS HTTP/2, proxied | Caddy | 45,703 | 1.21 | 2.64 | 3.96 | 5.02 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 148,457 | 0.43 | 0.47 | 0.56 | 0.65 | 0 |
| HTTPS HTTP/2, proxied | Traefik | 62,255 | 0.90 | 1.70 | 3.58 | 4.79 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 108,374 | 0.62 | 0.76 | 0.94 | 1.01 | 0 |
| HTTPS HTTP/2, proxied | YARP | 156,991 | 0.39 | 0.47 | 0.93 | 1.42 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 91,616 | 0.50 | 0.62 | 1.18 | 1.79 | 305251 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | **168,252** | **0.28** | **0.44** | **0.48** | **0.51** | 560225 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 27,990 | 1.48 | 3.26 | 4.70 | 5.82 | 92773 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 97,052 | 0.49 | 0.54 | 0.65 | 0.76 | 322565 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 39,701 | 1.05 | 2.04 | 4.20 | 5.50 | 133099 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 69,780 | 0.78 | 1.34 | 1.59 | 1.72 | 231707 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 97,156 | 0.47 | 0.57 | 1.11 | 1.63 | 323018 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 73,795 | 0.85 | 1.04 | 1.54 | 2.64 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 77,446 | 0.83 | 0.93 | 0.99 | **1.05** | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 44,996 | 1.02 | 3.25 | 5.51 | 7.08 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | **98,931** | **0.64** | **0.68** | **0.88** | 1.90 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 59,521 | 0.92 | 1.87 | 4.03 | 5.83 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 67,895 | 0.91 | 1.14 | 1.25 | 1.40 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 74,094 | 0.85 | 1.04 | 1.51 | 2.43 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 64,174 | 0.98 | 1.15 | 1.70 | 2.83 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | **121,629** | **0.52** | **0.67** | **0.81** | 1.97 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 33,317 | 1.52 | 3.93 | 5.73 | 8.13 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 78,759 | 0.82 | 0.85 | 0.91 | 3.50 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 48,413 | 1.01 | 2.74 | 5.15 | 6.78 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 67,749 | 0.95 | 1.23 | 1.30 | **1.37** | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 63,828 | 0.98 | 1.17 | 1.69 | 2.95 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 10,028 | 1.55 | **1.90** | **2.64** | 3.87 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 9,405 | 1.59 | 2.91 | 4.03 | 4.77 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 11,131 | 1.32 | 2.22 | 3.64 | 4.65 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 7,793 | 2.04 | 2.38 | 2.67 | **3.01** | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 11,553 | 1.26 | 2.10 | 3.98 | 5.41 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | **13,796** | **1.01** | 2.13 | 2.78 | 3.18 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 10,058 | 1.54 | 1.92 | 2.66 | 3.96 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 260,131 | 0.22 | 0.33 | 0.58 | 1.22 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | **507,318** | **0.11** | 0.18 | 0.43 | 0.91 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 387,463 | 0.15 | 0.25 | 0.56 | 1.40 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 414,735 | 0.14 | **0.16** | 0.48 | **0.86** | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 394,679 | 0.14 | 0.25 | 0.57 | 1.28 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 331,975 | 0.18 | 0.23 | **0.36** | 0.87 | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 214,590 | 0.25 | 0.51 | 0.73 | 1.29 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 383,897 | 0.15 | 0.20 | 0.55 | 2.38 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 472,334 | 0.14 | 0.15 | **0.17** | **0.22** | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 138,689 | 0.31 | 1.02 | 2.57 | 3.62 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | **548,025** | **0.11** | **0.12** | 0.22 | 0.23 | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 239,975 | 0.27 | 0.32 | 0.35 | 0.44 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 386,949 | 0.16 | 0.19 | 0.43 | 2.22 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 144,775 | 0.39 | 0.57 | 1.39 | 2.81 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | **320,174** | **0.19** | **0.24** | **0.27** | **0.29** | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 55,271 | 0.88 | 2.51 | 4.18 | 9.19 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 193,301 | 0.33 | 0.35 | 0.45 | 0.51 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 67,822 | 0.65 | 1.98 | 4.80 | 7.05 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 100,960 | 0.60 | 1.07 | 1.27 | 1.34 | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 148,147 | 0.41 | 0.52 | 0.91 | 1.90 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 168,281 | 0.37 | 0.42 | 0.86 | 1.98 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | **322,888** | **0.19** | **0.27** | **0.30** | **0.32** | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 233,219 | 0.26 | 0.33 | 0.40 | 0.45 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 97,150 | 0.53 | 1.15 | 3.21 | 4.53 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 105,625 | 0.58 | 0.73 | 0.93 | 0.97 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 166,449 | 0.37 | 0.43 | 0.82 | 1.93 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 383,363 | 0.16 | 0.20 | 0.49 | 2.12 | 3834571 |
| HTTP/1.1 plain, every request refused (429) | nginx | **461,236** | 0.14 | 0.15 | **0.18** | **0.22** | 4613120 |
| HTTP/1.1 plain, every request refused (429) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, every request refused (429) | HAProxy | 446,066 | **0.13** | **0.14** | 0.27 | 0.28 | 4461843 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 240,292 | 0.19 | 0.53 | 1.39 | 3.84 | 2403674 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 246,774 | 0.28 | 0.31 | 0.33 | 0.43 | 2468452 |
| HTTP/1.1 plain, every request refused (429) | YARP | 380,846 | 0.16 | 0.20 | 0.44 | 2.11 | 3809098 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 151,798 | 0.41 | 0.47 | 0.86 | 1.48 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | nginx | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | HAProxy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Envoy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | YARP | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 20,101 | 3.04 | 4.40 | 6.95 | 9.70 | 201029 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | **212,454** | **0.30** | **0.35** | 0.41 | 0.47 | 2125101 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 37,289 | 1.36 | 3.63 | 5.82 | 7.35 | 372941 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 188,347 | 0.34 | 0.36 | **0.40** | **0.45** | 1883910 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 52,137 | 1.02 | 2.12 | 4.57 | 5.93 | 521460 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 84,685 | 0.69 | 1.14 | 1.19 | 1.22 | 847070 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 21,502 | 2.89 | 3.95 | 5.99 | 8.80 | 215089 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Paduraksa | 63,067 | 1.00 | 1.26 | 1.72 | 2.35 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | nginx | 28,641 | 2.46 | 2.66 | 2.76 | 2.90 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Caddy | 26,586 | 2.07 | 4.52 | 7.44 | 9.97 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | HAProxy | **77,352** | **0.82** | **0.84** | **0.90** | **1.06** | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Traefik | 33,225 | 1.69 | 3.51 | 5.92 | 8.22 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | Envoy | 46,845 | 1.48 | 2.00 | 2.17 | 2.30 | 0 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | YARP | 63,806 | 0.98 | 1.25 | 1.74 | 2.56 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Paduraksa | 152,442 | 0.40 | 0.46 | 0.93 | 2.19 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | nginx | **306,031** | **0.21** | **0.26** | **0.29** | 0.76 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Caddy | 51,791 | 0.94 | 2.73 | 4.49 | 5.55 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | HAProxy | 225,059 | 0.27 | 0.34 | 0.40 | **0.64** | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Traefik | 92,132 | 0.56 | 1.21 | 3.27 | 4.61 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Envoy | 96,984 | 0.67 | 0.85 | 0.91 | 0.94 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | YARP | 152,894 | 0.40 | 0.46 | 0.89 | 2.20 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Paduraksa | 160,689 | 0.38 | 0.44 | 0.90 | 2.11 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | nginx | **311,378** | **0.20** | **0.22** | **0.24** | **0.28** | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Caddy | 56,145 | 0.86 | 2.52 | 4.25 | 5.31 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | HAProxy | 237,028 | 0.26 | 0.33 | 0.39 | 0.48 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Traefik | 95,021 | 0.54 | 1.17 | 3.24 | 4.50 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Envoy | 105,197 | 0.58 | 0.77 | 0.99 | 1.03 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | YARP | 163,912 | 0.37 | 0.43 | 0.85 | 2.25 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Paduraksa | 157,341 | 0.39 | 0.45 | 0.93 | 1.96 | 1573694 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | nginx | **305,107** | **0.21** | **0.24** | **0.26** | **0.28** | 3051658 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Caddy | 55,179 | 0.87 | 2.56 | 4.36 | 5.48 | 551916 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | HAProxy | 228,797 | 0.27 | 0.34 | 0.41 | 0.44 | 2288423 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Envoy | - | - | - | - | - | - |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | YARP | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 154,205 | 0.39 | 0.47 | 0.95 | 2.47 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | **315,477** | **0.23** | **0.28** | **0.30** | **0.33** | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 50,885 | 0.99 | 2.65 | 3.82 | 4.76 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 225,981 | 0.27 | 0.35 | 0.41 | 0.42 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 97,184 | 0.59 | 0.86 | 1.11 | 1.16 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 156,466 | 0.39 | 0.46 | 0.93 | 2.42 | 0 |

### Response status codes at fixed concurrency

A pair is listed when it isn't all 200, and so are the upload, WebSocket and extra-host scenarios even when
they are (101 is one per upgraded WebSocket connection).

| Scenario | Proxy | 101 | 200 | 302 | 404 | 429 | 502 |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 0 | 916,414 | 0 | 305,251 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | 0 | 1,682,976 | 0 | 560,225 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 0 | 279,990 | 0 | 92,773 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 0 | 970,736 | 0 | 322,565 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 0 | 397,108 | 0 | 133,099 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 0 | 698,007 | 0 | 231,707 | 0 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 0 | 971,851 | 0 | 323,018 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 0 | 641,919 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | 0 | 1,216,480 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 0 | 333,224 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 0 | 787,741 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 0 | 484,222 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 0 | 677,592 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 0 | 638,455 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 64 | 0 | 0 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 64 | 0 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 0 | 1,448,035 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | 0 | 3,202,349 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 0 | 552,861 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 0 | 1,933,237 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 0 | 678,348 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 0 | 1,009,809 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 0 | 1,481,707 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 0 | 1,683,314 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | 0 | 3,229,237 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 0 | 2,332,866 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 0 | 971,682 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 0 | 1,056,474 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 0 | 1,664,789 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 0 | 0 | 0 | 0 | 3,834,571 | 0 |
| HTTP/1.1 plain, every request refused (429) | nginx | 0 | 0 | 0 | 0 | 4,613,120 | 0 |
| HTTP/1.1 plain, every request refused (429) | HAProxy | 0 | 0 | 0 | 0 | 4,461,843 | 0 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 0 | 0 | 0 | 0 | 2,403,674 | 0 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 0 | 0 | 0 | 0 | 2,468,452 | 0 |
| HTTP/1.1 plain, every request refused (429) | YARP | 0 | 0 | 0 | 0 | 3,809,098 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 0 | 1,518,199 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 0 | 0 | 0 | 0 | 0 | 201,029 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | 0 | 0 | 0 | 0 | 0 | 2,125,101 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 0 | 0 | 0 | 0 | 0 | 372,941 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 0 | 0 | 0 | 0 | 0 | 1,883,910 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 0 | 0 | 0 | 0 | 0 | 521,460 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 0 | 0 | 0 | 0 | 0 | 847,070 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 0 | 0 | 0 | 0 | 0 | 215,089 |
| HTTP/1.1 plain, request and response header rules, proxied | Paduraksa | 0 | 1,524,777 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | nginx | 0 | 3,060,871 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Caddy | 0 | 517,980 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | HAProxy | 0 | 2,251,041 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Traefik | 0 | 921,515 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | Envoy | 0 | 970,022 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, request and response header rules, proxied | YARP | 0 | 1,529,283 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Paduraksa | 0 | 1,607,159 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | nginx | 0 | 3,114,673 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Caddy | 0 | 561,557 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | HAProxy | 0 | 2,370,925 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Traefik | 0 | 950,370 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | Envoy | 0 | 1,052,107 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | YARP | 0 | 1,639,397 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Paduraksa | 0 | 0 | 1,573,694 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | nginx | 0 | 0 | 3,051,658 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | Caddy | 0 | 0 | 551,916 | 0 | 0 | 0 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | HAProxy | 0 | 0 | 2,288,423 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 0 | 1,542,438 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | 0 | 3,155,406 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 0 | 508,964 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 0 | 2,260,425 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 0 | 971,960 | 0 | 0 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 0 | 1,564,859 | 0 | 0 | 0 | 0 |

`rate-limit` answered no 429s anywhere: every policy stayed inside its budget, so the runs measure
the cost of the check on one hot client, as intended.

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,998 | 0.43 | 0.68 | 0.73 | 1.63 | 3.43 | 156.5 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,998 | **0.32** | **0.51** | **0.57** | **0.69** | 2.14 | 67.1 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,998 | 0.39 | 1.89 | 2.94 | 4.98 | 10.00 | 169.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,998 | 0.38 | 0.65 | 0.72 | 0.85 | **1.95** | **65.3** | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,998 | 0.46 | 0.80 | 1.07 | 4.85 | 7.68 | 150.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,999 | 0.66 | 1.11 | 1.29 | 1.61 | 2.58 | 113.0 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,997 | 0.38 | 0.62 | 0.68 | 1.52 | 2.71 | 159.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,993 | 0.55 | 0.98 | 1.16 | 2.26 | 12.24 | 58.8 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,996 | 0.51 | **0.81** | **0.91** | 1.21 | **3.09** | **44.6** | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,991 | **0.42** | 1.46 | 2.01 | 3.07 | 17.01 | 63.8 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,993 | 0.70 | 1.10 | 1.21 | 3.13 | 5.82 | 51.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,993 | 0.47 | 1.42 | 2.71 | 4.87 | 10.89 | 61.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,994 | 0.69 | 0.98 | 1.06 | **1.20** | 3.90 | 49.8 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,994 | 0.47 | **0.81** | 1.01 | 1.92 | 7.54 | 55.4 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,998 | 0.49 | 0.78 | 0.90 | 2.08 | 4.58 | 168.8 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,999 | **0.39** | **0.63** | **0.71** | **0.90** | **2.92** | **58.8** | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,997 | 0.47 | 1.62 | 2.59 | 4.28 | 5.89 | 174.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,998 | 0.65 | 1.01 | 1.13 | 1.41 | 4.46 | 129.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,998 | 0.53 | 0.85 | 1.07 | 3.95 | 6.01 | 163.9 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,999 | 0.70 | 1.13 | 1.29 | 1.61 | 3.34 | 118.6 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | 0.42 | 0.65 | 0.73 | 1.97 | 3.08 | 162.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,995 | 0.50 | 0.82 | **0.94** | 1.84 | **4.49** | 56.3 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,996 | 0.55 | 0.91 | 1.04 | 2.19 | 6.27 | **37.0** | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,994 | 0.79 | 2.23 | 2.78 | 4.30 | 18.19 | 82.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,994 | 0.71 | 0.97 | 1.07 | 1.60 | 7.53 | 55.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,991 | 0.53 | 1.03 | 1.93 | 3.60 | 8.85 | 63.5 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,991 | 0.67 | 1.10 | 1.20 | **1.40** | 5.89 | 51.7 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,994 | **0.48** | **0.79** | 0.97 | 1.98 | 6.66 | 55.7 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (docs/design/hot-path.md, Budget and rules, tracks these here; the gate's own share is asserted 0 B in tests).
6 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,057 | 68.0 | 0.0 | 0 | 22.86 | 0.37 | 52 | 18 |
| HTTPS HTTP/1.1, proxied | 4,696 | 48.1 | 0.0 | 0 | 23.20 | 0.64 | 39 | 7 |
| HTTPS HTTP/2, proxied | 4,515 | 51.2 | 0.0 | 0 | 25.55 | 0.53 | 19 | 7 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,126 | 45.1 | 0.0 | 0 | 25.73 | 0.64 | 40 | 6 |
| HTTP/1.1 plain, 64 KiB body, proxied | 5,470 | 31.4 | 0.0 | 0 | 15.52 | 0.63 | 46 | 6 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 4,737 | 34.1 | 0.0 | 0 | 17.03 | 0.52 | 36 | 6 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 19,245 | 19.9 | 0.0 | 0 | 14.60 | 0.80 | 21 | 6 |
| WebSocket echo, proxied (echo round trips) | 0 | 0.0 | 0.0 | 0 | 0.00 | 0.00 | 35 | 6 |
| HTTP/1.1 plain, answered by the proxy | 893 | 64.9 | 0.0 | 0 | 22.30 | 0.33 | 62 | 7 |
| HTTP/1.1 plain, access log on, proxied | 4,662 | 52.7 | 11.9 | 4 | 36.93 | 1.45 | 32 | 8 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 4,065 | 67.8 | 0.0 | 0 | 26.37 | 0.46 | 42 | 9 |
| HTTP/1.1 plain, every request refused (429) | 892 | 67.2 | 0.0 | 0 | 18.93 | 0.32 | 48 | 9 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 4,061 | 59.8 | 0.0 | 0 | 21.46 | 0.37 | 36 | 9 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 88,119 | 100.4 | 21.1 | 0 | 43.42 | 0.51 | 56 | 8 |
| HTTP/1.1 plain, a 16 KiB HTML page compressed with gzip, proxied | 5,750 | 34.7 | 0.0 | 0 | 17.22 | 0.60 | 35 | 6 |
| HTTP/1.1 plain, request and response header rules, proxied | 4,471 | 62.6 | 0.0 | 0 | 26.80 | 0.52 | 46 | 6 |
| HTTP/1.1 plain, /app stripped and /api added, proxied | 4,339 | 65.3 | 0.0 | 0 | 27.73 | 0.42 | 30 | 6 |
| HTTP/1.1 plain, the same route, the upstream's 302 Location mapped back | 4,642 | 61.1 | 0.0 | 0 | 27.13 | 0.56 | 25 | 6 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 4,462 | 60.7 | 0.0 | 0 | 25.36 | 0.51 | 39 | 24 |
| h1 at 10,000 req/s (run 1) | 4,050 | 4.0 | 0.1 | 1 | 6.87 | 1.97 | 12 | 8 |
| h1 at 30,000 req/s (run 1) | 4,050 | 15.3 | 0.0 | 0 | 15.87 | 1.21 | 24 | 8 |
| h2-tls at 10,000 req/s (run 1) | 4,523 | 4.6 | 0.1 | 1 | 7.20 | 2.06 | 2 | 7 |
| h2-tls at 30,000 req/s (run 1) | 4,530 | 15.9 | 0.0 | 0 | 14.84 | 1.29 | 9 | 6 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 64 | 11 |
| nginx | 61 | 67 |
| Caddy | 117 | 121 |
| HAProxy | 60 | 70 |
| Traefik | 40 | 151 |
| Envoy | 50 | 101 |
| YARP | 64 | 47 |
