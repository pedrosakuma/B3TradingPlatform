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

## Run

```bash
dotnet run --project backend/tools/B3.Trading.SampleBot
```
