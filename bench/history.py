"""Keeps a history of bench/run-baseline.sh runs and regenerates bench/RESULTS.md.

Usage:
    python3 -I bench/history.py append bench/results/<run>-baseline [--allow-dirty] [--new-machine]
    python3 -I bench/history.py report

`append` adds the run's summary.json as one line to bench/results/history.jsonl. It refuses a dirty run (unless
--allow-dirty) and a run from a different machine than the last entry (unless --new-machine). `report` reads the
history and writes bench/RESULTS.md: the latest run's tables, Paduraksa's trend per commit, and the change since the
previous entry, flagging a >10% throughput loss or a >25% fixed-rate p99 increase as a possible regression.
"""

import argparse
import json
import math
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
SCENARIO_ORDER = [
    "h1", "h1-tls", "h2-tls", "browser", "body-64k", "upload-64k", "handshake", "websocket", "answered",
    "access-log", "rate-limit", "rate-limited", "browser-check", "error-page", "cf",
]
MAIN_SCENARIOS = ["h1", "h2-tls"]
MISSING = "-"


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


def bold_best(cells: list[str], values: list, higher: bool) -> list[str]:
    """Bolds the best of a comparison so a table can be skimmed and tallied: every value tied for best (compared as
    displayed), and nothing when fewer than two values compare."""
    numbers = [value for value in values if value is not None]
    if len(numbers) < 2:
        return cells
    best = max(numbers) if higher else min(numbers)
    return [f"**{cell}**" if value is not None and value == best else cell for cell, value in zip(cells, values)]


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
        values = [(by_key.get((p, scenario, None)) or {}).get("rps") for p in proxies]
        values = [None if v is None else round(v) for v in values]
        cells = bold_best([fmt(v) for v in values], values, higher=True)
        for other in ratios:
            mine = by_key.get(("paduraksa", scenario, None))
            theirs = by_key.get((other, scenario, None))
            cells.append(f"{mine['rps'] / theirs['rps']:.2f}"
                         if mine and theirs and theirs["rps"] else MISSING)
        lines.append(f"| {scenario} | " + " | ".join(cells) + " |")
    if any(scenario in {"rate-limited", "error-page"} for scenario in scenarios):
        lines.append("")
        lines.append("`rate-limited` and `error-page` count every response (429 and 502), not only 2xx: for those rows"
                     " the number is what the proxy refused or failed, and their `errors` column is the same count.")
    if "websocket" in scenarios:
        lines.append("")
        lines.append("`websocket` counts echo round trips (a message each way), not requests.")
    lines.append("")

    lines.append("### Latency at fixed concurrency (ms)")
    lines.append("")
    lines.append("| Scenario | Proxy | req/s | p50 | p99 | p99.9 | errors |")
    lines.append("|---|---|---:|---:|---:|---:|---:|")
    for scenario in scenarios:
        measured = [(proxy, by_key[(proxy, scenario, None)]) for proxy in proxies if (proxy, scenario, None) in by_key]
        # Each column is bolded within the scenario: the most throughput, the lowest latencies.
        rps = [None if r.get("rps") is None else round(r["rps"]) for _, r in measured]
        columns = [bold_best([fmt(v) for v in rps], rps, higher=True)]
        for field in ("p50_ms", "p99_ms", "p999_ms"):
            values = [None if r.get(field) is None else round(r[field], 2) for _, r in measured]
            columns.append(bold_best([fmt(v, 2) for v in values], values, higher=False))
        for index, (proxy, result) in enumerate(measured):
            lines.append(f"| {scenario} | {NAMES.get(proxy, proxy)} | " + " | ".join(column[index] for column in columns)
                         + f" | {result.get('errors', MISSING)} |")
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

    same_machine = machine_of(latest) == machine_of(previous)
    if not same_machine:
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
            note = "**possible regression**" if same_machine and change < -10 else ""
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
            note = "**possible regression**" if same_machine and change > 25 else ""
            lines.append(f"| {scenario} | {rate:,} | p99 ms | {fmt(old_fixed['p99_ms'], 2)} "
                         f"| {fmt(fixed['p99_ms'], 2)} | {pct(change)} | {note} |")
            seen = True
    if not seen:
        lines.append("| - | - | - | - | - | - | No comparable Paduraksa results. |")
    lines.append("")


