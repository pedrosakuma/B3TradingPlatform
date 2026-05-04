# syntax=docker/dockerfile:1.7
#
# Demo-driver console — exercises the trading-host with synthetic order flow
# so the trader UI looks alive without manual interaction. NEVER run against
# anything other than a Simulator/Mock-mode trading-host.

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY backend/tools/B3.Trading.DemoDriver ./backend/tools/B3.Trading.DemoDriver

WORKDIR /src/backend/tools/B3.Trading.DemoDriver
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
RUN groupadd --system --gid 10001 demo \
 && useradd  --system --uid 10001 --gid demo --home /app demo

WORKDIR /app
COPY --from=build /app/publish ./

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_EnableDiagnostics=0

USER demo
ENTRYPOINT ["dotnet", "B3.Trading.DemoDriver.dll"]
