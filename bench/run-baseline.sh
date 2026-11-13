#!/usr/bin/env bash
# S7 baseline (docs/design/milestones.md "Risks and spikes"): Paduraksa vs nginx, Caddy, HAProxy, Traefik, Envoy and a bare YARP app as reverse proxies in
# front of the same upstream, on one box. See bench/README.md for the method and caveats.
#
#   PROFILE=quick bench/run-baseline.sh                      # short harness iteration, never recorded
#   bench/run-baseline.sh                                    # full profile, all proxies and scenarios
#   PROXIES=paduraksa SCENARIOS=h1 RATES=30000 bench/run-baseline.sh
#   SCENARIOS="access-log rate-limit rate-limited browser-check error-page cf" bench/run-baseline.sh
#   SCENARIOS="compressed headers rewrite rewrite-redirect" bench/run-baseline.sh
#   RATES="" bench/run-baseline.sh                           # no fixed-rate runs
#   REPEATS=3 RATE_DURATION=30s RATES=30000 bench/run-baseline.sh
#   RECORD=1 bench/run-baseline.sh                           # append to the history and regenerate RESULTS.md
#
# Needs: Docker (host networking), the .NET SDK from global.json, curl, openssl, python3, taskset, ss, git, getconf,
# sha256sum, and socat (HAProxy's access-log sink).
# Results: bench/results/<date>-<time>-baseline/ (or $OUT, which must not exist yet):
#   raw/<run>.json       oha's output for every measured run
#   raw/<run>.meta.json  the run's time window, the proxy's CPU time, and upstream TCP states at its end
#   paduraksa*.log       the runner's logs (one per runner process), with their per-second METRICS lines
#   yarp.log             the bare YARP reference app's log
#   echo.log             the WebSocket echo upstream's log
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
# `websocket`: connections x message size (each connection has one message in flight, echoed back).
WS_CONNS=${WS_CONNS:-$CONNS}
WS_MESSAGE=${WS_MESSAGE:-1024}
# Every handshake request is a TLS handshake, which costs far more than a request on a warm connection; 64 in flight
# would mostly measure the load generator's own handshake work on the shared box.
HANDSHAKE_CONNS=${HANDSHAKE_CONNS:-$profile_handshake_conns}
# Fixed-rate points for h1 and h2-tls; RATES="" disables them. Each is warmed up at its own rate first.
RATES=${RATES-$profile_rates}
RATE_DURATION=${RATE_DURATION:-$DURATION}
RATE_WARMUP=${RATE_WARMUP:-$WARMUP}
REPEATS=${REPEATS:-$profile_repeats}
# The scenarios from `access-log` on run against every proxy that has a stock equivalent (`unsupported` lists the
# skips, bench/README.md the reasons), each on an extra host of the proxy's one instance: access-log.test,
# rate-limit.test, rate-limited.test, error-page.test, compressed.test, headers.test, rewrite.test,
# rewrite-redirect.test, and for `cf` cf.test (Paduraksa: bench.test on a second process, as it always was). A setting that can't be scoped to a host is run on a second instance started for that
# scenario only (`variant_of`), so the other scenarios' configuration is the one they always had.
SCENARIOS=${SCENARIOS:-h1 h1-tls h2-tls browser body-64k upload-64k handshake websocket answered access-log rate-limit rate-limited browser-check error-page compressed headers rewrite rewrite-redirect cf}
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
# The WebSocket echo server `websocket` runs against; it is the upstream for ws.test on every proxy.
ECHO_PORT=18090
# HAProxy has no log file, only syslog: the access-log scenario's UDP sink listens here.
SINK_PORT=18091
ADMIN_PORT=18943
OUT=${OUT:-$ROOT/bench/results/$(date +%Y-%m-%d-%H%M)-baseline}
RAW="$OUT/raw"
PREFIX=paduraksa-bench
WORK=$(mktemp -d)
RUNNER_PID=""
YARP_PID=""
ECHO_PID=""
SINK_PID=""
WS_DLL="$ROOT/bench/Paduraksa.Bench.WebSocket/bin/Release/net10.0/Paduraksa.Bench.WebSocket.dll"
# The solved proof-of-work cookie the browser-check scenario sends; set by start_paduraksa from the runner's output.
POW_COOKIE=""

cleanup() {
    local status=$? pid
    for pid in "$RUNNER_PID" "$YARP_PID" "$ECHO_PID" "$SINK_PID"; do
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
trap 'exit 130' INT
trap 'exit 143' TERM

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

# Scenarios a proxy skips for lack of a fair stock equivalent (bench/README.md gives the reasons).
unsupported() {
    [[ "$2" == browser-check && "$1" != paduraksa ]] && return 0
    case "$1:$2" in
        traefik:cf|caddy:rate-limit|caddy:rate-limited|traefik:rewrite-redirect|envoy:rewrite-redirect|yarp:rewrite-redirect) return 0 ;;
    esac
    return 1
}

