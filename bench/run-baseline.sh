#!/usr/bin/env bash
# S7 baseline (PLAN §13): Paduraksa vs nginx, Caddy, HAProxy, Traefik, Envoy and a bare YARP app as reverse proxies in
# front of the same upstream, on one box. See bench/README.md for the method and caveats.
#
#   PROFILE=quick bench/run-baseline.sh                      # short harness iteration, never recorded
#   bench/run-baseline.sh                                    # full profile, all proxies and scenarios
#   PROXIES=paduraksa SCENARIOS=h1 RATES=30000 bench/run-baseline.sh
#   RATES="" bench/run-baseline.sh                           # no fixed-rate runs
#   REPEATS=3 RATE_DURATION=30s RATES=30000 bench/run-baseline.sh
#   RECORD=1 bench/run-baseline.sh                           # append to the history and regenerate RESULTS.md
#
# Needs: Docker (host networking), the .NET SDK from global.json, curl, openssl, python3, taskset, ss, git, getconf,
# sha256sum.
# Results: bench/results/<date>-<time>-baseline/ (or $OUT, which must not exist yet):
#   raw/<run>.json       oha's output for every measured run
#   raw/<run>.meta.json  the run's time window, the proxy's CPU time, and upstream TCP states at its end
#   paduraksa*.log       the runner's logs (one per runner process), with their per-second METRICS lines
#   yarp.log             the bare YARP reference app's log
#   env.txt              machine, versions, image digests, CPU sets, git state, TLS parameters
#   summary.md           the tables (bench/baseline/summarize.py)
#   summary.json         the same numbers for bench/history.py
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
CONF="$ROOT/bench/baseline"

PROFILE=${PROFILE:-full}
RECORD=${RECORD:-0}

# Quick is for iterating on the harness, not for numbers: short runs, one rate, and it can never enter the history.
# Explicit variables still win over either profile.
case "$PROFILE" in
    full)
        profile_duration=10s
        profile_warmup=5s
        profile_proxy_warmup=10s
        profile_rates="10000 30000"
        profile_repeats=1
        profile_handshake_conns=16
        ;;
    quick)
        profile_duration=3s
        profile_warmup=2s
        profile_proxy_warmup=2s
        profile_rates="10000"
        profile_repeats=1
        profile_handshake_conns=8
        ;;
    *)
        echo "unknown PROFILE '$PROFILE' (use full or quick)" >&2
        exit 2
        ;;
esac

if [[ "$RECORD" != 0 && "$RECORD" != 1 ]]; then
    echo "RECORD must be 0 or 1" >&2
    exit 2
fi
if [[ "$PROFILE" == quick && "$RECORD" == 1 ]]; then
    echo "PROFILE=quick runs can't be recorded; use the full profile" >&2
    exit 2
fi

DURATION=${DURATION:-$profile_duration}
WARMUP=${WARMUP:-$profile_warmup}
# Once per proxy before its first scenario: .NET's tiered JIT and PGO need ~5-10 s of load to reach steady state
# (a 5 s run straight after start measured about half the steady-state throughput). Every proxy gets the same.
PROXY_WARMUP=${PROXY_WARMUP:-$profile_proxy_warmup}
CONNS=${CONNS:-64}
# HTTP/2: connections x streams per connection = CONNS in flight.
H2_CONNS=${H2_CONNS:-16}
H2_STREAMS=${H2_STREAMS:-4}
# Every handshake request is a TLS handshake, which costs far more than a request on a warm connection; 64 in flight
# would mostly measure the load generator's own handshake work on the shared box.
HANDSHAKE_CONNS=${HANDSHAKE_CONNS:-$profile_handshake_conns}
# Fixed-rate points for h1 and h2-tls; RATES="" disables them. Each is warmed up at its own rate first.
RATES=${RATES-$profile_rates}
RATE_DURATION=${RATE_DURATION:-$DURATION}
RATE_WARMUP=${RATE_WARMUP:-$WARMUP}
REPEATS=${REPEATS:-$profile_repeats}
# `cf` is Paduraksa only: a second runner process with loopback as a trusted Cloudflare Tunnel.
SCENARIOS=${SCENARIOS:-h1 h1-tls h2-tls browser body-64k handshake answered cf}
PROXIES=${PROXIES:-paduraksa nginx caddy haproxy traefik envoy yarp}
# Set to a directory to also keep oha's per-request SQLite database for every fixed-rate run (large; not committed).
OHA_DB_DIR=${OHA_DB_DIR:-}

