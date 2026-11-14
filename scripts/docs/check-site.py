#!/usr/bin/env python3
"""Checks the internal links of a built documentation site.

    python3 scripts/docs/check-site.py [site]

`mkdocs build --strict` already fails on a broken link or anchor between Markdown pages. This walks the HTML it wrote, which also covers
what Markdown never sees: raw HTML in a page (the API reference's iframe and links), the theme's own links, images and scripts, and the
generated API reference. Every internal href and src must name a file in the site, and an anchor must name an id on that page.

External links (a scheme, or `//host`) are not fetched, so the check is offline. Exits 1 and lists every problem.
"""

import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

LINKING = {("a", "href"), ("link", "href"), ("img", "src"), ("script", "src"), ("iframe", "src"), ("source", "src"), ("video", "src")}


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []
        self.ids = set()

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if values.get("id"):
            self.ids.add(values["id"])
        if tag == "a" and values.get("name"):
            self.ids.add(values["name"])
        for element, attribute in LINKING:
            if tag == element and values.get(attribute):
                self.links.append((tag, values[attribute]))


def parse(path: Path) -> Page:
    page = Page()
    page.feed(path.read_text(encoding="utf-8", errors="replace"))
    return page


def base_path() -> str:
    """The path the site is served under: that of `site_url` in mkdocs.yml (`/` on the custom domain), which 404.html's links are written from."""
    config = Path("mkdocs.yml")
    found = re.search(r"^site_url:\s*(\S+)", config.read_text(encoding="utf-8"), re.MULTILINE) if config.is_file() else None
    path = urlsplit(found.group(1)).path if found else "/"
    return path if path.endswith("/") else path + "/"


def resolve(site: Path, source: Path, target: str, base_url: str = "/") -> Path | None:
    """The file in `site` that `target` (written in `source`) names, or None."""
    path = unquote(urlsplit(target).path)
    base = source.parent
    if path.startswith("/"):
        if not path.startswith(base_url) and path != base_url.rstrip("/"):
            return None
        path = path[len(base_url):] if path.startswith(base_url) else ""
        candidate = site / path
    else:
        candidate = base / path
    candidate = Path(candidate.as_posix().replace("//", "/"))
    try:
        candidate = candidate.resolve()
        candidate.relative_to(site.resolve())
    except ValueError:
        return None
    if candidate.is_dir():
        candidate = candidate / "index.html"
    return candidate if candidate.is_file() else None


def main() -> int:
    site = Path(sys.argv[1] if len(sys.argv) > 1 else "site")
    if not site.is_dir():
        print(f"{site}: no such directory. Run `mkdocs build` first.", file=sys.stderr)
        return 2

    base_url = base_path()
    pages: dict[Path, Page] = {}
    problems: list[str] = []
    checked = 0

    for html in sorted(site.rglob("*.html")):
        if html.parent.name == "api" and html.parent.parent == site:
            continue  # the generated reference loads its own script and the document: checked as files below
        pages[html.resolve()] = parse(html)

    for html, page in pages.items():
        shown = html.relative_to(site.resolve())
        for tag, link in page.links:
            parts = urlsplit(link)
            if parts.scheme or link.startswith("//"):
                continue  # external: not fetched
            checked += 1
            if not parts.path and parts.fragment:
                if parts.fragment not in page.ids and parts.fragment != "top":
                    problems.append(f"{shown}: <{tag}> '{link}': no such anchor on this page")
                continue
            if not parts.path:
                continue
            target = resolve(site, html, link, base_url)
            if target is None:
                problems.append(f"{shown}: <{tag}> '{link}': no such file in the site")
                continue
            if parts.fragment and target.suffix == ".html":
                other = pages.get(target) or parse(target)
                if parts.fragment not in other.ids:
                    problems.append(f"{shown}: <{tag}> '{link}': no anchor '{parts.fragment}' in {target.relative_to(site.resolve())}")

    for required in ("api/index.html", "api/openapi.json", "api/scalar.js"):
        if not (site / required).is_file():
            problems.append(f"{required}: missing (the API reference is written by scripts/docs/hooks.py)")

    if problems:
        print(f"{len(problems)} problem(s) in {checked} internal links of {len(pages)} pages:", file=sys.stderr)
        for problem in sorted(set(problems)):
            print("  " + problem, file=sys.stderr)
        return 1

    print(f"ok: {checked} internal links in {len(pages)} pages")
    return 0


if __name__ == "__main__":
    sys.exit(main())
