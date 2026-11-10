"""Summarises a bench/run-baseline.sh run as Markdown tables and summary.json.

Usage: python3 -I bench/baseline/summarize.py bench/results/<run>-baseline

Reads raw/<run>.json (oha), raw/<run>.meta.json (time window, proxy CPU time, upstream TCP states), env.txt and, for
Paduraksa, the runner's per-second METRICS lines in paduraksa*.log (one per runner process). Writes summary.json next
to env.txt for bench/history.py; the Markdown goes to stdout for run-baseline.sh to tee into summary.md.
"""

import json
import re
import statistics
import sys
from pathlib import Path

PROXIES = ["paduraksa", "nginx", "caddy", "haproxy", "traefik", "envoy", "yarp"]
NAMES = {
    "paduraksa": "Paduraksa",
    "nginx": "nginx",
    "caddy": "Caddy",
    "haproxy": "HAProxy",
    "traefik": "Traefik",
    "envoy": "Envoy",
    "yarp": "YARP",
}
SCENARIOS = [
    ("h1", "HTTP/1.1 plain, proxied"),
    ("h1-tls", "HTTPS HTTP/1.1, proxied"),
    ("h2-tls", "HTTPS HTTP/2, proxied"),
    ("browser", "HTTPS HTTP/2, browser-like headers, proxied"),
    ("body-64k", "HTTP/1.1 plain, 64 KiB body, proxied"),
    ("handshake", "HTTPS HTTP/1.1, new connection per request (TLS handshake), proxied"),
    ("answered", "HTTP/1.1 plain, answered by the proxy"),
    ("access-log", "HTTP/1.1 plain, access log on, proxied"),
    ("rate-limit", "HTTP/1.1 plain, rate-limit take, proxied (no 429s expected)"),
    ("rate-limited", "HTTP/1.1 plain, every request refused (429)"),
    ("browser-check", "HTTP/1.1 plain, valid proof-of-work cookie, proxied"),
    ("error-page", "HTTP/1.1 plain, closed upstream, the built-in 502 page"),
    ("cf", "HTTP/1.1 plain, proxied, trusted Cloudflare Tunnel headers"),
]
LABELS = dict(SCENARIOS)
# A proxy that has no built-in static response can't run `answered`; it's a declared skip, not missing data.
UNAVAILABLE = {("traefik", "answered")}
# These two measure what the proxy answers when it doesn't answer 2xx: the refusal and the failure path. Their
# throughput is every response, 429s and 502s included, so that the table says how much abuse or failure the proxy
# absorbed instead of printing zero.
ALL_STATUSES = {"rate-limited", "error-page"}
# The Paduraksa-only paths are always listed in the status breakdown, even when every answer is 200: "no 429s" is
# the rate-limit scenario's result, not a missing row.
STATUS_SCENARIOS = {"access-log", "rate-limit", "rate-limited", "browser-check", "error-page"}
RATE_RUN = re.compile(r"^(?P<proxy>[a-z]+)\.(?P<scenario>[a-z0-9-]+)\.q(?P<rate>\d+)\.r(?P<repeat>\d+)$")
METRICS = re.compile(r"^METRICS (.*)$")

MISSING = "-"


def throughput(scenario: str, result) -> float:
    """Requests per second for this scenario: every response for the non-2xx scenarios, else the successful ones."""
    return result["all_rps"] if scenario in ALL_STATUSES else result["rps"]


def load(raw: Path, name: str):
    path = raw / f"{name}.json"
    if not path.exists():
        return None
    data = json.loads(path.read_text())
    statuses = data.get("statusCodeDistribution", {})
    ok = sum(n for code, n in statuses.items() if code.startswith("2"))
    bad = sum(n for code, n in statuses.items() if not code.startswith("2"))
    errors = sum(n for kind, n in data.get("errorDistribution", {}).items() if kind != "aborted due to deadline")
    seconds = data["summary"]["total"]
    pct = data.get("latencyPercentiles") or {}
    result = {
        "requests": ok + bad,
        "seconds": seconds,
        "rps": ok / seconds if seconds else 0.0,
        "all_rps": (ok + bad) / seconds if seconds else 0.0,
        "statuses": {int(code): n for code, n in statuses.items()},
        "p50": pct.get("p50"),
        "p90": pct.get("p90"),
        "p95": pct.get("p95"),
        "p99": pct.get("p99"),
        "p999": pct.get("p99.9"),
        "failed": bad + errors,
        "meta": None,
    }
    meta_path = raw / f"{name}.meta.json"
    if meta_path.exists():
        result["meta"] = json.loads(meta_path.read_text())
    return result


