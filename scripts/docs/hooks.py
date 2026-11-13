"""MkDocs hooks for the documentation site (mkdocs.yml, `hooks:`).

Three jobs, so that the files stay where the repository (and GitHub's own rendering of it) wants them:

1. Links that leave docs/ (to source files, deploy/, bench/) become links to the repository on GitHub, instead of warnings.
2. CHANGELOG.md, which lives at the repository root, becomes the page `changelog.md`.
3. The API reference: the shipped OpenAPI document and a pinned Scalar bundle are written beside the site, so the reference
   renders with no request to anything but the site itself.
"""

import base64
import hashlib
import io
import json
import logging
import posixpath
import re
import shutil
import tarfile
import urllib.request
from pathlib import Path

from mkdocs.structure.files import File

log = logging.getLogger("mkdocs.hooks.paduraksa")

REPO_URL = "https://github.com/hizkifw/paduraksa"
BRANCH = "main"

# The standalone bundle of @scalar/api-reference, from npm's registry, checked against the integrity value npm publishes for the
# tarball. Bump both together; the bundle is cached, so a build downloads it once per machine.
SCALAR_VERSION = "1.78.1"
SCALAR_TARBALL = f"https://registry.npmjs.org/@scalar/api-reference/-/api-reference-{SCALAR_VERSION}.tgz"
SCALAR_INTEGRITY = "sha512-d2M/gvqTkvrm7YAxcCxqM1xUvB58Ec1yhfw3uPeioHeaK+6j81MU9Z3nmFkIU6O5kMZMprG3dR9VdmxOG8potA=="
SCALAR_ENTRY = "package/dist/browser/standalone.js"
CACHE_DIRECTORY = Path.home() / ".cache" / "paduraksa-docs"

OPENAPI_SOURCE = "src/Paduraksa.Admin/OpenApi/openapi.v1.json"
API_DIRECTORY = "api"  # site/api/: index.html, openapi.json, scalar.js

# Settings as the server's own /api/docs page sets them: no fonts, telemetry, agent or MCP calls out.
SCALAR_CONFIGURATION = {
    "withDefaultFonts": False,
    "telemetry": False,
    "agent": {"disabled": True},
    "mcp": {"disabled": True},
    "hideClientButton": True,
    # The document's own server is "the admin app that serves this document", which a static site is not: show the address the guide uses,
    # and no button that would send a request to the docs.
    "servers": [{"url": "https://paduraksa.lan:9443", "description": "Your Paduraksa admin address."}],
    "hideTestRequestButton": True,
    "showDeveloperTools": "never",
    "metaData": {"title": "Paduraksa admin API"},
}

API_PAGE = """<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Paduraksa admin API</title>
</head>
<body>
  <script id="api-reference" data-url="openapi.json" data-configuration='{configuration}'></script>
  <script src="scalar.js"></script>
</body>
</html>
"""

# `[text](target "title")`, not `![image](...)` links to a docs file are checked by MkDocs itself; this only finds the ones to move.
LINK = re.compile(r"(?<!!)\[(?P<text>[^\]]*)\]\((?P<target>[^)\s]+)(?P<title>\s+\"[^\"]*\")?\)")
REFERENCE = re.compile(r"^(?P<lead>\[[^\]]+\]:\s*)(?P<target>\S+)", re.MULTILINE)

_repository = Path(__file__).resolve().parents[2]
_documents: set[str] = set()


def _external(target: str) -> bool:
    return target.startswith(("#", "/", "mailto:")) or re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", target) is not None


def _github(path: str) -> str:
    kind = "tree" if (_repository / path).is_dir() else "blob"
    return f"{REPO_URL}/{kind}/{BRANCH}/{path}"


def _move(target: str, source_directory: str, page_directory: str) -> str:
    """The target of a link written in `source_directory` (a repository path), as the page in `page_directory` (a docs path) should say it."""
    if _external(target):
        return target
    path, hash_, anchor = target.partition("#")
    if not path:
        return target
    resolved = posixpath.normpath(posixpath.join(source_directory, path))
    if resolved.startswith("docs/") and resolved[len("docs/"):] in _documents:
        if source_directory.startswith("docs"):
            return target  # a page linking a page: MkDocs checks it
        # Written from the repository root (the changelog): relative to the page instead.
        return posixpath.relpath(resolved[len("docs/"):], page_directory or ".") + hash_ + anchor
    if (_repository / resolved).exists():
        return _github(resolved) + hash_ + anchor  # outside the site: the repository's own page for it
    return target


def _rewrite(markdown: str, source_directory: str, page_directory: str) -> str:
    def inline(match: re.Match[str]) -> str:
        moved = _move(match["target"], source_directory, page_directory)
        return f"[{match['text']}]({moved}{match['title'] or ''})"

    def reference(match: re.Match[str]) -> str:
        return match["lead"] + _move(match["target"], source_directory, page_directory)

    return REFERENCE.sub(reference, LINK.sub(inline, markdown))


def on_files(files, config):
    _documents.clear()
    _documents.update(file.src_uri for file in files)

    changelog = (_repository / "CHANGELOG.md").read_text(encoding="utf-8")
    # Written from the repository root: `docs/design/x.md` is the page `design/x.md`, and anything else is the repository's.
    files.append(File.generated(config, "changelog.md", content=_rewrite(changelog, "", "")))
    _documents.add("changelog.md")
    return files


def on_page_markdown(markdown, page, config, files):
    if page.file.src_uri == "changelog.md":
        return markdown
    directory = posixpath.dirname(page.file.src_uri)
    return _rewrite(markdown, posixpath.normpath(posixpath.join("docs", directory)), directory)


def _scalar_bundle() -> Path:
    cached = CACHE_DIRECTORY / f"scalar-{SCALAR_VERSION}.js"
    if cached.exists():
        return cached

    log.info("Downloading @scalar/api-reference %s (once; cached in %s)", SCALAR_VERSION, CACHE_DIRECTORY)
    with urllib.request.urlopen(SCALAR_TARBALL, timeout=120) as response:  # noqa: S310 (a pinned https URL)
        tarball = response.read()
    algorithm, _, expected = SCALAR_INTEGRITY.partition("-")
    actual = base64.b64encode(hashlib.new(algorithm, tarball).digest()).decode("ascii")
    if actual != expected:
        raise RuntimeError(f"@scalar/api-reference {SCALAR_VERSION}: the tarball does not match the pinned integrity value")

    with tarfile.open(fileobj=io.BytesIO(tarball), mode="r:gz") as archive:
        entry = archive.extractfile(SCALAR_ENTRY)
        if entry is None:
            raise RuntimeError(f"{SCALAR_ENTRY} is not in the @scalar/api-reference tarball")
        bundle = entry.read()

    CACHE_DIRECTORY.mkdir(parents=True, exist_ok=True)
    temporary = cached.with_suffix(".tmp")
    temporary.write_bytes(bundle)
    temporary.replace(cached)
    return cached


def on_post_build(config):
    target = Path(config["site_dir"]) / API_DIRECTORY
    target.mkdir(parents=True, exist_ok=True)

    # The document is the one the server embeds and serves at /api/v1/openapi.json, byte for byte.
    shutil.copyfile(_repository / OPENAPI_SOURCE, target / "openapi.json")
    shutil.copyfile(_scalar_bundle(), target / "scalar.js")
    page = API_PAGE.replace("{configuration}", json.dumps(SCALAR_CONFIGURATION, separators=(",", ":")))
    (target / "index.html").write_text(page, encoding="utf-8")
