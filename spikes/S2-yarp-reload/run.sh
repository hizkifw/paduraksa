#!/usr/bin/env bash
# Spike S2 runner. Usage: ./run.sh [metadata|reload|load|health|gate|all]   (default: all)
# Output is also tee'd to results/<scenario>.txt.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build -c Release -nologo -v q >/dev/null
BIN=(dotnet bin/Release/net10.0/S2.YarpReload.dll)
mkdir -p results

what="${1:-all}"

run_metadata() { "${BIN[@]}" metadata 2>&1 | tee results/metadata.txt; }

run_reload() {
  : > results/reload.txt
  # One process per (size, mode) so JIT tiering / GC history of one case doesn't skew the next.
  for n in 100 500 2000; do
    for mode in one-route 10pct-routes 10pct-clusters noop gen-ids path-only; do
      "${BIN[@]}" reload "$n" "$mode" 2>&1 | grep -v '^Server GC' | tee -a results/reload.txt
    done
  done
}

run_load() {
  : > results/load.txt
  for n in 500 2000; do
    "${BIN[@]}" load "$n" 64 10 2>&1 | tee -a results/load.txt
  done
}

run_health() { "${BIN[@]}" health 2>&1 | tee results/health.txt; }

# Review follow-up: gate-selected YARP endpoints (probe (i)-(v), publish cost, short churn loads).
run_gate() {
  {
    "${BIN[@]}" gate
    "${BIN[@]}" gate-cost 500,2000
    for n in 500 2000; do
      "${BIN[@]}" gate-load GateSelect "$n" 64 5
      "${BIN[@]}" gate-load RoutingFirst "$n" 64 5
      "${BIN[@]}" gate-load RoutingFirst "$n" 64 5 pending
    done
  } 2>&1 | tee results/gate.txt
}

case "$what" in
  metadata) run_metadata ;;
  reload) run_reload ;;
  load) run_load ;;
  health) run_health ;;
  gate) run_gate ;;
  all) run_metadata; run_reload; run_load; run_health; run_gate ;;
  *) echo "unknown scenario: $what" >&2; exit 2 ;;
esac
