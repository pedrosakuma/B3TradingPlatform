# syntax=docker/dockerfile:1.7
#
# Standalone runner for the B3.Trading.Conformance suite.
#
# Build context MUST be the repo root so the multi-project layout
# (Directory.Build.props, slnx, the conformance .csproj) resolves the same
# way it does for the trading-host build. Compose passes `context: ..`.
#
# The image is intentionally a *single* stage on the SDK image: the
# artefact IS a test runner, not a publishable app. Restoring + building
# happens at image-build time so the runtime entrypoint just executes
# `dotnet test --no-build --no-restore`.
#
# Bring it up by setting B3T_BASE_URL / B3T_AUTH_USER / B3T_AUTH_PASS
# (the entrypoint validates them and bails with a clear exit code if
# missing or the API isn't reachable).

FROM mcr.microsoft.com/dotnet/sdk:10.0.201

# curl is used by the entrypoint to wait for /ready before invoking
# dotnet test. ca-certificates so https targets work too.
RUN apt-get update \
 && apt-get install --no-install-recommends -y curl ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy only what the conformance project needs. The .dockerignore lets
# backend/tests/B3.Trading.Conformance/** through and excludes every
# other test project plus all bin/obj.
COPY global.json Directory.Build.props ./
COPY backend/tests/B3.Trading.Conformance/ ./backend/tests/B3.Trading.Conformance/

# Restore + build the conformance project explicitly. We do NOT restore
# via the slnx because the trading-host source isn't in the build context
# and slnx-level restore would fail on the missing references.
RUN dotnet restore backend/tests/B3.Trading.Conformance/B3.Trading.Conformance.csproj
RUN dotnet build   backend/tests/B3.Trading.Conformance/B3.Trading.Conformance.csproj \
        --configuration Release \
        --no-restore

WORKDIR /src/backend/tests/B3.Trading.Conformance

COPY docker/conformance/entrypoint.sh /usr/local/bin/conformance-entrypoint
RUN chmod +x /usr/local/bin/conformance-entrypoint

ENTRYPOINT ["/usr/local/bin/conformance-entrypoint"]
