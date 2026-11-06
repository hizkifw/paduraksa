#!/usr/bin/env bash
# Spike S3 runner. Usage: ./run.sh [selftest|probe|bench|e2e|all]   (default: all)
# Output is also tee'd to results/<scenario>.txt. Runs are kept short: the machine is shared.
set -euo pipefail
cd "$(dirname "$0")"
dotnet build -c Release -nologo -v q >/dev/null
BIN=(dotnet bin/Release/net10.0/S3.ZeroAllocGate.dll)
mkdir -p results

what="${1:-all}"

run_selftest() { "${BIN[@]}" selftest 2>&1 | tee results/selftest.txt; }
run_probe() { "${BIN[@]}" probe 20000 2>&1 | tee results/probe.txt; }

# BenchmarkDotNet (in-process toolchain, ShortRun). Keep only the summary tables.
# Workstation GC: with the in-process toolchain the benchmark runs in this (Server GC) process, and BDN's
# "Allocated" column was wrong under Server GC (spurious or missing values); it is exact under workstation GC.
run_bench() {
  DOTNET_gcServer=0 "${BIN[@]}" bench 2>&1 | tee results/bench-full.log |
    awk '/^\| Method|^\|-|^\| .*\|$|^BenchmarkDotNet v|^Runtime=|^\/\/ \* Summary/ {print} /^  Job=|^  [A-Z][a-zA-Z]+ *: \.NET/ {print}' > results/bench.txt
  rm -f results/bench-full.log
  cat results/bench.txt
}

# e2e <bare|gate> <h1|h2> <path> [conns] [depth] [warm s] [measure s] [flags...]
#   paths through the gate: /x → proxied by YARP, /static → 200 from the gate, /admin → 403 from a policy
run_e2e() {
  {
    for proto in h1 h2; do
      "${BIN[@]}" e2e bare "$proto" /x 4 16 3 5
      "${BIN[@]}" e2e bare "$proto" /x 4 16 3 5 vary
      "${BIN[@]}" e2e gate "$proto" /static 4 16 3 5
      "${BIN[@]}" e2e gate "$proto" /admin 4 16 3 5
      "${BIN[@]}" e2e gate "$proto" /old 4 16 3 5
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 vary
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 cf
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 noauthz
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 noauthz xfoff
    done
    # Browser-like traffic: 13 Chrome headers (sec-ch-ua*, sec-fetch-*, priority, Cookie, varying Accept/Referer)
    # and 64 rotating paths with query strings. generichost = explicit pipeline, no implicit UseAuthorization().
    for proto in h1 h2; do
      "${BIN[@]}" e2e bare "$proto" /x 4 16 3 5 browser
      "${BIN[@]}" e2e gate "$proto" /static 4 16 3 5 browser generichost
      "${BIN[@]}" e2e gate "$proto" /admin 4 16 3 5 browser generichost
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 browser
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 browser generichost
      "${BIN[@]}" e2e gate "$proto" /x 4 16 3 5 browser generichost cf
    done
    # Which types the proxy allocates (AllocationTick sampling; the listener's own allocations are included).
    "${BIN[@]}" e2e gate h1 /x 4 16 3 5 ticks
    "${BIN[@]}" e2e gate h2 /x 4 16 3 5 ticks
  } 2>&1 | tee results/e2e.txt
}

case "$what" in
  selftest) run_selftest ;;
  probe) run_probe ;;
  bench) run_bench ;;
  e2e) run_e2e ;;
  all) run_selftest; run_probe; run_bench; run_e2e ;;
  *) echo "unknown scenario: $what" >&2; exit 2 ;;
esac
