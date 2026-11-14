#!/usr/bin/env python3
"""Judges one soak run: reads what the driver wrote (plan.json, samples.csv, facts.json, publishes.json, events.jsonl, server.log) and prints the pass/fail table.

    python3 -I bench/soak/analyze.py <run directory>

Writes summary.md and results.json next to the data and exits 1 when any check fails. The thresholds are all in THRESHOLDS; bench/soak/README.md gives the reasoning for each.
"""
import csv
import json
import math
import re
import statistics
import sys
from pathlib import Path

# Every limit the checks use, in one place. README.md ("Thresholds") says why each is what it is.
THRESHOLDS = {
    # The leak fits need a stretch long enough to tell a slope from noise.
    "min_segment_seconds": 600,
    "settle_after_restart_seconds": 600,
    "settle_after_event_seconds": 300,
    "rss_mb_per_hour": 64.0,
    "retained_mb_per_hour": 16.0,
    "fds_per_hour": 30.0,
    "fds_band": 100,
    "threads_band": 16,
    "tp_threads_band": 8,
    # Latency: the last window's p99 against the baseline window's, with the histogram's 3% bucket error and a quarter millisecond of slack on top.
    "p99_drift_ratio": 1.5,
    "p99_drift_slack_ms": 0.25,
    # Publishes.
    "publish_p99_ms": 500.0,
    "publish_trend_ratio": 1.5,
    "publish_trend_slack_ms": 5.0,
    "burst_slip_fraction": 0.10,
    "burst_issued_fraction": 0.90,
    "live_p99_ms": 1000.0,
    "canary_lag_revisions": 3,
    # S6: the limited host's 200s per second during the burst against the same measure outside it.
    "limited_ok_ratio": 1.25,
    "limited_ok_slack": 2.0,
    # The limited host's policy is 20/s with a burst of 20; the 200s per second it lets through in the baseline window must be about that, however busy the other hosts are.
    "limited_rate_factor": 1.5,
    "limited_rate_slack": 3.0,
    # Flap and restart.
    "flap_detect_seconds": 20.0,
    "flap_recover_seconds": 20.0,
    "restart_ready_seconds": 50.0,
    "restart_proxied_seconds": 55.0,
    "restart_healthy_seconds": 75.0,
    # Slow clients: the listener's header timeout is 15 s and its body-rate grace 10 s, with a margin for the client's own cadence.
    "slowloris_cut_seconds": 25.0,
    "slow_body_cut_seconds": 30.0,
    "ws_idle_slack_seconds": 30.0,
    # The generator must have kept up: the rate it achieved against the one asked for.
    "rate_achieved_fraction": 0.95,
    "mcp_min_calls_fraction": 0.8,
}

T = THRESHOLDS