# CPU sets (i5-13600K: CPUs 0-11 are the six P-cores with their SMT siblings, 12-19 the eight E-cores).
# - Proxy under test: two P-cores (4 logical CPUs).
# - Load generator: the other four P-cores. oha on two P-cores tops out near 580k req/s, below what nginx answers.
# - Upstream: the E-cores. It only needs throughput (identical for every proxy), and on two P-cores it capped nginx's
#   proxied HTTP/1.1; on the eight E-cores it serves >800k req/s, twice what any proxy asks of it.
PROXY_CPUS=${PROXY_CPUS:-0-3}
LOAD_CPUS=${LOAD_CPUS:-4-11}
LOAD_THREADS=${LOAD_THREADS:-8}
UPSTREAM_CPUS=${UPSTREAM_CPUS:-12-19}

NGINX_IMAGE=${NGINX_IMAGE:-nginx:1.29-alpine@sha256:5616878291a2eed594aee8db4dade5878cf7edcb475e59193904b198d9b830de}
CADDY_IMAGE=${CADDY_IMAGE:-caddy:2.11.7-alpine@sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f}
HAPROXY_IMAGE=${HAPROXY_IMAGE:-haproxy:3.2-alpine@sha256:1c3568e520453cc8ba98c39501febcf450429c80e6cc27ecad779961d76e597c}
TRAEFIK_IMAGE=${TRAEFIK_IMAGE:-traefik:v3.7.17@sha256:f4bb143d1135a579b630ed491e17ec50f6f7c9c6a30585ff3d385471d2a86f5a}
ENVOY_IMAGE=${ENVOY_IMAGE:-envoyproxy/envoy:v1.40.2@sha256:8de97e654644827d62afdbf7e1d3dc9fee01a8690d410dd1c63a30dc6b6cecbb}
OHA_IMAGE=${OHA_IMAGE:-ghcr.io/hatoo/oha:1.16.0@sha256:3ec3dbf549ea197793482d47a6324797411406bbf438c2fe8b91f244ec641a2f}

UPSTREAM_PORT=18080
ADMIN_PORT=18943
OUT=${OUT:-$ROOT/bench/results/$(date +%Y-%m-%d-%H%M)-baseline}
RAW="$OUT/raw"
PREFIX=paduraksa-bench
WORK=$(mktemp -d)
RUNNER_PID=""
YARP_PID=""