CHART_WIDTH = 960
CHART_MARGIN = 16
CHART_GAP = 24
CHART_COLUMNS = 2
PANEL_WIDTH = (CHART_WIDTH - 2 * CHART_MARGIN - CHART_GAP * (CHART_COLUMNS - 1)) // CHART_COLUMNS
PANEL_HEAD = 24
PANEL_FOOT = 4
BAR_LABEL_WIDTH = 64
BAR_VALUE_WIDTH = 54
BAR_ROW = 16
# 14 px bars on a 16 px pitch: the 2 px gap the data-viz rules ask for.
BAR_HEIGHT = 14
TREND_PLOT_HEIGHT = 84
CHART_FILES = {
    "throughput": "throughput-latest.svg",
    "trend": "paduraksa-trend.svg",
    "p99": "fixed-rate-p99-latest.svg",
}

# Light by default, dark through the media query, so one file works in both and no mark carries a hardcoded fill.
CHART_STYLE = """\
svg{font-family:system-ui,-apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif}
.surface{fill:#fcfcfb}
.text{fill:#0b0b0b}
.secondary{fill:#52514e}
.axis-label{fill:#898781}
.grid{stroke:#e1e0d9;stroke-width:1}
.axis{stroke:#c3c2b7;stroke-width:1}
.bar{fill:#c3c2b7}
.bar.accent{fill:#2a78d6}
.line{fill:none;stroke:#2a78d6;stroke-width:2}
.marker{fill:#2a78d6}
.marker.other{fill:#fcfcfb;stroke:#2a78d6;stroke-width:2}
.title{font-size:18px;font-weight:600}
.subtitle{font-size:12px}
.panel-title{font-size:13px;font-weight:600}
.value{font-size:11px}
.tick{font-size:11px}
@media (prefers-color-scheme:dark){.surface{fill:#1a1a19}.text{fill:#f0efec}.secondary{fill:#c3c2b7}.grid{stroke:#2c2c2a}.axis{stroke:#383835}.bar{fill:#52514e}.bar.accent{fill:#3987e5}.line{stroke:#3987e5}.marker{fill:#3987e5}.marker.other{fill:#1a1a19;stroke:#3987e5}}
"""


