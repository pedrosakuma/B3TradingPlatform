# syntax=docker/dockerfile:1.7
#
# Market-maker bot console — connects to matching-platform via FIXP and
# acts as a co-located two-sided market maker, providing continuous
# bid/ask liquidity so sandbox/demo instruments always have a market to
# trade against. Evolved from the earlier stochastic simulator-bot
# (issue #134); see RFC #682 for the design rationale.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY backend/Directory.Packages.props ./backend/
COPY backend/tools/B3.Trading.MarketMakerBot ./backend/tools/B3.Trading.MarketMakerBot

WORKDIR /src/backend/tools/B3.Trading.MarketMakerBot
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
 && mkdir -p /var/lib/b3-market-maker-bot \
 && chown bot:bot /var/lib/b3-market-maker-bot

WORKDIR /app
COPY --from=build /app/publish ./

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

USER bot
ENTRYPOINT ["dotnet", "B3.Trading.MarketMakerBot.dll"]
