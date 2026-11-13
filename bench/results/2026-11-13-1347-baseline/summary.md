### Throughput at fixed concurrency (successful requests/s)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP | Paduraksa ÷ nginx |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 171,033 | **324,905** | 61,648 | 255,488 | 106,968 | 110,446 | 172,722 | 0.53 |
| HTTPS HTTP/1.1, proxied | 137,827 | **305,333** | 69,574 | 166,132 | 102,011 | 101,848 | 140,470 | 0.45 |
| HTTPS HTTP/2, proxied | 150,430 | **248,390** | 45,955 | 179,729 | 62,889 | 113,240 | 158,000 | 0.61 |
| HTTPS HTTP/2, browser-like headers, proxied | 124,759 | **225,462** | 37,441 | 164,361 | 53,686 | 98,832 | 130,362 | 0.55 |
| HTTP/1.1 plain, 64 KiB body, proxied | 74,281 | 76,141 | 46,062 | **104,255** | 61,015 | 70,858 | 75,069 | 0.98 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 65,421 | **125,244** | 34,521 | 89,700 | 50,565 | 69,141 | 66,170 | 0.52 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 10,038 | 9,338 | 11,187 | 7,952 | 11,658 | **14,026** | 10,155 | 1.08 |
| WebSocket echo, proxied (echo round trips) | 251,423 | **497,327** | 407,943 | 420,016 | 417,734 | 334,345 | 261,924 | 0.51 |
| HTTP/1.1 plain, answered by the proxy | 385,590 | 481,731 | 145,142 | **561,275** | - | 261,202 | 387,153 | 0.80 |
| HTTP/1.1 plain, access log on, proxied | 147,562 | **310,625** | 55,964 | 201,716 | 68,585 | 108,429 | 148,574 | 0.48 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 169,585 | **315,447** | - | 243,534 | 98,065 | 111,306 | 167,165 | 0.54 |
| HTTP/1.1 plain, every request refused (429) | 395,167 | **477,211** | - | 458,879 | 242,307 | 262,521 | 380,572 | 0.83 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 151,396 | - | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 19,462 | **219,577** | 37,877 | 195,156 | 52,808 | 88,743 | 21,188 | 0.09 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 154,485 | **301,283** | 51,648 | 233,105 | - | 101,684 | 155,559 | 0.51 |

Upstream alone (HTTP/1.1, same concurrency): 1,041,684 requests/s.

WebSocket echo server alone (same connections and message size): 563,673 echo round trips/s.

For `websocket`, a request is one echo round trip (a message each way on one of the open connections), so the
throughput is echoes/s and the CPU per request is per echo; the status breakdown counts the upgrades (101), one per
connection.

For `rate-limited` and `error-page`, the number is every response, 429s or 502s included: those scenarios measure what the proxy
answers when it refuses or fails, so counting only 2xx would report zero.

### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)

| Scenario | Paduraksa | nginx | Caddy | HAProxy | Traefik | Envoy | YARP |
|---|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 19.6 (3.35) | **9.6 (3.11)** | 60.6 (3.74) | 12.8 (3.28) | 33.9 (3.62) | 33.2 (3.67) | 19.0 (3.29) |
| HTTPS HTTP/1.1, proxied | 24.4 (3.37) | **10.3 (3.14)** | 53.3 (3.71) | 19.6 (3.25) | 35.4 (3.61) | 36.1 (3.68) | 23.9 (3.36) |
| HTTPS HTTP/2, proxied | 23.8 (3.58) | **13.1 (3.26)** | 80.9 (3.72) | 18.2 (3.27) | 58.5 (3.68) | 32.6 (3.69) | 22.3 (3.52) |
| HTTPS HTTP/2, browser-like headers, proxied | 29.1 (3.63) | **14.8 (3.33)** | 99.6 (3.73) | 20.4 (3.35) | 68.8 (3.69) | 36.9 (3.64) | 27.2 (3.55) |
| HTTP/1.1 plain, 64 KiB body, proxied | 46.6 (3.46) | 41.2 (3.14) | 77.4 (3.57) | **32.7 (3.41)** | 55.9 (3.41) | 50.8 (3.60) | 46.2 (3.47) |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 52.7 (3.44) | **26.3 (3.30)** | 105.9 (3.65) | 35.5 (3.18) | 70.4 (3.56) | 51.4 (3.56) | 51.2 (3.39) |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 369.9 (3.71) | 364.2 (3.40) | 338.2 (3.78) | 477.4 (3.80) | 326.3 (3.80) | **223.8 (3.14)** | 369.1 (3.75) |
| WebSocket echo, proxied (echo round trips) | 11.3 (2.85) | **4.1 (2.06)** | 5.6 (2.28) | 5.8 (2.46) | 5.6 (2.33) | 7.9 (2.64) | 11.3 (2.96) |
| HTTP/1.1 plain, answered by the proxy | 7.7 (2.95) | 6.9 (3.35) | 25.6 (3.71) | **5.8 (3.26)** | - | 13.9 (3.63) | 7.5 (2.91) |
| HTTP/1.1 plain, access log on, proxied | 23.0 (3.39) | **10.1 (3.15)** | 66.8 (3.74) | 15.9 (3.21) | 54.3 (3.73) | 33.8 (3.67) | 22.6 (3.35) |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 19.7 (3.33) | **9.9 (3.13)** | - | 13.6 (3.30) | 37.0 (3.63) | 32.9 (3.66) | 19.8 (3.31) |
| HTTP/1.1 plain, every request refused (429) | 8.0 (3.15) | **7.0 (3.35)** | - | 7.4 (3.38) | 14.8 (3.59) | 13.8 (3.63) | 7.9 (3.02) |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 22.6 (3.42) | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 196.5 (3.82) | **14.0 (3.08)** | 98.0 (3.71) | 16.3 (3.19) | 67.8 (3.58) | 40.7 (3.61) | 180.4 (3.82) |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 21.8 (3.36) | **10.5 (3.17)** | 72.9 (3.77) | 14.3 (3.33) | - | 36.2 (3.68) | 21.4 (3.33) |