def theil_sen(points):
    """The median of the pairwise slopes: a few outliers (a GC, a hiccup) don't move it."""
    slopes = []
    n = len(points)
    step = max(1, n // 400)
    sub = points[::step]
    for i in range(len(sub)):
        for j in range(i + 1, len(sub)):
            dx = sub[j][0] - sub[i][0]
            if dx > 0:
                slopes.append((sub[j][1] - sub[i][1]) / dx)
    return statistics.median(slopes) if slopes else float("nan")


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return float("nan")


def load(directory):
    run = Path(directory)
    plan = json.loads((run / "plan.json").read_text())
    facts = json.loads((run / "facts.json").read_text())
    with open(run / "samples.csv", newline="") as handle:
        samples = [{k: number(v) for k, v in row.items()} for row in csv.DictReader(handle)]
    publishes = json.loads((run / "publishes.json").read_text())
    events = [json.loads(line) for line in (run / "events.jsonl").read_text().splitlines() if line.strip()]
    log = (run / "server.log").read_text(errors="replace").splitlines() if (run / "server.log").exists() else []
    return run, plan, facts, samples, publishes, events, log


class Report:
    def __init__(self):
        self.rows = []

    def add(self, group, name, status, value, limit, note=""):
        self.rows.append({"group": group, "check": name, "status": status, "value": value, "limit": limit, "note": note})

    def ok(self, group, name, passed, value, limit, note=""):
        self.add(group, name, "PASS" if passed else "FAIL", value, limit, note)

    def info(self, group, name, value, note=""):
        self.add(group, name, "INFO", value, "", note)

    def skip(self, group, name, why):
        self.add(group, name, "SKIP", "", "", why)

    @property
    def failed(self):
        return [r for r in self.rows if r["status"] == "FAIL"]


def fmt(value):
    if isinstance(value, float):
        if math.isnan(value):
            return "n/a"
        return f"{value:.3g}" if abs(value) < 1000 else f"{value:,.0f}"
    return str(value)


def window_ms(window):
    return window.get("p99Ms") if window else None


def check_errors(report, plan, facts, log_findings):
    requests = facts["requests"]
    kinds = facts["errorKinds"]
    unexpected = {k.split(":", 1)[1]: v for k, v in kinds.items() if k.startswith("unexpected:")}
    tolerated = sum(v for k, v in kinds.items() if k.startswith("tolerated:"))
    report.ok("Errors", "no unexpected client errors", requests["unexpected"] == 0, requests["unexpected"], 0,
              note=(", ".join(f"{k}={v}" for k, v in sorted(unexpected.items())) or f"{tolerated} tolerated in the flap and restart windows") + f"; {requests['requests']:,} requests")
    report.ok("Errors", "no unexpected server log errors", not log_findings["unexpected_errors"], len(log_findings["unexpected_errors"]), 0,
              note="; ".join(log_findings["unexpected_errors"][:3]))
    report.info("Errors", "server log warnings outside the expected ones", len(log_findings["unexpected_warnings"]), "; ".join(log_findings["unexpected_warnings"][:3]))
    generated = facts["generator"]["skipped"]
    report.ok("Errors", "generator kept up (no skipped requests)", generated == 0, generated, 0)


def quiet_points(plan, samples, facts, field, residual=None):
    """The samples a leak fit may use, one list per server process: after the warm-up, outside the stretches the test itself disturbs (the flap and the burst, and the minutes
    the collector takes to give back what they cost; the restart and the time a new process needs to settle)."""
    p = plan["plan"]
    restart = facts.get("restart") or {}
    begin = restart.get("beginT")
    ready = restart.get("readySecondsFromSignal", 0)
    busy = [(p["FlapStart"] - 10, max(p["FlapStart"] + p["FlapSeconds"], p["BurstStart"] + p["BurstSeconds"]) + T["settle_after_event_seconds"])]
    if begin is not None:
        busy.append((begin - 10, begin + ready + T["settle_after_restart_seconds"]))
    by_process = {}
    for s in samples:
        t = s["t"]
        if t < p["WarmupSeconds"] or t > p["DurationSeconds"] or math.isnan(s.get(field, float("nan"))) or any(lo <= t <= hi for lo, hi in busy):
            continue
        value = s[field] - (residual(s) if residual else 0)
        by_process.setdefault(s["pid"], []).append((t, value))
    return by_process


def fit(by_process):
    """(worst slope per hour, the spread p99 - p1, a description) over the processes that have a stretch long enough, or None."""
    results = []
    for pid, rows in by_process.items():
        if len(rows) < 20 or rows[-1][0] - rows[0][0] < T["min_segment_seconds"]:
            results.append((pid, None, None, rows[-1][0] - rows[0][0] if rows else 0))
            continue
        values = sorted(v for _, v in rows)
        spread = values[max(0, int(len(values) * 0.99) - 1)] - values[int(len(values) * 0.01)]
        results.append((pid, theil_sen(rows) * 3600, spread, rows[-1][0] - rows[0][0]))
    measured = [r for r in results if r[1] is not None]
    if not measured:
        return None
    detail = "; ".join(f"process {i + 1}: {r[1]:+.1f}/h, spread {r[2]:.0f}, over {r[3]:.0f} s of quiet samples" if r[1] is not None else f"process {i + 1}: too short" for i, r in enumerate(results))
    return max(r[1] for r in measured), max(r[2] for r in measured), detail


def check_leaks(report, plan, samples, facts):
    for label, field, bound, unit in (
        ("RSS growth", "rss_mb", T["rss_mb_per_hour"], "MiB/h"),
        ("anonymous RSS growth (heap and native, not mapped files)", "rss_anon_mb", T["rss_mb_per_hour"], "MiB/h"),
        ("GC heap retained after collections", "retained_mb", T["retained_mb_per_hour"], "MiB/h"),
    ):
        result = fit(quiet_points(plan, samples, facts, field))
        if result is None:
            report.skip("No leak", label, f"the run is too short for a fit (needs {T['min_segment_seconds']} s of quiet samples)")
            continue
        report.ok("No leak", label, result[0] <= bound, f"{result[0]:+.1f} {unit}", f"<= {bound:g} {unit}", result[2])

    # Descriptors follow the connections, which come and go with the clients' pools; what must be flat is what is left when the sockets are taken off.
    residual = lambda s: (s["client_conns"] if not math.isnan(s["client_conns"]) else 0) + (s["upstream_conns"] if not math.isnan(s["upstream_conns"]) else 0)
    result = fit(quiet_points(plan, samples, facts, "fds", residual))
    if result is None:
        report.skip("No leak", "open descriptors (less client and upstream sockets)", "the run is too short for a fit")
    else:
        report.ok("No leak", "open descriptors (less client and upstream sockets): slope", result[0] <= T["fds_per_hour"], f"{result[0]:+.1f}/h", f"<= {T['fds_per_hour']:g}/h", result[2])
        report.ok("No leak", "open descriptors (less client and upstream sockets): band (p99 - p1)", result[1] <= T["fds_band"], f"{result[1]:.0f}", f"<= {T['fds_band']}")
    raw = [s["fds"] for s in samples if s["t"] >= plan["plan"]["WarmupSeconds"] and not math.isnan(s["fds"]) and s["fds"] > 0]
    if raw:
        report.info("No leak", "open descriptors, raw", f"{min(raw):.0f} to {max(raw):.0f}", "follows the connection count")

    result = fit(quiet_points(plan, samples, facts, "threads"))
    if result is None:
        report.skip("No leak", "OS threads", "the run is too short for a fit")
    else:
        report.ok("No leak", "OS threads: band (p99 - p1)", result[1] <= T["threads_band"], f"{result[1]:.0f}", f"<= {T['threads_band']}", result[2])

    # The thread pool's size must not creep: the highest it reached after warm-up against the highest during it.
    warm = plan["plan"]["WarmupSeconds"]
    restart = facts.get("restart") or {}
    skip_from = restart.get("beginT", float("inf"))
    skip_to = skip_from + restart.get("readySecondsFromSignal", 0) + 60
    before = [s["tp_threads"] for s in samples if s["t"] <= warm and not math.isnan(s["tp_threads"])]
    after = [s["tp_threads"] for s in samples if s["t"] > warm and not (skip_from <= s["t"] <= skip_to) and not math.isnan(s["tp_threads"])]
    if before and after:
        report.ok("No leak", "thread-pool threads stay put", max(after) <= max(before) + T["tp_threads_band"], f"max {max(after):.0f}", f"<= {max(before) + T['tp_threads_band']:.0f}",
                  f"warm-up max {max(before):.0f}; queue max {max(s['tp_queue'] for s in samples if not math.isnan(s['tp_queue'])):.0f}")
    gc2 = [(s["t"], s["gen2"]) for s in samples if not math.isnan(s["gen2"])]
    if gc2:
        report.info("No leak", "gen2 collections / GC pause total", f"{gc2[-1][1]:.0f} / {max(s['gc_pause_ms'] for s in samples if not math.isnan(s['gc_pause_ms'])):.0f} ms", "after the last restart; for the record")
    during = [s["rss_mb"] for s in samples if s["rss_mb"] == s["rss_mb"]]
    if during:
        report.info("No leak", "RSS range over the run", f"{min(during):.0f} to {max(during):.0f} MiB", "the burst costs memory the collector gives back minutes later")


def check_latency(report, plan, facts):
    latency = facts["latency"]
    base, final = window_ms(latency["baseline"]), window_ms(latency["final"])
    p = plan["plan"]
    where = f"minutes {p['BaselineStart'] / 60:.0f}-{p['BaselineEnd'] / 60:.0f} vs {p['FinalStart'] / 60:.0f}-{p['FinalEnd'] / 60:.0f}"
    if base is None or final is None:
        report.skip("No latency drift", "p99 of the last window against the baseline", "no samples in a window")
        return
    limit = base * T["p99_drift_ratio"] + T["p99_drift_slack_ms"]
    report.ok("No latency drift", "small requests: p99, last window vs baseline", final <= limit, f"{final:.2f} ms vs {base:.2f} ms ({final / base:.2f}x)",
              f"<= {T['p99_drift_ratio']}x + {T['p99_drift_slack_ms']} ms", where)
    for key, label in (("p50Ms", "p50"), ("p999Ms", "p99.9")):
        report.info("No latency drift", f"small requests: {label}", f"{latency['final'][key]} vs {latency['baseline'][key]} ms", where)
    bb, fb = window_ms(latency["baselineBig"]), window_ms(latency["finalBig"])
    if bb is not None and fb is not None:
        report.ok("No latency drift", "64 KiB transfers: p99, last window vs baseline", fb <= bb * T["p99_drift_ratio"] + T["p99_drift_slack_ms"], f"{fb:.2f} ms vs {bb:.2f} ms", f"<= {T['p99_drift_ratio']}x", where)


def percentile(values, fraction):
    values = sorted(values)
    return values[min(len(values) - 1, int(len(values) * fraction))] if values else float("nan")


def check_publishes(report, plan, facts, publishes):
    p = plan["plan"]
    cycle = [r for r in publishes if not r["Burst"]]
    burst = [r for r in publishes if r["Burst"]]
    failed = [r for r in publishes if r["Error"]]
    report.ok("Publishes", "all publishes succeeded", not failed, f"{len(failed)} of {len(publishes)} failed", "0",
              "; ".join(f"t={r['T']:.0f}s {r['Kind']}: {r['Error'][:80]}" for r in failed[:3]))
    expected_cycle = p["DurationSeconds"] // 10
    # A publish that lands in the restart's downtime cannot succeed and is recorded as an error; that failure is the restart's, and is listed there.
    report.info("Publishes", "cycle publishes (every 10 s)", f"{len(cycle)} of about {expected_cycle}", "")
    times = [r["Ms"] for r in publishes if not r["Error"]]
    report.ok("Publishes", "publish latency p99", percentile(times, 0.99) <= T["publish_p99_ms"], f"{percentile(times, 0.99):.0f} ms", f"<= {T['publish_p99_ms']:.0f} ms",
              f"p50 {percentile(times, 0.5):.0f} ms, max {max(times) if times else 0:.0f} ms")
    ok_cycle = [r for r in cycle if not r["Error"]]
    if len(ok_cycle) >= 30:
        third = len(ok_cycle) // 3
        first = statistics.median(r["Ms"] for r in ok_cycle[:third])
        last = statistics.median(r["Ms"] for r in ok_cycle[-third:])
        report.ok("Publishes", "publish latency does not trend up", last <= first * T["publish_trend_ratio"] + T["publish_trend_slack_ms"], f"{last:.1f} ms vs {first:.1f} ms (median, last vs first third)",
                  f"<= {T['publish_trend_ratio']}x + {T['publish_trend_slack_ms']:g} ms")
    else:
        report.skip("Publishes", "publish latency does not trend up", "too few publishes")
    live = [r["LiveMs"] for r in cycle if r["LiveMs"] is not None]
    if live:
        report.ok("Publishes", "command to live (canary host)", percentile(live, 0.99) <= T["live_p99_ms"], f"p50 {percentile(live, 0.5):.0f} ms, p99 {percentile(live, 0.99):.0f} ms", f"p99 <= {T['live_p99_ms']:.0f} ms", f"{len(live)} checked")
    canary = facts["canary"]
    report.ok("Publishes", "clients never saw the canary go backwards", not any(e["Detail"].startswith("the canary went back") for e in facts["errors"]), f"highest seen {canary['highestSeen']} of {canary['published']}", "monotonic")
    report.ok("Publishes", "the last published revision is the one served", canary["highestSeen"] >= canary["published"] - T["canary_lag_revisions"], f"{canary['highestSeen']} vs {canary['published']}", f"within {T['canary_lag_revisions']}")
    return burst


def check_s6(report, plan, facts, publishes, samples, events):
    p = plan["plan"]
    limited = facts["limited"]
    ok, refused = limited["ok"], limited["refused"]
    start, end = p["BurstStart"] + 2, p["BurstStart"] + p["BurstSeconds"] - 2
    burst_window = range(start, end)
    base_window = range(p["BaselineStart"], p["BaselineEnd"])
    base_ok_all = [ok[i] for i in base_window if i < len(ok)]
    if base_ok_all:
        mean_all = statistics.mean(base_ok_all)
        bound = limited["perSecondLimit"] * T["limited_rate_factor"] + T["limited_rate_slack"]
        report.ok("S6 continuity", "the rate limit holds under the mixed load (limited host, 200s per second)", mean_all <= bound, f"{mean_all:.1f}/s", f"<= {bound:.0f}/s (policy {limited['perSecondLimit']}/s)",
                  "100 requests a second are sent; a bucket evicted by other hosts' buckets starts full and lets them through")
    if all(i < len(ok) for i in burst_window) and any(r.get("Burst") for r in publishes):
        in_burst_refused = [refused[i] for i in burst_window]
        in_burst_ok = [ok[i] for i in burst_window]
        base_ok = [ok[i] for i in base_window if i < len(ok)]
        report.ok("S6 continuity", "429s continue through the publish burst", all(r > 0 for r in in_burst_refused), f"{sum(1 for r in in_burst_refused if r == 0)} seconds without a 429 of {len(in_burst_refused)}", "0")
        mean_burst, mean_base = statistics.mean(in_burst_ok), statistics.mean(base_ok)
        report.ok("S6 continuity", "the limit holds through the burst (200s per second, burst vs baseline)", mean_burst <= mean_base * T["limited_ok_ratio"] + T["limited_ok_slack"],
                  f"{mean_burst:.1f}/s vs {mean_base:.1f}/s (limit {limited['perSecondLimit']}/s)", f"<= {T['limited_ok_ratio']}x + {T['limited_ok_slack']:g}", f"max in one second {max(in_burst_ok)}; a publish that reset the bucket would show as extra 200s")
    else:
        report.skip("S6 continuity", "429s continue through the publish burst", "no burst in this run")
    counters = facts["counters"]
    report.ok("S6 continuity", "counters are monotonic across publishes", counters["violations"] == 0, f"{counters['violations']} regressions in {counters['scrapes']} scrapes", "0",
              "; ".join(f"t={v['T']}s {v['Series'][:70]} {v['Previous']:g}->{v['Now']:g}" for v in counters["violationList"][:2]))
    report.ok("S6 continuity", "no scrape failed outside the restart", counters["scrapeFailures"] <= max(2, int(p["RestartAt"] and 6)), counters["scrapeFailures"], "restart window only")
    health = facts["health"]
    timeline = health["flapNodeTimeline"]
    flap = facts.get("flap")
    if flap and timeline:
        stop_t = flap["stopT"]
        start_t = flap["startT"] if flap["startT"] is not None else float("inf")  # still down when the run ended
        down = [e for e in timeline if e["State"] not in ("healthy", "unknown") and stop_t <= e["T"] <= start_t + 1]
        came_back_early = [e for e in timeline if e["State"] == "healthy" and down and down[0]["T"] < e["T"] < start_t - 1]
        publishes_while_down = [r for r in publishes if down and down[0]["T"] <= r["T"] <= start_t and not r["Error"]]
        report.ok("S6 continuity", "a destination marked down stays down across publishes", bool(down) and not came_back_early, f"{len(publishes_while_down)} publishes while it was down", "0 recoveries before it was started again",
                  "; ".join(f"{e['T']:.0f}s {e['State']}" for e in timeline))
    else:
        report.skip("S6 continuity", "a destination marked down stays down across publishes", "no flap in this run")
    report.ok("S6 continuity", "no healthy destination flaps", health["downVerdictsOnLiveDestinations"] == 0 and health["flappingFlags"] == 0,
              f"{health['downVerdictsOnLiveDestinations']} down verdicts, {health['flappingFlags']} flapping flags on live destinations", "0", f"{health['polls']} health polls")


def flap_from_events(events):
    """The flap as the driver's event log has it. The driver writes facts["flap"] only after it has waited 45 s past the restart for the outbox and the traffic, so a run that ends or
    fails before then has the flap in its events and nothing in facts.json: it was reported as "no flap in this run". The quick plan restarts the server at 2:30, between the flap's start (2:10) and that check (2:55)."""
    when = {e["kind"]: e for e in events if e["kind"].startswith("flap.")}
    if "flap.stop" not in when:
        return None
    checked = when.get("flap.checked")
    return {
        "stopT": when["flap.stop"]["t"],
        "startT": when["flap.start"]["t"] if "flap.start" in when else None,
        "requestsAfterRestart": checked["detail"]["afterRestartRequests"] if checked else None,
    }


def check_flap(report, plan, facts):
    flap = facts.get("flap")
    timeline = facts["health"]["flapNodeTimeline"]
    if not flap:
        report.skip("Flap", "detected and recovered", "no flap in this run")
        return
    stop_t, start_t = flap["stopT"], flap["startT"]
    down = next((e for e in timeline if e["State"] not in ("healthy", "unknown") and e["T"] >= stop_t), None)
    report.ok("Flap", "the stopped destination was marked down", down is not None and down["T"] - stop_t <= T["flap_detect_seconds"], f"{down['T'] - stop_t:.1f} s" if down else "never", f"<= {T['flap_detect_seconds']:g} s", down["Reason"] if down else "")
    if start_t is None:
        report.skip("Flap", "it was marked healthy again and got traffic", "the run ended before the destination was started again")
    else:
        up = next((e for e in timeline if e["State"] == "healthy" and e["T"] > start_t), None)
        after = flap["requestsAfterRestart"]
        # Without the driver's own count (the run ended first) the recovery is judged alone, and the note says the traffic wasn't counted.
        report.ok("Flap", "it was marked healthy again and got traffic", up is not None and up["T"] - start_t <= T["flap_recover_seconds"] and (after is None or after > 0),
                  (f"{up['T'] - start_t:.1f} s, {after} requests since" if after is not None else f"{up['T'] - start_t:.1f} s") if up else "never", f"<= {T['flap_recover_seconds']:g} s",
                  "" if after is not None else "the run ended before the traffic after the restart was counted")
    items = []
    for history in (flap.get("history"), facts.get("notificationsFinal")):
        for item in history.get("items", []) if isinstance(history, dict) else []:
            if item["id"] not in {i["id"] for i in items}:
                items.append(item)
    texts = [json.dumps(i).lower() for i in items]
    went_down = [i for i, t in zip(items, texts) if "flappy" not in t and ("down" in t or "unhealthy" in t)]
    came_up = [i for i, t in zip(items, texts) if "recover" in t or ".up" in t or "healthy again" in t or "is up" in t or " up" in t]
    down_events = [i for i in items if "down" in str(i.get("kind", "")).lower()]
    up_events = [i for i in items if str(i.get("kind", "")).lower().endswith("up") or ".up" in str(i.get("kind", "")).lower()]
    report.ok("Flap", "the notification outbox recorded down and up", bool(down_events) and (bool(up_events) or start_t is None), f"{len(down_events)} down, {len(up_events)} up of {len(items)} events",
              ">= 1 down" if start_t is None else ">= 1 each",
              ", ".join(sorted({str(i.get('kind')) for i in items})))
    _ = (went_down, came_up)


def check_restart(report, plan, facts):
    restart = facts.get("restart")
    if not restart:
        report.skip("Restart", "graceful restart", "no restart in this run")
        return
    report.ok("Restart", "the server stopped cleanly on SIGTERM", restart["stopExitCode"] == 0, f"exit {restart['stopExitCode']} after {restart['stopSeconds']} s", "exit 0",
              "with WebSockets open the drain runs to the ShutdownTimeout (30 s); see README")
    report.ok("Restart", "healthy again within N seconds of the stop signal", restart["readySecondsFromSignal"] <= T["restart_ready_seconds"], f"{restart['readySecondsFromSignal']} s", f"<= {T['restart_ready_seconds']:g} s", "/readyz")
    proxied = restart.get("firstProxiedOkSecondsFromSignal")
    report.ok("Restart", "first proxied 200", proxied is not None and proxied <= T["restart_proxied_seconds"], f"{proxied} s", f"<= {T['restart_proxied_seconds']:g} s")
    healthy = restart.get("upstreamsHealthySecondsFromSignal")
    report.ok("Restart", "every destination healthy again", healthy is not None and healthy <= T["restart_healthy_seconds"], f"{healthy} s", f"<= {T['restart_healthy_seconds']:g} s")
    counters = restart.get("counters")
    if counters:
        report.info("Restart", "Prometheus counters (process-local)", f"{counters['before']:,.0f} -> {counters['after']:,.0f}", "they start again from zero with the process, as Prometheus expects")
    pre, post = restart["pre"], restart["post"]
    pre_total = (pre.get("analytics") or {}).get("totals", {}).get("requests")
    post_total = (post.get("analytics") or {}).get("totals", {}).get("requests")
    if pre_total is not None and post_total is not None:
        report.ok("Restart", "analytics continue (the total carried across)", post_total >= pre_total, f"{pre_total:,} -> {post_total:,} requests (hour)", "no loss")
    else:
        report.skip("Restart", "analytics continue", "no analytics snapshot")
    series = post.get("overviewSeries") or []
    begin_wall = restart["beginWall"]
    check_overview_gap(report, series, begin_wall, restart)


def check_overview_gap(report, series, begin_wall, restart):
    from datetime import datetime, timedelta

    if not series:
        report.skip("Restart", "metrics overview continues with the downtime as a gap", "no overview snapshot")
        return
    begin = datetime.fromisoformat(begin_wall.replace("Z", "+00:00"))
    down_end = begin + timedelta(seconds=restart["readySecondsFromSignal"])
    before_rates = []
    for bucket in series:
        start = datetime.fromisoformat(bucket["start"])
        if start + timedelta(seconds=bucket.get("widthSeconds", 10)) <= begin - timedelta(seconds=20) and bucket["requestsPerSecond"] > 0:
            before_rates.append(bucket["requestsPerSecond"])
    typical = sorted(before_rates)[min(len(before_rates) - 1, int(len(before_rates) * 0.75))] if before_rates else 0
    before = after = gap = 0
    for bucket in series:
        start = datetime.fromisoformat(bucket["start"])
        width = bucket.get("widthSeconds", 10)
        end = start + timedelta(seconds=width)
        rate = bucket["requestsPerSecond"]
        if end <= begin and rate > 0:
            before += 1
        elif start >= down_end and rate > 0 and not (rate < typical * 0.9 and start < down_end + timedelta(seconds=width)):
            after += 1
        elif start < down_end and end > begin or (start < down_end + timedelta(seconds=width) and rate < typical * 0.9):
            # The downtime shows as a bucket with fewer seconds covered, or, for a stop shorter than the sampler's five-second gap rule, as a dip in the rate.
            if rate == 0 or bucket.get("coveredSeconds", width) < width or rate < typical * 0.9:
                gap += 1
    report.ok("Restart", "metrics overview continues, with the downtime shown as a gap", before > 0 and after > 0 and gap > 0, f"{before} busy buckets before, {gap} gap or dip buckets, {after} after", "all three > 0",
              f"downtime {restart['readySecondsFromSignal']} s; the hour's series comes back from the persisted rollups")


def check_long_lived(report, plan, facts):
    ws, slow = facts["webSockets"], facts["slowClients"]
    report.ok("Long-lived and slow clients", "WebSockets chatted", ws["messages"] > 0 and ws["opened"] >= plan["WebSockets"], f"{ws['messages']:,} echoes over {ws['opened']} sockets", f">= {plan['WebSockets']} sockets")
    idle = ws["idleCutSeconds"]
    timeout = ws["idleTimeoutSeconds"]
    if idle:
        bad = [s for s in idle if not (timeout - 10 <= s <= timeout + T["ws_idle_slack_seconds"])]
        report.ok("Long-lived and slow clients", "silent WebSockets are cut by the idle timeout", not bad, f"{len(idle)} cut, {min(idle):.0f}-{max(idle):.0f} s", f"{timeout} s +{T['ws_idle_slack_seconds']:g}")
    else:
        report.skip("Long-lived and slow clients", "silent WebSockets are cut by the idle timeout", "none reached the timeout in this run")
    cuts = slow["slowlorisCutSeconds"]
    report.ok("Long-lived and slow clients", "slowloris connections are cut by the header timeout", bool(cuts) and max(cuts) <= T["slowloris_cut_seconds"] and slow["slowlorisSurvived"] == 0,
              f"{len(cuts)} cut, max {max(cuts) if cuts else float('nan'):.1f} s, {slow['slowlorisSurvived']} survived", f"<= {T['slowloris_cut_seconds']:g} s")
    body = slow["slowBodyCutSeconds"]
    report.ok("Long-lived and slow clients", "slow request bodies are cut by the minimum rate", bool(body) and max(body) <= T["slow_body_cut_seconds"], f"{len(body)} cut, max {max(body) if body else float('nan'):.1f} s", f"<= {T['slow_body_cut_seconds']:g} s")


def check_admin(report, plan, facts):
    mcp = facts["mcp"]
    calls = [c for c in mcp["calls"] if not c["tolerated"]]
    if mcp["enabled"]:
        expected = plan["plan"]["DurationSeconds"] // 60
        report.ok("Admin, dashboard and MCP", "MCP calls succeeded", mcp["failures"] == 0 and len(calls) >= expected * T["mcp_min_calls_fraction"] - 1, f"{len(calls)} calls, {mcp['failures']} failed", f"about {expected}, none failed",
                  "; ".join(c["problem"] for c in calls if c["problem"])[:120])
    admin = facts["admin"]
    report.ok("Admin, dashboard and MCP", "admin API polling had no errors", admin["errors"] == 0, f"{admin['calls']:,} calls, {admin['errors']} errors", "0")
    browser = facts["browser"]
    if browser["mode"] == "chromium":
        report.ok("Admin, dashboard and MCP", "dashboard tabs (overview, analytics) stayed live", browser["stale"] == 0 and not browser["problems"], f"{browser['checks']} checks, {browser['stale']} stale, {len(browser['problems'])} console problems", "0",
                  "; ".join(browser["problems"][:2]))
    else:
        report.skip("Admin, dashboard and MCP", "dashboard tabs stayed live", f"no browser: {browser['mode']}")


def check_rate(report, plan, samples):
    p = plan["plan"]
    steady = [s for s in samples if p["WarmupSeconds"] <= s["t"] <= p["DurationSeconds"] and s["requests"] > 0]
    # The target counts the main mix and the 429 client; the PoW stream and the sockets' echoes come on top.
    mean = statistics.mean(s["rps"] for s in steady) if steady else 0
    cpu = [s["cpu_pct"] for s in steady if not math.isnan(s["cpu_pct"])]
    report.ok("Load", "the generator held the rate", mean >= plan["Rate"] * T["rate_achieved_fraction"], f"{mean:,.0f} req/s", f">= {plan['Rate'] * T['rate_achieved_fraction']:,.0f}",
              f"server CPU median {statistics.median(cpu):.0f}% of {len(plan['serverCpus'].split(',')) if ',' in plan['serverCpus'] else '4'} cores' worth" if cpu else "")


def scan_log(log, events, plan):
    """Server log lines at warn and above, split into the ones the test provokes (slow clients, the flap's probes, the restart) and the rest."""
    p = plan["plan"]
    windows = []
    for e in events:
        if e["kind"] == "restart.begin":
            windows.append(("restart", e["t"] - 2, e["t"] + 140))
        if e["kind"] == "flap.stop":
            windows.append(("flap", e["t"] - 1, e["t"] + p["FlapSeconds"] + 40))
    load_start_ms = next((e["ms"] - e["t"] * 1000 for e in events if e["kind"] == "load.begin"), None)
    pattern = re.compile(r"^(\d{4}-\d\d-\d\dT[\d:.]+)Z (fail|crit|warn): (\S+)")
    errors, warnings = [], []
    from datetime import datetime, timezone

    startup_end = 10
    for line in log:
        m = pattern.match(line)
        if not m:
            continue
        when = datetime.fromisoformat(m.group(1) + "+00:00").astimezone(timezone.utc).timestamp()
        t = (when * 1000 - load_start_ms) / 1000 if load_start_ms else 0
        level, category = m.group(2), m.group(3)
        in_window = any(lo <= t <= hi for _, lo, hi in windows)
        expected = (
            "RequestBodyClient" in line  # the slow-body clients
            or "Probing destination" in line or "Active health state of destination" in line  # the flap
            or (category.startswith("Paduraksa.Vault.VaultRuntime") or "DevelopmentWarnings" in category)  # this setup's own startup notes
            or in_window
            or t < startup_end
        )
        entry = f"t={t:.0f}s {level} {category}: {line.split('=> ', 2)[-1][:100]}"
        if level in ("fail", "crit") and not expected:
            errors.append(entry)
        elif level == "warn" and not expected:
            warnings.append(entry)
    return {"unexpected_errors": errors, "unexpected_warnings": warnings}


def write_notable(run, log, events, plan):
    """The few server log lines worth keeping beside a result (the full log is 10 MB and more, mostly the slow clients' own warnings): errors, the health checks' verdicts, the lifecycle."""
    keep = re.compile(r" (fail|crit): |passive health|Active health state|Shutting down|Paduraksa stopped|Now listening|never completed")
    lines = [line[:300] for line in log if keep.search(line)]
    (run / "server-log-notable.txt").write_text("\n".join(lines[:600]) + ("\n" if lines else ""))


def passive_periods(log):
    return sorted({m.group(1) for line in log for m in [re.search(r"scheduled for a reactivation in `([\d:.]+)`", line)] if m})


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    run, plan, facts, samples, publishes, events, log = load(argv[1])
    if not facts.get("flap") and (flap := flap_from_events(events)):
        facts["flap"] = flap
    report = Report()
    findings = scan_log(log, events, plan)
    check_errors(report, plan, facts, findings)
    check_rate(report, plan, samples)
    check_leaks(report, plan, samples, facts)
    check_latency(report, plan, facts)
    check_publishes(report, plan, facts, publishes)
    check_s6(report, plan, facts, publishes, samples, events)
    check_flap(report, plan, facts)
    periods = passive_periods(log)
    if periods:
        report.info("Flap", "passive health reactivation, as the server logged it", ", ".join(periods), "the upstreams are configured with a 10 s reactivation period; see README")
    write_notable(run, log, events, plan)
    check_restart(report, plan, facts)
    check_long_lived(report, plan, facts)
    check_admin(report, plan, facts)
    lines = render(report, plan)
    print("\n".join(lines))
    (run / "summary.md").write_text("\n".join(lines) + "\n")
    (run / "results.json").write_text(json.dumps({"thresholds": THRESHOLDS, "checks": report.rows, "failed": len(report.failed)}, indent=2))
    return 1 if report.failed else 0


def render(report, plan):
    out = [f"# Soak result: {plan['plan']['DurationSeconds'] // 60} min at {plan['Rate']:,} req/s, {plan['hosts']} hosts, {plan['upstreams']} upstreams ({plan['destinations']} destinations)", ""]
    out += ["| Group | Check | Result | Value | Limit | Note |", "|---|---|---|---|---|---|"]
    for r in report.rows:
        out.append(f"| {r['group']} | {r['check']} | {r['status']} | {fmt(r['value'])} | {r['limit']} | {str(r['note']).replace('|', '/')} |")
    counts = {s: sum(1 for r in report.rows if r["status"] == s) for s in ("PASS", "FAIL", "SKIP", "INFO")}
    out += ["", f"**{counts['PASS']} passed, {counts['FAIL']} failed, {counts['SKIP']} skipped**, {counts['INFO']} informational."]
    return out


if __name__ == "__main__":
    sys.exit(main(sys.argv))