# Scenarios whose setting is server-wide or static, so the proxy is restarted with it for that scenario only.
variant_of() {
    case "$1:$2" in
        traefik:access-log|caddy:cf|envoy:cf|yarp:cf|paduraksa:cf) return 0 ;;
    esac
    return 1
}

# The extra-host scenarios, in run order.
HOST_SCENARIOS="access-log rate-limit rate-limited browser-check error-page compressed headers rewrite rewrite-redirect cf"

# Does the proxy's one base instance run this scenario on an extra host?
runs_in_base() { wants "$2" && ! unsupported "$1" "$2" && ! variant_of "$1" "$2"; }

# The host that carries the scenario. cf is bench.test on Paduraksa (its second process trusts loopback for every
# host); the others have a cf.test host.
scenario_host() {
    if [[ "$2" == cf && "$1" == paduraksa ]]; then
        echo bench.test
    else
        echo "$2.test"
    fi
}

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
    if wants access-log && [[ " $PROXIES " == *" haproxy "* ]] && ! command -v socat >/dev/null 2>&1; then
        echo "missing tools: socat (HAProxy's access-log sink)" >&2
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

    for scenario in $SCENARIOS; do
        case "$scenario" in
            h1|h1-tls|h2-tls|browser|body-64k|upload-64k|handshake|websocket|answered|access-log|rate-limit|rate-limited|browser-check|error-page|compressed|headers|rewrite|rewrite-redirect|cf) ;;
            *) echo "unknown scenario '$scenario' (known: h1 h1-tls h2-tls browser body-64k upload-64k handshake websocket answered access-log rate-limit rate-limited browser-check error-page compressed headers rewrite rewrite-redirect cf)" >&2
               return 1 ;;
        esac
    done

    check_cpu_set PROXY_CPUS "$PROXY_CPUS"
    check_cpu_set LOAD_CPUS "$LOAD_CPUS"
    check_cpu_set UPSTREAM_CPUS "$UPSTREAM_CPUS"

    ports=("$UPSTREAM_PORT")
    if wants websocket; then
        ports+=("$ECHO_PORT")
    fi
    if wants access-log && [[ " $PROXIES " == *" haproxy "* ]] && [[ -n "$(ss -Hlun "sport = :$SINK_PORT")" ]]; then
        echo "UDP port $SINK_PORT is already in use; HAProxy's access-log sink needs it" >&2
        return 1
    fi
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
        if wants websocket; then total=$((total + base)); calls=$((calls + 2)); fi
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
        if wants upload-64k; then total=$((total + base)); calls=$((calls + 2)); fi
        if wants websocket; then total=$((total + base)); calls=$((calls + 2)); fi
        if wants handshake; then total=$((total + base)); calls=$((calls + 2)); fi
        if answers_locally "$proxy" && wants answered; then total=$((total + base)); calls=$((calls + 2)); fi
        for scenario in $HOST_SCENARIOS; do
            if runs_in_base "$proxy" "$scenario"; then
                total=$((total + base)); calls=$((calls + 2))
            elif wants "$scenario" && ! unsupported "$proxy" "$scenario"; then
                # A second instance: start, a proxy warm-up and the run.
                total=$((total + 5 + $(duration_seconds "$PROXY_WARMUP") + duration)); calls=$((calls + 2))
            fi
        done
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