### Latency at fixed concurrency (ms)

At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the
medians here restate the throughput table; the tails are the informative part.

| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |
|---|---|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | Paduraksa | 171,033 | 0.36 | 0.42 | 0.79 | 1.80 | 0 |
| HTTP/1.1 plain, proxied | nginx | **324,905** | **0.18** | **0.27** | **0.29** | **0.31** | 0 |
| HTTP/1.1 plain, proxied | Caddy | 61,648 | 0.79 | 2.30 | 3.52 | 4.47 | 0 |
| HTTP/1.1 plain, proxied | HAProxy | 255,488 | 0.24 | 0.31 | 0.36 | 0.37 | 0 |
| HTTP/1.1 plain, proxied | Traefik | 106,968 | 0.49 | 1.03 | 2.86 | 4.11 | 0 |
| HTTP/1.1 plain, proxied | Envoy | 110,446 | 0.56 | 0.73 | 0.85 | 0.87 | 0 |
| HTTP/1.1 plain, proxied | YARP | 172,722 | 0.35 | 0.41 | 0.79 | 2.21 | 0 |
| HTTPS HTTP/1.1, proxied | Paduraksa | 137,827 | 0.45 | 0.52 | 0.95 | 2.25 | 0 |
| HTTPS HTTP/1.1, proxied | nginx | **305,333** | **0.24** | **0.29** | **0.30** | **0.33** | 0 |
| HTTPS HTTP/1.1, proxied | Caddy | 69,574 | 0.70 | 2.03 | 3.27 | 4.18 | 0 |
| HTTPS HTTP/1.1, proxied | HAProxy | 166,132 | 0.38 | 0.44 | 0.49 | 0.54 | 0 |
| HTTPS HTTP/1.1, proxied | Traefik | 102,011 | 0.51 | 1.08 | 3.03 | 4.37 | 0 |
| HTTPS HTTP/1.1, proxied | Envoy | 101,848 | 0.59 | 0.95 | 1.16 | 1.20 | 0 |
| HTTPS HTTP/1.1, proxied | YARP | 140,470 | 0.43 | 0.50 | 0.98 | 2.06 | 0 |
| HTTPS HTTP/2, proxied | Paduraksa | 150,430 | 0.41 | 0.50 | 1.03 | 1.52 | 0 |
| HTTPS HTTP/2, proxied | nginx | **248,390** | **0.25** | **0.33** | **0.37** | **0.40** | 0 |
| HTTPS HTTP/2, proxied | Caddy | 45,955 | 1.21 | 2.61 | 3.90 | 4.96 | 0 |
| HTTPS HTTP/2, proxied | HAProxy | 179,729 | 0.35 | 0.39 | 0.47 | 0.53 | 0 |
| HTTPS HTTP/2, proxied | Traefik | 62,889 | 0.90 | 1.68 | 3.54 | 4.76 | 0 |
| HTTPS HTTP/2, proxied | Envoy | 113,240 | 0.53 | 1.10 | 1.29 | 1.39 | 0 |
| HTTPS HTTP/2, proxied | YARP | 158,000 | 0.38 | 0.47 | 1.04 | 1.66 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Paduraksa | 124,759 | 0.49 | 0.60 | 1.17 | 1.84 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | nginx | **225,462** | **0.28** | **0.37** | **0.41** | **0.44** | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Caddy | 37,441 | 1.47 | 3.25 | 4.70 | 5.93 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | HAProxy | 164,361 | 0.38 | 0.43 | 0.52 | 0.58 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Traefik | 53,686 | 1.04 | 2.02 | 4.08 | 5.36 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | Envoy | 98,832 | 0.31 | 1.67 | 1.97 | 2.09 | 0 |
| HTTPS HTTP/2, browser-like headers, proxied | YARP | 130,362 | 0.46 | 0.57 | 1.15 | 1.84 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Paduraksa | 74,281 | 0.84 | 1.03 | 1.59 | 2.57 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | nginx | 76,141 | 0.86 | 1.00 | 1.08 | 1.53 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Caddy | 46,062 | 1.00 | 3.17 | 5.35 | 6.82 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | HAProxy | **104,255** | **0.61** | **0.64** | **0.69** | **0.76** | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Traefik | 61,015 | 0.90 | 1.81 | 3.94 | 5.94 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | Envoy | 70,858 | 0.86 | 1.11 | 1.21 | 1.32 | 0 |
| HTTP/1.1 plain, 64 KiB body, proxied | YARP | 75,069 | 0.83 | 1.02 | 1.57 | 2.60 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 65,421 | 0.96 | 1.14 | 1.59 | 2.70 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | **125,244** | **0.51** | **0.64** | **0.77** | **0.89** | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 34,521 | 1.48 | 3.77 | 5.45 | 8.05 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 89,700 | 0.71 | 0.76 | 0.83 | 3.07 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 50,565 | 0.96 | 2.64 | 4.99 | 6.42 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 69,141 | 0.89 | 1.24 | 1.31 | 1.37 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 66,170 | 0.95 | 1.13 | 1.62 | 2.95 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Paduraksa | 10,038 | 1.54 | 1.92 | 2.74 | 4.11 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | nginx | 9,338 | 1.61 | 2.87 | 4.02 | 4.97 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Caddy | 11,187 | 1.31 | 2.20 | 3.74 | 4.70 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | HAProxy | 7,952 | 2.00 | 2.33 | **2.59** | **2.91** | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Traefik | 11,658 | 1.25 | 2.10 | 3.89 | 5.37 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | Envoy | **14,026** | **1.00** | 2.08 | 2.71 | 3.05 | 0 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | YARP | 10,155 | 1.52 | **1.91** | 2.80 | 3.97 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 251,423 | 0.23 | 0.35 | 0.65 | 1.39 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | **497,327** | **0.12** | **0.15** | 0.42 | 1.40 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 407,943 | 0.14 | 0.24 | 0.37 | 1.15 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 420,016 | 0.15 | 0.16 | **0.26** | 0.92 | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 417,734 | 0.14 | 0.23 | 0.36 | 1.00 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 334,345 | 0.17 | 0.28 | 0.38 | **0.91** | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 261,924 | 0.22 | 0.35 | 0.49 | 1.23 | 0 |
| HTTP/1.1 plain, answered by the proxy | Paduraksa | 385,590 | 0.16 | 0.19 | 0.69 | 2.20 | 0 |
| HTTP/1.1 plain, answered by the proxy | nginx | 481,731 | 0.14 | 0.17 | **0.18** | **0.22** | 0 |
| HTTP/1.1 plain, answered by the proxy | Caddy | 145,142 | 0.29 | 0.98 | 2.40 | 3.44 | 0 |
| HTTP/1.1 plain, answered by the proxy | HAProxy | **561,275** | **0.11** | **0.11** | 0.22 | **0.22** | 0 |
| HTTP/1.1 plain, answered by the proxy | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, answered by the proxy | Envoy | 261,202 | 0.27 | 0.31 | 0.39 | 0.43 | 0 |
| HTTP/1.1 plain, answered by the proxy | YARP | 387,153 | 0.15 | 0.19 | 0.46 | 2.37 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 147,562 | 0.38 | 0.60 | 1.30 | 2.36 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | **310,625** | **0.20** | **0.25** | **0.27** | **0.29** | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 55,964 | 0.87 | 2.50 | 4.09 | 8.75 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 201,716 | 0.31 | 0.33 | 0.43 | 0.48 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 68,585 | 0.65 | 1.95 | 4.78 | 7.05 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 108,429 | 0.59 | 0.74 | 0.80 | 0.86 | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 148,574 | 0.40 | 0.52 | 0.92 | 1.94 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 169,585 | 0.36 | 0.42 | 0.83 | 1.85 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | **315,447** | **0.20** | **0.23** | **0.25** | 0.57 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 243,534 | 0.25 | 0.32 | 0.38 | **0.40** | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 98,065 | 0.53 | 1.13 | 3.15 | 4.47 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 111,306 | 0.55 | 0.70 | 0.90 | 0.93 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 167,165 | 0.36 | 0.42 | 0.83 | 1.96 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 395,167 | 0.15 | 0.19 | 0.68 | 2.17 | 3952585 |
| HTTP/1.1 plain, every request refused (429) | nginx | **477,211** | 0.14 | 0.15 | **0.17** | **0.20** | 4772828 |
| HTTP/1.1 plain, every request refused (429) | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, every request refused (429) | HAProxy | 458,879 | **0.13** | **0.14** | 0.28 | 0.29 | 4589868 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 242,307 | 0.19 | 0.53 | 1.34 | 3.79 | 2423799 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 262,521 | 0.25 | 0.31 | 0.35 | 0.43 | 2625844 |
| HTTP/1.1 plain, every request refused (429) | YARP | 380,572 | 0.16 | 0.19 | 0.56 | 2.16 | 3806527 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 151,396 | 0.40 | 0.47 | 0.94 | 2.50 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | nginx | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Caddy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | HAProxy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Envoy | - | - | - | - | - | - |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | YARP | - | - | - | - | - | - |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 19,462 | 3.12 | 4.83 | 7.51 | 10.80 | 194668 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | **219,577** | **0.28** | 0.36 | 0.42 | 0.49 | 2196251 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 37,877 | 1.34 | 3.58 | 5.62 | 7.28 | 378823 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 195,156 | 0.33 | **0.35** | **0.39** | **0.44** | 1951966 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 52,808 | 1.02 | 2.06 | 4.48 | 6.10 | 528172 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 88,743 | 0.70 | 0.94 | 0.98 | 1.01 | 887575 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 21,188 | 2.92 | 4.03 | 6.15 | 9.17 | 211912 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 154,485 | 0.39 | 0.47 | 0.95 | 2.52 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | **301,283** | **0.20** | **0.28** | **0.30** | **0.34** | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 51,648 | 0.99 | 2.57 | 3.71 | 4.61 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 233,105 | 0.26 | 0.34 | 0.40 | 0.41 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Traefik | - | - | - | - | - | - |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 101,684 | 0.60 | 0.78 | 0.88 | 0.92 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 155,559 | 0.39 | 0.46 | 0.97 | 2.48 | 0 |

