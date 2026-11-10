"""Keeps a history of bench/run-baseline.sh runs and regenerates bench/RESULTS.md.

Usage:
    python3 -I bench/history.py append bench/results/<run>-baseline [--allow-dirty] [--new-machine]
    python3 -I bench/history.py report

`append` adds the run's summary.json as one line to bench/results/history.jsonl. It refuses a dirty run (unless
--allow-dirty) and a run from a different machine than the previous entries (unless --new-machine). `report` reads the
history and writes bench/RESULTS.md: the latest run's tables, Paduraksa's trend per commit, and the change since the
previous entry, flagging a >10% throughput loss or a >25% fixed-rate p99 increase as a possible regression.
"""

import argparse
import json
import sys
from pathlib import Path

BENCH = Path(__file__).resolve().parent
RESULTS = BENCH / "results"
HISTORY = RESULTS / "history.jsonl"
REPORT = BENCH / "RESULTS.md"

NAMES = {
    "paduraksa": "Paduraksa",
    "nginx": "nginx",
    "caddy": "Caddy",
    "haproxy": "HAProxy",
    "traefik": "Traefik",
    "envoy": "Envoy",
    "yarp": "YARP",
}
PROXY_ORDER = ["paduraksa", "nginx", "caddy", "haproxy", "traefik", "envoy", "yarp"]
SCENARIO_ORDER = ["h1", "h1-tls", "h2-tls", "browser", "body-64k", "handshake", "answered", "cf"]
MAIN_SCENARIOS = ["h1", "h2-tls"]
MISSING = "—"


def load_history(path: Path = HISTORY) -> list[dict]:
    if not path.exists():
        return []
    entries = []
    for line in path.read_text().splitlines():
        if line.strip():
            entries.append(json.loads(line))
    return entries


def ordered(proxies) -> list[str]:
    return [p for p in PROXY_ORDER if p in proxies] + sorted(p for p in proxies if p not in PROXY_ORDER)


def machine_of(entry: dict) -> dict:
    return entry.get("machine") or {}


def short_commit(commit) -> str:
    return (commit or "?")[:8]


def results_of(entry: dict) -> dict:
    return {(r["proxy"], r["scenario"], r["rate"]): r for r in entry.get("results", [])}


def fmt(value, digits=0) -> str:
    if value is None:
        return MISSING
    return f"{value:,.{digits}f}"


def pct(value) -> str:
    if value is None:
        return MISSING
    return f"{value:+.1f}%"


def latest_tables(lines: list[str], entry: dict) -> None:
    results = entry.get("results", [])
    proxies = ordered({r["proxy"] for r in results})
    scenarios = []
    for result in results:
        if result["rate"] is None and result["scenario"] not in scenarios:
            scenarios.append(result["scenario"])
    by_key = results_of(entry)

    lines.append("## Latest run")
    lines.append("")
    lines.append(f"{entry.get('date') or '?'} · commit `{short_commit(entry.get('commit'))}`"
                 f"{' (dirty tree)' if entry.get('dirty') else ''} · "
                 f"{machine_of(entry).get('cpu') or '?'} · "
                 f"{machine_of(entry).get('logical_cpus') or '?'} logical CPUs · "
                 f"CPU sets: proxy {entry.get('cpu_sets', {}).get('proxy') or '?'}, "
                 f"load {entry.get('cpu_sets', {}).get('load') or '?'}, "
                 f"upstream {entry.get('cpu_sets', {}).get('upstream') or '?'}.")
    lines.append("")

    if not results:
        lines.append("The run recorded no results.")
        return

    lines.append("### Throughput at fixed concurrency (successful requests/s)")
    lines.append("")
    ratios = ["nginx"] if "paduraksa" in proxies and "nginx" in proxies else []
    lines.append("| Scenario | " + " | ".join(NAMES.get(p, p) for p in proxies)
                 + "".join(f" | Paduraksa ÷ {NAMES.get(p, p)}" for p in ratios) + " |")
    lines.append("|---|" + "---:|" * (len(proxies) + len(ratios)))
    for scenario in scenarios:
        cells = [fmt((by_key.get((p, scenario, None)) or {}).get("rps")) for p in proxies]
        for other in ratios:
            mine = by_key.get(("paduraksa", scenario, None))
            theirs = by_key.get((other, scenario, None))
            cells.append(f"{mine['rps'] / theirs['rps']:.2f}"
                         if mine and theirs and theirs["rps"] else MISSING)
        lines.append(f"| {scenario} | " + " | ".join(cells) + " |")
    lines.append("")

    lines.append("### Latency at fixed concurrency (ms)")
    lines.append("")
    lines.append("| Scenario | Proxy | req/s | p50 | p99 | p99.9 | errors |")
    lines.append("|---|---|---:|---:|---:|---:|---:|")
    for scenario in scenarios:
        for proxy in proxies:
            result = by_key.get((proxy, scenario, None))
            if result is None:
                continue
            lines.append(f"| {scenario} | {NAMES.get(proxy, proxy)} | {fmt(result.get('rps'))} "
                         f"| {fmt(result.get('p50_ms'), 2)} | {fmt(result.get('p99_ms'), 2)} "
                         f"| {fmt(result.get('p999_ms'), 2)} | {result.get('errors', MISSING)} |")
    lines.append("")