# The extra hosts' scenarios on the base instance. Every check makes its scenario's own point, so a wrong config
# fails here instead of producing a plausible-looking number.
check_scenario_hosts() {
    local proxy=$1 http=$2 body code
    if runs_in_base "$proxy" access-log; then
        body=$(curl -sf -H "Host: access-log.test" "http://127.0.0.1:$http/")
        [[ "$body" == "upstream ok" ]] || { echo "access-log.test: unexpected body '$body'" >&2; return 1; }
    fi
    if runs_in_base "$proxy" rate-limit; then
        body=$(curl -sf -H "Host: rate-limit.test" "http://127.0.0.1:$http/")
        [[ "$body" == "upstream ok" ]] || { echo "rate-limit.test: unexpected body '$body'" >&2; return 1; }
    fi
    if runs_in_base "$proxy" rate-limited; then
        # One token: the first request spends it and the second must be the 429 the scenario measures.
        code=$(curl -s -o /dev/null -w '%{http_code}' -H "Host: rate-limited.test" "http://127.0.0.1:$http/")
        [[ "$code" == "200" ]] || { echo "rate-limited.test: the first request was $code, not 200" >&2; return 1; }
        code=$(curl -s -o /dev/null -w '%{http_code}' -H "Host: rate-limited.test" "http://127.0.0.1:$http/")
        [[ "$code" == "429" ]] || { echo "rate-limited.test: the second request was $code, not 429" >&2; return 1; }
    fi
    if runs_in_base "$proxy" browser-check; then
        body=$(curl -sf -H "Host: browser-check.test" -H "Accept: text/html" "http://127.0.0.1:$http/")
        [[ "$body" == *'name="challenge"'* ]] \
            || { echo "browser-check.test: no challenge input without a cookie" >&2; return 1; }
        body=$(curl -sf -H "Host: browser-check.test" -H "Accept: text/html" -H "Cookie: $POW_COOKIE" "http://127.0.0.1:$http/")
        [[ "$body" == "upstream ok" ]] \
            || { echo "browser-check.test: the solved cookie didn't forward (body '$body')" >&2; return 1; }
    fi
    if runs_in_base "$proxy" error-page; then
        # -w prints after the body, so the status and the page can be told apart.
        body=$(curl -s -w '\n%{http_code}' -H "Host: error-page.test" -H "Accept: text/html" "http://127.0.0.1:$http/")
        [[ "${body##*$'\n'}" == "502" ]] || { echo "error-page.test: the closed upstream answered ${body##*$'\n'}, not 502" >&2; return 1; }
        [[ "$body" == *"Bad Gateway"* ]] || { echo "error-page.test: the 502 carries no page" >&2; return 1; }
    fi
    if runs_in_base "$proxy" compressed; then
        local encoding
        encoding=$(curl -s -o /dev/null -w '%header{content-encoding}' -H "Host: compressed.test" -H "Accept-Encoding: gzip" "http://127.0.0.1:$http/text")
        [[ "$encoding" == "gzip" ]] || { echo "compressed.test: the page came back with Content-Encoding '$encoding', not gzip" >&2; return 1; }
    fi
    if runs_in_base "$proxy" headers; then
        check_headers "$http"
    fi
    if runs_in_base "$proxy" rewrite; then
        check_rewrite "$http"
    fi
    if runs_in_base "$proxy" rewrite-redirect; then
        check_rewrite_redirect "$http"
    fi
    if runs_in_base "$proxy" cf; then
        check_cf "$http" "$(scenario_host "$proxy" cf)"
    fi
}

# The values of one header in a response's header block (a line each), for the probes below.
header_values() { { grep -i "^$2:" <<<"$1" || true; } | sed 's/^[^:]*: *//'; }

# $1 = what the probe saw, $2 = what it should be, $3 = how to say so on failure.
expect_equal() { [[ "$1" == "$2" ]] || { echo "$3: got '$1', expected '$2'" >&2; return 1; }; }

# The request rules must reach the upstream and the response rules the client. The probe sends one header the
# rules replace, one they remove and one they never mention; the upstream's /hdr echoes (in X-Seen-*) what it received,
# a removed header as "(absent)", and sends X-Powered-By and a SAMEORIGIN X-Frame-Options that the response rules must
# take off and replace. A proxy that appended instead of replacing shows two values, and one that forgot a rule shows
# the client's own.
check_headers() {
    local http=$1 out
    out=$(curl -sf -D - -o /dev/null -H "Host: headers.test" -H "X-Bench-Probe: 1" -H "X-Bench-Replace: client" \
        -H "X-Bench-Drop: secret" "http://127.0.0.1:$http/hdr" | tr -d '\r')
    expect_equal "$(header_values "$out" x-seen-replace)" "replaced" "headers.test: the upstream's X-Bench-Replace (set over the client's)"
    expect_equal "$(header_values "$out" x-seen-tenant)" "acme" "headers.test: the upstream's X-Bench-Tenant (set)"
    expect_equal "$(header_values "$out" x-seen-added)" "one" "headers.test: the upstream's X-Bench-Added (added)"
    expect_equal "$(header_values "$out" x-seen-drop)" "(absent)" "headers.test: the upstream's X-Bench-Drop (removed)"
    expect_equal "$(header_values "$out" x-frame-options)" "DENY" "headers.test: the client's X-Frame-Options (set over the upstream's SAMEORIGIN)"
    expect_equal "$(header_values "$out" x-bench-policy)" "max-age=31536000" "headers.test: the client's X-Bench-Policy (set)"
    expect_equal "$(header_values "$out" x-powered-by)" "" "headers.test: the client's X-Powered-By (removed)"
}

# The upstream must see /api/... for the client's /app/..., query included. It answers 404 for the paths a missing
# strip or add leaves behind, and echoes the URI it received for a probe.
check_rewrite() {
    local http=$1 out
    out=$(curl -sf -D - -o /dev/null -H "Host: rewrite.test" -H "X-Bench-Probe: 1" "http://127.0.0.1:$http/app/users/abcdef/123?x=1" | tr -d '\r')
    expect_equal "$(header_values "$out" x-seen-uri)" "/api/users/abcdef/123?x=1" "rewrite.test: the URI the upstream received"
}

