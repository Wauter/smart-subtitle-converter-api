# syntax=docker/dockerfile:1
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 .
# The SDK stage runs on the build host ($BUILDPLATFORM) and cross-publishes for $TARGETARCH,
# so QEMU is only needed to *run* the arm64 image, not to compile it.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src
COPY src/SubtitleEditApi/SubtitleEditApi.csproj src/SubtitleEditApi/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet restore src/SubtitleEditApi/SubtitleEditApi.csproj -r $RID
COPY src/ src/
RUN RID=$([ "$TARGETARCH" = "arm64" ] && echo linux-arm64 || echo linux-x64) \
 && dotnet publish src/SubtitleEditApi/SubtitleEditApi.csproj -c Release -r $RID --no-self-contained --no-restore \
      -p:UseAppHost=false -p:DebugType=none -o /app

# -extra = chiseled + ICU + tzdata (libse does culture-sensitive casing/language detection).
# No shell, no package manager, non-root (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="subtitleedit-api" \
      org.opencontainers.image.description="HTTP API for subtitle conversion built on Subtitle Edit's libse" \
      org.opencontainers.image.source="https://github.com/Wauter/subtitleedit-api" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.vendor="Wauter De Bruyne" \
      com.subtitleedit.upstream="https://github.com/SubtitleEdit/subtitleedit (MIT, Copyright (c) Nikolaj Olsson)"
WORKDIR /app
COPY --from=build /app .
COPY LICENSE /app/LICENSE
ENV ASPNETCORE_HTTP_PORTS=8080 \
    PROFILES_DIR=/profiles \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1
EXPOSE 8080
USER app
# No HEALTHCHECK: the chiseled image has no curl/shell. Probe /healthz and /readyz from the orchestrator.
ENTRYPOINT ["dotnet", "SubtitleEditApi.dll"]