def load_metrics(out: Path):
    """The runner's METRICS samples. A sample whose GC.GetTotalAllocatedBytes is below the previous sample's is
    dropped (it can go backwards under Server GC, spike S3)."""
    samples, dropped, previous = [], 0, None
    # One log per runner process; a new process restarts the counters, so don't compare across logs.
    lines = [line for log in sorted(out.glob("paduraksa*.log")) for line in [None, *log.read_text().splitlines()]]
    for line in lines:
        if line is None:
            previous = None
            continue
        match = METRICS.match(line)
        if not match:
            continue
        sample = {k: float(v) for k, v in (field.split("=", 1) for field in match[1].split())}
        backwards = previous is not None and sample["alloc"] < previous["alloc"]
        previous = sample
        if backwards:
            dropped += 1
        else:
            samples.append(sample)
    return samples, dropped


def runtime(samples, result):
    """Paduraksa's steady-state runtime counters during one run, as rates between the per-second samples taken while
    oha was sending. The first second of traffic and the last half second are skipped, so neither the warm-up before
    the run nor the gap after it leaks in; runs shorter than ~4 s have too few samples and get none."""
    meta = result["meta"] if result else None
    if not samples or not meta or meta["proxy"] != "paduraksa" or not result["requests"]:
        return None
    traffic_ms = result["seconds"] * 1000
    lo, hi = meta["end_ms"] - traffic_ms + 1000, meta["end_ms"] - 500
    inside = [s for s in samples if lo <= s["ts"] <= hi]
    if len(inside) < 2:
        return None
    first, last = inside[0], inside[-1]
    seconds = (last["ts"] - first["ts"]) / 1000
    requests_per_s = result["requests"] / result["seconds"]
    # The allocation counter isn't reliably monotonic under Server GC (spike S3): besides samples that go backwards
    # (dropped on load), a single high reading makes the next ones look like drops or the run's total wrong. The
    # median of the per-second rates ignores such one-off readings.
    rates = [(b["alloc"] - a["alloc"]) / ((b["ts"] - a["ts"]) / 1000) for a, b in zip(inside, inside[1:])]
    rates = [r for r in rates if r >= 0]
    return {
        "bytes_per_request": statistics.median(rates) / requests_per_s if rates else None,
        "gen0_per_s": (last["gen0"] - first["gen0"]) / seconds,
        "gen1_per_s": (last["gen1"] - first["gen1"]) / seconds,
        "gen2": last["gen2"] - first["gen2"],
        "pause_ms_per_s": (last["pause_ms"] - first["pause_ms"]) / seconds,
        "max_pause_ms": max(s["max_pause_ms"] for s in inside[1:]),
        "tp_queue_max": max(s["tp_queue"] for s in inside),
        "tp_threads_max": max(s["tp_threads"] for s in inside),
    }


def ms(seconds) -> str:
    return MISSING if seconds is None else f"{seconds * 1000:.2f}"


def num(value, digits=0) -> str:
    return MISSING if value is None else f"{value:,.{digits}f}"


def cpu(result):
    """(µs of proxy CPU per request, average CPUs busy) for a run with metadata."""
    meta = result["meta"] if result else None
    if not meta or not result["requests"]:
        return None, None
    # The CPU window also spans oha's container start and stop (~1 s, the proxy idle), so "busy" uses oha's own
    # sending time; it slightly overstates load by whatever the idle proxy burned meanwhile.
    return meta["cpu_us"] / result["requests"], meta["cpu_us"] / 1e6 / result["seconds"]


def env_values(out: Path) -> dict[str, str]:
    text = (out / "env.txt").read_text()
    values = {}
    for line in text.splitlines():
        key, separator, value = line.partition(": ")
        if separator:
            values[key] = value
    return values


