# syntax=docker/dockerfile:1
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 .
# Every build stage runs on the build host ($BUILDPLATFORM) and cross-produces for $TARGETARCH,
# so QEMU is only needed to *run* the arm64 image, not to build it.
#
# Runtime = plain aspnet:noble-chiseled + an ICU/tzdata overlay (docker/icu-slim.sh): the same
# libicu as -chiseled-extra with ICU data .NET never reads removed. The old base is still one
# build-arg away for comparison or as a fallback:  --build-arg RUNTIME_BASE=base-extra

ARG DOTNET_VERSION=10.0
ARG RUNTIME_BASE=base-slim

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH
# Git commit, stamped into the assembly for /healthz and /readyz (CI passes github.sha). No git at runtime.
ARG REVISION=unknown
WORKDIR /src
COPY src/SmartSubtitleConverterApi/SmartSubtitleConverterApi.csproj src/SmartSubtitleConverterApi/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet restore src/SmartSubtitleConverterApi/SmartSubtitleConverterApi.csproj -r $RID
COPY src/ src/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/SmartSubtitleConverterApi/SmartSubtitleConverterApi.csproj -c Release -r $RID --no-self-contained --no-restore \
      -p:UseAppHost=false -p:DebugType=none "-p:SourceRevision=$REVISION" -o /app

# ICU + tzdata source: the target-arch chiseled-extra image (only files are copied out of it).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS icu-source

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS icu
ARG TARGETARCH
RUN apt-get update \
 && apt-get install -y --no-install-recommends icu-devtools binutils-x86-64-linux-gnu binutils-aarch64-linux-gnu \
 && rm -rf /var/lib/apt/lists/*
COPY --from=icu-source /usr/lib/ /src/usr/lib/
COPY --from=icu-source /usr/share/zoneinfo/ /src/usr/share/zoneinfo/
COPY --from=icu-source /usr/share/doc/ /src/usr/share/doc/
COPY docker/icu-slim.sh /icu-slim.sh
RUN PATH="$PATH:/usr/sbin" TARGETARCH=$TARGETARCH sh /icu-slim.sh

# No shell, no package manager, non-root (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS base-slim
COPY --from=icu /out/ /
# Plain chiseled defaults to invariant mode (no ICU); turn ICU back on now that it is present.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS base-extra

FROM ${RUNTIME_BASE} AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="smart-subtitle-converter-api" \
      org.opencontainers.image.description="HTTP API for subtitle conversion built on Subtitle Edit's libse" \
      org.opencontainers.image.source="https://github.com/Wauter/smart-subtitle-converter-api" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.vendor="Wauter De Bruyne" \
      com.subtitleedit.upstream="https://github.com/SubtitleEdit/subtitleedit (MIT, Copyright (c) Nikolaj Olsson)"
WORKDIR /app
COPY --from=build /app .
COPY LICENSE /app/LICENSE
ENV ASPNETCORE_HTTP_PORTS=8080 \
    API_KEYS_FILE=/api_keys.txt \
    PROFILES_DIR=/profiles \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1
EXPOSE 8080
USER app
# Auth is on by default: mount an empty, writable ./api_keys.txt at /api_keys.txt and a key is generated into it on
# first start (docs/security.md). Without it (and without API_KEYS) startup stops with exit code 78 and instructions.
# No HEALTHCHECK: the chiseled image has no curl/shell. Probe /healthz and /readyz from the orchestrator.
ENTRYPOINT ["dotnet", "SmartSubtitleConverterApi.dll"]
