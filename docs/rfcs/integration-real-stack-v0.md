# RFC: Integration real-stack v0

| Field    | Value                                                                  |
| -------- | ---------------------------------------------------------------------- |
| Status   | Implemented (v2)                                                       |
| Tracking | [#58](https://github.com/pedrosakuma/B3TradingPlatform/issues/58)      |
| Replaces | n/a (additive overlay on top of the family `docker-compose.yml`)       |

## 1. Context

The family compose ships three modes today, all honest and all
in-process from the trading-host's point of view:

- **Default** (`docker compose up`) — `Trading__Exchange__Mode=Unavailable`.
  `POST /orders` returns `502 BadGateway`, `/health` reports
  `readyForOrders=false`. There is no broker.
- **`docker-compose.e2e.yml`** — `Mode=Stub`. `StubExchangeGateway`
  echoes ERs synchronously so the Playwright smoke can drive the UI
  without external services. No FIXP wire involved.
- **`docker-compose.conformance.yml`** — same `Mode=Unavailable` as the
  base; the conformance suite asserts the *honest absence* contract
  (admin/auth/risk/algo HTTP shapes don't depend on a live broker).

That covers the surface area of the `B3.Trading.Api` and of the
in-process `Mock`/`Stub` gateways. What it does **not** cover is the
real-mode adapter `B3EntryPointClientGateway` end-to-end:

- The reconnect loop, exponential backoff, and `IsReconnecting`
  observable have unit tests against a fake `IEntryPointClient`, not
  against a counterparty actually closing the TCP socket.
- `FixpGapDetector` only fires on real out-of-order
  `MessageNumber`s — produced today only by upstream
  `B3.EntryPoint.Client` against a live exchange.
- `OrderEntryLatencyProbe` records `OrderEntryCallMs` against an
  in-process call (sub-microsecond histogram noise) instead of a real
  TCP round-trip.
- Multi-firm `FirmGatewayRegistry` was unit-tested but never had two
  real FIXP sessions in CI.

The 2026-05-04 audit (see `plan.md` § Phase status audit) tagged this
as the largest remaining gap in "integration tests with the complete
architecture": every other claim about the trading-host is covered by
unit, application, API, conformance, or Playwright tests; the FIXP
real-mode path is not.

Two upstream blockers cited in `docker-compose.yml`'s header comment
(*"matching-platform intentionally absent until B3MatchingPlatform#88
ships GHCR images + bridge-friendly transport"*) **are now stale**:

- [`pedrosakuma/B3MatchingPlatform#88`](https://github.com/pedrosakuma/B3MatchingPlatform/issues/88)
  closed 2026-05-01. `ghcr.io/pedrosakuma/b3-matching:latest` and
  `ghcr.io/pedrosakuma/b3-matching-synthtrader:latest` are public
  with 67+ sha-tagged builds since closure. Bridge transport was
  delivered as **option A** of the issue (per-channel unicast UDP
  with DNS-resolved consumer hostname) via
  `config/exchange-simulator.bridge.json`.
- [`pedrosakuma/B3MarketDataPlatform#2`](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/2)
  closed 2026-05-01. `ghcr.io/pedrosakuma/b3-marketdata:latest` (and
  `0.1.0` semver tag) public. WebSocket reference-price contract
  documented under `docs/CONFIGURATION.md` upstream.

The trading-host already speaks both wires:

- TCP FIXP via `B3.EntryPoint.Client.EntryPointClient` when
  `Mode=Real` (see `B3EntryPointClientGateway.cs`).
- WebSocket reference-price ingestion via
  `MarketDataReferencePriceClient` when `Trading__MarketData__WsUrl`
  is set.

What's missing is **plumbing**: a single docker-compose overlay that
wires `matching-platform → marketdata → trading-host` over the
existing `b3-net` bridge, plus a CI job that exercises it.

## 2. Goals

1. Add a `docker-compose.real.yml` overlay that brings up the real
   stack — `matching-platform` + `marketdata` + `trading-host` in
   `Mode=Real` — over the existing `b3-net` bridge network, in a
   single `docker compose up` invocation. No `network_mode: host`,
   no extra clones-side-by-side, no PCAP volumes.
2. Add a CI job (`docker.yml`) that boots the real stack and runs
   the existing conformance suite against it, on **every PR**. The
   matrix becomes: in-process tests (Domain/Application/Api), HTTP
   conformance against `Mode=Unavailable` stack, HTTP conformance
   against `Mode=Real` stack, optional Playwright smoke.
3. Keep the default and existing overlays untouched. The real stack
   is opt-in via `-f docker-compose.real.yml`. `Mode=Unavailable` and
   `Mode=Stub` remain the surfaces every existing user sees.
4. Refresh the stale "intentionally absent" comments + DOCKER.md
   table to point operators at the new overlay.

## 3. Non-goals

- **SHA-pinning of upstream images.** v0 uses `:latest`. Bumps that
  break the contract surface as a CI failure on the next PR (which
  is acceptable signal); a follow-up slice introduces sha pinning +
  a bump policy. Documented in `plan.md` D1.
- **New conformance specs that exercise real-mode behaviour the
  stub can't fake** (gap recovery, multi-firm fan-out, drain mid
  open-order). v0 just runs the existing 11 specs against the real
  stack; specs that assert behaviour only observable with a real
  matching engine are a v2 follow-up.
- **HA / multi-region.** Stays under the existing
  `ha-resilience-backlog` blocked todo (issue #29).
- **Replacing the in-process `IReferencePrice` fallback with a hard
  dependency on the marketdata WS.** The overlay sets
  `Trading__MarketData__WsUrl=ws://marketdata:8080` so the WS
  consumer wires up when the real stack is on, but the underlying
  fallback to the static `Trading:Risk:ReferencePrices` map remains
  and any deployment that doesn't set the env keeps the existing
  zero-background-work posture. (See §4.5.)
- **Synthtrader.** `b3-matching-synthtrader` exists and is useful for
  chaos / load, but it's noise for a correctness-shaped integration
  surface. Adding it is a one-liner and can land in a follow-up if
  there's demand.

## 4. Detailed design

### 4.1 Overlay file layout

```
docker/
├── docker-compose.yml                  # base, untouched (header refreshed)
├── docker-compose.e2e.yml               # untouched
├── docker-compose.conformance.yml       # untouched
├── docker-compose.observability.yml     # untouched
├── docker-compose.real.yml              # NEW
└── real/                                # NEW directory
    ├── exchange-simulator.bridge.json   # matching-platform config
    └── marketdata-transport.json        # marketdata UMDF config (if needed; see §4.4)
```

The overlay is the single new top-level moving piece. It composes
with the existing overlays:

- **Real stack only:**
  `docker compose -f docker/docker-compose.yml
                  -f docker/docker-compose.real.yml up`
- **Real stack + conformance:**
  `docker compose -f docker/docker-compose.yml
                  -f docker/docker-compose.real.yml
                  -f docker/docker-compose.conformance.yml
                  up -d --wait trading-host`
  followed by `docker compose ... run --rm conformance`.
- **Real stack + observability:** add
  `-f docker/docker-compose.observability.yml`.

### 4.2 `matching-platform` service

```yaml
services:
  matching-platform:
    image: ghcr.io/pedrosakuma/b3-matching:latest
    container_name: b3-matching-platform
    restart: unless-stopped
    networks:
      - b3-net
    expose:
      - "9876"   # FIXP TCP listener (consumed by trading-host)
      - "8080"   # liveness / health (matching-platform's own /health)
      - "30084/udp"  # UMDF Incremental (consumed by marketdata)
      - "30184/udp"  # UMDF Snapshot
      - "31084/udp"  # UMDF InstrumentDefinition
    volumes:
      - ../docker/real/exchange-simulator.bridge.json:/app/config/exchange-simulator.bridge.json:ro
    command: ["/app/config/exchange-simulator.bridge.json"]
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8080/health"]
      interval: 5s
      timeout: 2s
      retries: 12
```

**Bridge config (`docker/real/exchange-simulator.bridge.json`)** is a
near-verbatim copy of the upstream `config/exchange-simulator.bridge.json`,
adjusted only where v0 needs to pin a value:

- `tcp.listen` → `0.0.0.0:9876` (default).
- `firms[0].id` → `FIRM01` (matches the trading-host firm seed).
- `sessions[0]` → `{sessionId: "10101", firmId: "FIRM01",
  accessKey: "dev-key-1"}`.
- `channels[0].transport` → `unicast`.
- `channels[0].incrementalGroup` / `snapshot.group` /
  `instrumentDefinition.group` → `marketdata` (DNS name of the
  marketdata service on `b3-net`).
- `channels[0].incrementalPort: 30084`,
  `snapshot.port: 30184`,
  `instrumentDefinition.port: 31084`.

Why commit our own copy instead of mounting the upstream file
directly: pinning a known-good shape at our PR boundary keeps an
upstream config refactor from silently changing what our CI exercises.
The file is small (~50 lines, comments included). When upstream
changes shape, `:latest` will still parse it because backward
compatibility is owned by the matching repo, and if it ever doesn't,
that's exactly the signal we want from CI.

### 4.3 `trading-host` overlay

The overlay re-uses the base service definition and only overrides
environment:

```yaml
services:
  trading-host:
    depends_on:
      matching-platform:
        condition: service_healthy
    environment:
      Trading__Exchange__Mode: Real
      Trading__Exchange__Firms__0__FirmId: FIRM01
      Trading__Exchange__Firms__0__Endpoint: tcp://matching-platform:9876
      Trading__Exchange__Firms__0__SessionId: "10101"
      Trading__Exchange__Firms__0__AccessKey: dev-key-1
      Trading__Exchange__Firms__0__EnteringFirmCode: "100"
      # Resolve PETR4 / VALE3 to numeric SecurityIds so /algo + /orders
      # don't 400 on symbol lookup. Same ids the e2e overlay uses.
      Trading__SymbolDirectory__SecurityIds__PETR4: "4321"
      Trading__SymbolDirectory__SecurityIds__VALE3: "1234"
      # Wire the in-process WS reference-price client to the
      # marketdata service. When this env is unset (e.g. operators
      # using the overlay against a different upstream), the host
      # silently falls back to the static reference-prices map —
      # see §4.5.
      Trading__MarketData__WsUrl: ws://marketdata:8080
      Trading__MarketData__Symbols__0: PETR4
      Trading__MarketData__Symbols__1: VALE3
```

The overlay does **not** redefine `image`, `volumes`, `ports`, or
auth seeds — those are inherited from the base. Conformance overlay
still composes on top to add the `carol` admin seed.

### 4.4 `marketdata` service overlay

The base file already declares `marketdata` under
`profiles: [marketdata]`. The overlay replaces its env to consume
UMDF unicast from `matching-platform` instead of replaying PCAP
files, and **promotes it out of the profile** (the real stack
needs it unconditionally):

```yaml
services:
  marketdata:
    profiles: []   # always-on under the real overlay
    depends_on:
      matching-platform:
        condition: service_healthy
    environment:
      WS_PORT: "8080"
      # PCAP path inputs cleared. The consumer must run in
      # live UDP mode against matching-platform's unicast
      # transport (matches exchange-simulator.bridge.json above).
      PCAP_DIR: ""
      PCAP_PREFIX: ""
      # Transport JSON describing where to bind for unicast UDP.
      # Path is relative to the container; mounted from
      # docker/real/marketdata-transport.json.
      UMDF_MULTICAST_CONFIG: /app/config/transport.json
    volumes:
      - ../docker/real/marketdata-transport.json:/app/config/transport.json:ro
```

**Open question (R1):** the upstream `b3-marketdata` consumer
documents JSON config for **multicast** ingest (`multicastGroup` +
`port` per channel). Whether it accepts a unicast bind (e.g.
`multicastGroup` set to `0.0.0.0` so the socket listens on the port
without an IGMP join) is not documented. Two contingencies:

- **R1.a — unicast bind works.** The transport JSON ships as:
  ```json
  {
    "feeds": [{
      "name": "EQT",
      "channels": [
        {"channelId": 84, "type": "IncrementalA",
         "multicastGroup": "0.0.0.0", "port": 30084},
        {"channelId": 84, "type": "SnapshotRecovery",
         "multicastGroup": "0.0.0.0", "port": 30184},
        {"channelId": 184, "type": "InstrumentDefinition",
         "multicastGroup": "0.0.0.0", "port": 31084}
      ]
    }]
  }
  ```
  v0 ships as-is and we move on.
- **R1.b — unicast bind doesn't work.** v0 still ships the overlay
  but the `marketdata` service is back behind a profile and
  `Trading__MarketData__WsUrl` is **unset** in the trading-host
  overlay. The CI job exercises the FIXP real-mode wire only (which
  is the primary value of this slice). A cross-issue is opened in
  `B3MarketDataPlatform` requesting a first-class unicast/bridge
  mode; v1 of this RFC closes the loop once that ships.

The choice between R1.a and R1.b is made during the implementation
PR, after a local probe; the rest of the design holds either way.

### 4.5 Reference-price posture (D3)

Decision: keep the WS reference-price wire **opt-in via env**. The
overlay sets `Trading__MarketData__WsUrl=ws://marketdata:8080` so
the real stack does exercise the path end-to-end, but the host's
fallback to the static `Trading:Risk:ReferencePrices` map remains
the default behaviour anywhere the env is empty. This preserves two
properties that matter:

- The base compose stays zero-background-work (no SDK pull, no
  outbound WS). Operators running `docker compose up` get exactly
  what they had before this slice.
- Whoever takes the overlay can replace the WS endpoint with their
  own marketdata source (or unset it for static prices) without
  having to monkey-patch anything trading-host-side.

### 4.6 CI job (`.github/workflows/docker.yml`)

A new job `real-stack-conformance` parallels the existing
`conformance` job. Skeleton:

```yaml
real-stack-conformance:
  name: Run conformance suite against real stack
  runs-on: ubuntu-latest
  needs: [trading-host]
  timeout-minutes: 15
  env:
    TRADING_AUTH_SIGNING_KEY: ci-conformance-placeholder-key-min-32-bytes-long-aaaaa
    TRADING_SEED_PASSWORD_HASH: ${{ vars.TRADING_SEED_PASSWORD_HASH || '<committed CI default>' }}
    TRADING_SEED_PASSWORD_SALT: ${{ vars.TRADING_SEED_PASSWORD_SALT || '<committed CI default>' }}
  steps:
    - uses: actions/checkout@v6
    - uses: docker/setup-buildx-action@v3
    - name: Bring up real stack
      run: |
        docker compose \
            -f docker/docker-compose.yml \
            -f docker/docker-compose.real.yml \
            -f docker/docker-compose.conformance.yml \
            up -d --wait trading-host
    - name: Run conformance
      run: |
        docker compose \
            -f docker/docker-compose.yml \
            -f docker/docker-compose.real.yml \
            -f docker/docker-compose.conformance.yml \
            run --rm conformance
    - name: Logs (always)
      if: always()
      run: |
        for svc in trading-host matching-platform marketdata; do
          echo "::group::$svc"
          docker compose \
              -f docker/docker-compose.yml \
              -f docker/docker-compose.real.yml \
              logs --no-color --timestamps "$svc" || true
          echo "::endgroup::"
        done
    - name: Tear down
      if: always()
      run: |
        docker compose \
            -f docker/docker-compose.yml \
            -f docker/docker-compose.real.yml \
            -f docker/docker-compose.conformance.yml \
            down --volumes --remove-orphans
```

Trigger surface matches `docker.yml` defaults
(`push` to main + tags, `pull_request`, `workflow_dispatch`). The
job pulls two upstream `:latest` images each run; cache hits in
buildx make repeats cheap. Estimated wall-clock: 3-5 min.

### 4.7 Specs covered by v0

The existing 11 specs run unchanged. v0's value is observing them
pass against a real FIXP wire:

| Spec area | What real-mode adds beyond `Mode=Unavailable` |
|---|---|
| `Spec_HTTP_Auth` | Same shape; no FIXP involvement. |
| `Spec_HTTP_Admin/AdminFirmsSpecTests` | `/admin/firms` now lists a firm with `connected=true` instead of `unavailable`. |
| `Spec_HTTP_Risk/RiskRejectionShapeSpecTests` | Real venue ER path on accept; rejection shape unchanged. |
| `Spec_HTTP_Simulator/SimulatorErInjectionSpecTests` | Skipped — simulator endpoint only exists in `Mode=Simulator`. Marker stays. |
| `Spec_HTTP_Algo` (3 specs) | Iceberg + TWAP children submit through the real EntryPoint client; ERs return via the same wire; parent state machine assertions still hold. |

Specs that are explicitly *only* meaningful in real-mode (e.g.
"force a TCP close, expect reconnect within N s, expect ER replay
preserves position") are out of scope for v0 and tracked under v2.

### 4.8 Operator UX

`docs/DOCKER.md` gains a third section under "What runs by default":

> ### Real-stack overlay (opt-in)
>
> ```bash
> docker compose \
>     -f docker/docker-compose.yml \
>     -f docker/docker-compose.real.yml \
>     up
> ```
>
> Brings up `matching-platform` (`b3-matching:latest`), `marketdata`
> (`b3-marketdata:latest`), and a `trading-host` flipped into
> `Mode=Real` against the matching's FIXP listener. The reference-
> price WS wire is on. Use this overlay when you need to see the
> real EntryPoint client path drive end-to-end — gap detection,
> latency probes, multi-firm registry — instead of the in-process
> mock.

The "future PR will add `matching-platform`" sentence is removed
from the base compose header and from `docs/DOCKER.md`'s introduction.

## 5. Roadmap (PRs after this RFC)

1. **`integration-real-stack-v0` (this RFC)** — single PR per §4.
2. **`integration-real-stack-v1`** — sha-pinning upstream images,
   bump policy (script or Renovate). Driven by D1.
3. **`integration-real-stack-v2`** — new conformance specs that
   exercise behaviour only observable in real-mode: forced TCP
   close + reconnect, gap injection, multi-firm fan-out, drain
   mid-open-order. May open RFCs upstream for hooks (e.g. an
   admin endpoint on matching-platform to drop a session).
4. *(Out-of-scope, parallel)* Cross-issue in `B3MarketDataPlatform`
   if §4.4 R1.b lands.

## 6. Open questions

- **Q1 — Marketdata unicast bind (R1).** Resolved as **R1.b** during
  the implementation PR. Local probe confirmed that the upstream
  `b3-marketdata` consumer's `MulticastPacketSource` always issues
  `SetMulticastOption`, which fails `SocketException(22) Invalid
  argument` for `multicastGroup: "0.0.0.0"` (or any non-multicast
  address). The slice ships matching-platform alone in the overlay
  and points matching's UMDF unicast sink at its own loopback
  (`127.0.0.1`) so the simulator boots cleanly with no consumer.
  D3 (reference-price WS) is deferred to v1; cross-issue to be
  opened upstream in `B3MarketDataPlatform` requesting first-class
  unicast/bridge mode.
- **Q2 — Auth seed sharing.** Conformance overlay reuses alice's
  PBKDF2 hash/salt to add a `carol` admin user. The real overlay
  doesn't need its own seed (FIRM01 in `bridge.json` is matching's
  own auth, separate from trading-host's HTTP auth). Confirmed: no
  new seed handling required.
- **Q3 — Volume cleanup on real stack restart.** The base compose
  defines `trading-data` for `FileEventStore`. Restarting the real
  stack between CI runs reuses it; the conformance suite is
  idempotent against a non-empty store, but a `down --volumes` step
  is included in the CI teardown for symmetry with the existing
  `conformance` job.
- **Q4 — Firewall / outbound on CI runners.** `ubuntu-latest`
  GitHub runners can pull from `ghcr.io` without configuration.
  No proxy. Verified by the existing `docker.yml` workflow.

## 7. Planning decisions (post-RFC, 2026-05-04)

- **D1 — Image pinning policy.** Use `:latest` for both upstream
  images in v0. Defer sha-pinning + bump tooling to v1. Trade-off
  accepted: a breaking upstream change shows up as a CI failure on
  the next PR, which is acceptable signal at this stage.
- **D2 — CI cadence.** New `real-stack-conformance` job runs on
  every PR, mirroring the posture of the existing `conformance`
  job. ~3-5 min wall-clock acceptable; if it becomes painful,
  retreat to `workflow_dispatch + cron` (mirror of `e2e-smoke.yml`).
- **D3 — Reference price wiring.** Originally planned to set
  `Trading__MarketData__WsUrl` so the real stack would exercise the
  WS path end-to-end. **Reverted to "left unset"** after R1.b
  confirmation — marketdata is not in the v0 overlay so there is no
  WS endpoint to point at. The static-map fallback in the host
  remains. v1 (gated by upstream marketdata bridge support)
  reinstates the WS wire.

## 8. v1 amendment — marketdata leg reinstated (2026-05-04)

### What changed upstream

- [`pedrosakuma/B3MarketDataPlatform#10`](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/10)
  closed (PR #11 merged 2026-05-04). The consumer's
  `MulticastPacketSource` now honours a `transport: "unicast"` flag per
  channel: when set, the socket binds `multicastGroup`:`port` as a
  plain UDP listener and skips `SetMulticastOption` entirely. New
  schema documented under upstream `docs/CONFIGURATION.md` §
  *Example — unicast (Docker Compose bridge)*.
- New GHCR build `sha-2a252af` published; `:latest` updated.

### What this slice does

Reinstates the marketdata leg in the real overlay so the topology
finally matches the diagram users have been promised:

```
matching-platform ──FIXP TCP 9876──> trading-host ──> frontend
        │
        └──UMDF unicast UDP──> marketdata-live ──WS 8080──> trading-host
                               (alias: marketdata)         (IReferencePrice)
```

Concretely:

1. `docker/real/marketdata-transport.json` (new) — consumer config with
   the EQT group on matching's emit ports (30084/30085/31084/30184),
   all `transport: "unicast"` bound on `0.0.0.0`. Single-channel-group
   operation is supported as of the upstream fix for `#13` (closed
   2026-05-04); the v1 revision of this file carried a phantom DRV
   group on unused ports purely to satisfy the now-relaxed guard, and
   was simplified back to a single group in v1.1.
2. `docker/real/exchange-simulator.bridge.json` — UMDF unicast targets
   flipped from `127.0.0.1` to `marketdata` (DNS-resolved on `b3-net`).
3. `docker/docker-compose.real.yml` — adds a `marketdata-live` service
   (separate name from the PCAP-replay `marketdata` profile-gated in the
   base compose, with a `marketdata` alias on `b3-net` so DNS resolution
   from matching and from trading-host works unchanged) and flips
   `Trading__MarketData__WsUrl` to `ws://marketdata:8080/ws`,
   subscribing PETR4/VALE3/ITUB4. The base PCAP variant stays dormant.
4. `.github/workflows/docker.yml` — `real-stack-conformance` now
   captures `marketdata-live` logs in the always-run logs step.
5. `docs/DOCKER.md` § *Real-stack overlay* refreshed with the three-row
   service table.

### Local verification (2026-05-04)

```
docker compose -f docker-compose.yml -f docker-compose.real.yml up -d --wait trading-host
# Container b3-marketdata Healthy
# Container b3-matching-platform Healthy
# Container b3-trading-host Healthy

curl http://localhost:5000/health
# {"status":"ready", … "exchange":{"mode":"Real","readyForOrders":true,"firmCount":1}}

docker logs b3-trading-host | grep MarketData
# MarketData subscribed: PETR4
# MarketData subscribed: VALE3
# MarketData subscribed: ITUB4
# MarketData connection state: Connected

docker compose … -f docker-compose.conformance.yml run --rm conformance
# Test Run Successful. Total tests: 6 (5 Auth + 1 AdminFirms).
```

The marketdata consumer reaches `G0:Streaming` with 3 instruments / 3
books / 3 symbols. Trade frames have not been observed yet because no
order has crossed in this stack (matching has only one connected firm
in the real overlay; `IReferencePrice` cache misses keep falling back
to the static map until a real trade prints). v2 will exercise the
crossed-order path and assert that `MarketDataReferencePrice` displaces
the static fallback.

### Findings filed upstream — both resolved 2026-05-04

- [`B3MarketDataPlatform#13`](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/13)
  — single-channel-group guard at `B3.Umdf.ConsoleApp/Program.cs:500`
  fired regardless of transport. **Resolved upstream**; v1.1 of this
  overlay drops the phantom DRV group from `marketdata-transport.json`.
- [`B3MarketDataPlatform#12`](https://github.com/pedrosakuma/B3MarketDataPlatform/issues/12)
  — `SecurityDefinition_12.SecurityDesc` parsing threw
  `ArgumentOutOfRangeException` on every InstrumentDefinition cycle
  emitted by `b3-matching:latest`. **Resolved upstream**; v1.1 local
  verification shows clean logs (no parse warnings, books create on
  the first cycle).

### v1.1 verification (2026-05-04, single-group config)

```
docker logs b3-marketdata
# Channel groups: 1
#   Channel group 0: EQT
# Listening unicast UDP on 0.0.0.0:{30084,30085,31084,30184}
# WaitInstrumentDefinition → Streaming
# G0:Streaming  (no SecurityDesc warnings, no exceptions)

docker logs b3-trading-host | grep "MarketData connection"
# MarketData connection state: Connected
```

### Q1 update (R1)

Resolved as **R1.a** post-upstream-fix. Unicast bind works; the only
deviation from the original R1.a sketch is the JSON key shape
(`channelGroups` instead of the `feeds` placeholder used in the draft).
The transient multi-group workaround used in v1 was removed in v1.1
once upstream `#13` shipped. D3 (reference-price WS) is **enabled by
default in the real overlay**.

### v0 scoping items now retired

- "matching → marketdata UMDF leg is out of scope" — **closed**.
- "Trading__MarketData__WsUrl deliberately left unset" — **closed**;
  set to `ws://marketdata:8080/ws` in the overlay.
- "static-map fallback is the only reference-price path in the real
  stack" — **partially closed**; fallback still serves cache misses
  until trades print, but the live path is wired and connecting.

## 9. v2 amendment — destructive cross-pair conformance (2026-05-04)

§8/v1 wired the live marketdata leg but left the live → trading-host
ref-price cache unverifiable end-to-end: nothing in the real stack
actually printed a trade, so `MarketDataReferencePrice` lived on
`InfoSnapshot` data only and the static fallback could never be proven
to be displaced by a real execution. v2 closes that gap with a
destructive conformance scenario gated behind a dedicated env flag.

### What ships

- **New gate** `ConformanceFactAttribute.RequiresSandboxMatching` +
  `B3T_REAL_STACK_CONFORMANCE` env (resolved by `PlatformEndpoint`).
  Pattern matches the existing `RequiresAdmin` / `RequiresSimulator`
  flags. Tests carrying the flag skip at discovery time unless the
  env reads `true`/`1`.
- **Compose plumbing** — `docker-compose.real.yml` now stacks a
  `conformance:` service stanza that injects
  `B3T_REAL_STACK_CONFORMANCE=true` into the conformance one-shot.
  Compose merges environment maps additively, so:
  - base + conformance → flag absent → spec auto-skips.
  - base + real + conformance → flag present → spec runs.
  This is exactly what the real-stack-conformance CI job (and only
  that job) stacks.
- **Real-overlay corrections required by the destructive path**:
  - `Trading__Auth__Users__0__Firm: FIRM01` so alice's JWT firm claim
    actually resolves to the registered FirmGateway (was `default` for
    Mock back-compat; orders never reached the gateway).
  - `Trading__SymbolDirectory__SecurityIds__{PETR4,VALE3,ITUB4}` set
    to `900000000001/2/3`, the actual ids matching publishes via
    `instruments-eqt.json`. v1's placeholders (4321/1234) would have
    been silently mis-routed had any order arrived.
  - `Trading__Risk__ReferencePrices__{PETR4,VALE3,ITUB4}` seeded so
    the price-collar accepts cross prices before the live cache warms,
    and so the v2 spec has a deterministic baseline (ITUB4=30.00) for
    "live displaced this static value".
  - `Trading__Exchange__Firms__0__AccessKey` reformatted as the JSON
    envelope matching's `FixpSession` parses:
    `{"auth_type":"basic","username":"<sessionId>","access_key":"..."}`.
    Raw-string accessKey was rejected at Negotiate
    (`'d' is an invalid start of a value`).
- **New spec** `Spec_HTTP_MarketData/ReferencePriceLiveSpecTests.cs`:
  1. admin login → GET `/admin/marketdata/reference-prices?symbols=ITUB4`
     → capture baseline (don't assert source — `InfoSnapshot` may
     pre-populate the cache).
  2. user login → POST `/orders` ITUB4 BUY 100 @ 31.00 + SELL 100 @
     31.00. Assert both 202 with no `Rejected` status (failure mode
     `gateway unavailable` would surface as 502 with body).
  3. Poll diagnostics endpoint every 250ms up to 30s for
     `live.price == 31.00` AND `live.updatedUtc > submitStartUtc`.
  4. Bonus: assert `effectiveSource == "Live"` and
     `effectivePrice == 31.00`.
- Cross price (31.00) is chosen distinct from the configured fallback
  (30.00) so a coincidental fallback→live transition at the wrong
  price would still fail. Within the default ±10% collar
  ([27.00, 33.00]) and respects matching's lot/tick (100 / 0.01).

### Verification

```
$ docker compose -f docker-compose.yml -f docker-compose.real.yml \
                 -f docker-compose.conformance.yml run --rm conformance
Passed B3.Trading.Conformance.Spec_HTTP_MarketData.ReferencePriceLiveSpecTests
       .CrossedTrade_DisplacesFallback_ReachesLiveCacheWithCrossPrice [973 ms]
Total tests: 12  Passed: 9  Skipped: 3 (simulator-only)
```

End-to-end latency `POST /orders → live cache update` < 1s on the local
real stack.

### Upstream interop bugs surfaced en route

The v2 implementation was blocked three times in sequence by upstream
interop issues that no other downstream had hit. All three closed
within the same window:

- `B3MatchingPlatform#236` — FIXP `NewOrderSingle` template=102 v6 from
  EPC 0.8.0 rejected as `UnsupportedTemplate`.
- `B3MatchingPlatform#239` — `varData has 2 trailing byte(s) after
  declared fields` on the first business message.
- `B3MatchingPlatform#241` — `business reject (unsupported feature)
  RoutingInstruction=0 not supported`. EPC 0.8.0 doesn't expose
  `RoutingInstruction` on its public submit API; matching now accepts
  the SBE default.

The pattern (each interop discovered downstream via this exact spec)
suggests a CI gap on the matching repo. Recommended in #241 that
matching add a submit-and-execute path via EPC to its own CI.

### Status

- "static-map fallback is the only reference-price path in the real
  stack" — **fully closed**: live cache verifiably displaces the
  fallback under a real cross-pair execution.
