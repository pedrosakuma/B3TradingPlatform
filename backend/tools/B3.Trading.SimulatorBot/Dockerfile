# syntax=docker/dockerfile:1.7
#
# Simulator-bot console — connects to matching-platform via FIXP and
# generates a steady flow of orders so the trading-host blotter looks
# alive without anyone touching the UI. Replaces the trading-host's
# in-process synthetic-ER injector (Mode=Simulator), which is on the
# deprecation path (issue #134).

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY backend/Directory.Packages.props ./backend/
COPY backend/tools/B3.Trading.SimulatorBot ./backend/tools/B3.Trading.SimulatorBot

WORKDIR /src/backend/tools/B3.Trading.SimulatorBot
RUN dotnet restore --use-current-runtime
RUN dotnet publish \
        --configuration Release \
        --use-current-runtime \
        --no-restore \
        --output /app/publish \
        -p:PublishSingleFile=false \
        -p:UseAppHost=false

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
RUN groupadd --system --gid 10001 bot \
 && useradd  --system --uid 10001 --gid bot --home /app bot \
 && mkdir -p /var/lib/b3-simulator-bot \
 && chown bot:bot /var/lib/b3-simulator-bot

WORKDIR /app
COPY --from=build /app/publish ./

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

USER bot
ENTRYPOINT ["dotnet", "B3.Trading.SimulatorBot.dll"]