# The upstream answers 302 with Location: /api/login; the proxy must give the client /app/login. curl doesn't follow it.
check_rewrite_redirect() {
    local http=$1 out
    out=$(curl -s -D - -o /dev/null -H "Host: rewrite-redirect.test" -H "X-Bench-Probe: 1" "http://127.0.0.1:$http/app/redirect/abcdef?x=1" | tr -d '\r')
    expect_equal "$(awk 'NR == 1 { print $2 }' <<<"$out")" "302" "rewrite-redirect.test: the status"
    expect_equal "$(header_values "$out" x-seen-uri)" "/api/redirect/abcdef?x=1" "rewrite-redirect.test: the URI the upstream received"
    expect_equal "$(header_values "$out" location)" "/app/login" "rewrite-redirect.test: the Location the client got"
}

# The proxy must forward the client address it resolved from CF-Connecting-IP as X-Forwarded-For; the upstream's /xff
# answers with what it received.
check_cf() {
    local http=$1 host=$2 seen
    seen=$(curl -sf -D - -o /dev/null -H "Host: $host" -H "CF-Connecting-IP: 2001:db8::1234" "http://127.0.0.1:$http/xff" \
        | tr -d '\r' | grep -i '^x-seen-forwarded-for:' || true)
    [[ "$seen" == *"2001:db8::1234"* ]] \
        || { echo "$host: the upstream saw '$seen' instead of the CF-Connecting-IP address in X-Forwarded-For" >&2; return 1; }
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
    if wants upload-64k; then
        body=$(curl -sf -H "Host: bench.test" -H 'Content-Type: application/octet-stream' --data-binary "@$WORK/64k.txt" "http://127.0.0.1:$http/upload")
        [[ "$body" == "upstream ok" ]] || { echo "upload-64k: unexpected body '$body'" >&2; return 1; }
    fi
    if wants websocket; then
        # One connection for a second: the upgrade passed through, and the echo came back whole.
        wsload "" --url "ws://ws.test:$http/" --connect 127.0.0.1 --connections 1 --size 64 --duration 1 \
            || { echo "websocket: no echo through $proxy" >&2; return 1; }
    fi
    check_scenario_hosts "$proxy" "$http"
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
    local output=(--output-format quiet) mounts=(-v "$RAW:/out" -v "$WORK/64k.txt:/body/64k.txt:ro")
    if [[ -n "$name" ]]; then
        output=(--output-format json -o "/out/$name.json")
    fi
    if [[ -n "$OHA_DB_DIR" ]]; then
        # Docker would create a missing bind source root-owned, and oha runs as this user.
        mkdir -p "$OHA_DB_DIR"
        mounts+=(-v "$OHA_DB_DIR:/db")
    fi
    docker run --rm --name "$PREFIX-oha" --network host --cpuset-cpus "$LOAD_CPUS" --user "$(id -u):$(id -g)" \
        "${mounts[@]}" "$OHA_IMAGE" --no-tui --worker-threads "$LOAD_THREADS" "${output[@]}" "$@" >/dev/null
}

# The WebSocket load generator, natively on the load CPUs. $1 = output name ("" = warm-up or check, discarded), rest =
# its arguments. It exits non-zero when a connection failed or nothing was echoed, which stops the run.
wsload() {
    local name=$1
    shift
    local output=()
    if [[ -n "$name" ]]; then
        output=(--out "$RAW/$name.json")
    fi
    taskset -c "$LOAD_CPUS" dotnet "$WS_DLL" load "${output[@]}" "$@" >/dev/null
}

# A measured run: the load tool ($1: oha or wsload) plus the proxy's CPU time and the time window (for the runner's
# METRICS lines).
measure_with() {
    local tool=$1 proxy=$2 name=$3
    shift 3
    local start_ms end_ms cpu_before cpu_after
    cpu_before=$(proxy_cpu_us "$proxy")
    start_ms=$(now_ms)
    "$tool" "$name" "$@"
    end_ms=$(now_ms)
    cpu_after=$(proxy_cpu_us "$proxy")
    printf '{"proxy":"%s","start_ms":%s,"end_ms":%s,"cpu_us":%s,"upstream_time_wait":%s,"upstream_established":%s}\n' \
        "$proxy" "$start_ms" "$end_ms" "$((cpu_after - cpu_before))" "$(upstream_time_wait)" "$(upstream_established)" \
        >"$RAW/$name.meta.json"
}