def trend_table(lines: list[str], entries: list[dict]) -> None:
    lines.append("## Paduraksa trend per commit")
    lines.append("")
    lines.append("Throughput and p99 at fixed concurrency, with the ratio to nginx from the same run.")
    lines.append("")
    header = "| Date | Commit |"
    rule = "|---|---|"
    for scenario in MAIN_SCENARIOS:
        header += f" {scenario} req/s | {scenario} p99 | {scenario} ÷ nginx |"
        rule += "---:|---:|---:|"
    lines.append(header)
    lines.append(rule)
    for entry in entries:
        by_key = results_of(entry)
        cells = [entry.get("date") or "?", f"`{short_commit(entry.get('commit'))}`"
                 f"{'*' if entry.get('dirty') else ''}"]
        for scenario in MAIN_SCENARIOS:
            mine = by_key.get(("paduraksa", scenario, None))
            nginx = by_key.get(("nginx", scenario, None))
            cells.append(fmt((mine or {}).get("rps")))
            cells.append(fmt((mine or {}).get("p99_ms"), 2))
            cells.append(f"{mine['rps'] / nginx['rps']:.2f}"
                         if mine and nginx and nginx.get("rps") else MISSING)
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("")
    if any(entry.get("dirty") for entry in entries):
        lines.append("`*` marks a run from a dirty tree.")
        lines.append("")


