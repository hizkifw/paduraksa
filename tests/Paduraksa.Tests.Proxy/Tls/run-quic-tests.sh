#!/usr/bin/env bash
# Runs the QUIC / HTTP/3 TLS tests (trait Transport=Quic) in Docker, where libmsquic is installed.
# Normal `dotnet test` runs skip them (no libmsquic on dev machines or CI); this script is separate and changes
# nothing about that run. Same approach as spikes/S1-http3-sni: pinned SDK image + pinned libmsquic from
# packages.microsoft.com. PADURAKSA_REQUIRE_QUIC=1 turns a missing QUIC stack into a failure, so a green run here
# can't be all skips.
#
# The repository is mounted read-only and copied (without bin/obj) into the container, so nothing on the host is
# written. NuGet packages are cached in the Docker volume "paduraksa-quic-tests-nuget".
#
# Usage: tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh [extra test-app arguments]
# Override versions with SDK_TAG=... LIBMSQUIC_VERSION=...
# QUIC_TESTS_ALL=1 runs the whole Proxy test project instead (checks the rest on a QUIC-capable host too).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
sdk_tag="${SDK_TAG:-10.0.403-noble}"
libmsquic_version="${LIBMSQUIC_VERSION:-2.6.3}"
image="paduraksa-quic-tests:${sdk_tag}-msquic${libmsquic_version}"

# No build context: the image is just the SDK plus libmsquic.
docker build -t "$image" \
  --build-arg "SDK_TAG=$sdk_tag" \
  --build-arg "LIBMSQUIC_VERSION=$libmsquic_version" \
  - <<'DOCKERFILE'
ARG SDK_TAG=10.0.403-noble
FROM mcr.microsoft.com/dotnet/sdk:${SDK_TAG}
ARG LIBMSQUIC_VERSION=2.6.3
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && . /etc/os-release \
 && curl -fsSL -o /tmp/pmc.deb "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" \
 && dpkg -i /tmp/pmc.deb && rm /tmp/pmc.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends "libmsquic=${LIBMSQUIC_VERSION}" \
 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DOCKERFILE

docker run --rm \
  -v "$repo:/src:ro" \
  -v paduraksa-quic-tests-nuget:/root/.nuget/packages \
  -e PADURAKSA_REQUIRE_QUIC=1 \
  -e "QUIC_TESTS_ALL=${QUIC_TESTS_ALL:-0}" \
  "$image" bash -euo pipefail -c '
    dpkg-query -W -f="libmsquic \${Version}\n" libmsquic
    dotnet --version
    mkdir -p /work
    tar -C /src --exclude=bin --exclude=obj --exclude=.git -cf - . | tar -C /work -xf -
    cd /work
    dotnet build tests/Paduraksa.Tests.Proxy -c Release -v quiet -nologo
    filter=(-trait "Transport=Quic")
    if [ "$QUIC_TESTS_ALL" = 1 ]; then filter=(); fi
    dotnet tests/Paduraksa.Tests.Proxy/bin/Release/net10.0/Paduraksa.Tests.Proxy.dll "${filter[@]}" "$@"
  ' bash "$@"
