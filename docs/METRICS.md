# Metrics

The trading-host emits OpenTelemetry metrics + traces opt-in via the
standard `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. When that
variable is unset, the OTel SDK is **not registered at all** — no
exporter, no periodic pump, no warnings — so dev loops and unit tests pay
zero overhead.

## Activation

Minimum env to turn it on:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc        # default; can also be http/protobuf
```

All other `OTEL_*` standard envs (`OTEL_RESOURCE_ATTRIBUTES`,
`OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_METRIC_EXPORT_INTERVAL`,
`OTEL_TRACES_SAMPLER`, etc.) are honoured by the SDK directly — we don't
shadow them in code.

## Resource attributes

Every signal carries:

| Attribute | Value |
|---|---|
| `service.name` | `b3-trading-host` (const, used by Grafana datasources) |
| `service.version` | Assembly version (`1.0.0.0` today) |
| `deployment.environment` | `ASPNETCORE_ENVIRONMENT` (`Development`, `Docker`, `Production`, ...) |

Operators can layer more via `OTEL_RESOURCE_ATTRIBUTES=foo=bar,baz=qux`.

## Application meter (`B3.Trading`)

All app-layer instruments live on a single `Meter("B3.Trading", "1.0.0")`
declared in
[`MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).
One `AddMeter(MetricsRegistry.Meter.Name)` call wires the lot.

The tables below cover every named instrument the host emits, grouped
by area. Tag lists are the **bounded** label sets the call sites pass
— high-cardinality dimensions (per-user, per-IP, parent-algoId, full
URL paths, …) are intentionally never tags and live on the read side
(structured logs, `/admin/*` endpoints) instead.

### Order flow

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.orders.submitted` | Counter | `symbol`, `side`, `source` (manual/algo) | One per accepted `POST /orders`. |
| `trading.orders.rejected_by_risk` | Counter | `check` | One per pre-trade gate rejection; `check` = the failing `IRiskCheck` name. |
| `trading.orders.gateway_failed` | Counter | (none) | SDK await threw before any ER landed. |
| `trading.orders.cancel_requested` | Counter | (none) | Operator-initiated cancel (any path). |
| `trading.orders.gtd_expired` | Counter | `cancel_result` (`Accepted`/`NotFound`/`Stale`/`WalBackpressure`/`GatewayFailed`) | Q1.3 (#255) — GTD scheduler dispatch outcome. Non-`Accepted` bucket climbing flags scheduler-vs-pipeline drift. |
| `trading.orders.modify_requested` | Counter | (none) | One per cancel-replace dispatched. |
| `trading.orders.ioc_no_response` | Counter | `firmId`, `symbol`, `tif` | #351 — IOC/FOK watchdog synthesised a Cancel because the gateway never returned an ER (defends against upstream silent-drop). Non-zero rate is a regression detector after a matching-image bump. |
| `trading.orders.duplicate_clordid` | Counter | `op` (`submit`/`modify`), `scope` (`book`/`pending`) | #108 — DuplicateClOrdID guard. **MUST be flat at zero**; non-zero means registry/snapshot/WAL-replay regression. |
| `trading.orders.clordid_registry_corruption` | Counter | `end_client`, `reason` (`invalid_observed_clordid`/`prefix_mismatch`) | #157 — fires on WAL replay if a structurally invalid ClOrdID is observed or a per-end-client prefix mismatch is detected. **MUST be flat at zero**. |

### Execution reports (inbound)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.er.received` | Counter | `type` (Fill, Canceled, Rejected, …) | Every ER routed through `ExecutionReportProcessor`. |
| `trading.er.replay_dedup` | Counter | (none) | ER carries same/older cumulative-qty / terminal-state. Expected after FIXP retransmit on reconnect; sustained spike is operator signal. |
| `trading.er.fill_delta_mismatch` | Counter | (none) | Fill ER advanced cumulative-qty by an amount ≠ its own LastQuantity (intermediate fill lost / out-of-order). Position still booked at reported delta. |
| `trading.er.late_fill_after_terminal` | Counter | (none) | Fill ER arrived for an order already terminal. Position still booked (venue cum-qty is source of truth), order keeps terminal status. |
| `trading.er.firm_mismatch_total` | Counter | `exec_type` | PR #317 — live ER carrying FirmId ≠ resolved order's FirmId (routing bug / mis-configured per-firm gateway). Rejected without state mutation. Legacy WAL replay paths bypass the check. |
| `trading.execution_reports.dropped_known_owner_missing_order` | Counter | (none) | #241 — ER resolved to a known owner via `OrderOwnershipMap` but Order absent from `WorkingOrderBook` (silent fill loss with position/cash divergence). **Alertable**. |
| `trading.er_injection.enabled` | UpDownCounter | (none) | `1` when host booted with `Trading:Exchange:AllowErInjection=true` (admin-gated `POST /admin/simulator/er` is mapped). Set once at startup, never decremented. |

### Margin / risk

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.margin.commit_replace_dropped` | Counter | (none) | #247 — `CommitReplace` landed but neither original nor transient reservation present. With `Margin.Enabled=true` this is a Prepare/Commit pipeline mismatch — reserved figure will leak until restart. **Alertable**. |
| `trading.risk.margin_overcommit_on_restore` | Counter | `owner` | #153 — admin clear-stale Restore pushed reserved above resolved base capacity. Safety valve (Restore can't fail); operators should reconcile by cancelling stale orders. |
| `trading.risk.margin_stale_transition_failed` | Counter | `kind` | #153 — `OrderStalenessService` committed WAL event then couldn't apply `Suspended`/`Restored` to the in-process reservation ledger. WAL stays authoritative; non-zero rate signals a code bug or misbehaving sink. |
| `trading.risk.margin_reservations` | Observable Gauge | `state` (`active`/`suspended`) | #153 follow-up — memory-growth observability for the cash-margin ledger. Sustained climb of `suspended` = stale orders admin never cleared, leaking until host restart. |
| `trading.risk.refprice.lookups` | Counter | `symbol`, `source` (`live`/`fallback`/`missing`) | Collar slice 5 — drop in `live` (or rise in `fallback`) = live MD feed degraded, collar leaning on static config (fail-open by design). |
| `trading.risk.refprice.staleness_seconds` | Observable Gauge | `symbol` | Age of last live MD update held in `MarketDataReferencePrice` cache. Symbols never observed don't appear. |
| `trading.risk.collar.bypassed_no_reference` | Counter | `symbol` | `PriceCollarCheck` approved purely because no reference price was available. Per-symbol so a single misconfigured ticker doesn't get lost in the aggregate. |
| `trading.risk.stop_check_skipped_no_ref` | Counter | `symbol` | Q1.2 (#254) — `StopTriggerCheck` approved a Stop order without the Buy≥ref / Sell≤ref relation (StopPrice>0 invariant still ran). Coverage gap signal before a fat-finger stop hides. |
| `trading.risk.rolling_notional.bypassed_no_reference` | Counter | `symbol` | `RollingNotionalAccountant` couldn't price a market order (no reference price); order recorded with notional 0 (fail-open). Sustained non-zero = rolling cap silently underestimated. |
| `trading.risk.rolling_notional.active_buckets` | Observable Gauge | `scope` (`end_client`/`firm`) | Slice 7 — throttle ledger size observability. Intentionally aggregate (no per-end-client tag); detail at `GET /admin/risk/throttle`. |
| `trading.risk.order_rate.active_buckets` | Observable Gauge | `scope` (`end_client`/`firm`) | Same shape as rolling-notional buckets. |
| `trading.ratelimit.rejected_total` | Counter | `path` (rule's PathPattern), `principal_kind` (`user`/`ip`/`anonymous`) | Q4.4 (#304) — per-user × endpoint token-bucket rejections. Identity (sub-claim / IP) is **not** a tag (IP-spray attack would create unbounded series); per-actor attribution via the `ratelimit.rejected` log line. |
| `trading.kill_switch.toggled` | Counter | `state` (`on`/`off`) | Kill-switch flip count. |
| `trading.symbol_halt.toggled` | Counter | (see emit site) | Admin symbol-halt flip count. |
| `trading.session_phase.changed` | Counter | `scope` (`default`/`symbol`), `phase` (new value or `cleared`) | #108 — drives "did the venue transition into/out of an auction" dashboard and audit-trail correlation with reject spikes. |

### Algo engine

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.algo.signals_consumed` | Counter | `kind` (`created`/`cancel_requested`/`child_execution_observed`) | Engine signal pump throughput. |
| `trading.algo.signals_dropped` | Counter | `kind` | Producer-side backpressure — bounded signal channel rejected a write. **Should be flat at zero**; non-zero = stuck consumer or runaway loop. |
| `trading.algo.children_submitted` | Counter | `type` (`iceberg`/`twap`/…) | Child orders submitted on behalf of an algo parent. |
| `trading.algo.signal_queue_depth` | UpDownCounter | (none) | Live depth of the algo signal channel. Sustained climb = consumer behind scheduler/ER hot path. |
| `trading.algo.scheduler.tick_duration` | Histogram (ms) | (none) | Wall-clock duration of a single `AlgoScheduler` tick. Climbing before tick interval starts to slip. |
| `trading.algo.twap.slice_fire_jitter` | Histogram (ms) | (none) | `now − plannedAtUtc` at the moment the scheduler fires a TWAP slice (captures tick granularity + consumer backpressure). |
| `trading.algo.vwap.slices_emitted` | Counter | (none) | Q3.1 (#281) — child orders the VWAP scheduler actually placed (zero-qty slots skipped, not counted). |
| `trading.algo.vwap.target_vs_actual_diff` | Histogram (shares) | (none) | `targetCumQty − executedCum` at slice evaluation (positive = behind, negative = ahead). |
| `trading.algo.vwap.cancelled` | Counter | (none) | VWAP parents reaching the `Cancelled` terminal state. |
| `trading.algo.pov.slices_emitted` | Counter | (none) | Q3.2 (#282) — child orders the POV scheduler actually placed. |
| `trading.algo.pov.actual_participation_rate` | Histogram (ratio) | (none) | `cumExecuted / cumMarketVolume` sampled at each POV slice evaluation — how closely the algo tracks its target rate. |
| `trading.algo.pov.cancelled` | Counter | (none) | POV parents reaching `Cancelled`. |
| `trading.algo.pegged.repegs_total` | Counter | (none) | Q3.3 (#283) — successful cancel+place cycles for a Pegged parent (no-op evals NOT counted). |
| `trading.algo.pegged.repeg_failed` | Counter | (none) | Repeg cycles where the cancel leg threw (gateway transient / child terminal in a race); engine retries next tick. |
| `trading.algo.pegged.cancelled` | Counter | (none) | Pegged parents reaching `Cancelled`. |
| `trading.algo.pegged.repeg_dedup_ring_evicted_total` | Counter | (none) | #296 — per-parent cancelled-child FIFO ring evicted an entry. Sustained increments = ring cap too tight for venue tail-Fill latency. Correlate with `PeggedRepegBook.MarkCancelledChild` warn log. |
| `trading.algo.child_modifies_total` | Counter | `algoType`, `reason` (`operator`/`pegged_repeg`) | Q3.5 (#285) — algo child cancel-replace cycles dispatched. |
| `trading.algo.modify_rejected_total` | Counter | (see emit site) | Algo modify rejected before reaching the gateway (terminal algo / terminal child / invalid qty / …). |
| `trading.algo.modify_send_ambiguous_total` | Counter | `algoType` | #299 — gateway dispatch threw post-WAL but venue may have accepted. Intent preserved for late `Replaced` ER. |
| `trading.algo.modify_retired_child_evicted_total` | Counter | `algoType` | #299 — per-parent retired-child FIFO eviction. Each = `ChildBookedCum` row forgotten; late ER for that OLD child id would re-book from 0. |
| `trading.algo.modify_ambiguous_intent_expired_total` | Counter | `algoType` (or `unknown`) | #299 P1 — `AlgoScheduler` sweep released an ambiguous-send replace reservation past `RiskOptions.Margin.AmbiguousReplaceTtl` without a Replaced/Rejected ER. Each bump = one upsize-delta reservation reclaimed. |
| `trading.algos.algoid_registry_corruption` | Counter | `firm`, `reason` (`invalid_observed_algoid`) | #160 — `AlgoIdRegistry` watermark-advance refusals during WAL replay. **MUST be flat at zero**. |

### WAL / snapshots / recovery

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.wal.appended` | Counter | (none) | Every successful WAL record append. |
| `trading.wal.backpressure` | Counter | `call_site` | WAL channel full; the writing call site backed off / failed-closed. |
| `trading.wal.segments_rotated` | Counter | (none) | Segment file rolled over. |
| `trading.wal.unknown_kind_skipped` | Counter | `kind` | #296 — forward-compat: replayer encountered a record whose `kind` discriminator isn't in its `JsonDerivedType` set. Expected during rolling deploys, alertable outside. |
| `trading.wal.missing_kind_corruption` | Counter | (see emit site) | #296 — WAL record's `kind` discriminator missing / unextractable (torn write, corruption, writer bug). Replay halts on first occurrence; counter bumped first to feed alerting. |
| `trading.snapshots.taken` | Counter | (none) | Snapshot job completed successfully. |
| `trading.snapshots.failed` | Counter | (none) | Snapshot job threw. |
| `trading.snapshots.duration_ms` | Histogram | (none) | End-to-end snapshot duration. |
| `trading.recovery.events_replayed` | Counter | (none) | One per WAL event applied during boot recovery. |
| `trading.recovery.session_rolled_firms` | Counter | `firm` | #380 path B — gateway SessionVerId advanced past the verId recorded in the loaded snapshot (venue rolled session while process was down). |
| `trading.recovery.session_rolled_orders_dropped` | Counter | `firm` | #380 path B — `WorkingOrderBook` entries eagerly retired (`MarkCancelled`) by the session-version guard. Post-#419 only ticks for PendingNew orders (never acked by venue → safe to cancel). |
| `trading.recovery.session_rolled_orders_staled` | Counter | `firm` | #419 — Working/PartiallyFilled orders flagged stale by the session-version guard. Venue may still hold them (B3 persists book across FIXP rolls); blotter/accounting visible, Cancel/Modify gated until ER or `OrderMassStatusRequest` confirms fate. |

### PnL / fees / statement

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.fees.replay_synth` | Counter | `reconciled` (`true`/`false`) | Q2.3 (#270) — fee-keeper deterministic replay synth. `reconciled=true` = durable `FeeAccruedEvent` superseded the synth (happy path: process didn't actually crash, just ordering). `reconciled=false` = `FinalizeReplay` materialised the synth — that IS the crash-window case; alert above baseline. |
| `trading.pnl.realized_appended` | Counter | (none) | Q2.4 (#271) — successful `RealizedPnlEvent` append via the dispatcher (live path, NOT replay). |
| `trading.pnl.replay_synth` | Counter | `reconciled` (`true`/`false`) | Mirror of `fees.replay_synth` for P&L. |
| `trading.pnl.endpoint_requests` | Counter | (none) | `GET /pnl/today` hits. |
| `trading.pnl.refprice_publishes` | Counter | (none) | #278 — refprice fan-out coalesced ≥1 (subscriber, symbol) updates into a single `pnl.me` delta publish under the per-symbol throttle. |
| `trading.pnl.refprice_throttled` | Counter | (none) | Refprice fan-out skipped a publish because the per-symbol throttle was in cooldown. |
| `trading.pnl.legacy_snapshot_basis_seeded` | Counter | (none) | #278 — `StateSnapshotter` restored a legacy snapshot whose `PnlAvgCost` block was empty but `Positions` had rows; basis reconstructed from `AverageEntryPrice`. Non-zero = platform restored from a pre-#271 snapshot at least once. |
| `trading.pnl.legacy_snapshot_basis_skipped_zero` | Counter | (none) | #278 — legacy position carries zero `AverageEntryPrice`; seeding skipped to avoid phantom P&L. Operator follow-up. |
| `trading.pnl.snapshot_basis_inconsistent` | Counter | (none) | #278 — same key in BOTH `PnlAvgCost` AND `PnlUnknownBasis` (malformed snapshot — mutually exclusive by construction). Recovery prefers `Unknown`; counter surfaces inconsistency. |
| `trading.statement.endpoint_requests` | Counter | `format` (`json`/`csv`) | Q2.5 (#272) — daily statement endpoint hits split by output format. |
| `trading.statement.day_trade_detected` | Counter | (none) | One per request that returned ≥1 IR day-trade row (informational; not driving tax collection). |
| `trading.statement.master_avg_basis_degraded_total` | Counter | (none) | PR #316 P2 — daily-statement projection had to render a master-bucket row whose per-bucket avg-cost basis is absent OR whose recorded qty disagrees with (aggregate − sumSub). Post-P1 backfill, persistent ticks alongside `subaccount.master_basis_unrecoverable_total` indicate invariant violation. Emits `AvgPrice=0` (fail-closed). |
| `trading.subaccount.master_basis_unrecoverable_total` | Counter | (none) | PR #316 P1 — `StateSnapshotter.Restore` observed a legacy snapshot whose `SubAccountPnlBasis` block is absent for a master bucket AND the same (firm, owner, symbol) has non-zero sub-account positions. Master basis intentionally NOT seeded (aggregate is polluted cross-bucket weighted average). |

### Audit / compliance

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.audit.events_total` | Counter | `event_type` (canonical hierarchical, e.g. `auth.login.success`, `admin.config.change`), `outcome` (`success`/`failure`/`denied`) | Q4.5 (#305) — every `IAuditLogger` envelope. No per-user / per-firm / per-IP tag (high-cardinality dimensions live on `/admin/audit`). |
| `trading.reports.cvm.generated_total` | Counter | `type` (`35`/`505`), `firm_id` | Q4.8 (#308) — successful CVM 35/505 transaction reports. Per-firm cardinality bounded by firm registry (~tens). |
| `trading.reports.cvm.generation_seconds` | Histogram (seconds) | `type` (`35`/`505`) | CVM export generation latency (WAL scan + XML stream-write). Slow rebuild visible before compliance notices hanging request. |

### WebSocket / fan-out

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.ws.connections.active` | UpDownCounter | (none) | Live count of subscribed WS clients across all hubs. |
| `trading.ws.messages.sent` | Counter | `topic` | Per-topic publish count (book, fills, pnl, …). |
| `trading.dispatcher.ws_fanout_dropped` | Counter | (none) | RFC §5.2 — WS hub per-sink channel overflowed and DropOldest evicted an event. Non-zero = WS hub drain thread can't keep up; subscribers observe seq gaps and reconnect-and-replay. |
| `trading.drain.rejections` | Counter | `route` | Requests rejected during graceful-drain window. |

### EntryPoint (FIXP) — outbound (gateway client)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.entrypoint.connected` | UpDownCounter | `firm` | 1 while the SDK reports a live FIXP session per firm. |
| `trading.entrypoint.events_received` | Counter | `firm`, `type` | Every inbound message from the SDK (Order/Exec/Status/…). |
| `trading.entrypoint.reconnect_attempts` | Counter | `firm` | Reconnect loop iterations. |
| `trading.entrypoint.reconnect_succeeded` | Counter | `firm` | Reconnect loop completed an Establish. |
| `trading.entrypoint.reconnect_failed` | Counter | `firm` | Reconnect loop gave up on this attempt (continues). |
| `trading.entrypoint.reconnecting` | Observable Gauge | `firm` | 1 while the gateway is actively inside its reconnect loop. Combined with `session_state` distinguishes "SDK terminated + retrying" from "SDK terminated + gave up". |
| `trading.entrypoint.session_state` | Observable Gauge | `firm`, `state` | One-hot per firm — exactly one row at 1 (current SDK `FixpClientState`), rest at 0. Pull-based source so each scrape reflects live SDK state. |
| `trading.entrypoint.session_ver_id` | Observable Gauge | `firm` | Last `SessionVerId` successfully Established. Stuck gauge while `reconnect_attempts` climbs = reconnect not making progress. |
| `trading.entrypoint.gap_detected` | Counter | (see emit site) | Inbound seqnum gap detected by our defensive check on top of the SDK's own retransmit. SDK may still recover internally; worth alerting. |
| `trading.entrypoint.duplicate_inbound` | Counter | (see emit site) | Duplicate / out-of-order replay we dropped (or ER processor idempotently deduped). |
| `trading.entrypoint.order_entry_call_ms` | Histogram (ms) | `firm`, `op` (`submit`/`cancel`/`replace`) | Local SDK await duration (network write + SDK serialization). Successful calls only; failures tracked by `OrdersGatewayFailed`. |
| `trading.entrypoint.order_entry_to_ack_ms` | Histogram (ms) | `firm`, `op` (`submit`/`cancel`/`replace`) | Submit-to-first-ER round trip. Timer starts BEFORE SDK await to avoid losing samples when ER arrives before await completes (full-duplex). |
| `trading.entrypoint.translation_errors` | Counter | `firm` | Inbound SDK message failed translation to domain shape. |
| `trading.entrypoint.business_rejects` | Counter | `firm`, `code` | Venue `BusinessReject` ack (no ClOrdID, so not surfaced to the latency probes). |
| `trading.entrypoint.terminated` | Counter | `firm`, `cause` | SDK signalled session termination. |
| `trading.entrypoint.orders_auto_staled` | Counter | `firm`, `reason` | #132 slice 2 — `OrderStaleningVenueReactor` auto-marked orders stale after a venue desync signal (FIXP `InboundGapAtReconnect` or peer-terminate when `Trading:AutoStale:OnPeerTerminate=true`). One `Add` per bulk-mark with the count flipped. |

### EntryPoint listener (FIXP server — bot leg)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.fixp.outbound.drain.shutdown.abandoned` | Counter | (none) | RFC §5.3.2 / F3 / #233 — per-FIXP-connection outbound drain loop ignored cancellation for >250 ms past configured shutdown timeout and writer abandoned it (`FixpOutboundChannelWriter.WaitForDrainAsync`). Sibling to the structured warning log; counter intentionally untagged. |
| `trading.entrypoint_listener.outbound_drain_shutdown_timeout` | Observable Gauge (seconds) | (none) | #234 — build-info gauge: configured `EntryPointListener:Buffers:OutboundDrainShutdownTimeout`. Sourced from `IOptionsMonitor` (reload-reactive). See `docs/ops/perf-v0-alerts.md` §1.1 for drift rules. |

### Market data

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.marketdata.subscribe_errors` | Counter | `symbol`, `reason` | `B3.MarketData.WebSocketClient` subscribe failures. |

### Persistence (config drift)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.persistence.group_commit_max_records` | Observable Gauge (records) | (none) | #234 — build-info gauge: configured `Trading:Persistence:GroupCommitMaxRecords`. Sourced from `IOptionsMonitor` (reload-reactive). |

> **Cardinality convention.** Where you see `(none)` or a single small enum (`reconciled`, `scope`, `state=active|suspended`, …), the absence of higher-fidelity tags is deliberate — per-user / per-end-client / per-ClOrdID / per-parentAlgoId / per-URL drilldowns belong on the read side (`/admin/*`, structured logs, traces — see #369 follow-up for the tracing decision), not on Prometheus series.

The `trading.risk.*` series back the **B3 Trading — Risk** Grafana
dashboard (`docker/observability/grafana/dashboards/risk.json`); the
v2 risk pipeline is documented in
[`docs/rfcs/pre-trade-risk-v2.md`](rfcs/pre-trade-risk-v2.md).

The `trading.algo.*` series (plus the `source` tag on
`trading.orders.submitted`) back the **B3 Trading — Algo** Grafana
dashboard (`docker/observability/grafana/dashboards/algo.json`); the
v0 algo pipeline is documented in
[`docs/rfcs/algo-orders-v0.md`](rfcs/algo-orders-v0.md). Per RFC §7
C1, `parentAlgoId` is intentionally **not** a metric tag (cardinality
would explode); per-algo drill-down lives in structured logs and
traces.

## Auto-instrumentation also exported

Wired alongside the application meter so a single OTLP pipeline carries
everything:

- **ASP.NET Core**: `http.server.request.duration`,
  `http.server.active_requests`, `kestrel.active_connections`,
  `kestrel.connection.duration`, `kestrel.queued_connections`,
  `aspnetcore.routing.match_attempts`,
  `aspnetcore.authentication.authenticate.duration`,
  `aspnetcore.memory_pool.{rented,pooled,allocated,evicted}`.
- **.NET runtime**: `dotnet.gc.{collections,heap.total_allocated,...}`,
  `dotnet.thread_pool.*`, `dotnet.exceptions`, `dotnet.assembly.count`,
  `dotnet.process.{cpu,memory}`, `dotnet.jit.*`.

## Traces

- AspNetCore instrumentation only.
- Health probes (`/live`, `/ready`, `/health`) are filtered at the source
  — they flood the trace stream and never carry useful diagnostic
  signal.

## Prometheus naming

We export OTLP, **not** the Prometheus exposition format. The PR 7-2c
otel-collector translates with the standard rules:

- Dots become underscores: `trading.orders.submitted` →
  `trading_orders_submitted`.
- Counters get a `_total` suffix appended:
  `trading_orders_submitted_total`.
- Histograms become `<name>_bucket` / `_sum` / `_count` triples.
- UpDownCounters keep their natural name (no `_total`).

That mapping lives in the collector config, not in the host, so we keep
exactly one wire format here.

## Smoke test

Quick local verification with a debug-output collector — proves SDK
activation, resource attributes, and that at least one application
counter exports:

```bash
# 1. Start a collector that prints whatever it receives
cat > /tmp/otel-debug.yaml <<'EOF'
receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
exporters:
  debug:
    verbosity: detailed
service:
  pipelines:
    metrics: { receivers: [otlp], exporters: [debug] }
    traces:  { receivers: [otlp], exporters: [debug] }
EOF

docker run -d --rm --name otelcol-test -p 4317:4317 \
  -v /tmp/otel-debug.yaml:/etc/otelcol-contrib/config.yaml \
  otel/opentelemetry-collector-contrib:0.119.0

# 2. Run the host pointing at it (any non-default signing key works)
cd backend/src/B3.Trading.Host
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
OTEL_METRIC_EXPORT_INTERVAL=2000 \
ASPNETCORE_URLS=http://localhost:5050 \
Trading__Auth__SigningKey="dev-only-test-key-32-bytes-min-length____padding!" \
  dotnet run --no-build

# 3. Hit a counter (in another shell)
TOKEN=$(curl -sX POST http://localhost:5050/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"wonderland"}' | jq -r .token)
curl -sX POST http://localhost:5050/orders/ \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"symbol":"PETR4","securityId":1,"side":"Buy","type":"Limit","quantity":100,"price":30.5}'

# 4. Watch it land in the collector
docker logs otelcol-test 2>&1 | grep "Name: trading."
#  -> Name: trading.orders.submitted
#  -> Name: trading.wal.appended
```

The full observability stack (collector + Prometheus + Grafana) ships
behind the `obs` compose profile in PR 7-2c.
