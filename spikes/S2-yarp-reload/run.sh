#!/usr/bin/env bash
# Spike S2 runner. Usage: ./run.sh [metadata|reload|load|health|all]   (default: all)
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

case "$what" in
  metadata) run_metadata ;;
  reload) run_reload ;;
  load) run_load ;;
  health) run_health ;;
  all) run_metadata; run_reload; run_load; run_health ;;
  *) echo "unknown scenario: $what" >&2; exit 2 ;;
esac
