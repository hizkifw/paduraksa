#!/usr/bin/env bash
# Runs the test suite the way CI does, before a push: Release, Microsoft's official SDK (the distro build hides
# allocations the official one has), and few cores (GitHub's runners have 4; code that sizes itself from
# Environment.ProcessorCount behaves differently there than on a 20-core desktop).
#
#   scripts/test-ci-like.sh                 # 4 cores, the whole suite
#   CORES=2 scripts/test-ci-like.sh         # stricter than CI
#   scripts/test-ci-like.sh --project tests/Paduraksa.Tests.Proxy   # extra args go to dotnet test
set -euo pipefail

cores="${CORES:-4}"
# The SDK CI's setup-dotnet resolves today; bump it when CI moves.
sdk_version="${SDK_VERSION:-10.0.403}"
sdk_dir="${SDK_DIR:-$HOME/.cache/paduraksa/dotnet-$sdk_version}"

if [[ ! -x "$sdk_dir/dotnet" ]]; then
    echo "Installing the official .NET SDK $sdk_version into $sdk_dir"
    mkdir -p "$sdk_dir"
    curl -sSfL https://dot.net/v1/dotnet-install.sh -o "$sdk_dir/dotnet-install.sh"
    bash "$sdk_dir/dotnet-install.sh" --version "$sdk_version" --install-dir "$sdk_dir" >/dev/null
fi

export DOTNET_ROOT="$sdk_dir"
export PATH="$sdk_dir:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
# Both: taskset limits where threads run, DOTNET_PROCESSOR_COUNT is what Environment.ProcessorCount reports.
export DOTNET_PROCESSOR_COUNT="$cores"
cpus="0-$((cores - 1))"

cd "$(dirname "$0")/.."
echo "dotnet $(dotnet --version), $cores cores (CPUs $cpus), Release"
taskset -c "$cpus" dotnet build -c Release -warnaserror -nologo -v quiet
taskset -c "$cpus" dotnet test --no-build -c Release "$@"
