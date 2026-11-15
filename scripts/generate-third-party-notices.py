#!/usr/bin/env python3
"""Writes THIRD-PARTY-NOTICES.json (what the dashboard's About page renders) and THIRD-PARTY-NOTICES.md (the same, to read).

What it lists is what Paduraksa.Server's publish output carries: the packages in its paduraksa.deps.json (the runtime
packages after the SDK has dropped the ones the shared framework provides, so test-only, analyzer and build-only packages
are not there), plus the components the metadata does not describe (scripts/third-party-notices/extra.json: the .NET
runtime, the SQLite native library, the icon paths in MudBlazor, libmsquic in the image). Each package's license
expression, copyright line and project URL come from its nuspec in the NuGet cache; the license texts are the shared
ones in scripts/third-party-notices/licenses (Apache-2.0 is the repository's LICENSE).

    scripts/generate-third-party-notices.py                      # publishes the server to a temporary directory and reads that
    scripts/generate-third-party-notices.py --publish-dir DIR    # reads an existing publish output

Run it after changing a package in Directory.Packages.props or a package reference, and commit both files. The
dashboard's NoticesCompletenessTests fail until you do. The packages must be restored (dotnet restore).
"""
import argparse
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SETTINGS = os.path.join(ROOT, "scripts", "third-party-notices")
LICENSE_NAMES = {
    "MIT": "MIT License",
    "Apache-2.0": "Apache License 2.0",
    "BSD-2-Clause": "BSD 2-Clause License",
    "BSD-3-Clause": "BSD 3-Clause License",
    "blessing": "SQLite blessing (public domain)",
}


def fail(message):
    sys.exit(f"generate-third-party-notices: {message}")


def one_line(value):
    # A nuspec's text may span lines; a newline would break the Markdown list the value is written into.
    return re.sub(r"\s+", " ", value).strip() if value else value


def web_address(value, owner):
    if value and not re.match(r"https?://\S+$", value, re.I):
        fail(f"{owner} has the address {value!r}: only http:// and https:// addresses are written, because the About page links them; "
             "name a correct one in scripts/third-party-notices/extra.json (packageOverrides)")
    return value


def package_cache():
    explicit = os.environ.get("NUGET_PACKAGES")
    return explicit if explicit else os.path.join(os.path.expanduser("~"), ".nuget", "packages")


def publish(directory):
    print(f"Publishing Paduraksa.Server to {directory}")
    result = subprocess.run(
        ["dotnet", "publish", os.path.join(ROOT, "src", "Paduraksa.Server"), "-c", "Release", "-o", directory, "-nologo", "-v", "quiet"],
        cwd=ROOT,
    )
    if result.returncode != 0:
        fail("dotnet publish failed")


def nuspec_of(cache, name, version):
    folder = os.path.join(cache, name.lower(), version)
    found = glob.glob(os.path.join(folder, "*.nuspec"))
    if not found:
        fail(f"{name} {version} is not in the NuGet cache ({folder}); run dotnet restore")
    metadata = ET.parse(found[0]).getroot().find("{*}metadata")

    def text(tag):
        element = metadata.find("{*}" + tag)
        return one_line(element.text) if element is not None and element.text and element.text.strip() else None

    license_element = metadata.find("{*}license")
    expression = license_element.text.strip() if license_element is not None and license_element.get("type") == "expression" else None
    return {
        "license": expression,
        "copyright": text("copyright"),
        "authors": text("authors"),
        "url": text("projectUrl"),
        "repository": metadata.find("{*}repository").get("url") if metadata.find("{*}repository") is not None else None,
    }


def read_licenses(needed):
    licenses = {}
    for spdx in sorted(needed):
        if spdx == "Apache-2.0":
            path = os.path.join(ROOT, "LICENSE")
        else:
            path = os.path.join(SETTINGS, "licenses", spdx + ".txt")
        if not os.path.exists(path):
            fail(f"no license text for {spdx}: add scripts/third-party-notices/licenses/{spdx}.txt")
        with open(path, encoding="utf-8") as file:
            licenses[spdx] = {"name": LICENSE_NAMES.get(spdx, spdx), "text": file.read().strip("\n") + "\n"}
    return licenses


def sqlite_version(publish_dir):
    native = os.path.join(publish_dir, "runtimes", "linux-x64", "native", "libe_sqlite3.so")
    if not os.path.exists(native):
        fail(f"the publish output has no {native}")
    with open(native, "rb") as file:
        found = re.search(rb"(?<![0-9.])3\.[0-9]{1,2}\.[0-9]{1,2}(?![0-9.])\x00", file.read())
    if not found:
        fail("could not read the SQLite version from libe_sqlite3.so")
    return found.group(0).rstrip(b"\x00").decode()


def libmsquic_version():
    with open(os.path.join(ROOT, "Dockerfile"), encoding="utf-8") as file:
        found = re.search(r"^ARG LIBMSQUIC_VERSION=(\S+)", file.read(), re.M)
    if not found:
        fail("the Dockerfile has no ARG LIBMSQUIC_VERSION")
    return found.group(1)


