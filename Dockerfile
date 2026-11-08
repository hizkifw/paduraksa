# syntax=docker/dockerfile:1.7

# Paduraksa v0.1, single node (PLAN §16).
#
# Build:  docker build -t paduraksa .
# Run:    docker run -d --name paduraksa --stop-timeout 35 -p 80:80 -p 443:443 -p 443:443/udp -p 9443:9443 -v paduraksa-data:/data paduraksa
#
# Framework-dependent on the .NET 10 ASP.NET Core runtime for Ubuntu Noble: the app is a single web-server binary and
# gains nothing from a self-contained publish except ~80 MB of duplicated runtime, while the official image already
# tracks the exact patch level the SDK built against. ReadyToRun crossgens the app's own assemblies (not the framework's)
# so start-up is a little cheaper; turn it off with --build-arg PADURAKSA_READYTORUN=false.
#
# The image listens on 80/443 (tcp and udp) and 9443 *directly*, never on a remapped in-container port: Alt-Svc and the
# HTTP->HTTPS redirect advertise 443 because that is the port clients reach (PLAN §3, §16). Docker 20.10+ sets
# net.ipv4.ip_unprivileged_port_start=0 in the container's network namespace, so the non-root user can bind them;
# for runtimes that do not (network_mode: host, Kubernetes without the sysctl), the apphost carries
# cap_net_bind_service (setcap below).

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ARG PADURAKSA_READYTORUN=true
WORKDIR /src
COPY . .
# NuGet packages are cached across builds; restore is its own step so publish doesn't repeat it. TARGETARCH is Docker's
# architecture name ("amd64"), not the .NET RID ("linux-x64").
RUN --mount=type=cache,target=/root/.nuget/packages \
    set -eux; \
    arch="${TARGETARCH:-$(uname -m)}"; \
    case "$arch" in \
        amd64|x86_64) rid=linux-x64 ;; \
        arm64|aarch64) rid=linux-arm64 ;; \
        *) echo "Unsupported architecture '$arch' (only amd64 and arm64)." >&2; exit 1 ;; \
    esac; \
    dotnet restore src/Paduraksa.Server/Paduraksa.Server.csproj --runtime "$rid" -p:PublishReadyToRun="$PADURAKSA_READYTORUN"; \
    dotnet publish src/Paduraksa.Server/Paduraksa.Server.csproj \
        --configuration Release \
        --runtime "$rid" \
        --self-contained false \
        --no-restore \
        --output /app \
        -p:PublishReadyToRun="$PADURAKSA_READYTORUN" \
        -p:ContinuousIntegrationBuild=true \
        -nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# libmsquic, pinned, from Microsoft's Ubuntu 24.04 feed: Kestrel's HTTP/3 transport loads it at runtime. The signing key
# is checked against Microsoft's published fingerprint after dearmoring, curl/gnupg are purged in this same RUN (a later
# purge could not shrink an earlier layer), and the loader entry is asserted, so a missing library fails the build rather
# than the first QUIC handshake.
ARG LIBMSQUIC_VERSION=2.5.10
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl gnupg; \
    install -d -m 0755 /etc/apt/keyrings; \
    curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /etc/apt/keyrings/microsoft-prod.gpg; \
    fingerprint="$(gpg --show-keys --with-colons /etc/apt/keyrings/microsoft-prod.gpg | awk -F: '$1 == "fpr" { print $10; exit }')"; \
    test "$fingerprint" = "BC528686B50D79E339D3721CEB3E94ADBE1229CF" \
        || { echo "Microsoft's package signing key has fingerprint '$fingerprint', expected BC528686B50D79E339D3721CEB3E94ADBE1229CF." >&2; exit 1; }; \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/microsoft-prod.gpg] https://packages.microsoft.com/ubuntu/24.04/prod noble main" \
        > /etc/apt/sources.list.d/microsoft-prod.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends "libmsquic=${LIBMSQUIC_VERSION}"; \
    ldconfig -p | grep -q 'libmsquic\.so'; \
    apt-get purge -y curl gnupg; \
    rm -rf /var/lib/apt/lists/*

# The one volume (PLAN §16). /data is created with the image's owner so a fresh named volume inherits it; a bind mount
# must be chowned to 10001:10001 by the operator (docs/install.md, deploy/compose.yaml).
RUN groupadd --system --gid 10001 paduraksa \
    && useradd --system --uid 10001 --gid paduraksa --home-dir /data --shell /usr/sbin/nologin paduraksa \
    && install -d -o paduraksa -g paduraksa -m 0750 /data

WORKDIR /app
COPY --from=build /app /app

# Bind 80/443 without root where the runtime does not lift the unprivileged-port floor (PLAN §16). File capabilities are
# ignored under no-new-privileges; Docker's sysctl then makes them unnecessary. A runtime that drops capabilities
# (compose `cap_drop: [ALL]`, Kubernetes "restricted") must add NET_BIND_SERVICE back, or exec of this file fails with
# EPERM. libcap2-bin is installed and purged in this same RUN.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends libcap2-bin; \
    setcap cap_net_bind_service=+ep /app/paduraksa; \
    apt-get purge -y libcap2-bin; \
    rm -rf /var/lib/apt/lists/*

# The listeners come only from Paduraksa:Listen; blank the generic web-host knobs so an inherited env var cannot move one
# (PLAN §3, HostSetup.PinListenerConfiguration also pins them, this keeps it obvious in `docker inspect`).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_URLS= \
    PATH="/app:${PATH}" \
    Paduraksa__DataDirectory=/data \
    DOTNET_gcServer=1 \
    DOTNET_GCDynamicAdaptationMode=1 \
    DOTNET_TieredPGO=1 \
    DOTNET_EnableDiagnostics=0

# No curl/wget in the image: the healthcheck is the app's own short-lived process (PLAN §16).
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["/app/paduraksa", "doctor", "--liveness"]

EXPOSE 80/tcp
EXPOSE 443/tcp
EXPOSE 443/udp
EXPOSE 9443/tcp

# Kestrel drains with a shared 30 s budget; give Docker at least 35 s (`--stop-timeout 35`,
# `stop_grace_period: 35s` in compose; PLAN §3, §16).
STOPSIGNAL SIGTERM

USER 10001:10001
ENTRYPOINT ["/app/paduraksa"]
CMD ["serve"]