def delta_table(lines: list[str], latest: dict, previous: dict | None) -> None:
    lines.append("## Change since the previous run")
    lines.append("")
    if previous is None:
        lines.append("This is the first recorded run; there is nothing to compare against yet.")
        lines.append("")
        return

    if machine_of(latest) != machine_of(previous):
        lines.append("The two runs are from different machines, so this comparison is informational only.")
        lines.append("")

    latest_results = results_of(latest)
    previous_results = results_of(previous)
    lines.append("| Scenario | Rate | Metric | Previous | Latest | Change | |")
    lines.append("|---|---:|---|---:|---:|---:|---|")
    seen = False
    latest_scenarios = {key[1] for key in latest_results if key[0] == "paduraksa"}
    ordered_scenarios = sorted(
        latest_scenarios, key=lambda key: SCENARIO_ORDER.index(key) if key in SCENARIO_ORDER else len(SCENARIO_ORDER))
    for scenario in ordered_scenarios:
        fixed = latest_results.get(("paduraksa", scenario, None))
        old_fixed = previous_results.get(("paduraksa", scenario, None))
        if fixed and old_fixed and old_fixed.get("rps"):
            change = (fixed["rps"] / old_fixed["rps"] - 1) * 100
            note = "**possible regression**" if change < -10 else ""
            lines.append(f"| {scenario} | {MISSING} | req/s | {fmt(old_fixed['rps'])} | {fmt(fixed['rps'])} "
                         f"| {pct(change)} | {note} |")
            seen = True
        for (proxy, key, rate), fixed in sorted(latest_results.items(), key=lambda item: item[0][2] or 0):
            if proxy != "paduraksa" or key != scenario or rate is None:
                continue
            old_fixed = previous_results.get((proxy, key, rate))
            if not fixed or not old_fixed or not old_fixed.get("p99_ms") or not fixed.get("p99_ms"):
                continue
            change = (fixed["p99_ms"] / old_fixed["p99_ms"] - 1) * 100
            note = "**possible regression**" if change > 25 else ""
            lines.append(f"| {scenario} | {rate:,} | p99 ms | {fmt(old_fixed['p99_ms'], 2)} "
                         f"| {fmt(fixed['p99_ms'], 2)} | {pct(change)} | {note} |")
            seen = True
    if not seen:
        lines.append("| — | — | — | — | — | — | No comparable Paduraksa results. |")
    lines.append("")


def write_report(entries: list[dict]) -> None:
    lines = ["# Benchmark results", ""]
    lines.append("Generated by `bench/history.py report` from `bench/results/history.jsonl`. "
                 "Method, fairness rules and caveats: [bench/README.md](README.md). Runs are recorded with "
                 "`RECORD=1 bench/run-baseline.sh`; only `summary.md`, `summary.json` and `env.txt` are committed.")
    lines.append("")
    if not entries:
        lines.append("No runs have been recorded yet.")
    else:
        lines.append(f"{len(entries)} recorded run(s).")
        lines.append("")
        latest_tables(lines, entries[-1])
        trend_table(lines, entries)
        delta_table(lines, entries[-1], entries[-2] if len(entries) > 1 else None)
    REPORT.write_text("\n".join(lines).rstrip() + "\n")


def append_run(run_dir: Path, allow_dirty: bool, new_machine: bool) -> int:
    summary_path = run_dir / "summary.json"
    if not summary_path.exists():
        print(f"{summary_path} is missing; run bench/baseline/summarize.py on the run first", file=sys.stderr)
        return 1
    entry = json.loads(summary_path.read_text())
    if entry.get("dirty") and not allow_dirty:
        print("the run is from a dirty tree; commit first, or pass --allow-dirty", file=sys.stderr)
        return 1

    entries = load_history()
    if entries and not new_machine:
        previous = machine_of(entries[-1])
        if machine_of(entry) != previous:
            print(f"the run is from a different machine than the last entry ({previous}); "
                  "pass --new-machine if that's intended", file=sys.stderr)
            return 1

    HISTORY.parent.mkdir(parents=True, exist_ok=True)
    with HISTORY.open("a") as history:
        history.write(json.dumps(entry, separators=(",", ":")) + "\n")
    print(f"appended {run_dir} to {HISTORY}", file=sys.stderr)
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Keep and report a history of baseline runs.")
    commands = parser.add_subparsers(dest="command", required=True)
    append = commands.add_parser("append", help="append a run's summary.json to results/history.jsonl")
    append.add_argument("run_dir", type=Path)
    append.add_argument("--allow-dirty", action="store_true", help="accept a run from a dirty tree")
    append.add_argument("--new-machine", action="store_true", help="accept a run from a different machine")
    commands.add_parser("report", help="regenerate bench/RESULTS.md from the history")
    args = parser.parse_args()

    if args.command == "append":
        return append_run(args.run_dir, args.allow_dirty, args.new_machine)

    entries = load_history()
    write_report(entries)
    print(f"wrote {REPORT} from {len(entries)} run(s)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