def env_dirty(text: str) -> bool:
    """Newer env.txt files carry an explicit `dirty:` line; older ones only have the indented status block."""
    for line in text.splitlines():
        if line.startswith("dirty: "):
            return line.removeprefix("dirty: ").strip() == "true"
    lines = text.splitlines()
    for index, line in enumerate(lines):
        if not line.startswith("git status --porcelain"):
            continue
        for follow in lines[index + 1:]:
            if not follow.startswith(" "):
                break
            if follow.strip():
                return True
        break
    return False


def summary_json(out: Path, proxies, runs, rate_runs) -> dict:
    text = (out / "env.txt").read_text()
    values = env_values(out)
    machine = values.get("cpu", "")
    cpus = re.search(r"^(\d+) logical", values.get("cpus", ""))
    sets = re.search(r"proxy=(\S+) upstream=(\S+) load=(\S+)", values.get("cpu sets", ""))
    results = []
    for proxy in proxies:
        for scenario, _ in SCENARIOS:
            result = runs.get((proxy, scenario))
            if result is None:
                continue
            per_request, busy = cpu(result)
            results.append({
                "proxy": proxy,
                "scenario": scenario,
                "rate": None,
                "rps": round(throughput(scenario, result), 4),
                "statuses": {str(code): n for code, n in sorted(result["statuses"].items())},
                "p50_ms": None if result["p50"] is None else round(result["p50"] * 1000, 4),
                "p99_ms": None if result["p99"] is None else round(result["p99"] * 1000, 4),
                "p999_ms": None if result["p999"] is None else round(result["p999"] * 1000, 4),
                "cpu_us_per_req": None if per_request is None else round(per_request, 4),
                "busy_cpus": None if busy is None else round(busy, 4),
                "errors": result["failed"],
            })
    order = {key: index for index, (key, _) in enumerate(SCENARIOS)}
    for (scenario, rate, proxy) in sorted(
            rate_runs, key=lambda key: (order.get(key[0], 99), key[1], PROXIES.index(key[2]))):
        results_for_rate = rate_runs[(scenario, rate, proxy)]

        def median(field):
            values_for_field = [r[field] for r in results_for_rate if r[field] is not None]
            return statistics.median(values_for_field) if values_for_field else None

        per_requests = [cpu(r)[0] for r in results_for_rate if cpu(r)[0] is not None]
        busy_values = [cpu(r)[1] for r in results_for_rate if cpu(r)[1] is not None]
        results.append({
            "proxy": proxy,
            "scenario": scenario,
            "rate": rate,
            "rps": round(median("rps"), 4),
            "p50_ms": None if median("p50") is None else round(median("p50") * 1000, 4),
            "p99_ms": None if median("p99") is None else round(median("p99") * 1000, 4),
            "p999_ms": None if median("p999") is None else round(median("p999") * 1000, 4),
            "cpu_us_per_req": None if not per_requests else round(statistics.median(per_requests), 4),
            "busy_cpus": None if not busy_values else round(statistics.median(busy_values), 4),
            "errors": sum(r["failed"] for r in results_for_rate),
        })

    return {
        "date": values.get("date"),
        "commit": values.get("commit"),
        "dirty": env_dirty(text),
        "machine": {"cpu": machine, "logical_cpus": int(cpus.group(1)) if cpus else None},
        "cpu_sets": {
            "proxy": sets.group(1) if sets else None,
            "upstream": sets.group(2) if sets else None,
            "load": sets.group(3) if sets else None,
        },
        "results": results,
    }


