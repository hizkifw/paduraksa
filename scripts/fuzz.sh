#!/usr/bin/env bash
# Coverage-guided fuzzing of the byte-level parsers (see fuzz/README.md): SharpFuzz instruments the proxy's assemblies, libFuzzer
# (libfuzzer-dotnet) drives the harness. One run per target, each for a time box; a crash is kept as a file and fails the script.
#
#   scripts/fuzz.sh                                  # every target, 60 s each
#   scripts/fuzz.sh --time 3600                      # every target, an hour each
#   scripts/fuzz.sh --target proxy-protocol --time 600
#   scripts/fuzz.sh --list                           # the targets
#   scripts/fuzz.sh --merge                          # after a run: keep what the run found that the committed corpus lacks, minimised
#   scripts/fuzz.sh --replay                         # no libFuzzer: every committed input through every target (what CI runs per commit)
#
# Options: --time SECONDS (per target, default 60), --target NAME (repeatable), --out DIR (default artifacts/fuzz), --merge, --replay, --list.
# Needs: the .NET SDK, `dotnet tool restore` (sharpfuzz, pinned in .config/dotnet-tools.json) and network once, for libfuzzer-dotnet.
# A crash input is written to <out>/crashes/<target>/. To keep it: copy it to fuzz/regressions/<target>/, fix the bug and add a unit test beside the code.
set -euo pipefail

cd "$(dirname "$0")/.."
root="$PWD"

seconds=60
out="$root/artifacts/fuzz"
merge=false
mode=run
targets=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --time) seconds="$2"; shift 2 ;;
        --target) targets+=("$2"); shift 2 ;;
        --out) out="$(realpath -m "$2")"; shift 2 ;;
        --merge) merge=true; shift ;;
        --replay) mode=replay; shift ;;
        --list) mode=list; shift ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done

harness=(dotnet run -c Release --project fuzz/Paduraksa.Fuzz --)
if [[ "$mode" == list ]]; then
    "${harness[@]}" list
    exit 0
fi
if [[ "$mode" == replay ]]; then
    "${harness[@]}" replay "${targets[@]}"
    exit $?
fi
if [[ ${#targets[@]} -eq 0 ]]; then
    mapfile -t targets < <("${harness[@]}" list | cut -f1)
fi

# libfuzzer-dotnet: a pinned release of https://github.com/Metalnem/libfuzzer-dotnet, checked against the digest below.
libfuzzer_tag="v2025.05.02.0904"
libfuzzer_sha256="c2c2a90d94c409a4af339a0d4f244e0442c5a5d249be0f1252ba07871f285958"
libfuzzer_dir="${LIBFUZZER_DIR:-$HOME/.cache/paduraksa/libfuzzer-dotnet-$libfuzzer_tag}"
libfuzzer="$libfuzzer_dir/libfuzzer-dotnet"
if [[ ! -x "$libfuzzer" ]]; then
    echo "Downloading libfuzzer-dotnet $libfuzzer_tag into $libfuzzer_dir"
    mkdir -p "$libfuzzer_dir"
    curl -sSfL -o "$libfuzzer" "https://github.com/Metalnem/libfuzzer-dotnet/releases/download/$libfuzzer_tag/libfuzzer-dotnet-ubuntu"
    echo "$libfuzzer_sha256  $libfuzzer" | sha256sum -c - >/dev/null || { rm -f "$libfuzzer"; echo "libfuzzer-dotnet's digest is not the pinned one" >&2; exit 1; }
    chmod +x "$libfuzzer"
fi

# The harness is published to a folder of its own and the proxy's assemblies are instrumented there: the copies the tests and the server use stay as built.
bin="$out/bin"
rm -rf "$bin"
dotnet tool restore >/dev/null
dotnet publish fuzz/Paduraksa.Fuzz -c Release -o "$bin" -nologo -v quiet
# The mcp-sandbox target's script inputs run on a real child, which must be a copy that is not instrumented (instrumented code needs libFuzzer's shared memory).
rm -rf "$out/child"
dotnet publish fuzz/Paduraksa.Fuzz -c Release -o "$out/child" -nologo -v quiet
export PADURAKSA_FUZZ_CHILD="$out/child/Paduraksa.Fuzz.dll"
# The MCP sandbox's parent side (frames, request bridge, policy) is instrumented; the child runs from an uninstrumented copy.
for assembly in Paduraksa.Proxy.dll Paduraksa.ProxyProtocol.dll Paduraksa.Core.dll Paduraksa.Application.dll Paduraksa.Contracts.dll YamlDotNet.dll Paduraksa.Mcp.dll; do
    dotnet sharpfuzz "$bin/$assembly" >/dev/null
done

status=0
for target in "${targets[@]}"; do
    committed="$root/fuzz/corpus/$target"
    work="$out/work/$target"
    crashes="$out/crashes/$target"
    mkdir -p "$committed" "$work" "$crashes"
    cp -n "$committed"/* "$work"/ 2>/dev/null || true
    cp -n "$root/fuzz/regressions/$target"/* "$work"/ 2>/dev/null || true
    echo "=== $target: ${seconds}s, $(find "$work" -type f | wc -l) starting inputs"
    log="$out/$target.log"
    if ! PADURAKSA_FUZZ_TARGET="$target" "$libfuzzer" \
        --target_path="$bin/Paduraksa.Fuzz" \
        -max_total_time="$seconds" -max_len=4096 -timeout=10 -rss_limit_mb=4096 -print_final_stats=1 \
        -artifact_prefix="$crashes/" "$work" > "$log" 2>&1; then
        echo "!!! $target: libFuzzer stopped with an error (see $log)" >&2
        status=1
    fi
    tail -n 16 "$log"
    found=$(find "$crashes" -type f | wc -l)
    if [[ "$found" -gt 0 ]]; then
        echo "!!! $target: $found crashing input(s) in $crashes" >&2
        status=1
    fi
    if $merge && [[ "$found" -eq 0 ]]; then
        # -merge=1 copies into the first folder only the inputs that reach coverage the folder doesn't have yet.
        PADURAKSA_FUZZ_TARGET="$target" "$libfuzzer" --target_path="$bin/Paduraksa.Fuzz" -merge=1 "$committed" "$work" 2>&1 | tail -n 3
    fi
done
exit $status