### Response status codes at fixed concurrency

A pair is listed when it isn't all 200, and so are the upload, WebSocket and extra-host scenarios even when
they are (101 is one per upgraded WebSocket connection).

| Scenario | Proxy | 101 | 200 | 429 | 502 |
|---|---|---:|---:|---:|---:|
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Paduraksa | 0 | 654,294 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | nginx | 0 | 1,252,854 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Caddy | 0 | 345,247 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | HAProxy | 0 | 897,123 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Traefik | 0 | 505,728 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | Envoy | 0 | 691,560 | 0 | 0 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | YARP | 0 | 661,851 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Paduraksa | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | nginx | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Caddy | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | HAProxy | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Traefik | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | Envoy | 64 | 0 | 0 | 0 |
| WebSocket echo, proxied (echo round trips) | YARP | 64 | 0 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Paduraksa | 0 | 1,475,925 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | nginx | 0 | 3,107,015 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Caddy | 0 | 559,740 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | HAProxy | 0 | 2,017,639 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Traefik | 0 | 686,043 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | Envoy | 0 | 1,084,632 | 0 | 0 |
| HTTP/1.1 plain, access log on, proxied | YARP | 0 | 1,485,950 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Paduraksa | 0 | 1,696,209 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | nginx | 0 | 3,155,261 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | HAProxy | 0 | 2,435,880 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Traefik | 0 | 980,839 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | Envoy | 0 | 1,113,211 | 0 | 0 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | YARP | 0 | 1,671,932 | 0 | 0 |
| HTTP/1.1 plain, every request refused (429) | Paduraksa | 0 | 0 | 3,952,585 | 0 |
| HTTP/1.1 plain, every request refused (429) | nginx | 0 | 0 | 4,772,828 | 0 |
| HTTP/1.1 plain, every request refused (429) | HAProxy | 0 | 0 | 4,589,868 | 0 |
| HTTP/1.1 plain, every request refused (429) | Traefik | 0 | 0 | 2,423,799 | 0 |
| HTTP/1.1 plain, every request refused (429) | Envoy | 0 | 0 | 2,625,844 | 0 |
| HTTP/1.1 plain, every request refused (429) | YARP | 0 | 0 | 3,806,527 | 0 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | Paduraksa | 0 | 1,514,380 | 0 | 0 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Paduraksa | 0 | 0 | 0 | 194,668 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | nginx | 0 | 0 | 0 | 2,196,251 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Caddy | 0 | 0 | 0 | 378,823 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | HAProxy | 0 | 0 | 0 | 1,951,966 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Traefik | 0 | 0 | 0 | 528,172 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | Envoy | 0 | 0 | 0 | 887,575 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | YARP | 0 | 0 | 0 | 211,912 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Paduraksa | 0 | 1,545,270 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | nginx | 0 | 3,013,198 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Caddy | 0 | 516,560 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | HAProxy | 0 | 2,331,455 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | Envoy | 0 | 1,017,066 | 0 | 0 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | YARP | 0 | 1,555,943 | 0 | 0 |