def esc(text) -> str:
    """XML text or an attribute value; charts are string-built, so no value may carry markup."""
    return (str(text).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;"))


def compact(value: float) -> str:
    """A request rate at bar-end size: 178585 -> "178.6k", 200000 -> "200k", 1200000 -> "1.2M"."""
    if value >= 1_000_000:
        text = f"{value / 1_000_000:.2f}".rstrip("0").rstrip(".")
        return f"{text}M"
    if value >= 1_000:
        text = f"{value / 1_000:.1f}".rstrip("0").rstrip(".")
        return f"{text}k"
    return f"{value:.0f}"


def nice_max(value: float) -> float:
    """A round axis top at or above the data, so gridlines land on readable numbers."""
    if value <= 0:
        return 1.0
    magnitude = 10 ** math.floor(math.log10(value))
    for step in (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10):
        candidate = step * magnitude
        if value <= candidate:
            return candidate
    return 10 * magnitude


def bar_path(x: float, y: float, width: float, height: float) -> str:
    """A horizontal bar with 4 px rounded corners at its data end only (the end away from the baseline)."""
    width = max(width, 1.0)
    radius = min(4.0, width / 2.0, height / 2.0)
    end = x + width
    if radius < 0.5:
        return f"M{x:.2f} {y:.2f} H{end:.2f} V{y + height:.2f} H{x:.2f} Z"
    return (
        f"M{x:.2f} {y:.2f} H{end - radius:.2f} "
        f"A{radius:.2f} {radius:.2f} 0 0 1 {end:.2f} {y + radius:.2f} "
        f"V{y + height - radius:.2f} "
        f"A{radius:.2f} {radius:.2f} 0 0 1 {end - radius:.2f} {y + height:.2f} "
        f"H{x:.2f} Z"
    )


def svg_open(title: str, subtitles: list[str], height: float) -> str:
    return "\n".join([
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{CHART_WIDTH}" height="{height}" '
        f'viewBox="0 0 {CHART_WIDTH} {height}" role="img" aria-labelledby="chart-title chart-desc">',
        f'<title id="chart-title">{esc(title)}</title>',
        f'<desc id="chart-desc">{esc(" ".join(subtitles))}</desc>',
        f"<style>{CHART_STYLE}</style>",
        f'<rect class="surface" x="0" y="0" width="{CHART_WIDTH}" height="{height}"/>',
    ])


def chart_header(title: str, subtitles: list[str]) -> list[str]:
    lines = [f'<text class="text title" x="{CHART_MARGIN}" y="28">{esc(title)}</text>']
    for index, subtitle in enumerate(subtitles):
        lines.append(f'<text class="secondary subtitle" x="{CHART_MARGIN}" y="{46 + 14 * index}">{esc(subtitle)}</text>')
    return lines


def bar_panel(x: float, y: float, panel: dict, value_label, tooltip) -> list[str]:
    bars = panel["bars"]
    plot_x = x + BAR_LABEL_WIDTH
    plot_width = PANEL_WIDTH - BAR_LABEL_WIDTH - BAR_VALUE_WIDTH
    top = y + PANEL_HEAD
    bottom = top + len(bars) * BAR_ROW
    axis_max = nice_max(max(value for _, value in bars))
    lines = [f'<text class="text panel-title" x="{x}" y="{y + 14}">{esc(panel["title"])}</text>']
    for fraction in (0.5, 1.0):
        grid_x = plot_x + plot_width * fraction
        lines.append(f'<line class="grid" x1="{grid_x:.2f}" y1="{top}" x2="{grid_x:.2f}" y2="{bottom}"/>')
    lines.append(f'<line class="axis" x1="{plot_x}" y1="{top}" x2="{plot_x}" y2="{bottom}"/>')
    for index, (proxy, value) in enumerate(bars):
        row = top + index * BAR_ROW
        width = plot_width * value / axis_max
        css = "bar accent" if proxy == "paduraksa" else "bar"
        lines.append(
            f'<path class="{css}" d="{bar_path(plot_x, row, width, BAR_HEIGHT)}">'
            f"<title>{esc(tooltip(panel, proxy, value))}</title></path>")
        lines.append(f'<text class="axis-label tick" x="{x}" y="{row + 11}">{esc(NAMES.get(proxy, proxy))}</text>')
        lines.append(
            f'<text class="text value" x="{plot_x + max(width, 1.0) + 6:.2f}" y="{row + 11}">'
            f"{esc(value_label(value))}</text>")
    return lines


def bar_chart(title: str, subtitles: list[str], panels: list[dict], value_label, tooltip) -> str:
    if not panels:
        return ""
    top = 46 + 14 * len(subtitles) + 10
    row_heights = [
        max(PANEL_HEAD + len(panel["bars"]) * BAR_ROW + PANEL_FOOT for panel in panels[start:start + CHART_COLUMNS])
        for start in range(0, len(panels), CHART_COLUMNS)
    ]
    height = top + sum(row_heights) + CHART_GAP * (len(row_heights) - 1) + CHART_MARGIN
    lines = svg_open(title, subtitles, height)
    lines += "\n" + "\n".join(chart_header(title, subtitles))
    y = top
    for row_index, row_height in enumerate(row_heights):
        for column, panel in enumerate(panels[row_index * CHART_COLUMNS:(row_index + 1) * CHART_COLUMNS]):
            lines += "\n" + "\n".join(bar_panel(CHART_MARGIN + column * (PANEL_WIDTH + CHART_GAP), y, panel, value_label, tooltip))
        y += row_height + CHART_GAP
    lines += "\n</svg>\n"
    return lines


def trend_panel(x: float, y: float, panel: dict, entries: list[dict]) -> list[str]:
    points = panel["points"]
    plot_x = x + BAR_LABEL_WIDTH - 20
    plot_width = PANEL_WIDTH - (BAR_LABEL_WIDTH - 20) - BAR_VALUE_WIDTH
    top = y + PANEL_HEAD
    bottom = top + TREND_PLOT_HEIGHT
    axis_max = nice_max(max(value for _, _, value, _ in points))

    def x_at(index: int) -> float:
        # Every panel shares the run positions, so a scenario that only started in a later run starts on the right.
        fraction = 0.5 if len(entries) == 1 else index / (len(entries) - 1)
        return plot_x + plot_width * fraction

    lines = [f'<text class="text panel-title" x="{x}" y="{y + 14}">{esc(panel["title"])}</text>']
    for fraction in (0.0, 0.5, 1.0):
        grid_y = bottom - TREND_PLOT_HEIGHT * fraction
        lines.append(f'<line class="grid" x1="{plot_x}" y1="{grid_y:.2f}" x2="{plot_x + plot_width}" y2="{grid_y:.2f}"/>')
        lines.append(
            f'<text class="axis-label tick" text-anchor="end" x="{plot_x - 6}" y="{grid_y + 3:.2f}">'
            f"{esc(compact(axis_max * fraction))}</text>")
    lines.append(f'<line class="axis" x1="{plot_x}" y1="{top}" x2="{plot_x}" y2="{bottom}"/>')
    positions = [
        (x_at(index), bottom - TREND_PLOT_HEIGHT * value / axis_max)
        for index, _, value, _ in points
    ]
    if len(points) > 1:
        polyline = " ".join(f"{px:.2f},{py:.2f}" for px, py in positions)
        summary = f"Paduraksa {panel['title']}: {len(points)} runs, {points[0][2]:,.0f} to {points[-1][2]:,.0f} req/s"
        lines.append(f'<polyline class="line" points="{polyline}"><title>{esc(summary)}</title></polyline>')
    for position, (_, entry, value, same_machine) in zip(positions, points):
        px, py = position
        machine = "" if same_machine else " (different machine)"
        title = f"{entry.get('date') or '?'} (commit {short_commit(entry.get('commit'))}): {value:,.0f} req/s{machine}"
        if same_machine:
            lines.append(f'<circle class="marker" cx="{px:.2f}" cy="{py:.2f}" r="4"><title>{esc(title)}</title></circle>')
        else:
            diamond = f"M{px:.2f} {py - 5:.2f} L{px + 5:.2f} {py:.2f} L{px:.2f} {py + 5:.2f} L{px - 5:.2f} {py:.2f} Z"
            lines.append(f'<path class="marker other" d="{diamond}"><title>{esc(title)}</title></path>')
    step = max(1, math.ceil(len(entries) / 6))
    for index, entry in enumerate(entries):
        if index % step and index != len(entries) - 1:
            continue
        # Date and minute: two runs on one day (or on one commit) still get distinct ticks; the marker title has
        # the full date and the commit.
        label = (entry.get("date") or "?")[5:16].replace("T", " ")
        lines.append(
            f'<text class="axis-label tick" text-anchor="middle" x="{x_at(index):.2f}" y="{bottom + 16}">'
            f"{esc(label)}</text>")
    last_x, last_y = positions[-1]
    lines.append(
        f'<text class="text value" x="{last_x + 8:.2f}" y="{last_y - 8:.2f}">{esc(compact(points[-1][2]))}</text>')
    return lines


def trend_svg(entries: list[dict]) -> str:
    reference = machine_of(entries[-1])
    by_scenario: dict[str, list] = {}
    for index, entry in enumerate(entries):
        for result in entry.get("results", []):
            if result.get("proxy") != "paduraksa" or result.get("rate") is not None or result.get("rps") is None:
                continue
            by_scenario.setdefault(result["scenario"], []).append(
                (index, entry, result["rps"], machine_of(entry) == reference))
    panels = [
        {"title": scenario, "points": by_scenario[scenario]}
        for scenario in ordered_scenarios(by_scenario)
    ]
    if not panels:
        return ""
    subtitle = (f"{len(entries)} recorded run(s); Paduraksa req/s at fixed concurrency, oldest to newest. "
                "Source: bench/results/history.jsonl.")
    subtitles = [subtitle]
    if any(not same for panel in panels for _, _, _, same in panel["points"]):
        subtitles.append("Each panel starts at 0; a hollow diamond marks a run from a different machine.")
    else:
        subtitles.append("Each panel starts at 0.")
    if any(panel["title"] in {"rate-limited", "error-page"} for panel in panels):
        subtitles.append("rate-limited and error-page count every response (429 and 502), not only 2xx.")
    row_height = PANEL_HEAD + TREND_PLOT_HEIGHT + 22
    top = 46 + 14 * len(subtitles) + 10
    height = top + row_height * math.ceil(len(panels) / CHART_COLUMNS) + CHART_GAP * (math.ceil(len(panels) / CHART_COLUMNS) - 1) + CHART_MARGIN
    title = "Paduraksa over time"
    lines = svg_open(title, subtitles, height)
    lines += "\n" + "\n".join(chart_header(title, subtitles))
    y = top
    for row in range(math.ceil(len(panels) / CHART_COLUMNS)):
        for column, panel in enumerate(panels[row * CHART_COLUMNS:(row + 1) * CHART_COLUMNS]):
            lines += "\n" + "\n".join(trend_panel(CHART_MARGIN + column * (PANEL_WIDTH + CHART_GAP), y, panel, entries))
        y += row_height + CHART_GAP
    lines += "\n</svg>\n"
    return lines


def ordered_scenarios(scenarios) -> list[str]:
    return [s for s in SCENARIO_ORDER if s in scenarios] + sorted(s for s in scenarios if s not in SCENARIO_ORDER)


def throughput_svg(latest: dict) -> str:
    by_key = results_of(latest)
    scenarios = ordered_scenarios({r["scenario"] for r in latest.get("results", []) if r.get("rate") is None})
    panels = []
    for scenario in scenarios:
        bars = [
            (proxy, by_key[(proxy, scenario, None)]["rps"])
            for proxy in ordered({r["proxy"] for r in latest.get("results", []) if r.get("scenario") == scenario and r.get("rate") is None})
        ]
        # Best first, so a panel reads as a ranking; Paduraksa keeps its accent colour wherever it lands. The stable
        # sort keeps the fixed proxy order for ties.
        bars.sort(key=lambda bar: -bar[1])
        if bars:
            panels.append({"key": scenario, "title": scenario, "bars": bars})
    if not panels:
        return ""
    date = latest.get("date") or "?"
    subtitles = [
        f"Latest recorded run: {date} (commit {short_commit(latest.get('commit'))}). "
        "Requests per second at fixed concurrency; each panel has its own scale."
    ]
    if any(len(panel["bars"]) == 1 for panel in panels):
        subtitles.append("A panel with a single bar is a scenario only that proxy runs (browser-check, or one the others skip).")
    if any(panel["key"] in {"rate-limited", "error-page"} for panel in panels):
        subtitles.append("rate-limited and error-page count every response (429 and 502), not only 2xx.")
    if any(panel["key"] == "websocket" for panel in panels):
        subtitles.append("websocket counts echo round trips (a message each way) per second.")
    return bar_chart(
        "Throughput per scenario, latest run", subtitles, panels, compact,
        lambda panel, proxy, value: f"{proxy} {panel['key']}: {value:,.0f} req/s")


def p99_svg(latest: dict) -> str:
    groups: dict[tuple[str, int], dict[str, float]] = {}
    for result in latest.get("results", []):
        if result.get("rate") is None or result.get("p99_ms") is None:
            continue
        groups.setdefault((result["scenario"], result["rate"]), {})[result["proxy"]] = result["p99_ms"]
    panels = []
    for (scenario, rate) in sorted(groups, key=lambda key: (SCENARIO_ORDER.index(key[0]) if key[0] in SCENARIO_ORDER else 99, key[1])):
        values = groups[(scenario, rate)]
        # Best (lowest) first, like the throughput panels.
        bars = sorted(((proxy, values[proxy]) for proxy in ordered(values)), key=lambda bar: bar[1])
        if bars:
            panels.append({"key": f"{scenario} at {rate:,} req/s", "title": f"{scenario} at {rate:,} req/s", "bars": bars})
    if not panels:
        return ""
    date = latest.get("date") or "?"
    subtitles = [
        f"Latest recorded run: {date} (commit {short_commit(latest.get('commit'))}). "
        "p99 latency in ms at each fixed request rate; each panel has its own scale."
    ]
    return bar_chart(
        "Fixed-rate p99 latency, latest run", subtitles, panels, lambda value: f"{value:.2f}",
        lambda panel, proxy, value: f"{proxy} {panel['key']}: {value:.2f} ms p99")


def write_charts(entries: list[dict]) -> list[tuple[str, str, str]]:
    """Writes the SVG charts next to the history and returns (heading, alt text, file name) for the report."""
    if not entries:
        return []
    RESULTS.mkdir(parents=True, exist_ok=True)
    latest = entries[-1]
    charts = []
    for key, heading, alt, builder in [
        ("throughput", "Latest run, throughput per scenario",
         "Horizontal bar panels of requests per second per proxy for every scenario in the latest run", throughput_svg),
        ("trend", "Paduraksa over time",
         "Line panels of Paduraksa's requests per second across the recorded runs, one panel per scenario", trend_svg),
        ("p99", "Fixed-rate p99 latency",
         "Horizontal bar panels of p99 latency per proxy at each fixed request rate in the latest run", p99_svg),
    ]:
        svg = builder(entries) if key == "trend" else builder(latest)
        if not svg:
            continue
        (RESULTS / CHART_FILES[key]).write_text(svg, encoding="utf-8")
        charts.append((heading, alt, CHART_FILES[key]))
    return charts


def write_report(entries: list[dict]) -> None:
    lines = ["# Benchmark results", ""]
    lines.append("Generated by `bench/history.py report` from `bench/results/history.jsonl`, tables and SVG charts "
                 "alike. Method, fairness rules and caveats: [bench/README.md](README.md). Runs are recorded with "
                 "`RECORD=1 bench/run-baseline.sh`; only `summary.md`, `summary.json` and `env.txt` are committed per run.")
    lines.append("")
    if not entries:
        lines.append("No runs have been recorded yet.")
    else:
        lines.append(f"{len(entries)} recorded run(s).")
        lines.append("")
        latest_tables(lines, entries[-1])
        charts = write_charts(entries)
        lines.append("## Charts")
        lines.append("")
        if charts:
            for heading, alt, filename in charts:
                lines.append(f"### {heading}")
                lines.append("")
                lines.append(f"![{alt}](results/{filename})")
                lines.append("")
            if all(filename != CHART_FILES["p99"] for _, _, filename in charts):
                lines.append("The latest entry has no fixed-rate results, so the p99 chart is skipped.")
                lines.append("")
        else:
            lines.append("The recorded runs have no results to chart yet.")
            lines.append("")
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
