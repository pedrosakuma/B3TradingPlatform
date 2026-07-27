# Running B3TradingPlatform with Docker

This page covers the canonical container topology, what runs by default,
and the deliberate choices behind it. The compose file lives under
[`docker/docker-compose.yml`](../docker/docker-compose.yml).

## TL;DR

```bash
cp docker/.env.example docker/.env
# edit docker/.env — TRADING_AUTH_SIGNING_KEY is mandatory (>= 32 bytes)
docker compose -f docker/docker-compose.yml up --build
# open http://localhost:8080
```

You'll see the trader UI, log in as `alice / wonderland` (the seeded
default), and submit a PETR4 order — it routes through the real
matching engine, prints on the book, and the live tape lights up.
That's the **default Real stack** — see
[What runs by default](#what-runs-by-default) below.

To validate the no-broker fail-closed surface instead (POST /api/orders →
502, /health `readyForOrders=false`), stack the unavailable overlay:

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.unavailable.yml \
    up
```

## What runs by default

| Service | Image | Port (host) | Purpose |
|---|---|---|---|
| `matching-platform` | `ghcr.io/pedrosakuma/b3-matching@sha256:…` (pinned — see [Bumping the matching and marketdata images](#bumping-the-matching-and-marketdata-images)) | (internal `:9876`, `:8080`) | FIXP TCP listener + UMDF unicast publisher + `/api/admin/channels/*` snapshot ops |
| `marketdata` | `ghcr.io/pedrosakuma/b3-marketdata@sha256:…` (pinned with matching) | `8081` | UMDF unicast consumer (`30084/30184/31084 udp`) + WebSocket fanout (`:8080`) |
| `trading-host` | `ghcr.io/pedrosakuma/b3-trading-host:latest` | `5000` | REST + WebSocket; `Mode=Real`, FIRM01 session against matching, live `IReferencePrice` wired through marketdata WS |
| `frontend` | `ghcr.io/pedrosakuma/b3-trading-frontend:latest` | `8080` | nginx serving the static UI + reverse-proxy to trading-host |

Persistence:

- **`b3-trading-data`** named volume → trading-host WAL + snapshots.
- **`b3-matching-data`** named volume → matching-platform per-channel
  snapshots + WAL (upstream
  [B3MatchingPlatform#260](https://github.com/pedrosakuma/B3MatchingPlatform/issues/260)
  phase work). `docker compose restart matching-platform` no longer
  drops the working order book on the floor.

The PCAP-replay marketdata variant is now a separate service
(`marketdata-pcap`) gated behind the `marketdata-pcap` profile so it
doesn't conflict with the live `marketdata`:

```bash
PCAP_DIR=/path/to/pcaps docker compose -f docker/docker-compose.yml --profile marketdata-pcap up
```

> **Heads up — venue-restart caveat.** Working orders submitted before
> a `docker compose restart matching-platform` survive on the venue
> (snapshot + WAL), but the FIXP owner session is dropped on restore
> (matching's cross-channel consistency check). Trading-host still sees
> them as Working in the blotter — see
> [#132](https://github.com/pedrosakuma/B3TradingPlatform/issues/132)
> for the consumer-side reconciliation work.

## Honest no-broker overlay (opt-in)

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.unavailable.yml \
    up
```

Flips trading-host back to `Mode=Unavailable` and suppresses
`matching-platform` + `marketdata` so only `trading-host + frontend`
come up. Useful for:

- Validating the fail-closed surface (POST /api/orders → 502 BadGateway,
  /health `readyForOrders=false`, ticket disabled).
- Auth/admin-only flows (login, /api/admin/firms CRUD) without paying the
  matching+marketdata startup cost.
- CI conformance jobs that exercise the Unavailable contract
  (admin/firms tests, risk-rejection-shape).

## Historical: `docker-compose.real.yml`

Until the family compose was restructured to make Real the default
(2026-05-07), the real stack was an opt-in overlay
`docker-compose.real.yml`. That file is preserved as an empty no-op
shim so pre-existing CI command-lines and external tutorials still
parse. New tooling should use `docker-compose.yml` directly (or
combine with `docker-compose.unavailable.yml` for the inverse).

## Demo overlay (opt-in, laptop-only)

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.demo.yml \
    up -d --build
# open http://localhost:8080 and log in as bot-clientA / demopass
```

Flips the trading-host to `Mode=Mock` + `AllowErInjection=true`
(synthetic ER injection enabled; #163 collapsed the legacy
`Mode=Simulator` into this combination) and starts the **demo-driver**
console
([`backend/tools/B3.Trading.DemoDriver`](../backend/tools/B3.Trading.DemoDriver))
which:

- logs in as `bot-clientA` and `bot-clientB` (role=user),
- waits for order-ingress `/ready` before enabling submit/inject loops (important
  when pointed at a Real-mode host whose HTTP process is live before FIXP
  sessions establish),
- submits random buy/sell limit orders around the configured reference
  prices at `DEMO_SUBMIT_RATE_HZ` (default 0.5 Hz per bot),
- logs in as `demo-admin` (role=admin) and POSTs `/api/admin/simulator/er`
  to inject synthetic Fill / PartialFill ERs at `DEMO_INJECT_RATE_HZ`
  (default 0.3 Hz across the registry).

Result: the trader UI's blotter, executions and positions panels move
on their own. **Log in as one of the bots** to see that bot's view —
`alice` does not submit orders so her view stays empty.

| Service | What it does |
|---|---|
| `trading-host` | flipped to `Mode=Mock` + `AllowErInjection=true`; seeds 3 extra users (`bot-clientA`, `bot-clientB`, `demo-admin`) |
| `demo-driver` | hosts the submit + inject loops; logs to stdout |

### Safety

`AllowErInjection=true` MUST NOT be enabled against a host reachable
beyond your laptop — anyone with admin creds (default
`demo-admin/demopass`, public in this repo) can mint synthetic fills.
The trading-host **refuses to boot** in `ASPNETCORE_ENVIRONMENT=Production`
with `AllowErInjection=true` unless
`Trading:Exchange:AllowErInjectionInProduction=true` is also set; do not
flip that switch.

The demo passwords are PBKDF2-hashed and committed in
`docker/docker-compose.demo.yml` so the overlay works zero-config.
Override via `DEMO_BOT_A_HASH` / `DEMO_BOT_A_SALT` (and `_B_`, `_ADMIN_`)
+ matching `DEMO_BOT_A_PASSWORD` etc. when rotating credentials.

### Tuning

| Env var | Default | Purpose |
|---|---|---|
| `DEMO_SUBMIT_RATE_HZ` | 0.5 | Per-bot submission rate |
| `DEMO_INJECT_RATE_HZ` | 0.3 | Global inject rate (admin → registry) |
| `DEMO_MAX_OPEN_ORDERS` | 50 | Per-bot cap; submitter pauses once reached |
| `DEMO_MODE` | auto-detect | `auto-detect` \| `simulator-inject` \| `submit-only` |

### Out of scope (D1)

- No MD ticks: this overlay does not include `--profile marketdata`,
  so the MD panel stays static. Combine with the marketdata profile
  (PCAP) to also see trades on the tape.
- No `Mode=Real` cross-firm bots: real-stack cross requires multiple
  firm sessions wired through the `docker-compose.real.yml` overlay;
  tracked as a follow-up to issue #72.
- No order cancellation cycle: bots submit + injector fills; bounded
  via `DEMO_MAX_OPEN_ORDERS`, but stale working orders accumulate
  during long runs.

## Market-maker overlay (opt-in, real-market sandbox demo)

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.market-maker.yml \
    up -d --build
```

The counterpart to the Demo overlay above for a *real* order-matching
story (#683, evolved from the original simulator-bot in #134):
`trading-host` stays `Mode=Real` (the default) and a standalone FIXP
client
([`backend/tools/B3.Trading.MarketMakerBot`](../backend/tools/B3.Trading.MarketMakerBot))
connects on its own session (10102) and behaves as a co-located,
continuous two-sided market maker — one resting bid + one resting ask
per configured instrument, re-quoted immediately on fill/cancel and
(with a defensive delay) on reject. Any end-client can then actually
buy/sell PETR4/VALE3/ITUB4 against the bot's resting liquidity through
the real matching engine, not a synthetic ER injector.

This overlay also flips `Trading__Sandbox__AllowSelfCashDeposit=true`
(#679/#681) on `trading-host`, so a logged-in end-client can
self-service `POST /api/balance/deposit` for buying power instead of
needing an admin round-trip — without it there'd be a market but no way
for a fresh sandbox account to fund a trade against it.

**Do not additionally stack `docker-compose.demo.yml` on top of this
overlay.** `demo.yml` flips `trading-host` to `Mode=Mock`, which serves
end-client orders from an in-process `MockEntryPointClient` instead of
routing them to `matching-platform` — end-client orders would never
reach the book the bot is quoting into, defeating the whole point of
this overlay. The two overlays tell mutually exclusive stories (ER
injection vs. a real matching engine) and are not meant to compose.

| Service | What it does |
|---|---|
| `trading-host` | stays `Mode=Real` (base default); `Trading__Sandbox__AllowSelfCashDeposit` flipped to `true` |
| `market-maker-bot` | FIXP session 10102; one resting bid + ask per configured instrument; uses the `marketdata` WS feed with an explicit `StaticRefPrice` (default) or `PauseAndCancel` feed-loss policy |

`market-maker-bot` is now published the same way as `trading-host`/`frontend`:
CI builds+pushes a `candidate-<sha>` digest in the `trading-host` job,
`market-maker-conformance` proves that exact digest boots and negotiates
against the real stack, and `promote` retags the tested digest to
`ghcr.io/pedrosakuma/b3-market-maker-bot:latest` (+ semver/branch tags) on
merge to `main` — set `MARKET_MAKER_BOT_IMAGE=ghcr.io/pedrosakuma/b3-market-maker-bot:latest`
(the compose file's own default) to run it without a local build. A
Kubernetes deploy of this same overlay is available as the
[`charts/b3-market-maker-bot`](../charts/b3-market-maker-bot) Helm chart
(published to `oci://ghcr.io/pedrosakuma/charts/b3-market-maker-bot`,
see `charts/README.md`) — a single-replica StatefulSet + small RWO PVC for
the bot's FIXP session-state watermark, no Service/ports (outbound-only).

### Safety

`Trading__Sandbox__AllowSelfCashDeposit=true` lets **any** authenticated
end-client mint their own buying power. The host refuses to boot in
`ASPNETCORE_ENVIRONMENT=Production` with it set unless
`Trading__Sandbox__AllowSelfCashDepositInProduction=true` is also set
(`SandboxCashDepositBootGuard`) — do not flip that opt-out for a real
deployment; this overlay is sandbox/demo-only, same posture as the Demo
overlay's `AllowErInjection`.

### Tuning

The bot's instruments (`Symbol`/`SecurityId`/`RefPrice`/`TickSize`/
`LotSize`/`QuoteLots`/`SpreadTicks`/`InventorySkew`/`VolatilitySpread`) and reconcile interval are set via
`MarketMaker__*` env vars in `docker-compose.market-maker.yml` — see
that file's inline comments for the full list. `MarketMaker__Instruments`
must line up with `docker/real/instruments-eqt.json`'s `SecurityId`s
(matching-platform's wire truth), which is independent of whatever
`SecurityId` numbering `marketdata` itself uses for the same symbols.

Feed-loss behavior is explicit and defaults to the legacy-compatible static
fallback:

```yaml
MarketMaker__MarketData__WsUrl: ws://marketdata:8080/ws
MarketMaker__MarketData__FeedLossPolicy: StaticRefPrice
MarketMaker__MarketData__MaxReferenceAge: "00:00:30"
```

`StaticRefPrice` preserves the previous behavior exactly: live references are
used while the socket is connected, and the configured instrument `RefPrice`
is used before the first update or while disconnected. A reconnect may
immediately reuse the retained live cache.

Set `FeedLossPolicy=PauseAndCancel` for strict operation. It requires a
nonblank absolute `WsUrl` and positive `MaxReferenceAge`. Each symbol remains
paused until it receives a valid reference in the current connection epoch;
disconnect/reconnecting/faulted state, a subscription error, no current-epoch
update, or an over-age update suppresses new orders and repeatedly drives both
active sides through the normal cancel-ack lifecycle. Initial connection
failures keep the process alive and retry in the background while quotes stay
paused. Reconnect alone is not sufficient: a fresh reference for that symbol
is required before both missing sides resume.

Cancel acknowledgements are also bounded:

```yaml
MarketMaker__CancelAckTimeout: "00:00:10"
```

If a transmitted cancel's execution report is lost, the bot expires only that
matching pending marker after this positive timeout and retries through the
existing reconcile/coalescing guards. The original order remains tracked as
open until an authoritative cancel/fill ER arrives. Late cancel acknowledgements
still close it, while late rejects of expired cancel IDs are recognized as
cancel rejects and cannot free the original order as if they rejected a new
submit. `MinRequoteInterval` continues to throttle strategy/feed retries.

`MarketMaker__MaxOrderAge` is an order lease, not a missed-fill detector. Each
healthy expiry submits a TTL refresh cancel at Information level and replaces
that side after the authoritative cancel ACK. A brief one-sided book interval
during this cancel/ACK/requote round trip is expected; it should end with the
immediate replacement submit. Cancel rejection, synchronous cancel failure,
`CancelAckTimeout`, or replacement-submit failure remain warnings because they
mean the side could not be restored within the healthy cycle. An asynchronous
replacement reject also emits one restoration warning plus
`bot_orders_quote_restore_rejected_total`, while remaining included in the
generic reject counter. The cancel trigger follows the side reservation, so a
concurrent reconcile submit that wins before the direct requote is classified
the same way. Monitor `bot_orders_ttl_refresh_total` as expected lifecycle
volume, not as an incident.

Startup cleanup is an explicit rollout opt-in with a separate, deliberately
longer bound:

```yaml
MarketMaker__StartupCleanupEnabled: "false"
MarketMaker__StartupCleanupTimeout: "00:05:00"
```

The default `false` preserves historical startup behavior and is the only safe
setting with matching-platform versions from before
[`B3MatchingPlatform#569`](https://github.com/pedrosakuma/B3MatchingPlatform/issues/569).
Those versions emit `OrderMassActionReport(ACCEPTED)` before dispatcher
execution, so neither that report nor a FIXP `Sequence` heartbeat is an
execution barrier.

If persisted state identifies a prior session that requires cleanup, the bot
fails startup while this option is `false`; it never bypasses the compatibility
opt-in or submits replacement quotes over potentially restored venue orders.

Set `StartupCleanupEnabled=true` only after deploying a matching-platform
release containing #569. Under that minimum contract, the solicited
`MassActionExecuted(ACCEPTED)` correlated by the request `ClOrdID` is terminal:
all cancellation ERs have already traversed the ordered FIXP business stream.
The bot awaits both the outbound transport task and that terminal report before
starting market data or submitting quotes. A legacy session can contain
100,000 orders, so five minutes leaves production headroom without weakening
the 10-second single-order cancel retry policy. Timeout, rejection, request
failure, or event-stream failure terminates startup without quoting.

Inventory skew is opt-in and defaults off independently for every
instrument:

```yaml
MarketMaker__Instruments__0__InventorySkew__Enabled: "false"
MarketMaker__Instruments__0__InventorySkew__FullSkewAtLots: "10"
MarketMaker__Instruments__0__InventorySkew__MaxSkewTicks: "5"
```

When enabled, the bot reads signed net quantity from its process-local P&L
ledger and shifts both sides' mid down while long or up while short. The shift
scales linearly until `FullSkewAtLots * LotSize`, saturates at
`MaxSkewTicks`, and is combined with the spread before the single final tick
rounding. `FullSkewAtLots` is only the normalization/saturation band: it is
not a position limit, does not suppress either side, and does not cap exposure.
Each newly-accounted partial or full own fill reevaluates both resting sides
through the normal throttled cancel-ack-then-requote path.

Volatility-adaptive spread is also opt-in and defaults off per instrument:

```yaml
MarketMaker__Instruments__0__VolatilitySpread__Enabled: "false"
MarketMaker__Instruments__0__VolatilitySpread__Window: "00:01:00"
MarketMaker__Instruments__0__VolatilitySpread__MaxSamples: "120"
MarketMaker__Instruments__0__VolatilitySpread__MinSamples: "10"
MarketMaker__Instruments__0__VolatilitySpread__Multiplier: "1.0"
MarketMaker__Instruments__0__VolatilitySpread__MaxAdditionalSpreadTicks: "20"
```

When enabled, only valid `Trade` events contribute samples; repeated
`InfoSnapshot` reference updates never count as returns. After the first trade,
each absolute trade-to-trade move is measured in `TickSize` units, including
zero moves. The bounded, age-pruned arithmetic mean is multiplied and rounded
up, then capped before being added to the configured `SpreadTicks`. The static
spread is always the floor, and both bid and ask use the same unified pricing
decision and single final tick rounding.

The estimator retains bounded state across market-data disconnects, but dynamic
widening is disabled while disconnected: the existing `StaticRefPrice`
fallback remains in force and quotes revert to the configured static spread.
On reconnect, retained samples resume widening only if they are still inside
`Window` and satisfy `MinSamples`; otherwise fresh valid trades must rebuild
readiness. Under `PauseAndCancel`, the reference-readiness gate independently suppresses
the symbol; retained volatility samples never bypass that gate.

The standard OTLP meter exposes
`bot.strategy.volatility_move_estimate_ticks` and
`bot.strategy.volatility_additional_half_spread_ticks`, each tagged only by
configured `symbol`; effective-tick changes also emit structured
`[mm-volatility]` diagnostics.

### Strategy soak evidence

Use [`operations/market-maker-soak.md`](operations/market-maker-soak.md) and
`scripts/soak/run-market-maker-soak.sh` for isolated static, inventory-skew,
volatility-spread, and `PauseAndCancel` runs. The dedicated
`docker-compose.market-maker-soak.yml` overlay routes the bot to the bundled
Collector, parameterizes only the opt-in feature switches, and isolates
container/network/volume names. It also pins `Trading__Auth__Mode=Local` plus
local login and resolves the operator-selected trading/counterparty seed and
risk mappings without recording credentials.
Closure evidence uses one shared suite
manifest and compares actual runtime `sha256:` image IDs across profiles;
mutable tags alone are not accepted. It also proves container/image/start/restart
continuity for all six critical services, allowing only the explicitly recorded
marketdata stop/start in the outage profile. Evidence paths are restricted to
the checkout's ignored `soak-artifacts/` tree. Missing Prometheus series are
recorded as absent and fail rather than being treated as zero. Pre-run teardown
is mandatory, and final teardown failures invalidate the result while retaining
aggregated cleanup evidence. The strict profile stabilizes submitted-order
telemetry before outage, then proves a connected feed remains ineligible with
zero quotes for a full export-plus-scrape cycle before generating a fresh
recovery trade. Sensitive HTTP bodies and bearer headers use anonymous file
descriptors rather than process arguments; exported password inputs are unset
before the first child process and the live Docker-event environment is checked.
The first acceptance run must build from the recorded clean git SHA, while
later no-build profiles must exactly match the manifest's image IDs/digests.
Multi-hour evidence is operator-run and is not part of normal CI.

### Out of scope

- `KeepLastAndWiden` is not a supported feed-loss policy.
- No live-order-book-depth reaction: quotes anchor on
  `TradingReferencePrice`/`LastTradePrice`, not the SDK's `BookFeed`
  top-of-book. A closer-to-the-book anchor is a reasonable follow-up.
- No dynamic instrument discovery: the bot's instrument list is
  config-driven, not derived from `marketdata`'s `SecurityDefinition`
  stream — see `MarketDataFeed`'s doc comment for why (SecurityId
  namespaces don't line up).

### Conformance coverage (#683 item 4)

`backend/tests/B3.Trading.Conformance/Spec_HTTP_MarketMaker/MarketMakerLiquiditySpecTests.cs`
self-deposits cash as an ordinary end-client, then crosses the bot's
resting quote on both sides (buy into the ask, sell back into the
re-quoted bid) and asserts each leg fills within a bounded window. It's
gated on `RequiresMarketMakerSandbox` (operator sets
`B3T_MARKET_MAKER_SANDBOX=true`) — a flag deliberately separate from
`RequiresSandboxMatching`, because the bot's resting bid/ask would
otherwise intercept the same-user Buy+Sell pairs the
`RequiresSandboxMatching` specs submit to observe a self-print. CI runs
this scenario in its own job (`market-maker-conformance` in
`.github/workflows/docker.yml`) against
`docker-compose.yml` + `docker-compose.real.yml` +
`docker-compose.market-maker.yml` + `docker-compose.conformance.yml` +
`docker-compose.market-maker-conformance.yml`, so it never shares an
order book with the `real-stack-conformance` job.

## Honest no-broker mode

The trading-host has four exchange modes, configured via
`Trading:Exchange:Mode`:

| Mode | Behaviour | Use case |
|---|---|---|
| `Stub` | Silently accepts every order | Local dev only — never honest |
| `Mock` | Fakes execution reports in-process | Demo of the working-orders + positions UI |
| `Real` | Wires `B3.EntryPoint.Client` against `Firms[]` | Production / UAT |
| `Unavailable` | Fail-closed: every submit/cancel returns 502 | Standalone image default / unavailable overlay |

The standalone image's `appsettings.Docker.json` defaults to
`Mode=Unavailable`. The family `docker/docker-compose.yml` intentionally
overrides it to `Mode=Real` and supplies matching-platform plus three firm
sessions. Use `docker-compose.unavailable.yml` for the honest no-broker mode:

- `GET /health` returns `exchange.mode=Unavailable` and `readyForOrders=false`.
- `POST /api/orders` returns `502 BadGateway` with `reason: gateway unavailable`.
- Auth, WebSocket subscriptions, positions read-side, kill-switch admin —
  all keep working. The trader UI logs in and shows an empty book.

Switch modes by overriding `Trading__Exchange__Mode`:

```yaml
# docker/docker-compose.override.yml — try Mock to see the UI come alive
services:
  trading-host:
    environment:
      Trading__Exchange__Mode: Mock
```

## Logging in (dev only)

The `.env.example` ships a seed user that lets you smoke-test the UI
immediately:

| Field | Value |
|---|---|
| Username | `alice` |
| Password | `wonderland` |

These match the `Trading:Auth:Users[0]` block in
[`appsettings.json`](../backend/src/B3.Trading.Host/appsettings.json) and
are PBKDF2-HMAC-SHA256, 600 000 iterations. Anyone reading this repo can
log in with them — **rotate the hash + salt for anything beyond a
laptop demo.**

```bash
# verify the seed user works once the stack is up
TOKEN=$(curl -sS -X POST http://localhost:8080/api/auth/login \
    -H 'Content-Type: application/json' \
    -d '{"username":"alice","password":"wonderland"}' | jq -r .token)
curl -sS -H "Authorization: Bearer $TOKEN" http://localhost:8080/api/positions
# []  (no broker connected — see Honest no-broker mode)
```

## Required env vars

The trading-host **refuses to boot** without these. That's
[`AuthSigningKeyValidator`](../backend/src/B3.Trading.Api/Auth/AuthSigningKeyValidator.cs)
doing its job — better fail loudly than ship the committed dev key into
a real environment.

| Var | What | Generate |
|---|---|---|
| `TRADING_AUTH_SIGNING_KEY` | JWT HS256 signing key, >=32 UTF-8 bytes, not the dev default | `openssl rand -base64 48` |

For Entra rollout set `Trading__Auth__Mode=Hybrid` or `Entra` and configure
`Trading__Auth__ExternalIdentity__Authority`, `Issuer`, `TenantId`,
`Audience`, `RequiredScope` and `AllowedClientApplicationIds__0`. Local mode
is still the default; external tokens are accepted only by `POST /api/auth/exchange`
and are exchanged for the internal 10-minute trading JWT. `Entra` mode also
requires at least one active externally linked admin in the identity directory;
use the Hybrid admin binding route and runbook before flipping the mode.
| `TRADING_SEED_PASSWORD_HASH` | PBKDF2 hash of the seed user password | helper command (TBD: `backend/tools/PasswordHasher`) |
| `TRADING_SEED_PASSWORD_SALT` | matching salt | same |

`docker/.env.example` is your starting point — copy to `docker/.env`.

## Observability (opt-in)

Set `OTEL_EXPORTER_OTLP_ENDPOINT` (and friends) and the host wires up
OpenTelemetry: application meter (`B3.Trading`), ASP.NET Core, .NET
runtime instrumentation, traces. Without it the SDK is not registered at
all — zero overhead for the no-broker default. See
[`docs/METRICS.md`](METRICS.md) for the full instrument list and a
collector-only smoke test recipe.

### Bundled obs stack

An overlay file ships otel-collector, Prometheus with versioned rules,
Alertmanager with a local inspectable receiver, and Grafana. Bring it up
alongside the base stack:

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.observability.yml \
    up -d --build
```

This exports `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`
into the trading-host container automatically — no extra env required. The
market-maker bot remains opt-in; the strategy-soak overlay wires that service
to the same Collector.

Default ports:

| Service        | URL                          | Credentials       |
| -------------- | ---------------------------- | ----------------- |
| trading-host   | http://localhost:5000        | `alice` / `wonderland` |
| frontend       | http://localhost:8080        | (proxy to host)   |
| Prometheus     | http://localhost:9090        | —                 |
| Alertmanager   | http://localhost:9093        | —                 |
| Alert receiver | http://localhost:18093/received | local smoke only |
| Grafana        | http://localhost:3000        | `admin` / `admin` (change at first login) |
| otel-collector | (internal: 4317 OTLP, 8889 prom expo) | — |

The Grafana home dashboard is pre-set to "B3 Trading — Process Up". It
shows: service-up indicator, orders submitted, active WS connections,
working-set memory, HTTP request rate by route, and GC collections by
generation. All panels query only verified-real instruments — no fake
ack-latency placeholders.

Tear down (keeping data volumes):

```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.observability.yml down
```

Use `down -v` to also drop Prometheus, Alertmanager, and Grafana volumes.

## Running conformance

The conformance suite (`backend/tests/B3.Trading.Conformance/`) lives
behind its own overlay. It builds a standalone runner image and runs it
against the trading-host on the internal `b3-net`:

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.conformance.yml \
    up -d --build --wait trading-host

docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.conformance.yml \
    run --rm conformance
```

The runner expects three env vars (the overlay wires sane defaults from
your `.env`):

| Var | Default | Notes |
| --- | --- | --- |
| `B3T_BASE_URL` | `http://trading-host:5000` | Internal DNS via the bridge network. |
| `B3T_AUTH_USER` | `${TRADING_SEED_USER:-alice}` | Must exist in the host's user store. |
| `B3T_AUTH_PASS` | `${TRADING_SEED_PASSWORD:-wonderland}` | Plaintext that produced the host's hash/salt. |

The entrypoint preflights the env, waits up to 60 s for process `/live`, and
then waits for `/ready` unless `/health` reports `Mode=Unavailable` (override
with `B3T_REQUIRE_READY=true|false`). It attempts a login before invoking
`dotnet test`. Exit codes follow BSD
sysexits so failures are easy to distinguish:

| Code | Meaning |
| --- | --- |
| `0` | All tests passed. |
| `1` | A test failed (or `B3T_REQUIRE_CONFIGURED=true` and env is invalid — the runner image always sets this so a misconfig fails loudly instead of silently skipping). |
| `64` | A required env var is missing. |
| `69` | `B3T_BASE_URL/live`, or required order-ingress `/ready`, never came up within 60 s. |
| `78` | Preflight login was rejected (creds don't match the host's seed). |

You can also point the same image at any deployed host (UAT / staging),
no compose needed:

```bash
docker run --rm \
    -e B3T_BASE_URL=https://trading.uat.example.com \
    -e B3T_AUTH_USER=alice \
    -e B3T_AUTH_PASS='...' \
    b3-trading-conformance:dev
```

CI runs this on every PR via the `conformance` job in
`.github/workflows/docker.yml`, with the alice/wonderland defaults that
match the committed seed hash/salt.

## Live reference price (B3MarketDataPlatform)

The price collar in the risk pipeline can run in one of two modes:

| Mode    | Toggle                              | Source of `IReferencePrice`                                |
| ------- | ----------------------------------- | ---------------------------------------------------------- |
| Static  | `Trading__MarketData__WsUrl` empty  | `Trading:Risk:ReferencePrices` dictionary (config-only)    |
| Live    | `Trading__MarketData__WsUrl` set    | `MarketDataReferencePrice` over `B3.MarketData.WebSocketClient` SDK with the static dict as fallback |

The static mode is the default and has zero overhead — no SDK pull, no
background tasks. Switching to live keeps the same fail-open semantics:
on cache miss, stale entry, or SDK fault, the collar falls back to the
static dict and ultimately approves when neither side has a number.

Wire it up against the bundled marketdata service:

```bash
# .env additions
TRADING_MARKETDATA_WS_URL=ws://marketdata:8080/ws
TRADING_MARKETDATA_SYMBOL_0=PETR4
MARKETDATA_WS_URL=ws://localhost:8081/ws
APP_TITLE="Acme Trader"
# extra symbols: TRADING_MARKETDATA_SYMBOL_1=VALE3, etc. (compose env
# only forwards index 0; for more, add Trading__MarketData__Symbols__N
# directly to docker-compose.yml or appsettings.Docker.json).

docker compose --profile marketdata up -d
```

The frontend container also renders `frontend/js/env.js` at boot from
deploy-time env vars:

| Variable | Default | Effect |
| -------- | ------- | ------ |
| `MARKETDATA_WS_URL` | empty | Seeds `window.__B3_CONFIG__.marketDataWsUrl` for the Market Data panel; empty preserves the localhost/off-localhost fallback chain in `frontend/js/protocol.js`. |
| `APP_TITLE` | `B3TradingPlatform` | Seeds `window.__B3_CONFIG__.appTitle`, which `frontend/js/app.js` applies to the browser `<title>`, login heading, and topbar brand text. |
| `AUTH_MODE` | `Local` | Shared frontend + trading-host auth mode: `Local`, `Hybrid`, or `Entra`. Local preserves password/TOTP/signup compatibility and leaves `/api/auth/exchange` absent. |
| `AUTH_LOCAL_LOGIN_ENABLED` | empty | Optional override for showing local password login in Hybrid; Entra mode should leave this disabled. |
| `AUTH_SIGNUP_ENABLED` | empty | Optional override for local signup. Defaults: Local on, Hybrid/Entra off. |
| `AUTH_TOTP_ENABLED` | empty | Optional override for local TOTP controls. Entra mode hides them. |
| `AUTH_AUTHORITY` | empty | Tenant-specific Entra External ID authority, e.g. `https://tenant.ciamlogin.com/<tenant-id>/v2.0`. |
| `AUTH_ISSUER` | empty | Backend exact issuer expected after trusted metadata validation. Required by host validation in Hybrid/Entra. |
| `AUTH_TENANT_ID` | empty | Backend `tid` claim requirement for the configured tenant. Required in Hybrid/Entra unless host config opts out. |
| `AUTH_CLIENT_ID` | empty | Public SPA client id. This is not a secret. |
| `AUTH_API_SCOPE` | empty | Delegated API scope requested by MSAL, usually `api://<api-app-id-uri>/<scope-name>`. |
| `AUTH_API_AUDIENCE` | empty | Backend exact access-token audience, usually the API application ID URI. Required by host validation in Hybrid/Entra. |
| `AUTH_REQUIRED_SCOPE` | empty | Backend exact delegated `scp` value, usually the scope name (for example `access_as_user`). Required by host validation in Hybrid/Entra. |
| `AUTH_REDIRECT_URI` / `AUTH_LOGOUT_URI` | empty | SPA redirect and post-logout URLs. Empty falls back to the current page URL for non-container local dev. |
| `AUTH_KNOWN_AUTHORITIES` | empty | Comma-separated authority hosts for MSAL, e.g. `tenant.ciamlogin.com`. |

The same variables are wired into the trading-host as `Trading:Auth:*`; if
`AUTH_MODE=Hybrid` or `AUTH_MODE=Entra` and any backend external-identity value
is missing, the host options validator fails closed at startup rather than
silently serving Local auth.

Example Hybrid/Entra settings:

```bash
AUTH_MODE=Hybrid
AUTH_AUTHORITY=https://your-tenant.ciamlogin.com/your-tenant-id/v2.0
AUTH_ISSUER=https://your-tenant.ciamlogin.com/your-tenant-id/v2.0
AUTH_TENANT_ID=your-tenant-id
AUTH_CLIENT_ID=00000000-0000-0000-0000-000000000000
AUTH_API_SCOPE=api://your-trading-api-client-id/access_as_user
AUTH_API_AUDIENCE=api://your-trading-api-client-id
AUTH_REQUIRED_SCOPE=access_as_user
AUTH_REDIRECT_URI=https://trader.example.com/
AUTH_LOGOUT_URI=https://trader.example.com/
AUTH_KNOWN_AUTHORITIES=your-tenant.ciamlogin.com
```

The frontend image never accepts or renders a client secret. CSP `connect-src`
and `frame-src` are rendered from the configured authority/known-authority
origins plus the configured market-data WebSocket origin; no wildcard or broad
`ws:`/`wss:` source is added.

Tunables (defaults shown):

| Setting (`Trading:MarketData:`) | Default       | Notes                                                                  |
| ------------------------------- | ------------- | ---------------------------------------------------------------------- |
| `WsUrl`                         | empty         | empty = feature off                                                    |
| `Symbols`                       | `[]`          | empty + WsUrl set logs a warning                                       |
| `MaxStaleness`                  | `00:05:00`    | older cache entries fall through to the static fallback; `0` disables  |

Operationally: the SDK reconnects transparently with backoff and
auto-resubscribes after disconnects. Watch
`trading_marketdata_subscribe_errors_total` (tagged by `symbol` +
`reason`) for venue-side rejections — symbol-mapping mismatches between
this host and the matching engine surface there.

## Persistence

The event store WAL + snapshots live in the named volume `b3-trading-data`
mounted at `/var/lib/b3trading`. The SQLite trading-user directory defaults to
`/var/lib/b3trading/identity/users.db` and is opened in WAL mode with
`synchronous=FULL`. Never copy selected files while the host is live. Run
`scripts/backup/backup-and-restore-drill.sh`: it gracefully quiesces the host,
archives the whole volume, verifies a file manifest, and boots an isolated
real-mode restore. The drill also proves a seeded durable bot credential
survives and a fresh trade fills before the original host resumes.

## Image release gates

`.github/workflows/docker.yml` builds PR images without publishing. On `main`
or a `v*.*.*` tag it first publishes candidate multi-arch manifests and records
their immutable digests. Recovery, unavailable-mode conformance, real-stack
conformance, and ARM64 identity runtime then pull the exact host digest (compose
builds are disabled). Promotion reuses those same digests, boots the exact
host/frontend manifests on AMD64 and ARM64, and only then attaches `sha-*`,
semver, branch, and `latest` tags. A failed or skipped required job cannot move
a release tag.

## Health & readiness

- `GET /live` — process is up (used by the image/container `HEALTHCHECK` and
  liveness probes)
- `GET /ready` — order ingress is safe: not draining, identity and WAL healthy,
  and every required exchange session established (used by readiness probes
  and ingress assertions; intentionally 503 in `Mode=Unavailable`)
- `GET /health` — full snapshot, including `exchange.{mode, readyForOrders, firmCount}`
  `persistence.{enabled, dataDirectory}` and `identityDirectory.{provider, ready, schemaVersion}`

Compose's `frontend` service waits for trading-host to be `service_healthy`
before starting, so the UI is never served against a dead backend.

## Building locally

CI builds the image on every PR (no push) and on `main` (pushes to GHCR).
Local one-off:

```bash
docker buildx build \
    -f backend/src/B3.Trading.Host/Dockerfile \
    -t b3-trading-host:dev .
```

The build context is the **repo root** (the Dockerfile copies
`global.json`, `Directory.Build.props`, `B3TradingPlatform.slnx` and
`backend/src/`).

## Limitations / known gaps

- **No matching-platform yet** — pending [B3MatchingPlatform#88](https://github.com/pedrosakuma/B3MatchingPlatform/issues/88).
  Until that's resolved, `Mode=Real` requires you to BYO an EntryPoint
  endpoint or use `B3.EntryPoint.Client.TestPeer` from a separate test
  container.
- **Frontend isn't AOT-compiled** — it's static HTML/JS served by nginx;
  good enough for the trader UI scope.
- **Single-instance only** — the event store is local-disk; running two
  trading-host replicas against the same volume would corrupt the WAL.

## FIXP Listener

The inbound FIXP listener can be enabled in the docker stack. Add these env vars
to the `trading-host` service:

```yaml
environment:
  Trading__EntryPointListener__Enabled: "true"
  Trading__EntryPointListener__Endpoint: "0.0.0.0:5001"
  Trading__EntryPointListener__Tls__Required: "false"  # true in Production
  # For TLS, mount cert files and set:
  # Trading__EntryPointListener__Tls__CertPath: /certs/server.crt
  # Trading__EntryPointListener__Tls__KeyPath: /certs/server.key
  # For mTLS, mount the bot CA bundle and optional SHA-256 deny-list:
  # Trading__EntryPointListener__Tls__ClientCertificateMode: Required  # None|Optional|Required
  # Trading__EntryPointListener__Tls__ClientCa__BundlePath: /certs/bot-ca-bundle.pem
  # Trading__EntryPointListener__Tls__ClientCa__DenyListPath: /certs/bot-denylist.txt
  # Trading__EntryPointListener__Tls__ClientCa__ReloadInterval: "00:05:00"
  # Trading__EntryPointListener__Tls__RequireClientAuthEku: "true"
  # Trading__EntryPointListener__AcceptRateLimit__ConnectionsPerSecondPerIp: "0"
  # Trading__EntryPointListener__AcceptRateLimit__BurstPerIp: "30"
volumes:
  # - ./certs/server.crt:/certs/server.crt:ro
  # - ./certs/server.key:/certs/server.key:ro
  # - ./certs/bot-ca-bundle.pem:/certs/bot-ca-bundle.pem:ro
  # - ./certs/bot-denylist.txt:/certs/bot-denylist.txt:ro
ports:
  - "5001:5001"  # expose FIXP port
```

When mTLS is enabled in a compose overlay, set the required mTLS variables in
that overlay and mount the referenced `/certs` files there. Demo and
conformance overlays must do this explicitly when they exercise mTLS so the
base stack remains PAT-only by default.

See the full [FIXP listener operations guide](operations/fixp-listener.md) for
TLS setup, rate-limit tuning, and monitoring.

## Bumping the matching and marketdata images

The `matching-platform` and `marketdata` services are pinned to
**immutable image digests** in `docker/docker-compose.yml`. We deliberately
do **not** track `:latest` — every bump is a reviewable, CI-validated PR.
When a UMDF snapshot or epoch contract changes, update both digests in one
PR so the producer and consumer remain compatible.

### Daily local override

Devs who want the bleeding edge for a one-off run can override both sides:

```bash
MATCHING_IMAGE=ghcr.io/pedrosakuma/b3-matching:latest \
MARKETDATA_IMAGE=ghcr.io/pedrosakuma/b3-marketdata:latest \
  docker compose -f docker/docker-compose.yml up matching-platform marketdata
```

### Detecting drift

```bash
scripts/matching-image/check-upstream.sh           # tag=latest
scripts/matching-image/check-upstream.sh v1.2.3    # specific tag
```

Exit `0` means the pin matches upstream; `2` means upstream has
advanced and a bump is due; `1` means an IO / argument error. The new
digest is printed on the last stdout line when drift is detected.

### Automated bump cadence

The [`matching-image-bump`](../.github/workflows/matching-image-bump.yml)
workflow runs the script on a weekly cron (Mondays 06:00 UTC) **and**
on `workflow_dispatch`. When drift is detected it opens a PR bumping
the matching pin; the standard CI matrix (`real-stack-conformance` included)
validates it against the pinned marketdata digest before a human merges.
Marketdata digest changes are intentionally manual so a newer consumer cannot
silently activate an incompatible snapshot contract.

### Manual bump (one-liner)

```bash
new=$(scripts/matching-image/check-upstream.sh | tail -n1)
old=$(grep -oE 'b3-matching@sha256:[0-9a-f]{64}' docker/docker-compose.yml | head -n1 | sed 's/.*@//')
sed -i "s|${old}|${new}|g" docker/docker-compose.yml
```