measure() { measure_with oha "$@"; }

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

    if wants upload-64k; then
        log "$proxy: upload-64k (plain HTTP/1.1, POST with a 64 KiB request body, c=$CONNS)"
        local upload_url="http://bench.test:$http/upload"
        local upload=("${h1[@]}" -m POST -T application/octet-stream -D /body/64k.txt)
        oha "" -z "$WARMUP" "${upload[@]}" "$upload_url"
        measure "$proxy" "$proxy.upload-64k" -z "$DURATION" "${upload[@]}" "$upload_url"
    fi

    if wants handshake; then
        log "$proxy: handshake (HTTPS HTTP/1.1, a new connection per request, c=$HANDSHAKE_CONNS)"
        local handshake=("${tls[@]}" --http-version 1.1 --disable-keepalive -c "$HANDSHAKE_CONNS")
        oha "" -z "$WARMUP" "${handshake[@]}" "$tls_url"
        measure "$proxy" "$proxy.handshake" -z "$DURATION" "${handshake[@]}" "$tls_url"
    fi

    if wants websocket; then
        log "$proxy: websocket (plain WebSocket echo, $WS_CONNS connections, $WS_MESSAGE-byte messages)"
        local ws=(--url "ws://ws.test:$http/" --connect 127.0.0.1 --connections "$WS_CONNS" --size "$WS_MESSAGE")
        wsload "" "${ws[@]}" --duration "$(duration_seconds "$WARMUP")"
        measure_with wsload "$proxy" "$proxy.websocket" "${ws[@]}" --duration "$(duration_seconds "$DURATION")"
    fi

    if wants answered && answers_locally "$proxy"; then
        log "$proxy: answered (plain HTTP/1.1, the proxy answers itself, c=$CONNS)"
        local answered_url="http://respond.test:$http/"
        local answered=(--connect-to "respond.test:$http:127.0.0.1:$http" -c "$CONNS")
        oha "" -z "$WARMUP" "${answered[@]}" "$answered_url"
        measure "$proxy" "$proxy.answered" -z "$DURATION" "${answered[@]}" "$answered_url"
    fi

    local scenario
    for scenario in $HOST_SCENARIOS; do
        if runs_in_base "$proxy" "$scenario"; then
            run_host_scenario "$proxy" "$http" "$scenario" "$WARMUP"
        fi
    done
}

# The Cloudflare Tunnel headers the `cf` scenario sends: the client address, the scheme and the ray ID.
CF_HEADERS=(-H "CF-Connecting-IP: 2001:db8:85a3::8a2e:370:7334" -H 'CF-Visitor: {"scheme":"https"}' -H "CF-Ray: 8f1e2d3c4b5a6978-SIN")

# One plain HTTP/1.1 scenario on the extra host that carries it. $1 = proxy, $2 = HTTP port, $3 = scenario, $4 = warm-up.
run_host_scenario() {
    local proxy=$1 http=$2 scenario=$3 warmup=$4 host what extra=() path=/ url_args
    host=$(scenario_host "$proxy" "$scenario")
    case "$scenario" in
        access-log) what="the access log on" ;;
        rate-limit) what="one hot client well inside its budget" ;;
        rate-limited) what="every request refused with 429" ;;
        browser-check)
            what="a solved PoW cookie"
            if [[ -z "$POW_COOKIE" ]]; then
                echo "browser-check needs the runner's POW_COOKIE line" >&2
                return 1
            fi
            extra=(-H "Cookie: $POW_COOKIE" -H 'Accept: text/html')
            ;;
        error-page) what="a closed upstream, the proxy's 502 page"; extra=(-H 'Accept: text/html') ;;
        compressed) what="a 16 KiB HTML page compressed with gzip"; extra=(-H 'Accept-Encoding: gzip'); path=/text ;;
        headers)
            what="three request and three response header rules"
            # The headers the request rules replace and remove; the response rules act on what /hdr sends.
            extra=(-H 'X-Bench-Replace: client' -H 'X-Bench-Drop: secret')
            path=/hdr
            ;;
        rewrite) what="/app/... forwarded as /api/..." ;;
        rewrite-redirect) what="/app/... forwarded as /api/..., the upstream's 302 Location mapped back" ;;
        cf) what="a trusted Cloudflare Tunnel's headers"; extra=("${CF_HEADERS[@]}") ;;
    esac
    log "$proxy: $scenario (plain HTTP/1.1, $what, c=$CONNS, $warmup warm-up)"
    # The rewrite scenarios vary the path (oha's regex URLs, as `browser` does) under the prefix the route strips.
    # oha's default is not to follow redirects, which rewrite-redirect needs: one answer per request, not two.
    case "$scenario" in
        rewrite) url_args=(--rand-regex-url "http://$host:$http/app/(users|items|orders)/[a-z]{6}/[0-9]{3}\?x=[0-9]") ;;
        rewrite-redirect) url_args=(--redirect 0 --rand-regex-url "http://$host:$http/app/redirect/[a-z]{6}\?x=[0-9]") ;;
        *) url_args=("http://$host:$http$path") ;;
    esac
    local args=(--connect-to "$host:$http:127.0.0.1:$http" -c "$CONNS" "${extra[@]}")
    oha "" -z "$warmup" "${args[@]}" "${url_args[@]}"
    measure "$proxy" "$proxy.$scenario" -z "$DURATION" "${args[@]}" "${url_args[@]}"
}

