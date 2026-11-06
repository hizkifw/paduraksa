#!/usr/bin/env bash
# Builds and runs spike S1 (HTTP/3 + per-SNI certificates) entirely in Docker.
# Server and client run in the same process inside the container; the test host names are
# mapped to 127.0.0.1 with --add-host so both TCP and QUIC connections send the right SNI.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
image="paduraksa-spike-s1:local"

docker build -f "$here/Dockerfile" -t "$image" "$repo"

docker run --rm \
  --add-host a.test:127.0.0.1 \
  --add-host b.test:127.0.0.1 \
  --add-host x.wild.test:127.0.0.1 \
  --add-host unknown.test:127.0.0.1 \
  "$image" "$@"
