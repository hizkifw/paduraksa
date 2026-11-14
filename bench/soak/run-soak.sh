#!/usr/bin/env bash
# Soak test (docs/design/testing.md "Soak test", bench/soak/README.md): one real Paduraksa under an hour of realistic traffic and change, sampled every ten seconds, judged by
# bench/soak/analyze.py. It starts the Release build of the server itself (`dotnet paduraksa.dll`, as the container does, with its settings in environment variables), the
# upstreams, the load, the config changes, a flap, a publish burst, a graceful restart halfway, and prints the pass/fail table at the end.
#
#   bench/soak/run-soak.sh --quick                  # 5 minutes: validates the harness and every event once, scaled down
#   bench/soak/run-soak.sh                          # 60 minutes
#   bench/soak/run-soak.sh --duration 1800 --rate 3000
#   bench/soak/run-soak.sh --analyze-only DIR       # judge a run again
#   bench/soak/run-soak.sh --quick --keep-logs      # keep the server's access log (big: for chasing one request in a short run)
#
# Needs: the .NET SDK from global.json, python3, ss (iproute2), curl is not needed. taskset pins the server and the load to separate CPUs when it is there. Chromium
# (PADURAKSA_E2E_CHROMIUM, /usr/bin/chromium) gives the two dashboard tabs; without it the dashboard is left out and the report says so. libmsquic turns HTTP/3 on.
# Exit: 0 every check passed, 1 a check failed, 2 usage, 3 the harness itself failed.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
SOAK="$ROOT/bench/soak"

DURATION=3600
RATE=4000
SERVER_CPUS=${SERVER_CPUS:-0-3}
LOAD_CPUS=${LOAD_CPUS:-4-19}
WEBSOCKETS=300
BROWSER=auto
MCP=true
HTTP3=auto
BUILD=true
KEEP_LOGS=false
OUT=""
ANALYZE_ONLY=""

usage() { sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//' >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --quick) DURATION=300 ;;
        --duration) DURATION=$2; shift ;;
        --rate) RATE=$2; shift ;;
        --out) OUT=$2; shift ;;
        --server-cpus) SERVER_CPUS=$2; shift ;;
        --load-cpus) LOAD_CPUS=$2; shift ;;
        --websockets) WEBSOCKETS=$2; shift ;;
        --no-browser) BROWSER=false ;;
        --no-mcp) MCP=false ;;
        --http3) HTTP3=$2; shift ;;
        --skip-build) BUILD=false ;;
        --keep-logs) KEEP_LOGS=true ;;
        --analyze-only) ANALYZE_ONLY=$2; shift ;;
        -h | --help) usage; exit 0 ;;
        *) echo "unknown option '$1'" >&2; usage; exit 2 ;;
    esac
    shift
done

if [[ -n "$ANALYZE_ONLY" ]]; then
    exec python3 -I "$SOAK/analyze.py" "$ANALYZE_ONLY"
fi

if ! [[ "$DURATION" =~ ^[0-9]+$ ]] || ((DURATION < 240)); then
    echo "--duration is in seconds and at least 240 (the events need room: warm-up, flap, burst, restart, and the windows to compare)" >&2
    exit 2
fi

for tool in dotnet python3 ss; do
    command -v "$tool" >/dev/null || { echo "$tool is needed" >&2; exit 2; }
done

OUT=${OUT:-$SOAK/results/$(date +%Y-%m-%d-%H%M)-soak}
if [[ -e "$OUT" ]]; then
    echo "$OUT exists; pick another --out" >&2
    exit 2
fi
mkdir -p "$OUT"
OUT=$(cd "$OUT" && pwd)

# The harness's ports: the public listeners, the admin listener, the upstreams (19000 and up) and the forward-auth provider. A busy one would fail in an unhelpful way.
for port in 18081 18443 18943 19900 $(seq 19000 19019); do
    if [[ -n "$(ss -Hltn "sport = :$port")" ]]; then
        echo "port $port is in use; stop what holds it (a soak, a bench run) first" >&2
        exit 2
    fi
done

if [[ "$HTTP3" == auto ]]; then
    if ldconfig -p 2>/dev/null | grep -q libmsquic; then HTTP3=true; else HTTP3=false; fi
fi
CHROMIUM=${PADURAKSA_E2E_CHROMIUM:-}
if [[ -z "$CHROMIUM" ]]; then
    for candidate in /usr/bin/chromium /usr/bin/chromium-browser /usr/bin/google-chrome; do
        [[ -x "$candidate" ]] && { CHROMIUM=$candidate; break; }
    done
