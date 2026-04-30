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
Browser / Mobile
       │ WebSocket (subscribe: orders.me, executions.me, positions.me, …)
       │ REST     (submit/cancel/replace, login, account ops)
       ▼
B3TradingPlatform backend
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

## Layout

```
backend/
  src/
    B3.Trading.Domain/           end-client, position, order aggregate
    B3.Trading.Application/      use-cases, registry, position keeper
    B3.Trading.Api/              REST + WebSocket endpoints
    B3.Trading.Infrastructure/   B3EntryPointClient adapter (stub for now)
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
curl -XPOST http://localhost:5000/orders \
  -H 'content-type: application/json' \
  -d '{"login":"alice","symbol":"PETR4","side":"Buy","type":"Limit","quantity":100,"price":30.50}'
curl 'http://localhost:5000/orders?login=alice'
```

The `IExchangeGateway` is a no-op stub today; once wired to
[`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient)
it will dispatch to a real `B3MatchingPlatform` instance (or B3 UAT — same
lib, different endpoint + creds).

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