`rate-limit` answered no 429s anywhere: every policy stayed inside its budget, so the runs measure
the cost of the check on one hot client, as intended.

### Latency at fixed request rates (ms, corrected for coordinated omission)

| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |
|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 10,000 | Paduraksa | 9,997 | 0.43 | 0.66 | 0.73 | 1.34 | 3.12 | 159.8 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | nginx | 9,998 | 0.36 | 0.61 | 0.68 | **0.81** | **1.17** | 68.6 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Caddy | 9,998 | 0.38 | 1.89 | 3.07 | 5.17 | 9.32 | 162.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | HAProxy | 9,998 | 0.39 | 0.67 | 0.73 | 0.83 | 1.80 | **66.3** | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Traefik | 9,998 | 0.48 | 0.88 | 1.10 | 5.18 | 8.13 | 149.9 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | Envoy | 9,998 | 0.69 | 1.14 | 1.29 | 1.64 | 8.31 | 121.4 | 0 |
| HTTP/1.1 plain, proxied | 10,000 | YARP | 9,997 | **0.35** | **0.55** | **0.61** | 1.77 | 3.74 | 146.0 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Paduraksa | 29,994 | 0.51 | **0.88** | 1.08 | 2.48 | 16.28 | 55.7 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | nginx | 29,996 | 0.49 | **0.88** | **1.02** | **1.31** | **3.37** | **41.8** | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Caddy | 29,994 | **0.43** | 1.64 | 2.19 | 3.28 | 22.43 | 64.2 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | HAProxy | 29,994 | 0.69 | 1.00 | 1.08 | 1.33 | 5.28 | 52.3 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Traefik | 29,994 | 0.49 | 1.38 | 2.74 | 5.02 | 8.47 | 62.9 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | Envoy | 29,994 | 0.74 | 1.05 | 1.12 | **1.31** | 10.14 | 52.5 | 0 |
| HTTP/1.1 plain, proxied | 30,000 | YARP | 29,996 | 0.52 | 0.90 | 1.15 | 2.25 | 13.37 | 60.6 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Paduraksa | 9,997 | 0.47 | 0.70 | 0.80 | 2.07 | 5.07 | 165.1 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | nginx | 9,998 | 0.41 | 0.63 | 0.70 | **0.82** | **2.07** | **64.1** | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Caddy | 9,998 | 0.48 | 1.58 | 2.62 | 4.29 | 6.49 | 172.8 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | HAProxy | 9,998 | 0.55 | 0.82 | 0.87 | 0.98 | 3.83 | 100.5 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Traefik | 9,998 | 0.59 | 0.97 | 1.41 | 4.62 | 7.06 | 182.0 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | Envoy | 9,998 | 0.73 | 1.16 | 1.32 | 1.63 | 2.74 | 120.7 | 0 |
| HTTPS HTTP/2, proxied | 10,000 | YARP | 9,998 | **0.37** | **0.57** | **0.65** | 1.76 | 2.70 | 151.3 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Paduraksa | 29,994 | 0.50 | 0.84 | **0.96** | 1.85 | 6.42 | 56.1 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | nginx | 29,997 | 0.53 | 1.20 | 1.56 | 4.49 | 9.25 | 43.8 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Caddy | 29,997 | 0.78 | 2.16 | 2.69 | 3.84 | 11.84 | 82.2 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | HAProxy | 29,995 | 0.66 | 0.92 | 1.03 | 2.08 | 6.96 | 51.4 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Traefik | 29,994 | 0.55 | 1.08 | 2.03 | 3.86 | 10.31 | 63.1 | 0 |
| HTTPS HTTP/2, proxied | 30,000 | Envoy | 29,992 | 0.59 | 1.00 | 1.13 | **1.37** | 6.41 | **41.7** | 0 |
| HTTPS HTTP/2, proxied | 30,000 | YARP | 29,994 | **0.49** | **0.82** | 0.98 | 2.00 | **3.85** | 57.6 | 0 |

### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)

Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,
YARP and the gate together (docs/design/hot-path.md, Budget and rules, tracks these here; the gate's own share is asserted 0 B in tests).
7 non-monotonic allocation sample(s) were dropped.

| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| HTTP/1.1 plain, proxied | 4,071 | 68.9 | 0.0 | 0 | 24.42 | 0.45 | 34 | 12 |
| HTTPS HTTP/1.1, proxied | 4,642 | 63.6 | 0.0 | 0 | 24.36 | 0.41 | 40 | 6 |
| HTTPS HTTP/2, proxied | 4,511 | 58.5 | 0.0 | 0 | 29.37 | 0.57 | 29 | 6 |
| HTTPS HTTP/2, browser-like headers, proxied | 6,045 | 55.1 | 0.0 | 0 | 30.13 | 0.68 | 44 | 6 |
| HTTP/1.1 plain, 64 KiB body, proxied | 5,494 | 38.6 | 0.0 | 0 | 20.62 | 0.65 | 39 | 6 |
| HTTP/1.1 plain, POST with a 64 KiB request body, proxied | 4,723 | 39.5 | 0.0 | 0 | 16.99 | 0.44 | 39 | 6 |
| HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied | 19,136 | 25.8 | 0.0 | 0 | 15.19 | 0.65 | 14 | 6 |
| WebSocket echo, proxied (echo round trips) | 0 | 0.0 | 0.0 | 0 | 0.00 | 0.00 | 49 | 6 |
| HTTP/1.1 plain, answered by the proxy | 898 | 52.0 | 0.0 | 0 | 26.15 | 0.70 | 50 | 6 |
| HTTP/1.1 plain, access log on, proxied | 4,565 | 63.1 | 12.6 | 4 | 35.80 | 1.30 | 55 | 6 |
| HTTP/1.1 plain, rate-limit take, proxied (no 429s expected) | 4,053 | 68.1 | 0.0 | 0 | 25.78 | 0.44 | 42 | 6 |
| HTTP/1.1 plain, every request refused (429) | 901 | 32.9 | 0.0 | 0 | 19.27 | 0.66 | 52 | 7 |
| HTTP/1.1 plain, valid proof-of-work cookie, proxied | 4,123 | 69.7 | 0.1 | 1 | 25.77 | 0.68 | 27 | 9 |
| HTTP/1.1 plain, closed upstream, the proxy's 502 page | 87,857 | 79.3 | 21.1 | 0 | 38.64 | 0.59 | 55 | 9 |
| HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers | 4,475 | 66.0 | 0.0 | 0 | 26.38 | 0.74 | 54 | 24 |
| h1 at 10,000 req/s (run 1) | 4,054 | 4.8 | 0.1 | 1 | 6.87 | 1.65 | 5 | 8 |
| h1 at 30,000 req/s (run 1) | 4,049 | 13.0 | 0.1 | 1 | 15.21 | 1.47 | 5 | 7 |
| h2-tls at 10,000 req/s (run 1) | 4,519 | 5.4 | 0.1 | 1 | 9.31 | 2.24 | 0 | 6 |
| h2-tls at 30,000 req/s (run 1) | 4,527 | 15.4 | 0.0 | 0 | 13.77 | 1.00 | 14 | 6 |

### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run

| Proxy | established | TIME_WAIT |
|---|---:|---:|
| Paduraksa | 62 | 10 |
| nginx | 57 | 71 |
| Caddy | 71 | 130 |
| HAProxy | 59 | 81 |
| Traefik | 71 | 111 |
| Envoy | 43 | 126 |
| YARP | 60 | 61 |
