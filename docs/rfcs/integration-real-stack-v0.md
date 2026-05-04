# RFC: Integration real-stack v0

| Field    | Value                                                                  |
| -------- | ---------------------------------------------------------------------- |
| Status   | Draft                                                                  |
| Tracking | TBD (tracking issue link added once opened)                            |
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

- **Q1 — Marketdata unicast bind (R1).** Does the upstream consumer
  accept `multicastGroup: "0.0.0.0"` as a degenerate "listen unicast"
  configuration, or does it always issue an IGMP join? Resolved by
  local probe during the implementation PR; affects whether D3 is
  on or off in v0.
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
- **D3 — Reference price wiring.** Overlay sets
  `Trading__MarketData__WsUrl` so the real stack exercises the WS
  path end-to-end; static-map fallback in the host is preserved for
  any deployment that leaves the env empty.
