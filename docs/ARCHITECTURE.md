# Architecture (bootstrap)

This document is the participant-side mirror of
[`B3MatchingPlatform/docs/B3-ENTRYPOINT-ARCHITECTURE.md`](https://github.com/pedrosakuma/B3MatchingPlatform/blob/docs/b3-entrypoint-compliance/docs/B3-ENTRYPOINT-ARCHITECTURE.md).
It will grow alongside the implementation; for now it captures the
design-intent of the bootstrap and flags the open questions that issue #1
deliberately did **not** resolve.

## Layered model

```
                ┌──────────┐  ┌────────────┐  ┌──────────────────┐
inbound ───►    │  REST    │  │ WebSocket  │  │ FIXP/SBE TCP     │
                │  /api/orders │  │   /ws      │  │ (user-bot listener)│
                └────┬─────┘  └─────┬──────┘  └─────────┬────────┘
                     │              │                   │
┌────────────────────▼──────────────▼───────────────────▼─────────┐
│ B3.Trading.Host   (ASP.NET Core composition)                    │
└───────────────┬─────────────────────────────────────────────────┘
                │ depends on
┌───────────────▼──────────────────────────────┐
│ B3.Trading.Api                               │
│   - REST endpoints (Orders, Positions, …)    │
│   - WebSocket hub for subscriptions          │
│ B3.Trading.EntryPointListener                │
│   - Native FIXP/SBE listener for user bots   │
└───────────────┬──────────────────────────────┘
                │
┌───────────────▼──────────────────────────────┐
│ B3.Trading.Application                       │
│   - EndClientRegistry                        │
│   - WorkingOrderBook                         │
│   - PositionKeeper                           │
│   - SubscriptionManager, PreTradeRisk        │
└──────┬───────────────────────────┬───────────┘
       │                           │
┌──────▼───────────────┐  ┌────────▼────────────────┐
│ B3.Trading.Domain    │  │ B3.Trading.Infrastructure│
│   - aggregates       │  │   - IExchangeGateway     │
│   - value objects    │  │   - Stub / Mock / Real / │
│                      │  │     Unavailable gateways │
│                      │  │   - B3EntryPointClient   │
│                      │  │     adapter              │
└──────────────────────┘  └──────────────────────────┘
```

`Application` knows nothing about ASP.NET Core or FIXP; `Domain` knows
nothing about anyone. `Infrastructure` owns the `IExchangeGateway`
abstraction so the wire library is a swappable detail.

## Key design points (and open questions)

### 1. End-client ↔ FIXP session mapping

Likely `1 platform → N FIXP sessions (one per firm) → M end-clients per firm`.
ClOrdID is namespaced per FIXP session; the platform allocates per-end-client
prefixes so ER routing back to the owner is a hash lookup.

**ClOrdID encoding (Phase 1):** `{prefix}-{counter:D12}` — 17 chars
total, comfortably under EntryPoint's 20-char limit.

- `prefix` — 4 chars, base36 (`0-9a-z`), zero-padded. Allocated by
  `ClOrdIdPrefixRegistry` on first use of an `EndClientId`; idempotent
  on subsequent calls. Capacity: 36⁴ ≈ 1.68M end-clients per platform
  deployment, far beyond the participant-side scope.
- `counter` — 12-digit zero-padded decimal, per-end-client monotonic,
  advanced atomically with `Interlocked.Increment`. Capacity: 10¹²
  orders per end-client.

Allocation is process-local and resets on restart. Persistence of the
allocation is a Phase 6 concern; until then, restart looks like "new
platform" from EntryPoint's correlation standpoint, which is acceptable
because state is ephemeral by design.

### 2. ER routing

Mirror of `OrderOwnershipMap` on the matching side, but participant-side:
`ClOrdID → endClientId`. Lives in `B3.Trading.Application.OrderOwnershipMap`,
populated by `OrdersEndpoints.POST /api/orders` immediately after
`WorkingOrderBook.TryAdd` (registration is intentionally synchronous with
the book mutation so an immediate ER cannot race the routing path).

`ExecutionReportProcessor` consumes ERs (delivered by
`EntryPointExecutionReportRouter` from the wire) and:

1. Resolves owner via `OrderOwnershipMap`.
2. Mutates the `Order` in `WorkingOrderBook` (status, leaves, cumulative).
3. On fills, calls `PositionKeeper.ApplyFill`.
4. Publishes an `ExecutionEvent` to `IExecutionEventSink` for Phase 2 to
   fan out (default impl is `NoOpExecutionEventSink`; the WebSocket hub
   in #3 will plug a real one).

## Wire boundary

`B3.Trading.Infrastructure` references the upstream
[`B3EntryPointClient`](https://github.com/pedrosakuma/B3EntryPointClient)
NuGet (pinned in `B3.Trading.Infrastructure.csproj`) for the FIXP/SBE
wire layer. The `IEntryPointClient` contract owns the surface the
`Infrastructure` adapters consume so the wire library remains a
swappable detail.

- `EntryPointClientGateway` implements `IExchangeGateway` against
  `IEntryPointClient` (one gateway instance per FIXP session).
- `MockEntryPointClient` provides an in-memory implementation that
  records outbound calls and lets tests / the dev host inject ERs
  manually via `EmitExecutionReport`.
- `EntryPointExecutionReportRouter` subscribes to the client's ER event
  and dispatches into `ExecutionReportProcessor` (translating the wire
  enum `EpExecType` into the Application-layer `ExecKind` so Application
  stays unaware of the wire types).
- `ExchangeOptions` (bound from `Trading:Exchange` in `appsettings.json`)
  drives the wiring via the `Mode` enum (with legacy `UseStubGateway` /
  `UseRealEntryPointClient` flags as fallback):

  | Mode          | Gateway                      | Use                                  |
  | ------------- | ---------------------------- | ------------------------------------ |
  | `Stub`        | `StubExchangeGateway`        | No-op; CI smoke / API-only           |
  | `Mock`        | `EntryPointClientGateway` + `MockEntryPointClient` | Dev loop, integration tests. With `AllowErInjection=true`, also maps admin-gated `POST /api/admin/simulator/er` (slice-4 algo engine harness; #163 merged the legacy `Simulator` variant here). |
  | `Real`        | `MultiFirmExchangeGateway` over per-firm `B3EntryPointClient` | Production, UAT |
  | `Unavailable` | `UnavailableExchangeGateway` | Fail-closed no-broker (Docker bootstrap) — submits return 502 |

  The first firm in `Firms[]` is currently the default; multi-session
  routing lands in Phase 3.

  ⚠ **ER injection** (`Mode=Mock` + `AllowErInjection=true`) lets any
  admin-role caller emit synthetic
  `ExecutionReport`s for any working `ClOrdId`. It exists to unblock the
  algo engines (Iceberg/TWAP) without requiring a real venue. Four
  guardrails (RFC `algo-orders-v0` §4.10/§7-B3; updated by #163 when
  `Mode=Simulator` was collapsed into `Mode=Mock` + `AllowErInjection`)
  prevent it from leaking into production: (1) the boot path emits a
  loud Warning log line, (2) the `trading.er_injection.enabled`
  UpDownCounter ticks to 1 so dashboards/alerts can spot drift,
  (3) `/health` reports `exchange.erInjectionEnabled = true`, (4) the
  host **refuses to boot** when `Environment=Production` unless
  `Trading:Exchange:AllowErInjectionInProduction=true` is explicitly set.

When upstream releases new wire surface, the swap is local: update the
`IEntryPointClient` adapter to the new shape. The rest of the codebase
only depends on the small POCO contract declared alongside the interface.

## Auth + WebSocket hub (Phase 2)

The participant-side platform exposes three inbound channels:

- **REST + internal JWT.** Local mode keeps `POST /api/auth/login` against
  the local user store (PBKDF2-SHA256, 600k iterations by default). Hybrid
  and Entra add `POST /api/auth/exchange`, which validates a Microsoft Entra
  External ID **access token** under the named `EntraExternal` scheme, then
  issues the existing HS256 internal JWT from the SQLite trading-user
  directory. External tokens never authenticate REST or WebSocket routes
  directly; the default bearer scheme remains the internal JWT.
- **WebSocket hub at `/ws`.** Same JWT, accepted via either the standard
  `Authorization: Bearer` header or `?access_token=` (for browsers,
  which can't attach headers on a WS upgrade). Query-string acceptance
  is scoped to `/ws` only.
- **FIXP listener (inbound).** A native B3 EntryPoint–compatible TCP
  listener that lets external user bots connect via FIXP/SBE, authenticate
  with platform-issued credentials, and submit orders through the same
  post-auth pipeline. Opt-in via `Trading:EntryPointListener:Enabled`.
  See the [FIXP listener operations guide](operations/fixp-listener.md)
  and the [RFC](rfcs/user-bot-fixp-listener-v0.md).
- **Subscription channels.** `orders.me`, `executions.me`,
  `positions.me`. Snapshot-on-subscribe + delta stream. Per-`(connection,
  channel)` monotonic `seq` (snapshot is `seq=0`, deltas start at 1).
- **Real `IExecutionEventSink`.** `WebSocketExecutionEventSink` replaces
  the no-op sink: each ER becomes an `executions.me` delta, plus an
  `orders.me` delta with the post-mutation order state, plus (for
  fills) a `positions.me` delta with the new net position.
- **Backpressure.** Each connection has a bounded outbound queue. On
  overflow the platform refuses to silently drop a delta — it emits
  `slow_consumer_resync_required` and closes the socket so the client
  can reconnect and re-snapshot.
- **Cross-tenant safety.** REST endpoints derive owner from the JWT
  `sub` claim (never from request payload). `DELETE /api/orders/{clOrdId}`
  returns 404 if the order belongs to a different end-client — the
  authenticated tenant cannot see, much less mutate, foreign orders.

Wire format and error codes are documented in
[`docs/WEBSOCKET-PROTOCOL.md`](WEBSOCKET-PROTOCOL.md).

### 3. Position-keeper persistence

Same dilemma the matching side faced: ephemeral vs. WAL+snapshot. v1 is
ephemeral (in-memory `ConcurrentDictionary`) with re-derivation from ER
replay on (re)connect. Revisit when the matching side does.

### 4. Two auth layers

- **End-client ↔ platform.** `Trading:Auth:Mode=Local` preserves local
  password/TOTP + HS256 JWTs (default 60 min). `Hybrid` keeps local login
  only as a migration bridge but reads firm/role/status from
  `ITradingUserDirectory`; `Entra` maps only `/api/auth/exchange`. Hybrid/Entra
  internal sessions are 10 minutes, have no refresh token, and must be
  renewed by exchanging a valid Entra access token again. Operators must
  provide `Trading:Auth:SigningKey` ≥ 32 bytes via environment /
  user-secrets in production.
  The static frontend mirrors the same modes: Local remains the default,
  Hybrid presents explicit Entra/local choices, and Entra hides all public
  local password/signup/TOTP controls while using MSAL Browser
  Authorization Code + PKCE and storing only the exchanged internal JWT in
  `sessionStorage`.
- **Platform ↔ exchange.** FIXP credentials per firm, in config, mirrors
  `B3EntryPointClient`'s API. Lands when that lib is wired in.

### 5. Market data

The host consumes `B3.MarketData.WebSocketClient` (SDK) through
`SdkMarketDataSubscriber` and exposes events via the
application-side `IMarketDataSubscriber` seam. Trade + info events
are always on; the opt-in `Trading:MarketData:EnableBook` flag adds
the MBO (L3) stream consumed by `MboBookStore` and bridged to
Pegged BBO by `MboPegBookPump`. See [`MARKET-DATA.md`](MARKET-DATA.md)
for configuration, event flow and the SDK→app DTO mapping.

## Pre-trade risk (Phase 4)

Risk lives in `B3.Trading.Application/Risk/`. Every order goes through
the `RiskPipeline` **before** `IExchangeGateway.SubmitAsync`. Checks are
ordered (lower runs first) and short-circuit on the first rejection:

| Order | Check               | What it enforces                                       |
| -----:| ------------------- | ------------------------------------------------------ |
|     0 | `KillSwitchCheck`   | per-end-client + per-firm hard halt                    |
|   100 | `MaxQuantityCheck`  | per-symbol quantity cap                                |
|   100 | `MaxNotionalCheck`  | qty × price cap (skipped for market orders)            |
|   200 | `PositionLimitCheck`| projected `|net + signed|` vs. cap                     |
|   300 | `PriceCollarCheck`  | ± `PriceCollarPercent` band around `IReferencePrice`   |

`IMarginProvider` is consulted asynchronously after the synchronous
pipeline approves. v1 ships `NoOpMarginProvider`; real providers plug
in later.

**Synthetic rejections.** When any check rejects, the endpoint
synthesizes an `ExecutionEvent` with `Kind=Rejected` and publishes it via
the same `IExecutionEventSink` real exchange ERs flow through, so a WS
client subscribed to `executions.me` can't tell a risk rejection from an
exchange rejection. The HTTP response is still `202 Accepted` with
`{ ClOrdId, Status=Rejected, Reason }` so callers that only rely on the
ER stream behave consistently.

**Kill-switch admin API** (require `admin` role claim):

```
GET    /api/admin/kill                       → { endClients, firms }
POST   /api/admin/kill/end-client/{id}       enable
DELETE /api/admin/kill/end-client/{id}       disable
POST   /api/admin/kill/firm/{id}             enable
DELETE /api/admin/kill/firm/{id}             disable
```

State is in-memory; toggles take effect on the very next order
submission with no restart. Persistence is a Phase 6 concern.

**Configuration.** `Trading:Risk` schema:

```jsonc
{
  "Default":        { "MaxQuantity": 100000, "MaxNotional": 10000000.0,
                      "PriceCollarPercent": 10.0, "PositionLimit": 500000 },
  "PerEndClient":   { "alice":  { "MaxQuantity": 1000 } },
  "PerSymbol":      { "PETR4":  { "PriceCollarPercent": 5.0 } },
  "ReferencePrices":{ "PETR4":  30.0 }
}
```

Resolution per field is **per-end-client → per-symbol → default**, first
non-null wins. `null`/missing fields skip the check (open-by-default; an
operator must opt in). `IReferencePrice` reads from `ReferencePrices` in
v1; a future implementation will subscribe to `B3MarketDataPlatform`.

## Why deviate from `B3MarketDataPlatform`'s flat `src/`

`B3MarketDataPlatform` puts projects directly under `src/` because it has
no frontend coupling worth foregrounding. `B3TradingPlatform` has a
first-class frontend that ships and is deployed alongside the backend, so
the issue's proposed `backend/` + `frontend/` split is honored here. The
.NET solution layout, conventions (`Directory.Build.props`, `global.json`,
`net10.0`, warnings-as-errors) are otherwise identical.