fi
if [[ "$BROWSER" == auto ]]; then
    if [[ -n "$CHROMIUM" ]]; then BROWSER=true; else BROWSER=false; fi
fi

# Hundreds of sockets and thousands of connections: the default soft limit of 1024 would be the first thing to fail.
ulimit -n 65536 2>/dev/null || ulimit -n "$(ulimit -Hn)" 2>/dev/null || true

DRIVER_PID=""
cleanup() {
    local status=$?
    if [[ -n "$DRIVER_PID" ]] && kill -0 "$DRIVER_PID" 2>/dev/null; then
        kill -TERM "$DRIVER_PID" 2>/dev/null || true
        wait "$DRIVER_PID" 2>/dev/null || true
    fi
    # The driver stops the server itself; this is for a driver that was killed hard. Only the pid it wrote, and only if it is still a paduraksa server.
    if [[ -f "$OUT/server.pid" ]]; then
        local pid
        pid=$(cat "$OUT/server.pid")
        if [[ -n "$pid" ]] && grep -q 'paduraksa.dll' "/proc/$pid/cmdline" 2>/dev/null; then
            kill -TERM "$pid" 2>/dev/null || true
        fi
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ "$BUILD" == true ]]; then
    echo "== Building (Release)" >&2
    dotnet build "$ROOT/src/Paduraksa.Server" -c Release -v q --nologo >&2
    dotnet build "$ROOT/bench/Paduraksa.Bench.Soak" -c Release -v q --nologo >&2
fi
SERVER_DLL="$ROOT/src/Paduraksa.Server/bin/Release/net10.0/paduraksa.dll"
PROBE_DLL="$ROOT/bench/Paduraksa.Bench.Soak.Probe/bin/Release/net10.0/StartupHook.dll"
DRIVER_DLL="$ROOT/bench/Paduraksa.Bench.Soak/bin/Release/net10.0/Paduraksa.Bench.Soak.dll"
for file in "$SERVER_DLL" "$PROBE_DLL" "$DRIVER_DLL"; do
    [[ -f "$file" ]] || { echo "missing $file (build first, or drop --skip-build)" >&2; exit 3; }
done

{
    echo "date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "host: $(uname -srm)"
    echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ //') ($(nproc) logical)"
    echo "memory: $(awk '/MemTotal/ {printf "%.0f GiB", $2/1048576}' /proc/meminfo)"
    echo "dotnet: $(dotnet --version)"
    echo "git: $(git -C "$ROOT" rev-parse --short HEAD) $(git -C "$ROOT" diff --quiet HEAD 2>/dev/null && echo clean || echo 'dirty')"
    echo "server: Release build of src/Paduraksa.Server run as 'dotnet paduraksa.dll' (not the container image: see README)"
    echo "server cpus: $SERVER_CPUS; load cpus: $LOAD_CPUS"
    echo "duration: ${DURATION}s; rate: $RATE req/s; websockets: $WEBSOCKETS"
    echo "http3: $HTTP3 (libmsquic $(ldconfig -p 2>/dev/null | grep -q libmsquic && echo present || echo absent))"
    echo "browser: $BROWSER ${CHROMIUM:+($CHROMIUM $("$CHROMIUM" --version 2>/dev/null | head -1))}"
    echo "mcp: $MCP"
    echo "ulimit -n: $(ulimit -n)"
} >"$OUT/env.txt"

echo "== Soak: $((DURATION / 60)) min at $RATE req/s; output in $OUT" >&2
PIN=()
if command -v taskset >/dev/null; then PIN=(taskset -c "$LOAD_CPUS"); fi
"${PIN[@]}" dotnet "$DRIVER_DLL" \
    --out "$OUT" --server-dll "$SERVER_DLL" --probe-dll "$PROBE_DLL" \
    --duration "$DURATION" --rate "$RATE" --server-cpus "$SERVER_CPUS" --websockets "$WEBSOCKETS" \
    --browser "$BROWSER" --chromium "$CHROMIUM" --mcp "$MCP" --http3 "$HTTP3" --keep-logs "$KEEP_LOGS" \
    > >(tee "$OUT/driver.log") 2>&1 &
DRIVER_PID=$!
set +e
wait "$DRIVER_PID"
DRIVER_STATUS=$?
set -e
DRIVER_PID=""
if [[ "$DRIVER_STATUS" -ne 0 ]]; then
    echo "the driver failed (exit $DRIVER_STATUS); see $OUT/driver.log and $OUT/events.jsonl" >&2
    exit 3
fi

echo >&2
python3 -I "$SOAK/analyze.py" "$OUT"
