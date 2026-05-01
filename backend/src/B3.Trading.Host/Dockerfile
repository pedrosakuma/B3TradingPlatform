# syntax=docker/dockerfile:1.7
#
# Multi-stage build for B3.Trading.Host.
#
# Build context MUST be the repo root (Directory.Build.props, slnx and the
# referenced projects all live there). Compose uses `context: ..`.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

# Copy the things that change rarely first so the restore layer caches well.
COPY global.json Directory.Build.props B3TradingPlatform.slnx ./
COPY backend/src ./backend/src

WORKDIR /src/backend/src/B3.Trading.Host
RUN dotnet restore --use-current-runtime
RUN dotnet publish \
        --configuration Release \
        --use-current-runtime \
        --no-restore \
        --output /app/publish \
        -p:PublishSingleFile=false \
        -p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl is used by the HEALTHCHECK below. Kept tiny: install + cleanup in one
# layer, no recommends.
RUN apt-get update \
 && apt-get install --no-install-recommends -y curl \
 && rm -rf /var/lib/apt/lists/*

# Persistence root (event store WAL + snapshots). Created and chowned BEFORE
# we drop to non-root, otherwise the running user can't write to it.
RUN mkdir -p /var/lib/b3trading \
 && groupadd --system --gid 10001 b3trading \
 && useradd  --system --uid 10001 --gid b3trading --home /var/lib/b3trading b3trading \
 && chown -R b3trading:b3trading /var/lib/b3trading

WORKDIR /app
COPY --from=build /app/publish ./

# Defaults consumed by Program.cs and AuthSigningKeyValidator. The empty
# SigningKey forces a fail-fast unless the operator overrides via
# Trading__Auth__SigningKey (or sets ASPNETCORE_ENVIRONMENT=Development to
# fall back to the dev key in appsettings.json — strongly discouraged).
ENV ASPNETCORE_ENVIRONMENT=Docker \
    ASPNETCORE_URLS=http://+:5000 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

EXPOSE 5000
VOLUME ["/var/lib/b3trading"]

USER b3trading

# /ready (not /live) is the right gate for orchestration: it flips to 503 once
# graceful drain begins, so a rolling update sees the instance go unready
# before the connection drops.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:5000/ready || exit 1

ENTRYPOINT ["dotnet", "B3.Trading.Host.dll"]
