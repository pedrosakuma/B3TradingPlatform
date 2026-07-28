# B3.Trading.SampleBot

Small authenticated .NET sample for the participant-facing `B3TradingPlatform` API.

## What it demonstrates

- `POST /api/auth/login` (`LocalPassword` mode)
- `POST /api/auth/exchange` (`ExternalExchange` mode)
- direct use of an existing internal trading JWT (`InternalToken` mode)
- authenticated `GET /ws` subscription to `orders.me`, `executions.me`, and `positions.me`
- `GET /api/sub-accounts` validation for an optional configured sub-account
- `POST /api/orders` and `DELETE /api/orders/{clOrdId}` with `Idempotency-Key`

## What it does **not** do

- no `B3.EntryPoint.Client` reference
- no FIXP socket
- no direct `matching-platform` connection
- no new machine-to-machine credential contract
- no market-data-driven strategy logic (`#723`)

## Configuration

`Host.CreateApplicationBuilder` loads `appsettings.json`, environment variables, and user-secrets.
Prefer secrets via environment variables or secret stores, never in source control.

Example environment overrides:

```bash
export SampleBot__BaseUrl='https://localhost:5001'
export SampleBot__Auth__Mode='LocalPassword'
export SampleBot__Auth__Username='alice'
export SampleBot__Auth__Password='wonderland'
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
When enabled, it submits one limit order and can auto-cancel it after a short delay.

## Run

```bash
dotnet run --project backend/tools/B3.Trading.SampleBot
```