def check_static_assets(publish_dir, known):
    content = os.path.join(publish_dir, "wwwroot", "_content")
    sources = sorted(os.listdir(content)) if os.path.isdir(content) else []
    unknown = [source for source in sources if source not in known]
    if unknown:
        fail(f"the publish output serves static files from {', '.join(unknown)}: list what they carry in extra.json "
             "(components, and knownStaticAssetSources once it is covered)")
    served = os.path.join(publish_dir, "wwwroot")
    fonts = [name for _, _, files in os.walk(served) for name in files if name.lower().endswith((".woff", ".woff2", ".ttf", ".otf", ".eot"))]
    if fonts:
        fail(f"the publish output serves fonts ({', '.join(sorted(set(fonts)))}): add them to extra.json")


def components(publish_dir, extra):
    with open(os.path.join(publish_dir, "paduraksa.deps.json"), encoding="utf-8") as file:
        libraries = json.load(file)["libraries"]
    cache = package_cache()
    result = []
    for key, library in libraries.items():
        if library["type"] != "package":
            continue
        name, version = key.split("/")
        meta = nuspec_of(cache, name, version)
        override = extra["packageOverrides"].get(name, {})
        license_id = override.get("license") or meta["license"]
        if not license_id:
            fail(f"{name} {version}'s nuspec has no SPDX license expression: name the license it is used under in "
                 "scripts/third-party-notices/extra.json (packageOverrides)")
        if re.search(r"\s(OR|AND|WITH)\s", license_id):
            fail(f"{name} {version} has the license expression {license_id!r}: name the one it is used under in "
                 "scripts/third-party-notices/extra.json (packageOverrides)")
        result.append({
            "name": name,
            "version": version,
            "license": license_id,
            "copyright": override.get("copyright") or meta["copyright"] or f"Copyright (c) {meta['authors']}",
            "url": web_address(override.get("url") or meta["url"] or meta["repository"], f"{name} {version}"),
            "kind": "package",
        })

    values = {"@sqlite-version": sqlite_version(publish_dir), "@libmsquic-version": libmsquic_version()}
    for component in extra["components"]:
        entry = {key: value for key, value in component.items() if value is not None}
        for key in ("url", "noticesUrl"):
            web_address(entry.get(key), component["name"])
        if "version" in entry:
            entry["version"] = values.get(entry["version"], entry["version"])
        result.append(entry)
    result.sort(key=lambda c: c["name"].lower())
    return result


def markdown(document):
    lines = [
        "# Third-party notices",
        "",
        "Paduraksa itself is Apache-2.0 (see LICENSE and NOTICE). It ships with the components below. This file is generated by",
        "scripts/generate-third-party-notices.py from the server's publish output; the dashboard's About page shows the same list.",
        "",
    ]
    for component in document["components"]:
        version = f" {component['version']}" if component.get("version") else ""
        lines += [f"## {component['name']}{version}", "", f"- License: {component['license']}", f"- {component['copyright']}"]
        if component.get("url"):
            lines.append(f"- {component['url']}")
        if component.get("noticesUrl"):
            lines.append(f"- Its own third-party notices: {component['noticesUrl']}")
        lines.append("")
    lines += ["# License texts", ""]
    for spdx, license_ in document["licenses"].items():
        lines += [f"## {license_['name']} ({spdx})", "", "```text", license_["text"].rstrip("\n"), "```", ""]
    return "\n".join(lines)


def check_characters(text, name):
    # The repository's NoConfusableCharactersTests allow ASCII and the copyright sign; a package's metadata can carry smart quotes and dashes.
    bad = sorted({c for c in text if ord(c) > 126 and c != "©"})
    if bad:
        fail(f"{name} would contain {', '.join(f'U+{ord(c):04X}' for c in bad)}: replace them in extra.json (copyright override)")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--publish-dir", help="an existing publish output of src/Paduraksa.Server")
    parser.add_argument("--output-dir", default=ROOT, help="where to write the two files (the repository root by default)")
    args = parser.parse_args()
    with open(os.path.join(SETTINGS, "extra.json"), encoding="utf-8") as file:
        extra = json.load(file)

    with tempfile.TemporaryDirectory(prefix="paduraksa-publish-") as scratch:
        publish_dir = args.publish_dir
        if publish_dir is None:
            publish_dir = scratch
            publish(publish_dir)
        check_static_assets(publish_dir, extra["knownStaticAssetSources"])
        listed = components(publish_dir, extra)

    document = {
        "generator": "scripts/generate-third-party-notices.py",
        "components": listed,
        "licenses": read_licenses({c["license"] for c in listed}),
    }
    rendered = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    check_characters(rendered, "THIRD-PARTY-NOTICES.json")
    os.makedirs(args.output_dir, exist_ok=True)
    json_out = os.path.join(args.output_dir, "THIRD-PARTY-NOTICES.json")
    md_out = os.path.join(args.output_dir, "THIRD-PARTY-NOTICES.md")
    with open(json_out, "w", encoding="utf-8", newline="\n") as file:
        file.write(rendered)
    with open(md_out, "w", encoding="utf-8", newline="\n") as file:
        file.write(markdown(document))
    print(f"Wrote {len(listed)} components and {len(document['licenses'])} license texts to THIRD-PARTY-NOTICES.json and THIRD-PARTY-NOTICES.md")


if __name__ == "__main__":
    main()