# $1 = log name (default paduraksa), rest = extra runner arguments.
RUNNER_LOG=paduraksa
start_paduraksa() {
    local http https
    RUNNER_LOG=${1:-paduraksa}
    shift || true
    POW_COOKIE=""
    read -r http https < <(ports_for paduraksa)
    # DOTNET_* GC settings in the environment (DOTNET_GCDynamicAdaptationMode, DOTNET_GCHeapCount, ...) pass through.
    taskset -c "$PROXY_CPUS" dotnet "$ROOT/bench/Paduraksa.Bench.Runner/bin/Release/net10.0/Paduraksa.Bench.Runner.dll" \
        --upstream "http://127.0.0.1:$UPSTREAM_PORT" --websocket-upstream "http://127.0.0.1:$ECHO_PORT" \
        --http "$http" --https "$https" --admin "$ADMIN_PORT" "$@" \
        >"$WORK/$RUNNER_LOG.log" 2>&1 &
    RUNNER_PID=$!
    wait_ready bench.test "$http"
    if wants browser-check; then
        read_pow_cookie
    fi
}

# The runner solves one challenge at startup and prints it before READY; the log is already written by the time
# wait_ready returns, but poll briefly in case the two writes land out of order.
read_pow_cookie() {
    local i line
    for i in $(seq 1 40); do
        line=$(grep -m1 '^POW_COOKIE=' "$WORK/$RUNNER_LOG.log" || true)
        if [[ -n "$line" ]]; then
            POW_COOKIE=${line#POW_COOKIE=}
            if [[ "$POW_COOKIE" != paduraksa_pow=* ]]; then
                echo "the runner's POW_COOKIE line holds no paduraksa_pow cookie: '$line'" >&2
                return 1
            fi
            return 0
        fi
        if ! kill -0 "$RUNNER_PID" 2>/dev/null; then
            break
        fi
        sleep 0.25
    done
    echo "the runner printed no POW_COOKIE line; browser-check can't run without it" >&2
    return 1
}

stop_paduraksa() {
    kill -TERM "$RUNNER_PID"
    wait "$RUNNER_PID" || true
    RUNNER_PID=""
    cp "$WORK/$RUNNER_LOG.log" "$OUT/$RUNNER_LOG.log"
    # The first lines are the startup, which is what a failed run needs; the error-page scenario also logs a YARP
    # warning per failed forward, and printing all of those would flood the terminal (they stay in the copied log).
    grep -v '^METRICS ' "$WORK/$RUNNER_LOG.log" | head -n 60 >&2 || true
}

# $1 = scenario for a second instance: `cf` turns the forwarded-headers middleware on.
start_yarp() {
    local http https cloudflare=()
    mkdir -p "$WORK/yarp-access-log"
    if [[ "${1:-}" == cf ]]; then
        cloudflare=(--cloudflare-tunnel true)
    fi
    read -r http https < <(ports_for yarp)
    taskset -c "$PROXY_CPUS" dotnet "$ROOT/bench/Paduraksa.Bench.Yarp/bin/Release/net10.0/Paduraksa.Bench.Yarp.dll" \
        --upstream "http://127.0.0.1:$UPSTREAM_PORT" --cert "$WORK/certs/cert.pem" --key "$WORK/certs/key.pem" \
        --websocket-upstream "http://127.0.0.1:$ECHO_PORT" --error-page "$CONF/error-502.html" \
        --access-log-dir "$WORK/yarp-access-log" "${cloudflare[@]}" --http "$http" --https "$https" >"$WORK/yarp.log" 2>&1 &
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

# Paduraksa's `cf` scenario: a runner that trusts loopback as a Cloudflare Tunnel (--cloudflare-tunnel true), so the
# CF-Connecting-IP and CF-Visitor headers are honoured for every host and resolved per request.
run_cloudflare() {
    local http https
    read -r http https < <(ports_for paduraksa)
    log "Starting paduraksa as a Cloudflare Tunnel origin (CPUs $PROXY_CPUS)"
    start_paduraksa paduraksa-cf --cloudflare-tunnel true
    check_cf "$http" bench.test
    run_host_scenario paduraksa "$http" cf "$PROXY_WARMUP"
    stop_paduraksa
}

# A second instance of a proxy for the scenarios whose setting is server-wide or static (`variant_of`).
run_variants() {
    local proxy=$1 http https scenario
    read -r http https < <(ports_for "$proxy")
    for scenario in access-log cf; do
        if ! wants "$scenario" || ! variant_of "$proxy" "$scenario" || unsupported "$proxy" "$scenario"; then
            continue
        fi
        if [[ "$proxy" == paduraksa ]]; then
            run_cloudflare
            continue
        fi
        log "Starting $proxy again for $scenario (CPUs $PROXY_CPUS)"
        "start_$proxy" "$scenario"
        if [[ "$scenario" == cf ]]; then
            check_cf "$http" "$(scenario_host "$proxy" cf)"
        else
            curl -sf -o /dev/null -H "Host: $scenario.test" "http://127.0.0.1:$http/" \
                || { echo "$scenario.test: $proxy didn't forward" >&2; return 1; }
        fi
        run_host_scenario "$proxy" "$http" "$scenario" "$PROXY_WARMUP"
        stop_instance "$proxy"
    done
}

stop_instance() {
    case "$1" in
        paduraksa) stop_paduraksa ;;
        yarp) stop_yarp ;;
        *) stop_container "$1" ;;
    esac
}

