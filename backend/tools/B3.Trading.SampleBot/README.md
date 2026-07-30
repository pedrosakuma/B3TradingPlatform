# B3.Trading.SampleBot

Small authenticated .NET sample for the participant-facing `B3TradingPlatform` API.

## What it demonstrates

- `POST /api/auth/login` (`LocalPassword` mode)
- `POST /api/auth/exchange` (`ExternalExchange` mode)
- direct use of an existing internal trading JWT (`InternalToken` mode)
- authenticated `GET /ws` subscription to `orders.me`, `executions.me`, and `positions.me`
- independent `B3MarketDataPlatform` WebSocket subscription for one symbol's public reference/trade feed
- `GET /api/sub-accounts` validation for an optional configured sub-account
- one bounded market-data-driven `POST /api/orders` / `DELETE /api/orders/{clOrdId}` lifecycle with `Idempotency-Key`

## What it does **not** do

- no `B3.EntryPoint.Client` reference
- no FIXP socket
- no direct `matching-platform` connection
- no new machine-to-machine credential contract
- no production-grade strategy guarantees

## Configuration

`Host.CreateApplicationBuilder` loads `appsettings.json`, environment variables, and user-secrets.
Prefer secrets via environment variables or secret stores, never in source control.

Example environment overrides:

```bash
export SampleBot__BaseUrl='https://localhost:5001'
export SampleBot__MarketData__WsUrl='ws://localhost:8080/ws'
export SampleBot__Auth__Mode='LocalPassword'
export SampleBot__Auth__Username='alice'
export SampleBot__Auth__Password='wonderland'
export SampleBot__DemoOrder__Enabled='true'
```

### Auth modes

- `LocalPassword`: uses the existing local login flow. If `/api/auth/login` returns `requires2fa` or `requires2faEnrollment`, the sample fails explicitly because TOTP/WebAuthn is interactive.
- `ExternalExchange`: send an externally acquired Entra token in `Authorization: Bearer ...` to `/api/auth/exchange`, then use the returned internal trading JWT.
- `InternalToken`: supply an already issued internal trading JWT directly.

Platform relationship:

- `Local` mode deployments expose only `/api/auth/login`.
- `Hybrid` deployments expose both `/api/auth/login` and `/api/auth/exchange`.
- `Entra` deployments expose `/api/auth/exchange` and intentionally do not map local password login.

## Demo order safety

`SampleBot:DemoOrder:Enabled` defaults to `false` so simply running the sample will only authenticate, connect `/ws`, subscribe, and log private events.
When enabled, it also connects to `SampleBot:MarketData:WsUrl`, waits for a fresh public quote, computes a passive limit price by moving `PriceOffsetTicks * TickSize` away from the reference, submits at most one order, and cancels it after `OrderTimeout` if it is still working.

The sample intentionally takes public prices from `B3MarketDataPlatform` instead of talking to `matching-platform` directly. In this repository, FIXP/SBE stays behind `IExchangeGateway`; external bots should use the participant API for order mutations and the public market-data platform for venue prices.

### Default outcome: passive, not a guaranteed fill

`ComputePassiveLimitPrice` always moves a `Buy` **below** the observed reference (and a `Sell` above it), so the order is deliberately non-marketable against normal two-sided liquidity. The expected, deterministic journey when trading is enabled is:

1. submit `POST /api/orders` — order becomes `Working`;
2. observe `Working` over the private `orders.me` WebSocket channel;
3. `DemoOrder:OrderTimeout` elapses with the order still resting;
4. `DELETE /api/orders/{clOrdId}` best-effort cancel, bounded by `DemoOrder:CancellationAttemptTimeout`;
5. terminal `Cancelled`, confirmed by `GET /api/orders` reporting no `Working`/`PartiallyFilled` order for this end-client.

An unexpected `Filled` (fresh third-party flow happening to cross the resting price first) is not a bug — it is simply a different valid terminal outcome. What the sample (and its conformance coverage) actually guards is **no order left `Working`/`PartiallyFilled` after the run**, not a specific status.

## Run

### Locally (against a host you already have running)

```bash
dotnet run --project backend/tools/B3.Trading.SampleBot
```

### Docker, from a clean checkout

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

`run --rm` blocks until the one-shot container exits (`SampleBotWorker` stops the host itself once the workflow finishes) and streams its logs inline. The overlay stacks on the market-maker overlay so the sample bot sees a fresh, continuously-refreshed public reference price instead of racing a possibly-empty book on a freshly booted stack — see [`docs/DOCKER.md` § Sample-bot overlay](../../../docs/DOCKER.md#sample-bot-overlay-opt-in-authenticated-end-client-smoke-722) for the full walkthrough, the dedicated seeded credentials, and safety notes. Tear down with:

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.market-maker.yml \
    -f docker/docker-compose.sample-bot.yml \
    down -v
```

### Optional sub-account

`SampleBot:SubAccountId` is validated (`GET /api/sub-accounts`, must exist and be active) but never required — the sample refuses to start only if a *configured* id is missing/deactivated. Sub-account lifecycle is admin-only (there is no config-based seed), so pre-create one before pointing the sample at it:

```bash
curl -s -XPOST http://localhost:5000/api/sub-accounts \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -H 'content-type: application/json' \
  -d '{"id":"tradingdesk","displayName":"Sample desk"}'
```

## How this differs from the other bots/consumers

| | Trust boundary | Lifecycle | Purpose |
|---|---|---|---|
| **`SampleBot`** (this project) | Participant REST/WS + public market-data WS only — the same surface any external consumer uses | One-shot; exits after one bounded order lifecycle (or stays idle, observation-only, if trading is disabled) | Documented, reproducible proof of the ordinary end-client journey |
| [`DemoDriver`](../B3.Trading.DemoDriver) | In-process `Mode=Mock` + `AllowErInjection` — a same-process shortcut, not a real wire | Long-running; continuous submit + synthetic-fill injection loops | Make the trader UI look alive for a laptop demo, no real matching |
| [`MarketMakerBot`](../B3.Trading.MarketMakerBot) | Co-located FIXP client with its own `matching-platform` session/credentials | Long-running; continuously re-quotes both sides | Provide real two-sided liquidity so *other* end-clients have a market |
| External FIXP user-bot listener (`docs/operations/fixp-listener.md`) | Native FIXP/SBE over `Trading:EntryPointListener`, self-service credentials | Operator/bot-owned; independent of this repo's tooling | Let *third-party* bots connect over the wire protocol directly, bypassing REST/WS entirely |
