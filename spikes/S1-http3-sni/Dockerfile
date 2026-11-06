# Spike S1 image. Build context = repository root (for the repo-wide props / global.json).
#   docker build -f spikes/S1-http3-sni/Dockerfile .
# Versions are pinned so the recorded results are reproducible; override with --build-arg.
ARG SDK_TAG=10.0.403-noble
ARG ASPNET_TAG=10.0.14-noble

FROM mcr.microsoft.com/dotnet/sdk:${SDK_TAG} AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props BannedSymbols.txt BannedSymbols.KeyMaterial.txt .editorconfig ./
COPY spikes/Directory.Build.props spikes/
COPY spikes/S1-http3-sni/S1.Http3Sni.csproj spikes/S1-http3-sni/
RUN dotnet restore spikes/S1-http3-sni/S1.Http3Sni.csproj
COPY spikes/S1-http3-sni/ spikes/S1-http3-sni/
RUN dotnet publish spikes/S1-http3-sni/S1.Http3Sni.csproj -c Release --no-restore -o /out

# Runtime: the stock ASP.NET image (Ubuntu 24.04 "noble" for .NET 10) does NOT ship libmsquic.
# It comes from packages.microsoft.com (the same repo the product image will need).
FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_TAG} AS run
ARG LIBMSQUIC_VERSION=2.6.3
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && . /etc/os-release \
 && curl -fsSL -o /tmp/pmc.deb "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" \
 && dpkg -i /tmp/pmc.deb && rm /tmp/pmc.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends "libmsquic=${LIBMSQUIC_VERSION}" \
 && dpkg-query -W -f='libmsquic ${Version}\n' libmsquic \
 && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*
# The base image sets ASPNETCORE_HTTP_PORTS=8080; clear it so Kestrel doesn't log "Overriding address(es)".
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "S1.Http3Sni.dll"]
