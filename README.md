# B3TradingPlatform

> **Status:** bootstrap. See [issue #1](https://github.com/pedrosakuma/B3TradingPlatform/issues/1).

Open-source **participant-side** platform in the B3 ecosystem family — what
a corretora's order-management backend looks like. Symmetric to
[`B3MarketDataPlatform`](https://github.com/pedrosakuma/B3MarketDataPlatform)
but for the **order-entry plane**: it owns end-client identity, holds
positions, manages own working orders, and exposes a modern API
(REST + WebSocket + frontend) on top of the raw FIXP/SBE protocol.

## Where this fits

| Repo | Role | Wire IN | Wire OUT | Frontend? |
| --- | --- | --- | --- | --- |
| [`B3MatchingPlatform`](https://github.com/pedrosakuma/B3MatchingPlatform) | The "exchange" (matching engine + UMDF publisher) | EntryPoint orders | UMDF MD + EntryPoint ER | Operator-only |
| [`B3MarketDataPlatform`](https://github.com/pedrosakuma/B3MarketDataPlatform) | Market-data subscriber | UMDF | — | Yes |
| **`B3TradingPlatform`** *(this repo)* | Participant / OMS-like backend | EntryPoint ER | EntryPoint orders | Yes |
| [`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient) | Wire-puro EntryPoint client lib + conformance suite | EntryPoint ER | EntryPoint orders | — |

This repo **consumes** `B3EntryPointClient` (as a NuGet or project reference)
for the wire layer, and adds everything above it: end-client management,
subscriptions, position keeping, frontend.

## Architecture sketch

```
Browser / Mobile                          External user bots
       │ WebSocket (subscribe: orders.me, executions.me, positions.me, …)
       │ REST     (submit/cancel/replace, login, account ops)
       │                                          │ FIXP/SBE TCP
       ▼                                          ▼
B3TradingPlatform backend
  ├── REST + WebSocket API     (browsers, REST clients)
  ├── FIXP Listener            (external user bots; opt-in via Trading:EntryPointListener:Enabled)
  ├── EndClientRegistry        end-client identity, login (JWT/session)
  ├── SubscriptionManager      per-end-client streams
  ├── PositionKeeper           cumulative position derived from ER stream
  ├── WorkingOrderBook         per-end-client open orders
  ├── PreTradeRisk             (v2) margin / position limits / fat-finger
  └── B3EntryPointClient       ← wire-puro lib (separate repo)
         │ TCP / SBE / SOFH / FIXP
         ▼
   B3MatchingPlatform   ← or B3 UAT (same lib, just swap endpoint + creds)
```

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the longer-form notes,
including ER routing, ClOrdID namespacing, and the open architecture
questions flagged in issue #1.

### FIXP Listener (inbound)

External user bots can connect via native B3 FIXP/SBE protocol using
self-service credentials. See the [FIXP listener operations guide](docs/operations/fixp-listener.md)
and the [RFC](docs/rfcs/user-bot-fixp-listener-v0.md).

## Layout

```
backend/
  src/
    B3.Trading.Domain/           end-client, position, order aggregate
    B3.Trading.Application/      use-cases, registry, position keeper
    B3.Trading.Api/              REST + WebSocket endpoints
    B3.Trading.Infrastructure/   B3EntryPointClient adapter (Stub/Mock/Real/Unavailable modes)
    B3.Trading.Host/             composition root (ASP.NET Core)
  tests/
    B3.Trading.Domain.Tests/
    B3.Trading.Application.Tests/
    B3.Trading.Api.Tests/        (uses WebApplicationFactory<Program>)
frontend/
  index.html  +  js/app.js       (vanilla, mirrors B3MarketDataPlatform stack)
docs/
  ARCHITECTURE.md
.github/workflows/ci.yml         (build + test + format, mirrors B3MatchingPlatform)
```

## Build & test

Requires the .NET SDK pinned in [`global.json`](global.json) (10.0.201).

```bash
dotnet restore B3TradingPlatform.slnx
dotnet build   B3TradingPlatform.slnx -c Release
dotnet test    B3TradingPlatform.slnx -c Release
```

## Run locally

```bash
dotnet run --project backend/src/B3.Trading.Host
```

The host listens on the default ASP.NET Core ports (typically
`http://localhost:5000`). Smoke-test with:

```bash
curl http://localhost:5000/health
curl -XPOST http://localhost:5000/api/orders \
  -H 'content-type: application/json' \
  -d '{"login":"alice","symbol":"PETR4","side":"Buy","type":"Limit","quantity":100,"price":30.50}'
curl 'http://localhost:5000/api/orders?login=alice'
```

The `IExchangeGateway` wiring is selected at startup via
`Trading:Exchange:Mode` (composition root in
[`Program.cs`](backend/src/B3.Trading.Host/Program.cs); see
[ARCHITECTURE.md § Wire boundary](docs/ARCHITECTURE.md#wire-boundary)
for the full table):

| Mode          | Behavior                                                                     |
| ------------- | ---------------------------------------------------------------------------- |
| `Stub`        | No-op gateway; CI smoke / API-only tests                                     |
| `Mock`        | In-process `MockEntryPointClient`; dev loop, integration tests, demo overlay |
| `Real`        | Per-firm [`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient) over FIXP/SBE against `B3MatchingPlatform` (or B3 UAT) |
| `Unavailable` | Fail-closed; submits return 502 (Docker bootstrap default before broker wiring) |

## Quick demo (laptop)

Want to see the trader UI move on its own — blotter filling up,
executions ticking, positions evolving — without manually clicking
through the order ticket? Use the demo overlay:

```bash
cp docker/.env.example docker/.env
# edit docker/.env and set TRADING_AUTH_SIGNING_KEY (>= 32 bytes)

docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.demo.yml \
    up -d --build
# open http://localhost:8080 and log in as bot-clientA / demopass
```

This brings up the trading-host in `Mode=Mock` + `AllowErInjection=true`
(synthetic ER injection enabled; #163 collapsed the legacy
`Mode=Simulator` into this combination) and starts a companion
**demo-driver** process
([`backend/tools/B3.Trading.DemoDriver`](backend/tools/B3.Trading.DemoDriver))
that:

- logs in as `bot-clientA` and `bot-clientB` and submits random
  buy/sell limit orders around the configured reference prices, and
- logs in as `demo-admin` and injects synthetic Fill / PartialFill
  ERs against the bots' working orders via `/api/admin/simulator/er`.

Log in as **`bot-clientA`** (password `demopass`) or **`bot-clientB`**
to see that bot's blotter, executions and positions panels evolve in
real time. Logging in as `alice` — the original seed — shows the empty
view, since alice does not submit orders.

Tear down with `docker compose -f docker/docker-compose.yml -f docker/docker-compose.demo.yml down -v`.

The overlay is **for laptop demos only** — `AllowErInjection=true` is
gated to non-Production environments and the demo credentials are
public in this repo. See
[docs/DOCKER.md § Demo overlay](docs/DOCKER.md#demo-overlay-opt-in-laptop-only)
for tuning, safety notes, and what is intentionally out of scope (live
MD ticks, real-stack cross-firm bots).

## Sample bot (authenticated end-client smoke)

Want to see what an *ordinary end-client integration* looks like, not a
demo/simulation shortcut? [`backend/tools/B3.Trading.SampleBot`](backend/tools/B3.Trading.SampleBot)
is a small one-shot .NET console that authenticates through the same
`POST /api/auth/login` a browser uses, opens the same authenticated
`/ws`, subscribes to `B3MarketDataPlatform`'s public feed, submits one
bounded REST order, and reconciles before exiting. It never receives a
`matching-platform` endpoint or FIXP credential — that's enforced at
the option-validation layer, not just documented.

```bash
cp docker/.env.example docker/.env
# edit docker/.env — TRADING_AUTH_SIGNING_KEY is mandatory (>= 32 bytes)

docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.market-maker.yml \
    -f docker/docker-compose.sample-bot.yml \
    up -d --build --wait trading-host market-maker-bot
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.market-maker.yml \
    -f docker/docker-compose.sample-bot.yml \
    run --rm --build sample-bot
```

`LocalPassword` auth is the required default; `ExternalExchange` and
`InternalToken` cover the Hybrid/Entra and supplied-token cases. The
default order is deliberately priced away from the reference price, so
the expected outcome on a clean run is submit -> observe `Working` ->
timeout -> best-effort cancel -> `Cancelled`, with `GET /api/orders`
showing no working order left behind — not a guaranteed fill. See
[docs/DOCKER.md § Sample-bot overlay](docs/DOCKER.md#sample-bot-overlay-opt-in-authenticated-end-client-smoke-722)
for auth-mode details, the optional sub-account flow, safety notes, and
how this differs from `DemoDriver`, `MarketMakerBot`, and the external
FIXP user-bot listener.

## Documentation

Start at [`docs/README.md`](docs/README.md) for the full index — it
maps every architecture note, RFC, runbook, and operations guide in
this repo. Highlights:

- [Architecture](docs/ARCHITECTURE.md) — layered model, wire boundary, ER routing.
- [FIXP listener — operations](docs/operations/fixp-listener.md) — the third inbound channel.
- [Docker](docs/DOCKER.md) — canonical container topology + overlays.
- [Market-maker strategy soak](docs/operations/market-maker-soak.md) —
  reproducible baseline, feature, and feed-loss evidence procedure.
- [WebSocket protocol](docs/WEBSOCKET-PROTOCOL.md) and [Frontend](docs/FRONTEND.md).

## Bootstrap scope (issue #1)

In:

- Backend skeleton (Clean-Architecture-ish: Domain / Application /
  Infrastructure / Api / Host)
- Frontend skeleton matching `B3MarketDataPlatform`'s vanilla-JS stack
- This README + `docs/ARCHITECTURE.md`
- CI: build + test + `dotnet format` (mirrors `B3MatchingPlatform`)

Out (deliberately deferred):

- Pre-trade risk (v2)
- Persistence (start ephemeral, derive from ER replay)
- Algo / basket
- Multi-region / HA
- Smoke E2E against `B3MatchingPlatform` — blocked on the matching-side
  FIXP lifecycle
  ([Phase 2 epic](https://github.com/pedrosakuma/B3MatchingPlatform/issues/60))

## License

[MIT](LICENSE)