start_container() {
    local name=$1
    shift
    docker rm -f "$PREFIX-$name" >/dev/null 2>&1 || true
    docker run -d --name "$PREFIX-$name" --network host --cpuset-cpus "$PROXY_CPUS" --ulimit nofile=65536:65536 \
        -v "$WORK/certs:/certs:ro" "$@" >/dev/null
}

start_nginx() {
    start_container nginx -v "$CONF/proxy.nginx.conf:/etc/nginx/nginx.conf:ro" \
        -v "$CONF/error-502.html:/srv/error-502.html:ro" "$NGINX_IMAGE"
    wait_ready bench.test "$(ports_for nginx | cut -d' ' -f1)"
}

# $1 = scenario for a second instance: `cf` adds the server-wide trusted_proxies and client_ip_headers options.
start_caddy() {
    local server_extra=""
    if [[ "${1:-}" == cf ]]; then
        server_extra=$'trusted_proxies static 127.0.0.1/32\n\t\tclient_ip_headers CF-Connecting-IP'
    fi
    start_container caddy -e GOMAXPROCS=4 -e "CADDY_SERVER_EXTRA=$server_extra" \
        -v "$CONF/Caddyfile:/etc/caddy/Caddyfile:ro" -v "$CONF/error-502.html:/srv/error-502.html:ro" "$CADDY_IMAGE"
    wait_ready bench.test "$(ports_for caddy | cut -d' ' -f1)"
}

# HAProxy wants the certificate and its key in one PEM file. An errorfile is a whole HTTP response, so the 502 page
# gets its status line and headers here. The syslog sink for the access-log scenario's lines is a socat on the upstream
# CPUs writing every datagram to a file; a datagram it can't keep up with is lost, which costs HAProxy nothing.
start_haproxy() {
    {
        printf 'HTTP/1.1 502 Bad Gateway\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: %s\r\n\r\n' \
            "$(wc -c <"$CONF/error-502.html")"
        cat "$CONF/error-502.html"
    } >"$WORK/error-502.http"
    if wants access-log; then
        taskset -c "$UPSTREAM_CPUS" socat -u "UDP4-RECV:$SINK_PORT,bind=127.0.0.1" "OPEN:$WORK/haproxy-syslog.log,creat,append" &
        SINK_PID=$!
    fi
    start_container haproxy -v "$CONF/haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro" \
        -v "$WORK/error-502.http:/usr/local/etc/haproxy/error-502.http:ro" "$HAPROXY_IMAGE"
    wait_ready bench.test "$(ports_for haproxy | cut -d' ' -f1)"
}

# $1 = scenario for a second instance: `access-log` swaps in the static config that turns Traefik's access log on.
start_traefik() {
    local static=traefik.yml
    if [[ "${1:-}" == access-log ]]; then
        static=traefik-accesslog.yml
    fi
    start_container traefik -e GOMAXPROCS=4 \
        -v "$CONF/$static:/etc/traefik/traefik.yml:ro" \
        -v "$CONF/traefik-dynamic.yml:/etc/traefik/dynamic.yml:ro" \
        "$TRAEFIK_IMAGE" --configFile=/etc/traefik/traefik.yml
    wait_ready bench.test "$(ports_for traefik | cut -d' ' -f1)"
}

# $1 = scenario for a second instance: `cf` replaces use_remote_address with the original-IP detection of
# envoy-cf.yml (Envoy refuses to have both).
start_envoy() {
    local config="$CONF/envoy.yaml"
    if [[ "${1:-}" == cf ]]; then
        sed "s|^\( *\)use_remote_address: true\$|\1$(cat "$CONF/envoy-cf.yml")|" "$CONF/envoy.yaml" >"$WORK/envoy-cf.yaml"
        config="$WORK/envoy-cf.yaml"
    fi
    start_container envoy -v "$config:/etc/envoy/envoy.yaml:ro" -v "$CONF/error-502.html:/srv/error-502.html:ro" \
        "$ENVOY_IMAGE" -c /etc/envoy/envoy.yaml --concurrency 4 --log-level warn
    wait_ready bench.test "$(ports_for envoy | cut -d' ' -f1)"
}

