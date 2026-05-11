# Documentation index

Start here. This is the map for everything under `docs/` in
`B3TradingPlatform`. For a high-level project overview, see the
[repo README](../README.md).

## Architecture

- [ARCHITECTURE.md](ARCHITECTURE.md) — layered model, ER routing,
  ClOrdID namespacing, wire boundary, and the four exchange-gateway
  modes (`Stub` / `Mock` / `Real` / `Unavailable`).
- [ENTRYPOINT_INTEGRATION.md](ENTRYPOINT_INTEGRATION.md) — how the
  trading platform talks to B3 EntryPoint (outbound side).
- [WEBSOCKET-PROTOCOL.md](WEBSOCKET-PROTOCOL.md) — wire format and
  error codes for the `/ws` subscription channels.
- [FRONTEND.md](FRONTEND.md) — vanilla JS + Web Worker trader console.

## RFCs (latest first)

- [user-bot-fixp-listener-v0](rfcs/user-bot-fixp-listener-v0.md) —
  third inbound channel: native FIXP/SBE listener for external user
  bots (epic #166).
- [pre-trade-risk-v2](rfcs/pre-trade-risk-v2.md) — ordered risk
  pipeline with kill-switch, max-qty, max-notional, position-limit,
  price-collar checks.
- [integration-real-stack-v0](rfcs/integration-real-stack-v0.md) —
  Docker topology for the real `Mode=Real` stack against
  `B3MatchingPlatform`.
- [algo-orders-v0](rfcs/algo-orders-v0.md) — Iceberg / TWAP algo
  engines and the ER-injection harness that supports them.

## Operations / Runbooks

- [operations/fixp-listener.md](operations/fixp-listener.md) —
  enabling, sizing, credentials, retransmit, drain & shutdown for the
  FIXP listener.

## Persistence

- [PERSISTENCE.md](PERSISTENCE.md) — Phase 6 WAL + snapshot design
  (replaces the previous fully-ephemeral in-memory state).
- [spikes/persistence-strategy.md](spikes/persistence-strategy.md) —
  exploratory notes that fed the persistence design.

## Docker

- [DOCKER.md](DOCKER.md) — canonical container topology, what runs by
  default, and the demo / unavailable / observability overlays.

## Conformance

- [CONFORMANCE.md](CONFORMANCE.md) — scenario inventory for the
  `backend/tests/B3.Trading.Conformance/` suite.

## Metrics & Observability

- [METRICS.md](METRICS.md) — OpenTelemetry metric surface emitted by
  the trading-host.
- [OBSERVABILITY.md](OBSERVABILITY.md) — generic Kubernetes / Linux
  host wiring (lifecycle, drain, health).
- For the FIXP listener metric surface specifically, see code at
  [`backend/src/B3.Trading.EntryPointListener/Hosting/FixpListenerMetrics.cs`](../backend/src/B3.Trading.EntryPointListener/Hosting/FixpListenerMetrics.cs)
  and the application-wide registry at
  [`backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).
