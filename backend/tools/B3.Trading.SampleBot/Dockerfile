# syntax=docker/dockerfile:1.7
#
# Sample-bot console (#722) — small authenticated .NET sample that
# proves an ordinary end-client journey against the real Trading
# Platform REST/private-WS surface + B3MarketDataPlatform's public WS
# feed. One-shot: it authenticates, subscribes, optionally submits a
# single bounded passive limit order, and exits. NEVER give it a
# matching-platform endpoint or FIXP credential — see
# SampleBotOptionsValidator for the code-level guard.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY backend/Directory.Packages.props ./backend/
COPY backend/tools/B3.Trading.SampleBot ./backend/tools/B3.Trading.SampleBot

WORKDIR /src/backend/tools/B3.Trading.SampleBot
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
RUN groupadd --system --gid 10001 sample \
 && useradd  --system --uid 10001 --gid sample --home /app sample

WORKDIR /app
COPY --from=build /app/publish ./

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

USER sample
ENTRYPOINT ["dotnet", "B3.Trading.SampleBot.dll"]
