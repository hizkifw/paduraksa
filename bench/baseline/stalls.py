"""Are a run's slow requests independent, or clustered into stalls that hit every connection at once?

Usage: python3 -I bench/baseline/stalls.py <oha SQLite db>...   (from OHA_DB_DIR=... bench/run-baseline.sh)

Works on fixed-rate runs (oha -q with --latency-correction), where requests are scheduled at a steady rate. A
request is slow above THRESHOLD of corrected latency. Slow requests whose scheduled start times are within GAP of
each other form one stall. A process-wide pause (GC, the process being descheduled) delays every request scheduled
during it, across all connections, so it shows up as one stall holding many slow requests; per-connection or
per-request causes leave slow requests scattered.
"""

import sqlite3
import sys

THRESHOLD = 0.003  # seconds of corrected latency
GAP = 0.002  # seconds between scheduled starts within one stall
MIN_STALL = 10  # slow requests for a cluster to count as a stall


def median(values):
    values = sorted(values)
    return values[len(values) // 2] if values else 0.0


def analyse(path: str) -> str:
    rows = sqlite3.connect(path).execute(
        "SELECT start_latency_correction, end - start_latency_correction FROM oha ORDER BY start_latency_correction"
    ).fetchall()
    if not rows:
        return f"{path}: no requests"
    seconds = rows[-1][0] - rows[0][0]
    slow = [(start, latency) for start, latency in rows if latency > THRESHOLD]
    clusters = []
    for start, latency in slow:
        if clusters and start - clusters[-1]["last"] <= GAP:
            cluster = clusters[-1]
            cluster["last"] = start
            cluster["count"] += 1
            cluster["worst"] = max(cluster["worst"], latency)
        else:
            clusters.append({"last": start, "count": 1, "worst": latency})
    stalls = [c for c in clusters if c["count"] >= MIN_STALL]
    in_stalls = sum(c["count"] for c in stalls)
    worst = [c["worst"] * 1000 for c in stalls]
    return (
        f"{path}: {len(rows)} requests, {len(slow)} over {THRESHOLD * 1000:.0f} ms ({100 * len(slow) / len(rows):.1f} %); "
        f"{len(stalls)} stalls ({len(stalls) / seconds:.1f}/s) hold {100 * in_stalls / max(1, len(slow)):.0f} % of them; "
        f"worst latency per stall: median {median(worst):.1f} ms, max {max(worst, default=0):.1f} ms; "
        f"slow requests per stall: median {median([c['count'] for c in stalls]):.0f}"
    )


if __name__ == "__main__":
    for db in sys.argv[1:]:
        print(analyse(db))