cleanup() {
    local status=$? pid
    for pid in "$RUNNER_PID" "$YARP_PID"; do
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            kill -TERM "$pid" 2>/dev/null || true
            wait "$pid" 2>/dev/null || true
        fi
    done
    if [[ -d "$OUT" ]]; then
        cp "$WORK"/*.log "$OUT/" 2>/dev/null || true
    fi
    docker rm -f "$PREFIX-oha" "$PREFIX-upstream" "$PREFIX-nginx" "$PREFIX-caddy" "$PREFIX-haproxy" \
        "$PREFIX-traefik" "$PREFIX-envoy" >/dev/null 2>&1 || true
    rm -rf "$WORK"
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

log() { printf '\n== %s\n' "$*" >&2; }

# "10s", "2m" or a bare number of seconds, for the duration estimate and the profile.
duration_seconds() {
    case "$1" in
        *m) echo $(( ${1%m} * 60 )) ;;
        *s) echo "${1%s}" ;;
        *) echo "$1" ;;
    esac
}

format_seconds() {
    local seconds=$1
    if ((seconds >= 3600)); then
        printf '%dh%02dm' $((seconds / 3600)) $(((seconds % 3600) / 60))
    elif ((seconds >= 60)); then
        printf '%dm%02ds' $((seconds / 60)) $((seconds % 60))
    else
        printf '%ds' "$seconds"
    fi
}

expand_cpus() {
    local part
    for part in ${1//,/ }; do
        if [[ "$part" == *-* ]]; then
            seq "${part%-*}" "${part#*-}"
        else
            echo "$part"
        fi
    done
}

check_cpu_set() {
    local name=$1 set=$2 max=$(($(nproc) - 1)) cpu
    if [[ ! "$set" =~ ^[0-9]+(-[0-9]+)?(,[0-9]+(-[0-9]+)?)*$ ]]; then
        echo "$name='$set' is not a CPU list" >&2
        return 1
    fi
    for cpu in $(expand_cpus "$set"); do
        if ((cpu > max)); then
            echo "$name=$set needs CPU $cpu, but this machine has $(nproc) logical CPUs (0-$max)." \
                "Override $name (and the other *_CPUS) on other hardware." >&2
            return 1
        fi
    done
}

port_in_use() { [[ -n "$(ss -Hltn "sport = :$1")" ]]; }

known_proxy() {
    case "$1" in
        paduraksa|nginx|caddy|haproxy|traefik|envoy|yarp) return 0 ;;
        *) return 1 ;;
    esac
}

answers_locally() { [[ "$1" != traefik ]]; }

ports_for() {
    case "$1" in
        paduraksa) echo "18081 18443" ;;
        nginx) echo "18082 18444" ;;
        caddy) echo "18083 18445" ;;
        haproxy) echo "18084 18446" ;;
        traefik) echo "18085 18447" ;;
        envoy) echo "18086 18448" ;;
        yarp) echo "18087 18449" ;;
    esac
}

preflight() {
    local missing=() proxy http https port ports=()
    for tool in docker dotnet curl openssl python3 taskset ss git getconf sha256sum; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    if ((${#missing[@]})); then
        echo "missing tools: ${missing[*]}" >&2
        return 1
    fi
    docker info >/dev/null 2>&1 || { echo "docker isn't usable by this user" >&2; return 1; }
    if [[ -e "$OUT" ]]; then
        echo "$OUT already exists; remove it or set OUT" >&2
        return 1
    fi

    for proxy in $PROXIES; do
        if ! known_proxy "$proxy"; then
            echo "unknown proxy '$proxy' (known: paduraksa nginx caddy haproxy traefik envoy yarp)" >&2
            return 1
        fi
    done

    check_cpu_set PROXY_CPUS "$PROXY_CPUS"
    check_cpu_set LOAD_CPUS "$LOAD_CPUS"
    check_cpu_set UPSTREAM_CPUS "$UPSTREAM_CPUS"

    ports=("$UPSTREAM_PORT")
    for proxy in $PROXIES; do
        read -r http https < <(ports_for "$proxy")
        ports+=("$http" "$https")
        if [[ "$proxy" == paduraksa ]]; then
            ports+=("$ADMIN_PORT")
        fi
    done
    for port in "${ports[@]}"; do
        if port_in_use "$port"; then
            echo "port $port is already in use; a container or process from an earlier run is still around." \
                "Check 'docker ps' (its name starts with $PREFIX-) or free the port; a new run won't clobber it." >&2
            return 1
        fi
    done
}

# Rough wall time from the profile, scenarios, proxies and rates. Includes ~1 s of container overhead per oha call
# and 5 s per proxy for start and stop; the real number is usually a little higher.
estimate_seconds() {
    local proxy total=0 calls=0 base fixed rate_count=0 rate
    local warmup duration rate_warmup rate_duration
    warmup=$(duration_seconds "$WARMUP")
    duration=$(duration_seconds "$DURATION")
    rate_warmup=$(duration_seconds "$RATE_WARMUP")
    rate_duration=$(duration_seconds "$RATE_DURATION")
    base=$((warmup + duration))
    fixed=$((rate_warmup + REPEATS * rate_duration))
    for rate in $RATES; do
        rate_count=$((rate_count + 1))
    done

    if [[ "${UPSTREAM_DIRECT:-1}" != 0 ]]; then
        total=$((total + base))
        calls=$((calls + 2))
    fi
    for proxy in $PROXIES; do
        total=$((total + 2 * $(duration_seconds "$PROXY_WARMUP")))
        calls=$((calls + 2))
        if wants h1; then
            total=$((total + base + rate_count * fixed))
            calls=$((calls + 2 + rate_count * (1 + REPEATS)))
        fi
        if wants h2-tls; then
            total=$((total + base + rate_count * fixed))
            calls=$((calls + 2 + rate_count * (1 + REPEATS)))
        fi
        if wants h1-tls; then total=$((total + base)); calls=$((calls + 2)); fi
        if wants browser; then total=$((total + base)); calls=$((calls + 2)); fi
        if wants body-64k; then total=$((total + base)); calls=$((calls + 2)); fi
        if wants handshake; then total=$((total + base)); calls=$((calls + 2)); fi
        if answers_locally "$proxy" && wants answered; then total=$((total + base)); calls=$((calls + 2)); fi
        if [[ "$proxy" == paduraksa ]] && wants cf; then
            total=$((total + $(duration_seconds "$PROXY_WARMUP") + duration))
            calls=$((calls + 2))
        fi
    done
    echo $((total + calls + 5 * $(wc -w <<<"$PROXIES")))
}

now_ms() { date +%s%3N; }

# CPU time (user + system, microseconds) the proxy under test has used so far.
CLK_TCK=$(getconf CLK_TCK)
proxy_pid() {
    case "$1" in
        paduraksa) echo "$RUNNER_PID" ;;
        yarp) echo "$YARP_PID" ;;
        *) echo "" ;;
    esac
}

proxy_cpu_us() {
    local proxy=$1 pid
    pid=$(proxy_pid "$proxy")
    if [[ -n "$pid" ]]; then
        # /proc/<pid>/stat fields 14 and 15 (utime, stime) in clock ticks, for the whole process. The command name
        # (field 2) has no spaces here ("dotnet"), so plain field splitting works.
        awk -v tck="$CLK_TCK" '{ printf "%d\n", ($14 + $15) * 1000000 / tck }' "/proc/$pid/stat"
    else
        local id
        id=$(docker inspect -f '{{.Id}}' "$PREFIX-$proxy")
        awk '/^usage_usec/ { print $2 }' "/sys/fs/cgroup/system.slice/docker-$id.scope/cpu.stat"
    fi
}

# Proxy <-> upstream TCP sockets right now. Established: the proxy's ends only (each loopback connection has two
# sockets). TIME_WAIT: either end, whichever closed first; it lingers 60 s, so it shows the last minute's churn.
upstream_established() { ss -Htan state established "( dport = :$UPSTREAM_PORT )" | wc -l; }
upstream_time_wait() { ss -Htan state time-wait "( dport = :$UPSTREAM_PORT or sport = :$UPSTREAM_PORT )" | wc -l; }

wait_ready() {
    local host=$1 port=$2
    for _ in $(seq 1 120); do
        if curl -sf -o /dev/null -H "Host: $host" "http://127.0.0.1:$port/"; then
            return 0
        fi
        sleep 0.25
    done
    echo "timed out waiting for $host on :$port" >&2
    return 1
}

# Checks that a proxy forwards, answers (when it can), speaks H2 over TLS, and serves the fixed 64 KiB body; records
# the TLS parameters.
check_proxy() {
    local proxy=$1 http=$2 https=$3 body version tls size
    body=$(curl -sf -H "Host: bench.test" "http://127.0.0.1:$http/")
    [[ "$body" == "upstream ok" ]] || { echo "unexpected proxied body: $body" >&2; return 1; }
    if answers_locally "$proxy"; then
        body=$(curl -sf -H "Host: respond.test" "http://127.0.0.1:$http/")
        [[ "$body" == "ok" ]] || { echo "unexpected answered body: $body" >&2; return 1; }
    fi
    if wants body-64k; then
        size=$(curl -sf -H "Host: bench.test" "http://127.0.0.1:$http/64k" | wc -c)
        ((size == 65536)) || { echo "expected a 64 KiB body, got $size bytes" >&2; return 1; }
    fi
    version=$(curl -sfk --http2 -o /dev/null -w '%{http_version}' --resolve "bench.test:$https:127.0.0.1" "https://bench.test:$https/")
    [[ "$version" == "2" ]] || { echo "HTTPS did not negotiate H2 ($version)" >&2; return 1; }
    tls=$(openssl s_client -connect "127.0.0.1:$https" -servername bench.test -alpn h2 </dev/null 2>/dev/null \
        | sed -n 's/^New, \(.*\)$/\1/p; s/^Peer signature type: \(.*\)$/sig \1/p; s/^Negotiated TLS1.3 group: \(.*\)$/group \1/p' | paste -sd ' ')
    echo "tls $proxy (OpenSSL client): $tls" >>"$OUT/env.txt"
}

# oha in Docker on the load-generator CPUs. $1 = output name ("" = warm-up, discarded), rest = oha arguments.
oha() {
    local name=$1
    shift
    local output=(--output-format quiet) mounts=(-v "$RAW:/out")
    if [[ -n "$name" ]]; then
        output=(--output-format json -o "/out/$name.json")
    fi
    if [[ -n "$OHA_DB_DIR" ]]; then
        mounts+=(-v "$OHA_DB_DIR:/db")
    fi
    docker run --rm --name "$PREFIX-oha" --network host --cpuset-cpus "$LOAD_CPUS" --user "$(id -u):$(id -g)" \
        "${mounts[@]}" "$OHA_IMAGE" --no-tui --worker-threads "$LOAD_THREADS" "${output[@]}" "$@" >/dev/null
}

# A measured run: oha plus the proxy's CPU time and the time window (for the runner's METRICS lines).
measure() {
    local proxy=$1 name=$2
    shift 2
    local start_ms end_ms cpu_before cpu_after
    cpu_before=$(proxy_cpu_us "$proxy")
    start_ms=$(now_ms)
    oha "$name" "$@"
    end_ms=$(now_ms)
    cpu_after=$(proxy_cpu_us "$proxy")
    printf '{"proxy":"%s","start_ms":%s,"end_ms":%s,"cpu_us":%s,"upstream_time_wait":%s,"upstream_established":%s}\n' \
        "$proxy" "$start_ms" "$end_ms" "$((cpu_after - cpu_before))" "$(upstream_time_wait)" "$(upstream_established)" \
        >"$RAW/$name.meta.json"
}

# Fixed-rate runs for one scenario: per rate, a warm-up at that rate, then REPEATS measured runs.
measure_rates() {
    local proxy=$1 scenario=$2
    shift 2
    local rate i
    local db=()
    for rate in $RATES; do
        log "$proxy: $scenario at $rate req/s (${REPEATS}x $RATE_DURATION)"
        oha "" -z "$RATE_WARMUP" -q "$rate" --latency-correction "$@"
        for i in $(seq 1 "$REPEATS"); do
            db=()
            if [[ -n "$OHA_DB_DIR" ]]; then
                db=(--db-url "/db/$proxy.$scenario.q$rate.r$i.db")
            fi
            measure "$proxy" "$proxy.$scenario.q$rate.r$i" -z "$RATE_DURATION" -q "$rate" --latency-correction "${db[@]}" "$@"
        done
    done
}

# The Chrome-like request headers from spike S3 (the `browser` traffic shape). oha sends the same values on every
# request; paths rotate through --rand-regex-url.
BROWSER_HEADERS=(
    -H 'sec-ch-ua: "Chromium";v="130", "Google Chrome";v="130", "Not?A_Brand";v="99"'
    -H 'sec-ch-ua-mobile: ?0'
    -H 'sec-ch-ua-platform: "Linux"'
    -H 'User-Agent: Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'
    -H 'Accept: image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8'
    -H 'Sec-Fetch-Site: same-origin'
    -H 'Sec-Fetch-Mode: no-cors'
    -H 'Sec-Fetch-Dest: image'
    -H 'Referer: https://bench.test/dashboard/3'
    -H 'Accept-Encoding: gzip, deflate, br, zstd'
    -H 'Accept-Language: en-US,en;q=0.9,id;q=0.8'
    -H 'Cookie: session=8f14e45fceea167a5a36dedd4bea2543; theme=dark; _ga=GA1.1.123456789.1700000000'
    -H 'priority: u=1, i'
)

wants() { [[ " $SCENARIOS " == *" $1 "* ]]; }

# Runs the selected scenarios against one proxy. $1 = proxy, $2 = HTTP port, $3 = HTTPS port.
run_scenarios() {
    local proxy=$1 http=$2 https=$3
    local h1_url="http://bench.test:$http/"
    local h1=(--connect-to "bench.test:$http:127.0.0.1:$http" -c "$CONNS")
    local tls_url="https://bench.test:$https/"
    local tls=(--insecure --connect-to "bench.test:$https:127.0.0.1:$https")
    local h2=("${tls[@]}" --http2 -c "$H2_CONNS" -p "$H2_STREAMS")

    if wants h1; then
        log "$proxy: h1 (plain HTTP/1.1, c=$CONNS)"
        oha "" -z "$WARMUP" "${h1[@]}" "$h1_url"
        measure "$proxy" "$proxy.h1" -z "$DURATION" "${h1[@]}" "$h1_url"
        measure_rates "$proxy" h1 "${h1[@]}" "$h1_url"
    fi

    if wants h1-tls; then
        log "$proxy: h1-tls (HTTPS HTTP/1.1, c=$CONNS)"
        oha "" -z "$WARMUP" "${tls[@]}" --http-version 1.1 -c "$CONNS" "$tls_url"
        measure "$proxy" "$proxy.h1-tls" -z "$DURATION" "${tls[@]}" --http-version 1.1 -c "$CONNS" "$tls_url"
    fi

    if wants h2-tls; then
        log "$proxy: h2-tls (HTTPS HTTP/2, ${H2_CONNS}x$H2_STREAMS streams)"
        oha "" -z "$WARMUP" "${h2[@]}" "$tls_url"
        measure "$proxy" "$proxy.h2-tls" -z "$DURATION" "${h2[@]}" "$tls_url"
        measure_rates "$proxy" h2-tls "${h2[@]}" "$tls_url"
    fi

    if wants browser; then
        log "$proxy: browser (HTTPS HTTP/2, Chrome-like headers, rotating paths)"
        # oha's regex URLs treat '.' as a literal; '?' still needs escaping.
        local browser_url="https://bench.test:$https/(static|img|api|app)/[a-z]{6}.(js|css|png)\?v=[0-9]{3}"
        oha "" -z "$WARMUP" "${h2[@]}" "${BROWSER_HEADERS[@]}" --rand-regex-url "$browser_url"
        measure "$proxy" "$proxy.browser" -z "$DURATION" "${h2[@]}" "${BROWSER_HEADERS[@]}" --rand-regex-url "$browser_url"
    fi

    if wants body-64k; then
        log "$proxy: body-64k (plain HTTP/1.1, a 64 KiB upstream body, c=$CONNS)"
        local body_url="http://bench.test:$http/64k"
        oha "" -z "$WARMUP" "${h1[@]}" "$body_url"
        measure "$proxy" "$proxy.body-64k" -z "$DURATION" "${h1[@]}" "$body_url"
    fi

    if wants handshake; then
        log "$proxy: handshake (HTTPS HTTP/1.1, a new connection per request, c=$HANDSHAKE_CONNS)"
        local handshake=("${tls[@]}" --http-version 1.1 --disable-keepalive -c "$HANDSHAKE_CONNS")
        oha "" -z "$WARMUP" "${handshake[@]}" "$tls_url"
        measure "$proxy" "$proxy.handshake" -z "$DURATION" "${handshake[@]}" "$tls_url"
    fi

    if wants answered && answers_locally "$proxy"; then
        log "$proxy: answered (plain HTTP/1.1, the proxy answers itself, c=$CONNS)"
        local answered_url="http://respond.test:$http/"
        local answered=(--connect-to "respond.test:$http:127.0.0.1:$http" -c "$CONNS")
        oha "" -z "$WARMUP" "${answered[@]}" "$answered_url"
        measure "$proxy" "$proxy.answered" -z "$DURATION" "${answered[@]}" "$answered_url"
    fi
}

# $1 = log name (default paduraksa), rest = extra runner arguments.
RUNNER_LOG=paduraksa
start_paduraksa() {
    local http https
    RUNNER_LOG=${1:-paduraksa}
    shift || true
    read -r http https < <(ports_for paduraksa)
    # DOTNET_* GC settings in the environment (DOTNET_GCDynamicAdaptationMode, DOTNET_GCHeapCount, ...) pass through.
    taskset -c "$PROXY_CPUS" dotnet "$ROOT/bench/Paduraksa.Bench.Runner/bin/Release/net10.0/Paduraksa.Bench.Runner.dll" \
        --upstream "http://127.0.0.1:$UPSTREAM_PORT" --http "$http" --https "$https" --admin "$ADMIN_PORT" "$@" \
        >"$WORK/$RUNNER_LOG.log" 2>&1 &
    RUNNER_PID=$!
    wait_ready bench.test "$http"
}

stop_paduraksa() {
    kill -TERM "$RUNNER_PID"
    wait "$RUNNER_PID" || true
    RUNNER_PID=""
    cp "$WORK/$RUNNER_LOG.log" "$OUT/$RUNNER_LOG.log"
    grep -v '^METRICS ' "$WORK/$RUNNER_LOG.log" >&2 || true
}

start_yarp() {
    local http https
    read -r http https < <(ports_for yarp)
    taskset -c "$PROXY_CPUS" dotnet "$ROOT/bench/Paduraksa.Bench.Yarp/bin/Release/net10.0/Paduraksa.Bench.Yarp.dll" \
        --upstream "http://127.0.0.1:$UPSTREAM_PORT" --cert "$WORK/certs/cert.pem" --key "$WORK/certs/key.pem" \
        --http "$http" --https "$https" >"$WORK/yarp.log" 2>&1 &
    YARP_PID=$!
    wait_ready bench.test "$http"
}

stop_yarp() {
    kill -TERM "$YARP_PID"
    wait "$YARP_PID" || true
    YARP_PID=""
    cp "$WORK/yarp.log" "$OUT/yarp.log"
    tail -n 20 "$WORK/yarp.log" >&2 || true
}

# The `cf` scenario: plain HTTP/1.1 through a runner that trusts loopback as cloudflared, with the client address in
# CF-Connecting-IP and the scheme in CF-Visitor (resolved per request, forwarded as X-Forwarded-For/-Proto).
run_cloudflare() {
    local http https
    read -r http https < <(ports_for paduraksa)
    log "Starting paduraksa as a Cloudflare Tunnel origin (CPUs $PROXY_CPUS)"
    start_paduraksa paduraksa-cf --cloudflare-tunnel true
    local cf=(--connect-to "bench.test:$http:127.0.0.1:$http" -c "$CONNS"
        -H "CF-Connecting-IP: 2001:db8:85a3::8a2e:370:7334" -H 'CF-Visitor: {"scheme":"https"}' -H "CF-Ray: 8f1e2d3c4b5a6978-SIN")
    log "paduraksa: cf (plain HTTP/1.1 from a trusted tunnel, c=$CONNS, $PROXY_WARMUP warm-up)"
    oha "" -z "$PROXY_WARMUP" "${cf[@]}" "http://bench.test:$http/"
    # Proxy label "paduraksa", not "paduraksa-cf", on purpose: summarize.py and proxy_cpu_us key on it.
    measure paduraksa paduraksa.cf -z "$DURATION" "${cf[@]}" "http://bench.test:$http/"
    stop_paduraksa
}

start_container() {
    local name=$1
    shift
    docker rm -f "$PREFIX-$name" >/dev/null 2>&1 || true
    docker run -d --name "$PREFIX-$name" --network host --cpuset-cpus "$PROXY_CPUS" --ulimit nofile=65536:65536 \
        -v "$WORK/certs:/certs:ro" "$@" >/dev/null
}

start_nginx() {
    start_container nginx -v "$CONF/proxy.nginx.conf:/etc/nginx/nginx.conf:ro" "$NGINX_IMAGE"
    wait_ready bench.test "$(ports_for nginx | cut -d' ' -f1)"
}

start_caddy() {
    start_container caddy -v "$CONF/Caddyfile:/etc/caddy/Caddyfile:ro" "$CADDY_IMAGE"
    wait_ready bench.test "$(ports_for caddy | cut -d' ' -f1)"
}

# HAProxy wants the certificate and its key in one PEM file.
start_haproxy() {
    start_container haproxy -v "$CONF/haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro" "$HAPROXY_IMAGE"
    wait_ready bench.test "$(ports_for haproxy | cut -d' ' -f1)"
}

start_traefik() {
    start_container traefik -e GOMAXPROCS=4 \
        -v "$CONF/traefik.yml:/etc/traefik/traefik.yml:ro" \
        -v "$CONF/traefik-dynamic.yml:/etc/traefik/dynamic.yml:ro" \
        "$TRAEFIK_IMAGE" --configFile=/etc/traefik/traefik.yml
    wait_ready bench.test "$(ports_for traefik | cut -d' ' -f1)"
}

start_envoy() {
    start_container envoy -v "$CONF/envoy.yaml:/etc/envoy/envoy.yaml:ro" \
        "$ENVOY_IMAGE" -c /etc/envoy/envoy.yaml --concurrency 4 --log-level warn
    wait_ready bench.test "$(ports_for envoy | cut -d' ' -f1)"
}

stop_container() {
    docker logs "$PREFIX-$1" 2>&1 | tail -n 20 >&2 || true
    docker rm -f "$PREFIX-$1" >/dev/null
}

record_environment() {
    local status
    # The harness's own output is untracked or modified while a run is in progress; it says nothing about the code
    # being measured, so it is excluded from the dirty check (otherwise every recorded run would look dirty).
    status=$(git -C "$ROOT" status --porcelain | grep -v '^.. bench/results/' || true)
    {
        echo "date: $(date -u +%FT%TZ)"
        echo "commit: $(git -C "$ROOT" rev-parse HEAD)"
        echo "git status --porcelain (empty = clean; bench/results/ excluded):"
        printf '%s\n' "$status" | sed '/^$/d; s/^/  /'
        echo "dirty: $([[ -n "$status" ]] && echo true || echo false)"
        echo "git diff HEAD sha256: $(git -C "$ROOT" diff HEAD --binary | sha256sum | cut -d' ' -f1)"
        echo "profile: $PROFILE (record=$RECORD)"
        echo "cpu: $(lscpu | sed -n 's/^Model name: *//p')"
        echo "cpus: $(nproc) logical ($(lscpu | sed -n 's/^Core(s) per socket: *//p') cores, $(lscpu | sed -n 's/^Thread(s) per core: *//p') threads/core)"
        echo "memory: $(free -g | awk '/^Mem:/ {print $2 " GiB"}')"
        echo "kernel: $(uname -sr)"
        echo "governor: $(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null || echo unknown)"
        echo "load before run: $(cut -d' ' -f1-3 /proc/loadavg)"
        echo "docker: $(docker version --format '{{.Server.Version}}')"
        echo "dotnet sdk: $(dotnet --version)"
        echo "dotnet runtime: $(dotnet --list-runtimes | awk '/Microsoft.AspNetCore.App/ {v=$2} END {print "ASP.NET Core " v}')"
        echo "DOTNET_ GC environment: $(env | grep '^DOTNET_GC' | paste -sd ' ' || true)"
        echo "nginx: $NGINX_IMAGE ($(docker run --rm --entrypoint nginx "$NGINX_IMAGE" -v 2>&1))"
        echo "caddy: $CADDY_IMAGE ($(docker run --rm "$CADDY_IMAGE" caddy version | cut -d' ' -f1))"
        echo "haproxy: $HAPROXY_IMAGE ($(docker run --rm --entrypoint haproxy "$HAPROXY_IMAGE" -v 2>&1 | sed -n '1s/^HAProxy version \([^ ]*\).*/\1/p'))"
        echo "traefik: $TRAEFIK_IMAGE ($(docker run --rm --entrypoint traefik "$TRAEFIK_IMAGE" version 2>&1 | sed -n 's/^Version: *//p'))"
        echo "envoy: $ENVOY_IMAGE ($(docker run --rm --entrypoint envoy "$ENVOY_IMAGE" --version 2>&1 | sed -n 's/^envoy  version: //p'))"
        echo "yarp: Yarp.ReverseProxy $(sed -n 's/.*"Yarp.ReverseProxy" Version="\([^"]*\)".*/\1/p' "$ROOT/Directory.Packages.props")"
        echo "oha: $OHA_IMAGE ($(docker run --rm "$OHA_IMAGE" --version))"
        echo "cpu sets: proxy=$PROXY_CPUS upstream=$UPSTREAM_CPUS load=$LOAD_CPUS (oha worker threads: $LOAD_THREADS)"
        echo "runs: $DURATION (warm-up: $PROXY_WARMUP per proxy, $WARMUP per scenario), c=$CONNS (h1/body-64k), ${H2_CONNS}x$H2_STREAMS (h2), c=$HANDSHAKE_CONNS (handshake)"
        echo "fixed rates: ${RATES:-none} (${REPEATS}x $RATE_DURATION each, $RATE_WARMUP warm-up per rate)"
        echo "scenarios: $SCENARIOS; proxies: $PROXIES"
    } >"$OUT/env.txt"
}

main() {
    preflight
    mkdir -p "$RAW"

    log "Profile $PROFILE: $PROXIES x [$SCENARIOS], estimated wall time ~$(format_seconds "$(estimate_seconds)")"

    for image in "$NGINX_IMAGE" "$CADDY_IMAGE" "$HAPROXY_IMAGE" "$TRAEFIK_IMAGE" "$ENVOY_IMAGE" "$OHA_IMAGE"; do
        docker image inspect "$image" >/dev/null 2>&1 || docker pull -q "$image" >/dev/null
    done

    log "Building the Paduraksa runner and the bare YARP reference (Release)"
    dotnet build "$ROOT/bench/Paduraksa.Bench.Runner" -c Release -nologo -v quiet >&2
    dotnet build "$ROOT/bench/Paduraksa.Bench.Yarp" -c Release -nologo -v quiet >&2

    # One throwaway ECDSA P-256 certificate for every proxy that isn't Paduraksa (Paduraksa serves its own P-256
    # placeholder).
    mkdir -p "$WORK/certs"
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 2 -subj "/CN=bench.test" \
        -addext "subjectAltName=DNS:bench.test,DNS:respond.test,DNS:localhost,IP:127.0.0.1" \
        -keyout "$WORK/certs/key.pem" -out "$WORK/certs/cert.pem" 2>/dev/null
    chmod 644 "$WORK/certs/key.pem"
    cat "$WORK/certs/cert.pem" "$WORK/certs/key.pem" >"$WORK/certs/haproxy.pem"
    # The 64 KiB body body-64k streams. The upstream config mounts it read-only.
    python3 -I -c 'import sys; sys.stdout.write("x" * 65536)' >"$WORK/64k.txt"

    record_environment

    log "Starting the upstream (nginx, CPUs $UPSTREAM_CPUS)"
    docker rm -f "$PREFIX-upstream" >/dev/null 2>&1 || true
    docker run -d --name "$PREFIX-upstream" --network host --cpuset-cpus "$UPSTREAM_CPUS" --ulimit nofile=65536:65536 \
        -v "$CONF/upstream.nginx.conf:/etc/nginx/nginx.conf:ro" -v "$WORK/64k.txt:/srv/64k.txt:ro" \
        "$NGINX_IMAGE" >/dev/null
    wait_ready upstream "$UPSTREAM_PORT"

    # The upstream alone, for the ceiling the proxies are measured against (UPSTREAM_DIRECT=0 skips it).
    if [[ "${UPSTREAM_DIRECT:-1}" != 0 ]]; then
        log "upstream direct (h1, c=$CONNS)"
        local direct=(--connect-to "bench.test:$UPSTREAM_PORT:127.0.0.1:$UPSTREAM_PORT" -c "$CONNS")
        oha "" -z "$WARMUP" "${direct[@]}" "http://bench.test:$UPSTREAM_PORT/"
        oha "upstream.h1" -z "$DURATION" "${direct[@]}" "http://bench.test:$UPSTREAM_PORT/"
    fi

    local http https
    for proxy in $PROXIES; do
        read -r http https < <(ports_for "$proxy")
        log "Starting $proxy (CPUs $PROXY_CPUS)"
        "start_$proxy"
        check_proxy "$proxy" "$http" "$https"
        log "$proxy: warm-up ($PROXY_WARMUP h1 + $PROXY_WARMUP h2-tls)"
        oha "" -z "$PROXY_WARMUP" --connect-to "bench.test:$http:127.0.0.1:$http" -c "$CONNS" "http://bench.test:$http/"
        oha "" -z "$PROXY_WARMUP" --insecure --connect-to "bench.test:$https:127.0.0.1:$https" --http2 -c "$H2_CONNS" \
            -p "$H2_STREAMS" "https://bench.test:$https/"
        run_scenarios "$proxy" "$http" "$https"
        case "$proxy" in
            paduraksa)
                stop_paduraksa
                if wants cf; then run_cloudflare; fi
                ;;
            yarp) stop_yarp ;;
            *) stop_container "$proxy" ;;
        esac
    done

    cat "$OUT/env.txt" >&2
    python3 -I "$CONF/summarize.py" "$OUT" | tee "$OUT/summary.md"

    if [[ "$RECORD" == 1 ]]; then
        log "Recording the run in bench/results/history.jsonl and regenerating bench/RESULTS.md"
        python3 -I "$ROOT/bench/history.py" append "$OUT"
        python3 -I "$ROOT/bench/history.py" report
    fi
}

main "$@"
