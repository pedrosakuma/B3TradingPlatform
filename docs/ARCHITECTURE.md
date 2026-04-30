# Architecture (bootstrap)

This document is the participant-side mirror of
[`B3MatchingPlatform/docs/B3-ENTRYPOINT-ARCHITECTURE.md`](https://github.com/pedrosakuma/B3MatchingPlatform/blob/docs/b3-entrypoint-compliance/docs/B3-ENTRYPOINT-ARCHITECTURE.md).
It will grow alongside the implementation; for now it captures the
design-intent of the bootstrap and flags the open questions that issue #1
deliberately did **not** resolve.

## Layered model

```
┌──────────────────────────────────────────────┐
│ B3.Trading.Host   (ASP.NET Core composition) │
└───────────────┬──────────────────────────────┘
                │ depends on
┌───────────────▼──────────────────────────────┐
│ B3.Trading.Api                               │
│   - REST endpoints (Orders, Positions, …)    │
│   - (later) WebSocket hub for subscriptions  │
└───────────────┬──────────────────────────────┘
                │
┌───────────────▼──────────────────────────────┐
│ B3.Trading.Application                       │
│   - EndClientRegistry                        │
│   - WorkingOrderBook                         │
│   - PositionKeeper                           │
│   - (later) SubscriptionManager, PreTradeRisk│
└──────┬───────────────────────────┬───────────┘
       │                           │
┌──────▼───────────────┐  ┌────────▼───────────┐
│ B3.Trading.Domain    │  │ B3.Trading.Infra   │
│   - aggregates       │  │   - IExchangeGateway│
│   - value objects    │  │   - StubExchange…   │
│                      │  │   - (later) EntryPoint │
└──────────────────────┘  │     adapter via       │
                          │     B3EntryPointClient│
                          └───────────────────────┘
```

`Application` knows nothing about ASP.NET Core or FIXP; `Domain` knows
nothing about anyone. `Infrastructure` owns the `IExchangeGateway`
abstraction so the wire library is a swappable detail.

## Key design points (and open questions)

### 1. End-client ↔ FIXP session mapping

Likely `1 platform → N FIXP sessions (one per firm) → M end-clients per firm`.
ClOrdID is namespaced per FIXP session; the platform allocates per-end-client
prefixes (e.g. `e3a4-7421`) so ER routing back to the owner is a hash lookup.
The bootstrap allocates a naive `<login>-<guid>` ClOrdID; a proper bounded
prefix registry is a follow-up.

### 2. ER routing

Mirror of `OrderOwnershipMap` on the matching side, but participant-side:
`ClOrdID → endClientId`. Lives in `Application` next to
`WorkingOrderBook`. Bootstrap currently only mutates the local order on
synchronous submit; once a real `IExchangeGateway` lands, ER callbacks will
look up the order via this map and dispatch fills to `PositionKeeper`.

### 3. Position-keeper persistence

Same dilemma the matching side faced: ephemeral vs. WAL+snapshot. v1 is
ephemeral (in-memory `ConcurrentDictionary`) with re-derivation from ER
replay on (re)connect. Revisit when the matching side does.

### 4. Two auth layers

- **End-client ↔ platform.** Open question (issue #1). Likely JWT or
  OIDC; the bootstrap exposes a `?login=` query parameter as a stand-in
  identity so the rest of the stack can compile and be tested.
- **Platform ↔ exchange.** FIXP credentials per firm, in config, mirrors
  `B3EntryPointClient`'s API. Lands when that lib is wired in.

### 5. Market data for the trader UI

Three options on the table (see issue #1 §5). Not committed yet. The
bootstrap is order-only; market data will arrive in a follow-up.

## Why deviate from `B3MarketDataPlatform`'s flat `src/`

`B3MarketDataPlatform` puts projects directly under `src/` because it has
no frontend coupling worth foregrounding. `B3TradingPlatform` has a
first-class frontend that ships and is deployed alongside the backend, so
the issue's proposed `backend/` + `frontend/` split is honored here. The
.NET solution layout, conventions (`Directory.Build.props`, `global.json`,
`net10.0`, warnings-as-errors) are otherwise identical.
