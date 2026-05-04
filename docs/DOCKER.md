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

You'll see the trader UI; logging in works; submitting an order returns
**502 BadGateway with `reason: gateway unavailable`**. That's the honest
default — see [Honest no-broker mode](#honest-no-broker-mode) below.

## What runs by default

| Service | Image | Port (host) | Purpose |
|---|---|---|---|
| `trading-host` | `ghcr.io/pedrosakuma/b3-trading-host:latest` | `5000` | REST + WebSocket; `Mode=Unavailable` |
| `frontend` | `ghcr.io/pedrosakuma/b3-trading-frontend:latest` | `8080` | nginx serving the static UI + reverse-proxy to trading-host |

The `marketdata` service is defined but **gated behind the `marketdata`
profile** because it needs PCAP files this repo doesn't ship. Bring it up
explicitly:

```bash
PCAP_DIR=/path/to/your/pcaps docker compose -f docker/docker-compose.yml --profile marketdata up
```

A future PR will refresh this section once
[B3MarketDataPlatform#2](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/2)
follow-ups land a unicast-bind mode (see RFC integration-real-stack-v0
§4.4 R1.b). Today, marketdata only knows how to consume multicast
UMDF, which is not routable across the docker bridge.

## Real-stack overlay (opt-in)

```bash
docker compose \
    -f docker/docker-compose.yml \
    -f docker/docker-compose.real.yml \
    up
```

Brings up the real-wire trio against the trading-host:

| Service | Image | What it does |
|---|---|---|
| `matching-platform` | `ghcr.io/pedrosakuma/b3-matching:latest` | FIXP TCP listener (`:9876`) + UMDF unicast publisher |
| `marketdata-live` | `ghcr.io/pedrosakuma/b3-marketdata:latest` | UMDF consumer (binds `30084/30184/31084 udp`) + WebSocket fanout (`:8080`, hostname `marketdata` on `b3-net`) |
| `trading-host` | (this repo) | `Mode=Real`, FIRM01 session against matching, `IReferencePrice` wired through `marketdata-live` WS |

The overlay is opt-in — `docker compose up` (no `-f`) keeps the honest
`Mode=Unavailable` default.

Use this overlay when you need to see the real `B3.EntryPoint.Client`
path drive end-to-end (gap detection, latency probes, multi-firm
registry) and the live reference-price WS pipeline (versus the in-process
mock + static-map fallback).

The base compose's profile-gated `marketdata` (PCAP-replay) is unrelated
and stays dormant under this overlay; the live variant is a separate
service named `marketdata-live` with a network alias `marketdata` so the
hostnames in `docker/real/exchange-simulator.bridge.json` and the
trading-host `Trading__MarketData__WsUrl` resolve.

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

The entrypoint preflights the env, waits up to 60 s for `/ready`, and
attempts a login before invoking `dotnet test`. Exit codes follow BSD
sysexits so failures are easy to distinguish:

| Code | Meaning |
| --- | --- |
| `0` | All tests passed. |
| `1` | A test failed (or `B3T_REQUIRE_CONFIGURED=true` and env is invalid — the runner image always sets this so a misconfig fails loudly instead of silently skipping). |
| `64` | A required env var is missing. |
| `69` | `B3T_BASE_URL/ready` never came up within 60 s. |
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
# extra symbols: TRADING_MARKETDATA_SYMBOL_1=VALE3, etc. (compose env
# only forwards index 0; for more, add Trading__MarketData__Symbols__N
# directly to docker-compose.yml or appsettings.Docker.json).

docker compose --profile marketdata up -d
```

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
mounted at `/var/lib/b3trading`. Backups: `docker run --rm -v
b3-trading-data:/data -v $(pwd):/out alpine tar czf /out/b3-trading.tgz -C
/data .` (or your usual volume backup tool).

## Health & readiness

- `GET /live` — process is up
- `GET /ready` — process is up AND not draining (used by the container's
  HEALTHCHECK and by orchestrators for rolling updates)
- `GET /health` — full snapshot, including `exchange.{mode, readyForOrders, firmCount}`
  and `persistence.{enabled, dataDirectory}`

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
