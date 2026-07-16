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

To validate the no-broker fail-closed surface instead (POST /orders →
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
| `matching-platform` | `ghcr.io/pedrosakuma/b3-matching@sha256:…` (pinned — see [Bumping the matching image](#bumping-the-matching-image)) | (internal `:9876`, `:8080`) | FIXP TCP listener + UMDF unicast publisher + `/admin/channels/*` snapshot ops |
| `marketdata` | `ghcr.io/pedrosakuma/b3-marketdata:latest` | `8081` | UMDF unicast consumer (`30084/30184/31084 udp`) + WebSocket fanout (`:8080`) |
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

- Validating the fail-closed surface (POST /orders → 502 BadGateway,
  /health `readyForOrders=false`, ticket disabled).
- Auth/admin-only flows (login, /admin/firms CRUD) without paying the
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
- submits random buy/sell limit orders around the configured reference
  prices at `DEMO_SUBMIT_RATE_HZ` (default 0.5 Hz per bot),
- logs in as `demo-admin` (role=admin) and POSTs `/admin/simulator/er`
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

## Honest no-broker mode

The trading-host has four exchange modes, configured via
`Trading:Exchange:Mode`:

| Mode | Behaviour | Use case |
|---|---|---|
| `Stub` | Silently accepts every order | Local dev only — never honest |
| `Mock` | Fakes execution reports in-process | Demo of the working-orders + positions UI |
| `Real` | Wires `B3.EntryPoint.Client` against `Firms[]` | Production / UAT |
| `Unavailable` | Fail-closed: every submit/cancel returns 502 | **Container default** |

The Docker layer sets `Mode=Unavailable` because shipping `Stub` would be
a lie (orders silently succeed without any broker), and shipping `Real`
without configured firms throws on boot. `Unavailable` is what an honest
"no broker connected" state looks like:

- `GET /health` returns `exchange.mode=Unavailable` and `readyForOrders=false`.
- `POST /orders` returns `502 BadGateway` with `reason: gateway unavailable`.
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
TOKEN=$(curl -sS -X POST http://localhost:8080/auth/login \
    -H 'Content-Type: application/json' \
    -d '{"username":"alice","password":"wonderland"}' | jq -r .token)
curl -sS -H "Authorization: Bearer $TOKEN" http://localhost:8080/positions
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
is still the default; external tokens are accepted only by `POST /auth/exchange`
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

An overlay file ships otel-collector + Prometheus + Grafana with a
provisioned datasource and a starter dashboard ("B3 Trading — Process
Up"). Bring it up alongside the base stack:

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.observability.yml \
    up -d --build
```

This exports `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`
into the trading-host container automatically — no extra env required.

Default ports:

| Service        | URL                          | Credentials       |
| -------------- | ---------------------------- | ----------------- |
| trading-host   | http://localhost:5000        | `alice` / `wonderland` |
| frontend       | http://localhost:8080        | (proxy to host)   |
| Prometheus     | http://localhost:9090        | —                 |
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

Use `down -v` to also drop the Prometheus/Grafana volumes
(`b3-prometheus-data`, `b3-grafana-data`).

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
attempts a login before invoking `dotnet test`. Exit codes follow BSD
sysexits so failures are easy to distinguish:

| Code | Meaning |
| --- | --- |
| `0` | All tests passed. |
| `1` | A test failed (or `B3T_REQUIRE_CONFIGURED=true` and env is invalid — the runner image always sets this so a misconfig fails loudly instead of silently skipping). |
| `64` | A required env var is missing. |
| `69` | `B3T_BASE_URL/live` never came up within 60 s. |
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
| `AUTH_MODE` | `Local` | Shared frontend + trading-host auth mode: `Local`, `Hybrid`, or `Entra`. Local preserves password/TOTP/signup compatibility and leaves `/auth/exchange` absent. |
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
`synchronous=FULL`; use the application's online backup primitive rather than
copying `users.db`, `users.db-wal` and `users.db-shm` separately while the host
is live. Volume-level backups should still capture the WAL/snapshots,
`users.json` during Hybrid/local migration, and `dp-keys`.

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

## Bumping the matching image

The `matching-platform` service is pinned to an **immutable image
digest** (`ghcr.io/pedrosakuma/b3-matching@sha256:…`) in
`docker/docker-compose.yml`. We deliberately do **not** track
`:latest` — every bump is a reviewable, CI-validated PR. This
mitigates the class of flake where upstream churn lands on our builds
without warning (e.g. #332, #345, #347).

### Daily local override

Devs who want the bleeding edge for a one-off run can override:

```bash
MATCHING_IMAGE=ghcr.io/pedrosakuma/b3-matching:latest \
  docker compose -f docker/docker-compose.yml up matching-platform
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
the pin; the standard CI matrix (`real-stack-conformance` included)
validates the new digest before a human merges.

### Manual bump (one-liner)

```bash
new=$(scripts/matching-image/check-upstream.sh | tail -n1)
old=$(grep -oE 'b3-matching@sha256:[0-9a-f]{64}' docker/docker-compose.yml | head -n1 | sed 's/.*@//')
sed -i "s|${old}|${new}|g" docker/docker-compose.yml
```