stop_container() {
    docker logs "$PREFIX-$1" 2>&1 | tail -n 20 >&2 || true
    docker rm -f "$PREFIX-$1" >/dev/null
    if [[ "$1" == haproxy && -n "$SINK_PID" ]]; then
        kill "$SINK_PID" 2>/dev/null || true
        wait "$SINK_PID" 2>/dev/null || true
        SINK_PID=""
    fi
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
        echo "runs: $DURATION (warm-up: $PROXY_WARMUP per proxy, $WARMUP per scenario), c=$CONNS (h1, body-64k, upload-64k and the extra-host paths), ${H2_CONNS}x$H2_STREAMS (h2), c=$HANDSHAKE_CONNS (handshake), $WS_CONNS connections x $WS_MESSAGE B (websocket)"
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

    log "Building the Paduraksa runner, the bare YARP reference and the WebSocket tool (Release)"
    dotnet build "$ROOT/bench/Paduraksa.Bench.Runner" -c Release -nologo -v quiet >&2
    dotnet build "$ROOT/bench/Paduraksa.Bench.Yarp" -c Release -nologo -v quiet >&2
    dotnet build "$ROOT/bench/Paduraksa.Bench.WebSocket" -c Release -nologo -v quiet >&2

    # One throwaway ECDSA P-256 certificate for every proxy that isn't Paduraksa (Paduraksa serves its own P-256
    # placeholder).
    mkdir -p "$WORK/certs"
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 2 -subj "/CN=bench.test" \
        -addext "subjectAltName=DNS:bench.test,DNS:respond.test,DNS:localhost,IP:127.0.0.1" \
        -keyout "$WORK/certs/key.pem" -out "$WORK/certs/cert.pem" 2>/dev/null
    chmod 644 "$WORK/certs/key.pem"
    cat "$WORK/certs/cert.pem" "$WORK/certs/key.pem" >"$WORK/certs/haproxy.pem"
    # The 64 KiB body body-64k streams and upload-64k sends. The upstream config mounts it read-only.
    python3 -I -c 'import sys; sys.stdout.write("x" * 65536)' >"$WORK/64k.txt"
    # The compressed scenario's page: 16 KiB of HTML built from a fixed-seed word list, so it compresses like text (about
    # 4:1 with gzip) rather than like a run of one byte.
    python3 -I -c '
import random, sys
random.seed(7)
words = "the a proxy route host upstream request header path page cache text compress gzip brotli client server".split()
out = []
while sum(map(len, out)) < 16384:
    out.append("<p>" + " ".join(random.choice(words) for _ in range(12)) + "</p>\n")
sys.stdout.write("".join(out)[:16384])' >"$WORK/text.html"

    record_environment

    log "Starting the upstream (nginx, CPUs $UPSTREAM_CPUS)"
    docker rm -f "$PREFIX-upstream" >/dev/null 2>&1 || true
    docker run -d --name "$PREFIX-upstream" --network host --cpuset-cpus "$UPSTREAM_CPUS" --ulimit nofile=65536:65536 \
        -v "$CONF/upstream.nginx.conf:/etc/nginx/nginx.conf:ro" -v "$WORK/64k.txt:/srv/64k.txt:ro" -v "$WORK/text.html:/srv/text.html:ro" \
        -v "$CONF/error-502.html:/srv/error-502.html:ro" \
        "$NGINX_IMAGE" >/dev/null
    wait_ready upstream "$UPSTREAM_PORT"

    if wants websocket; then
        log "Starting the WebSocket echo upstream (CPUs $UPSTREAM_CPUS)"
        taskset -c "$UPSTREAM_CPUS" dotnet "$WS_DLL" echo --port "$ECHO_PORT" >"$WORK/echo.log" 2>&1 &
        ECHO_PID=$!
        wait_ready ws.test "$ECHO_PORT"
    fi

    # The upstream alone, for the ceiling the proxies are measured against (UPSTREAM_DIRECT=0 skips it).
    if [[ "${UPSTREAM_DIRECT:-1}" != 0 ]]; then
        log "upstream direct (h1, c=$CONNS)"
        local direct=(--connect-to "bench.test:$UPSTREAM_PORT:127.0.0.1:$UPSTREAM_PORT" -c "$CONNS")
        oha "" -z "$WARMUP" "${direct[@]}" "http://bench.test:$UPSTREAM_PORT/"
        oha "upstream.h1" -z "$DURATION" "${direct[@]}" "http://bench.test:$UPSTREAM_PORT/"
        if wants websocket; then
            local direct_ws=(--url "ws://ws.test:$ECHO_PORT/" --connect 127.0.0.1 --connections "$WS_CONNS" --size "$WS_MESSAGE")
            wsload "" "${direct_ws[@]}" --duration "$(duration_seconds "$WARMUP")"
            wsload "upstream.websocket" "${direct_ws[@]}" --duration "$(duration_seconds "$DURATION")"
        fi
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
        stop_instance "$proxy"
        run_variants "$proxy"
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