def main() -> None:
    out = Path(sys.argv[1])
    raw = out / "raw"
    samples, dropped = load_metrics(out)
    lines = []
    emit = lines.append

    runs = {(p, k): load(raw, f"{p}.{k}") for p in PROXIES for k, _ in SCENARIOS}
    proxies = [p for p in PROXIES if any(runs[(p, k)] for k, _ in SCENARIOS)]
    scenarios = [(k, label) for k, label in SCENARIOS if any(runs[(p, k)] for p in proxies)]

    if scenarios:
        emit("### Throughput at fixed concurrency (successful requests/s)")
        emit("")
        ratios = ["nginx"] if "paduraksa" in proxies and "nginx" in proxies else []
        emit("| Scenario | " + " | ".join(NAMES[p] for p in proxies)
             + "".join(f" | Paduraksa ÷ {NAMES[p]}" for p in ratios) + " |")
        emit("|---|" + "---:|" * (len(proxies) + len(ratios)))
        for key, label in scenarios:
            cells = [num(throughput(key, runs[(p, key)])) if runs[(p, key)] else MISSING for p in proxies]
            for other in ratios:
                mine, theirs = runs[("paduraksa", key)], runs[(other, key)]
                cells.append(f"{throughput(key, mine) / throughput(key, theirs):.2f}"
                             if mine and theirs and throughput(key, theirs) else MISSING)
            emit(f"| {label} | " + " | ".join(cells) + " |")
        upstream = load(raw, "upstream.h1")
        if upstream is not None:
            emit("")
            emit(f"Upstream alone (HTTP/1.1, same concurrency): {num(upstream['rps'])} requests/s.")
        measured = [key for key, _ in scenarios if key in ALL_STATUSES]
        if measured:
            emit("")
            emit("For " + " and ".join(f"`{key}`" for key in measured)
                 + ", the number is every response, 429s or 502s included: those scenarios measure what the proxy")
            emit("answers when it refuses or fails, so counting only 2xx would report zero.")

        emit("")
        emit("### Proxy CPU per request (µs of user + system CPU; in brackets, CPUs busy on average out of 4)")
        emit("")
        emit("| Scenario | " + " | ".join(NAMES[p] for p in proxies) + " |")
        emit("|---|" + "---:|" * len(proxies))
        for key, label in scenarios:
            cells = []
            for p in proxies:
                per_request, busy = cpu(runs[(p, key)])
                cells.append(MISSING if per_request is None else f"{per_request:.1f} ({busy:.2f})")
            emit(f"| {label} | " + " | ".join(cells) + " |")

        emit("")
        emit("### Latency at fixed concurrency (ms)")
        emit("")
        emit("At a fixed number of requests in flight, mean latency is concurrency ÷ throughput (Little's law), so the")
        emit("medians here restate the throughput table; the tails are the informative part.")
        emit("")
        emit("| Scenario | Proxy | req/s | p50 | p90 | p99 | p99.9 | failed |")
        emit("|---|---|---:|---:|---:|---:|---:|---:|")
        for key, label in scenarios:
            for p in proxies:
                r = runs[(p, key)]
                if r is not None:
                    emit(f"| {label} | {NAMES[p]} | {num(throughput(key, r))} | {ms(r['p50'])} | {ms(r['p90'])} | {ms(r['p99'])} "
                         f"| {ms(r['p999'])} | {r['failed']} |")
                elif (p, key) in UNAVAILABLE:
                    emit(f"| {label} | {NAMES[p]} | {MISSING} | {MISSING} | {MISSING} | {MISSING} "
                         f"| {MISSING} | {MISSING} |")

        status_rows = []
        codes = set()
        for key, label in scenarios:
            for p in proxies:
                r = runs[(p, key)]
                if r is None or (key not in STATUS_SCENARIOS and all(code == 200 for code in r["statuses"])):
                    continue
                status_rows.append((key, label, p, r))
                codes.update(r["statuses"])
        if status_rows:
            emit("")
            emit("### Response status codes at fixed concurrency")
            emit("")
            emit("A pair is listed when it isn't all 200, and the Paduraksa-only paths are listed even when they are.")
            emit("")
            order = sorted(codes)
            emit("| Scenario | Proxy | " + " | ".join(str(code) for code in order) + " |")
            emit("|---|---|" + "---:|" * len(order))
            for key, label, p, r in status_rows:
                cells = [f"{r['statuses'].get(code, 0):,}" for code in order]
                emit(f"| {label} | {NAMES[p]} | " + " | ".join(cells) + " |")
            limit = runs.get(("paduraksa", "rate-limit"))
            if limit is not None:
                refused = limit["statuses"].get(429, 0)
                emit("")
                if refused:
                    emit(f"`rate-limit` answered {refused:,} 429s: the policy's rate or burst is below the load, so it no")
                    emit("longer measures the token take alone.")
                else:
                    emit("`rate-limit` answered no 429s: the policy stayed inside its budget, so the run measures the")
                    emit("token take on one hot bucket, as intended.")

    rate_runs = {}
    for path in sorted(raw.glob("*.json")):
        match = RATE_RUN.match(path.stem)
        if match and not path.stem.endswith(".meta"):
            key = (match["scenario"], int(match["rate"]), match["proxy"])
            rate_runs.setdefault(key, []).append(load(raw, path.stem))
    if rate_runs:
        repeats = max(len(v) for v in rate_runs.values())
        emit("")
        emit("### Latency at fixed request rates (ms, corrected for coordinated omission)")
        emit("")
        if repeats > 1:
            emit(f"Median of {repeats} runs per row; the p99 column also shows the lowest and highest run.")
            emit("")
        emit("| Scenario | Rate | Proxy | achieved req/s | p50 | p90 | p95 | p99 | p99.9 | CPU µs/req | failed |")
        emit("|---|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|")
        scenario_order = {k: i for i, (k, _) in enumerate(SCENARIOS)}
        for (scenario, rate, proxy) in sorted(rate_runs, key=lambda k: (scenario_order.get(k[0], 99), k[1], PROXIES.index(k[2]))):
            results = rate_runs[(scenario, rate, proxy)]

            def med(field):
                values = [r[field] for r in results if r[field] is not None]
                return statistics.median(values) if values else None

            p99 = ms(med("p99"))
            if len(results) > 1:
                p99 += f" ({ms(min(r['p99'] for r in results))}-{ms(max(r['p99'] for r in results))})"
            cpus = [cpu(r)[0] for r in results if cpu(r)[0] is not None]
            emit(f"| {LABELS.get(scenario, scenario)} | {num(rate)} | {NAMES[proxy]} | {num(med('rps'))} | {ms(med('p50'))} "
                 f"| {ms(med('p90'))} | {ms(med('p95'))} | {p99} | {ms(med('p999'))} "
                 f"| {num(statistics.median(cpus), 1) if cpus else MISSING} | {sum(r['failed'] for r in results)} |")

    paduraksa_runs = [(k, runs[("paduraksa", k)]) for k, _ in SCENARIOS if runs.get(("paduraksa", k))]
    paduraksa_runs += [(f"{s} at {num(rate)} req/s (run {i + 1})", r)
                       for (s, rate, p), results in sorted(rate_runs.items()) if p == "paduraksa"
                       for i, r in enumerate(results)]
    stats = [(name, runtime(samples, r)) for name, r in paduraksa_runs]
    stats = [(name, s) for name, s in stats if s]
    if stats:
        emit("")
        emit("### Paduraksa runtime counters per run (whole process, from the runner's per-second samples)")
        emit("")
        emit("Allocated bytes are the median per-second rate of `GC.GetTotalAllocatedBytes(precise: true)` ÷ the request rate: Kestrel, routing,")
        emit("YARP and the gate together (PLAN §7.1 tracks these here; the gate's own share is asserted 0 B in tests).")
        if dropped:
            emit(f"{dropped} non-monotonic allocation sample(s) were dropped.")
        emit("")
        emit("| Run | B/request | gen0 GCs/s | gen1 GCs/s | gen2 GCs | GC pause ms/s | max pause ms | max thread-pool queue | max threads |")
        emit("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
        for name, s in stats:
            emit(f"| {LABELS.get(name, name)} | {num(s['bytes_per_request'])} | {s['gen0_per_s']:.1f} | {s['gen1_per_s']:.1f} "
                 f"| {s['gen2']:.0f} | {s['pause_ms_per_s']:.2f} | {s['max_pause_ms']:.2f} | {s['tp_queue_max']:.0f} "
                 f"| {s['tp_threads_max']:.0f} |")

    h1 = [(p, runs[(p, "h1")]) for p in proxies if runs.get((p, "h1")) and runs[(p, "h1")]["meta"]]
    if h1:
        emit("")
        emit("### Proxy → upstream TCP connections at the end of the plain HTTP/1.1 run")
        emit("")
        emit("| Proxy | established | TIME_WAIT |")
        emit("|---|---:|---:|")
        for p, r in h1:
            emit(f"| {NAMES[p]} | {r['meta']['upstream_established']} | {r['meta']['upstream_time_wait']} |")

    (out / "summary.json").write_text(json.dumps(summary_json(out, proxies, runs, rate_runs), indent=2) + "\n")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
