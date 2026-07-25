# Metrics

The trading-host emits OpenTelemetry metrics + traces, and the standalone
market-maker bot emits metrics, opt-in via the
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
The market-maker bot uses `service.name=b3-market-maker-bot` and
`DOTNET_ENVIRONMENT` for `deployment.environment`.

## Market-maker meter (`B3.Trading.MarketMakerBot`)

The bot's position ledger is process-local, gross/pre-fee, and derived only
from validated `OrderTrade` events for known bot orders. It is deliberately
independent of the trading application's persisted P&L state. Per-order
CumQty/execution identities remain available for FIXP replay deduplication
while an order is active and for a quiet `MarketMaker:MaxOrderAge` window
after terminal status; reconcile cleanup then evicts only that per-order
metadata, never accumulated position or P&L.

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `bot.position.net_quantity` | Observable Gauge | `symbol` | Signed net quantity |
| `bot.position.average_entry_price` | Observable Gauge | `symbol` | Weighted-average open cost |
| `bot.orders.open` | Observable Gauge | `symbol` | Bot-tracked open/resting-or-submitting orders; normally `2` per eligible configured symbol |
| `bot.strategy.configured_half_spread_ticks` | Observable Gauge | `symbol` | Static configured `SpreadTicks` floor |
| `bot.strategy.effective_half_spread_ticks` | Observable Gauge | `symbol` | Configured floor plus the current volatility addition |
| `bot.strategy.inventory_skew_ticks` | Observable Gauge | `symbol` | Signed applied inventory skew for enabled instruments; positive means long inventory shifts the quote mid down, negative means short inventory shifts it up |
| `bot.strategy.volatility_move_estimate_ticks` | Observable Gauge | `symbol` | Mean absolute valid trade-to-trade move in ticks; omitted until an estimate exists |
| `bot.strategy.volatility_additional_half_spread_ticks` | Observable Gauge | `symbol` | Capped ticks added to the configured half-spread |
| `bot.pnl.realized` | Observable Gauge | `symbol` | Process-lifetime gross realized P&L |
| `bot.pnl.unrealized` | Observable Gauge | `symbol` | Omitted unless a connected live mark is no older than `MarketMaker:Telemetry:MarkMaxAge` |
| `bot.pnl.total` | Observable Gauge | `symbol` | Realized + unrealized; omitted under the same fresh-mark gate |
| `bot.orders.submitted` | Counter | `symbol`, `side` | Successfully transmitted new quotes |
| `bot.orders.submit_failed` | Counter | `symbol` | Quote submissions that failed before acknowledgement |
| `bot.fills.received` | Counter | `symbol` | Own execution events received before ledger classification |
| `bot.pnl.fills_applied` | Counter | `symbol` | Valid own executions booked |
| `bot.pnl.fills_unknown_order` | Counter | `symbol=unknown` | Fill ignored because ClOrdID is not owned by this process |
| `bot.pnl.fills_duplicate` | Counter | `symbol` | Execution identity replay ignored |
| `bot.pnl.fills_invalid` | Counter | `symbol` | Invalid price/quantity/identity ignored |
| `bot.pnl.fills_inconsistent` | Counter | `symbol` | CumQty, LeavesQty, status, or replay payload mismatch ignored |
| `bot.pnl.fill_delta_mismatch` | Counter | `symbol` | Advancing CumQty-derived delta differed from LastQty; authoritative cumulative delta was booked |
| `bot.orders.rejected` | Counter | `symbol` | Venue quote rejects |
| `bot.orders.cancelled` | Counter | none | Terminal cancel acknowledgements |
| `bot.orders.stale_cancelled` | Counter | `symbol` | Stale-order guard cancel requests |
| `bot.orders.stale_cancel_rejected` | Counter | `symbol` | Stale cancel rejects |
| `bot.orders.stale_cancel_submit_failed` | Counter | `symbol` | Stale cancel transmission failures |
| `bot.orders.safety_cap_hit` | Counter | `symbol` | `MaxOpenOrders` prevented a new quote |
| `bot.orders.book_driven_requote` | Counter | `symbol`, `side` | Price-drift cancel/requote requests |
| `bot.orders.book_driven_requote_submit_failed` | Counter | `symbol` | Book-driven cancel transmission failures |
| `bot.orders.book_driven_requote_cancel_rejected` | Counter | `symbol` | Book-driven cancel rejects |
| `bot.market_data.availability_transition` | Counter | `symbol`, `available`, `reason` | Strict feed eligibility changes |
| `bot.market_data.quote_suppressed` | Counter | `symbol`, `side`, `reason` | Quote decisions suppressed by `PauseAndCancel` |
| `bot.market_data.reference_age_seconds` | Observable Gauge | `symbol`, `source` | Age of the last valid live reference |
| `bot.market_data.reference_eligible` | Observable Gauge | `symbol`, `reason` | `1` only when the current connection epoch has a fresh valid reference; emitted only for `PauseAndCancel` |
| `bot.market_data.reference_eligible_current` | Observable Gauge | `symbol` | Stable-label current eligibility (`1`/`0`) for state checks; avoids stale Prometheus series when the diagnostic `reason` label changes |
| `bot.orders.feed_unavailable_cancel` | Counter | `symbol`, `side` | Active quote cancelled because the feed became ineligible |
| `bot.orders.feed_unavailable_cancel_rejected` | Counter | `symbol` | Feed-loss cancel rejects |
| `bot.orders.feed_unavailable_cancel_submit_failed` | Counter | `symbol` | Feed-loss cancel transmission failures |
| `bot.orders.feed_unavailable_cancel_retry` | Counter | `symbol` | Guarded feed-loss cancel retries |
| `bot.orders.cancel_ack_expired` | Counter | `symbol`, `reason` | Pending cancel exceeded `MarketMaker:CancelAckTimeout`; marker expired and guarded retry was enabled |

Configured-symbol counters publish bounded zero baselines when the metric
publisher starts. Position, average-entry, and realized-P&L gauges likewise
emit `0` for a configured symbol with no ledger entry. This makes a healthy
zero distinguishable from an absent exporter/series. Unrealized and total P&L
still require a fresh mark: no fresh mark means no series, never a fabricated
numeric zero.

Structured snapshots use the same ledger and mark-freshness gate at
`MarketMaker:Telemetry:SnapshotInterval`. A missing/stale mark is logged as
null and is never exported as zero unrealized P&L. Every snapshot also carries
the process accounting-period start timestamp:

```text
[mm-pnl] accountingPeriodStartedAtUtc=... symbol=... position=...
averageCost=... realizedPnl=... unrealizedPnl=... totalPnl=...
mark=... markAge=...
```

Other bounded diagnostic records used by the strategy soak are:

- `[mm-volatility]`: `symbol`, `estimateTicks`, `samples`, `ready`,
  `connected`, `previousAdditionalTicks`, `additionalTicks`;
- `[mm-feed]`: `symbol`, `available`, `reason`, `epoch`, `age`, `source`,
  and suppressed-decision `side`;

The soak helper copies the current `accountingPeriodStartedAtUtc` into every
metric sample and fails if it changes. Separate presence evidence requires
mandatory symbol/profile series to exist before their numeric values are used;
Prometheus absence is never coerced to zero. The helper also verifies every collected
counter (`*_total` after Prometheus translation) is monotonically
non-decreasing, so a bot restart cannot erase earlier integrity/error evidence.
Before the strict outage boundary, `bot.orders.submitted` must be unchanged
across at least two complete OTLP export plus Prometheus scrape cycles. After
reconnect and before any fresh market event, the stable eligibility gauge and
open-order gauge must remain zero for another complete cycle.
In the bundled Prometheus view, the scrape target's `source` label causes the
instrument's reference-source attribute to appear as `exported_source`; the
helper normalizes that bounded value into its CSV `source` column.
- `[mm-pnl]` fill diagnostics: `symbol`, `clordid`, `tradeId`, quantities,
  prices, and a bounded reason;
- `[mm] safety cap hit`: `OpenCount`, `MaxOpenOrders`, `symbol`, `side`.

Metric dimensions stay low-cardinality: configured `symbol`; `side` only as
`buy|sell`; and bounded `reason`, `available`, or `source` values shown above.
ClOrdID, order ID, trade ID, account, and free-form exception text are logs,
never metric tags.

The bot exports only to an OTLP endpoint. The intended deployment path is
`bot -> OTLP Collector in b3deploy -> Azure Monitor`; Azure-specific exporters
and credentials belong to the collector deployment, not this repository.
See the evidence, dashboard, and alert contract in
[`operations/market-maker-soak.md`](operations/market-maker-soak.md).

## Application meter (`B3.Trading`)

All app-layer instruments live on a single `Meter("B3.Trading", "1.0.0")`
declared in
[`MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).
One `AddMeter(MetricsRegistry.Meter.Name)` call wires the lot.

| OTel name | Type | Tags |
|---|---|---|
| `trading.orders.submitted` | Counter | `symbol`, `side`, `source` (manual/algo), `firmId`, `security_type` (equity/option/unknown) |
| `trading.orders.rejected_by_risk` | Counter | `check` |
| `trading.orders.gateway_failed` | Counter | (none) |
| `trading.orders.cancel_requested` | Counter | (none) |
| `trading.er.received` | Counter | `type` (Fill, Canceled, Rejected, ...) |
| `trading.kill_switch.toggled` | Counter | `scope`, `killed` |
| `trading.wal.appended` | Counter | (none) |
| `trading.wal.backpressure` | Counter | `call_site` |
| `trading.wal.segments_rotated` | Counter | (none) |
| `trading.snapshots.taken` | Counter | (none) |
| `trading.snapshots.failed` | Counter | (none) |
| `trading.snapshots.duration_ms` | Histogram | (none) |
| `trading.recovery.events_replayed` | Counter | (none) |
| `trading.ws.connections.active` | UpDownCounter | (none) |
| `trading.ws.messages.sent` | Counter | `topic` |
| `trading.drain.rejections` | Counter | `route` |
| `trading.entrypoint.connected` | UpDownCounter | `firm` |
| `trading.entrypoint.events_received` | Counter | `firm`, `type` |
| `trading.entrypoint.reconnect_attempts` | Counter | `firm` |
| `trading.entrypoint.translation_errors` | Counter | `firm` |
| `trading.entrypoint.business_rejects` | Counter | `firm`, `code` |
| `trading.entrypoint.terminated` | Counter | `firm`, `cause` |
| `trading.outbound.operator_resolution_total` | Counter | `firm`, `decision`, `evidence_type`, `result` |
| `trading.outbound.contradictory_evidence_total` | Counter | `firm`, `evidence_type` |
| `trading.outbound.ambiguous` | Observable Gauge | `firm`, `kind`, `age_bucket`, `ambiguity_reason` |
| `trading.outbound.legacy_unknown` | Observable Gauge | `firm`, `kind`, `ambiguity_reason` |
| `trading.outbound.oldest_ambiguous_age_seconds` | Observable Gauge | `firm` |
| `trading.outbound.oldest_legacy_unknown_age_seconds` | Observable Gauge | `firm` |
| `trading.marketdata.subscribe_errors` | Counter | `symbol`, `reason` |
| `trading.risk.refprice.lookups` | Counter | `source` (live/fallback/missing) |
| `trading.risk.refprice.staleness_seconds` | Observable Gauge | `symbol` |
| `trading.risk.collar.bypassed_no_reference` | Counter | `symbol` |
| `trading.risk.price_band.reject` | Counter | `symbol`, `side`, `reason` (above/below) |
| `trading.risk.price_band.age_seconds` | Histogram | `symbol` |
| `trading.risk.price_band.bypassed_no_band` | Counter | `symbol` |
| `trading.risk.rolling_notional.bypassed_no_reference` | Counter | `symbol` |
| `trading.risk.rolling_notional.active_buckets` | Observable Gauge | `scope` (end_client/firm) |
| `trading.risk.order_rate.active_buckets` | Observable Gauge | `scope` (end_client/firm) |
| `trading.algo.signals_consumed` | Counter | `kind` (created/cancel_requested/child_execution_observed) |
| `trading.algo.signals_dropped` | Counter | `kind` |
| `trading.algo.children_submitted` | Counter | `type` (iceberg/twap) |
| `trading.algo.twap.slice_fire_jitter` | Histogram (ms) | (none) |
| `trading.algo.scheduler.tick_duration` | Histogram (ms) | (none) |
| `trading.algo.signal_queue_depth` | UpDownCounter | (none) |
| `trading.options.zero_price_orders_submitted` | Counter | `symbol`, `side`, `firmId`, `put_call` |

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

The `trading.outbound.*` reconciliation series use only configured firm IDs
and bounded categorical values. Mutation IDs, ClOrdIDs, evidence references,
accounts, investors, and end-client identifiers are never labels. `/health`
adds unresolved mutation/firm counts and oldest ambiguity/legacy ages under
`outboundRecovery`; `/ready` remains the fail-closed gate for required firms.
Executable alerts page on contradictory evidence and surface aging
ambiguity/legacy work in
[`b3-trading.rules.yml`](../docker/observability/prometheus/rules/v1/b3-trading.rules.yml).

## Extended application instruments (#369)

The table above is the curated "dashboard-first" subset. The host emits
many more named instruments on the same `B3.Trading` meter; the tables
below document the remainder, grouped by area. Source of truth for every
row is the declaration (and surrounding `.Add(...)` call sites) in
[`MetricsRegistry.cs`](../backend/src/B3.Trading.Application/Observability/MetricsRegistry.cs).
"Type" is the `System.Diagnostics.Metrics` instrument; "(none)" means the
instrument is intentionally label-less to keep cardinality bounded.

### Order flow

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.orders.routing_instruction_stamped` | Counter | `value`, `firmId` | Approved routing-instruction stamps on an outbound NewOrder/Replace (#473); the `BrokerOnly` slice is conflict-of-interest sensitive and alertable per firm. |
| `trading.orders.gtd_expired` | Counter | `cancel_result` | GTD expiries the scheduler dispatched; tag is the `OrderCancelResultKind` (Accepted/NotFound/Stale/WalBackpressure/GatewayFailed). A climb on a non-Accepted bucket flags scheduler-vs-pipeline drift (#255). |
| `trading.orders.modify_requested` | Counter | `symbol`, `side`, `firmId` | Cancel-replace (modify) requests accepted into the order pipeline. |
| `trading.orders.ioc_no_response` | Counter | `firmId`, `symbol`, `tif` | IOC/FOK watchdog synthesised a terminal Cancel because the gateway returned no ER within the timeout (#351). A non-zero rate is a matching-image regression detector. |
| `trading.orders.duplicate_clordid` | Counter | `op` (submit/modify), `scope` (book/pending) | Pre-flight ClOrdID collision rejections. MUST be flat at zero; non-zero means a counter-watermark regression (#108). |
| `trading.orders.clordid_registry_corruption` | Counter | `end_client`, `reason` (invalid_observed_clordid/prefix_mismatch) | WAL-replay ClOrdID corruption / per-end-client prefix-mismatch detector. MUST be flat at zero (#157). |

### Execution reports

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.er.replay_dedup` | Counter | `kind` | Idempotent ER drop (cum/terminal already known). Expected after FIXP retransmit on reconnect; a sustained spike is operator-visible. |
| `trading.er.fill_delta_mismatch` | Counter | `kind` | Fill ER advanced cumulative-quantity by an amount ≠ its own LastQuantity — a lost or out-of-order intermediate fill. |
| `trading.er.late_fill_after_terminal` | Counter | `kind` | Fill ER arrived for an order already terminal (Cancelled/Rejected); position is still booked, order keeps its terminal status. |
| `trading.execution_reports.dropped_known_owner_missing_order` | Counter | `kind` | ER resolved to a known owner but the order is absent from the working book — a silent fill loss with position/cash divergence; alertable (#241). |
| `trading.er.firm_mismatch_total` | Counter | `exec_type` | Live-wire ER carried a FirmId that does not match the resolved order's FirmId; rejected without mutating state (#317). |

### Margin / risk

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.margin.commit_replace_dropped` | Counter | (none) | CommitReplace landed with neither original nor transient reservation present — reserved figure leaks until restart; alertable (#247). |
| `trading.risk.margin_overcommit_on_restore` | Counter | `owner` | Reservation Restore (admin clear-stale) pushed the per-owner reserved figure above base capacity — safety valve; reconcile by cancelling stale orders (#153). |
| `trading.risk.margin_stale_transition_failed` | Counter | `kind` | Suspended/Restored could not be applied to the in-process reservation ledger; recoverable on restart, but a non-zero rate signals a bug (#153). |
| `trading.risk.margin_reservations` | ObservableGauge | `state` (active/suspended) | Live size of the cash-margin reservation ledger; a sustained climb on `suspended` is a dictionary leak until restart (#153). |
| `trading.risk.stop_check_skipped_no_ref` | Counter | `symbol` | Stop* order approved purely because no reference price was available (the Buy≥ref / Sell≤ref relation was skipped) (#254). |
| `trading.ratelimit.rejected_total` | Counter | `path`, `principal_kind` (user/ip/anonymous) | Requests rejected by the per-user × endpoint token-bucket limiter (#304). |
| `trading.er_injection.enabled` | UpDownCounter | (none) | 1 when the host booted with `Trading:Exchange:AllowErInjection=true` (the synthetic-ER admin endpoint is mapped). |

### Audit / compliance / session

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.audit.events_total` | Counter | `event_type`, `outcome` (success/failure/denied) | Audit envelopes emitted via `IAuditLogger`. Deliberately no per-user/firm/IP tag (#305). |
| `trading.auth.exchange.requests_total` | Counter | `result`, `reason`, `issuer_alias` | `/api/auth/exchange` attempts. Reasons are stable RFC codes; no token, raw external subject, user or firm labels (#607). |
| `trading.auth.exchange.duration_seconds` | Histogram (s) | `result`, `reason`, `issuer_alias` | End-to-end external-token validation + directory lookup + internal JWT issuance latency (#607). |
| `trading.reports.cvm.generated_total` | Counter | `type` (35/505), `firm_id` | Successful CVM 35/505 transaction reports generated by the on-demand export pipeline (#308). |
| `trading.reports.cvm.generation_seconds` | Histogram (s) | `type` | CVM 35/505 generation latency — full WAL scan + XML stream-write (#308). |
| `trading.symbol_halt.toggled` | Counter | `halted`, `origin` | Per-symbol halt/resume toggles. |
| `trading.session_phase.changed` | Counter | `scope` (default/symbol), `phase` | Session-phase changes (auction transitions, `cleared` on override removal); correlate with reject spikes (#108). |

### WAL / persistence

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.wal.unknown_kind_skipped` | Counter | `kind` | WAL record whose discriminator is not in the reader's set — expected during rolling deploys, alert otherwise (#296). |
| `trading.wal.missing_kind_corruption` | Counter | (none) | WAL record whose discriminator is missing/unextractable — torn write or corruption; replay halts on the first such record (#296). |
| `trading.persistence.group_commit_max_records` | ObservableGauge (records) | (none) | Configured `Trading:Persistence:GroupCommitMaxRecords` — build-info gauge, reflects config reloads (#234). |
| `trading.dispatcher.ws_fanout_dropped` | Counter | (none) | WS hub per-sink channel `DropOldest` eviction; subscribers should observe a gap and reconnect-and-replay. |

### Recovery

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.recovery.session_rolled_firms` | Counter | `firm` | Firm whose gateway SessionVerId advanced past the snapshot verId — the venue rolled the session while the process was down (#380). |
| `trading.recovery.session_rolled_orders_dropped` | Counter | `firm` | PendingNew working-book entries eagerly retired (MarkCancelled) by the session-version guard (only PendingNew, per #504). |

### P&L / statement

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.fees.replay_synth` | Counter | `reconciled` | Fee-keeper deterministic replay synth over the ER→FeeAccruedEvent crash window; `reconciled=false` is the real crash case — alert above baseline (#270). |
| `trading.pnl.realized_appended` | Counter | `firmId` | RealizedPnlEvent appended on the live dispatcher path (not replay) (#271). |
| `trading.pnl.replay_synth` | Counter | `reconciled` | P&L replay synth mirroring the fee synth; `reconciled=false` is the ER-then-crash window (#271). |
| `trading.pnl.endpoint_requests` | Counter | (none) | `/api/pnl/today` hits. |
| `trading.statement.endpoint_requests` | Counter | `format` (json/csv) | Daily-statement endpoint hits, split by output format (#272). |
| `trading.statement.day_trade_detected` | Counter | (none) | Statement requests that returned ≥1 IR day-trade row (informational; no tax collected) (#272). |
| `trading.statement.master_avg_basis_degraded_total` | Counter | (none) | Master-bucket rows rendered with absent/disagreeing avg-cost basis → `AvgPrice=0` fail-closed (#316). |
| `trading.subaccount.master_basis_unrecoverable_total` | Counter | (none) | Legacy snapshot missing `SubAccountPnlBasis` while sub-account positions exist — master basis left unseeded (#316). |
| `trading.pnl.legacy_snapshot_basis_seeded` | Counter | (none) | Avg-cost basis reconstructed from a legacy (pre-#271) snapshot's position rows on restore (#278). |
| `trading.pnl.legacy_snapshot_basis_skipped_zero` | Counter | (none) | Legacy position row skipped because its AverageEntryPrice was zero (would realise phantom P&L) (#278). |
| `trading.pnl.snapshot_basis_inconsistent` | Counter | (none) | Same key present in both PnlAvgCost and PnlUnknownBasis on restore; "prefer unknown" policy applied (#278). |
| `trading.pnl.refprice_publishes` | Counter | (none) | Coalesced refprice fan-out `pnl.me` delta publishes under the per-symbol throttle (#278). |
| `trading.pnl.refprice_throttled` | Counter | (none) | Refprice publishes suppressed by the per-symbol throttle (#278). |

### Algo engine (extended)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.algo.vwap.slices_emitted` | Counter | (none) | Child orders the VWAP scheduler actually placed (zero-qty slots skipped) (#281). |
| `trading.algo.vwap.target_vs_actual_diff` | Histogram (shares) | (none) | `targetCumQty − executedCum` at VWAP slice evaluation (positive = behind) (#281). |
| `trading.algo.vwap.cancelled` | Counter | (none) | VWAP parents reaching the Cancelled terminal state (#281). |
| `trading.algo.pov.slices_emitted` | Counter | (none) | Child orders the POV scheduler actually placed (#282). |
| `trading.algo.pov.actual_participation_rate` | Histogram (ratio) | (none) | `cumExecuted / cumMarketVolume` sampled at each POV slice evaluation (#282). |
| `trading.algo.pov.cancelled` | Counter | (none) | POV parents reaching the Cancelled terminal state (#282). |
| `trading.algo.pegged.repegs_total` | Counter | (none) | Pegged repeg (cancel + place at new target) cycles; no-op evaluations excluded (#283). |
| `trading.algo.pegged.repeg_failed` | Counter | (none) | Repeg attempts that aborted on the cancel leg; retried next tick (#283). |
| `trading.algo.pegged.cancelled` | Counter | (none) | Pegged parents reaching the Cancelled terminal state (#283). |
| `trading.algo.pegged.repeg_dedup_ring_evicted_total` | Counter | (none) | FIFO evictions in PeggedRepegBook's per-parent cancelled-child dedup ring; sustained increments mean the cap is too tight for venue tail-fill latency (#296). |
| `trading.algo.child_modifies_total` | Counter | `algoType`, `reason` | Algo child cancel-replace (modify) cycles dispatched to the gateway (#285). |
| `trading.algo.modify_rejected_total` | Counter | `algoType`, `reason` | Algo modify requests rejected before the gateway (terminal algo/child, invalid qty) (#285). |
| `trading.algo.modify_send_ambiguous_total` | Counter | `algoType` | Algo modify dispatch threw post-WAL but may have been venue-accepted; intent preserved for a late Replaced ER (#299). |
| `trading.algo.modify_retired_child_evicted_total` | Counter | `algoType` | Retired-child FIFO entries evicted from the per-parent `ChildBookedCum` bookkeeping cap (#299). |
| `trading.algo.modify_ambiguous_intent_expired_total` | Counter | `algoType` | Ambiguous-send replace intents expired by the scheduler TTL sweep; held margin reservation released (#299). |
| `trading.algos.algoid_registry_corruption` | Counter | `firm`, `reason` | AlgoId watermark-advance refusals during WAL replay. MUST be flat at zero (#160). |

### EntryPoint (extended)

| OTel name | Type | Tags | Notes |
|---|---|---|---|
| `trading.entrypoint.reconnect_succeeded` | Counter | `firm`, `kind` | Successful gateway reconnects (reattach vs renegotiate). |
| `trading.entrypoint.reconnect_failed` | Counter | `firm`, `reason` | Failed gateway reconnect attempts. |
| `trading.entrypoint.reconnecting` | ObservableGauge | `firm` | 1 while the gateway is actively inside its reconnect loop (distinguishes "trying" from "gave up"). |
| `trading.entrypoint.session_state` | ObservableGauge | `firm`, `state` | One-hot FIXP wire-protocol state per firm (exactly one row = 1), pulled live from the SDK on each scrape. |
| `trading.entrypoint.session_ver_id` | ObservableGauge | `firm` | Last SessionVerId successfully Established per firm; a frozen gauge while attempts climb flags a stuck reconnect. |
| `trading.entrypoint.gap_detected` | Counter | `firm` | Inbound seqnum gap flagged by the defensive check on top of SDK retransmit. |
| `trading.entrypoint.duplicate_inbound` | Counter | `firm` | Duplicate/out-of-order inbound replay dropped (or idempotently deduped downstream). |
| `trading.entrypoint.order_entry_call_ms` | Histogram (ms) | `firm`, `op` (submit/cancel/replace) | Local SDK await duration (network write + serialization) for successful order-entry calls. |
| `trading.entrypoint.order_entry_to_ack_ms` | Histogram (ms) | `firm`, `op` | Submit-to-first-ER round trip. |
| `trading.entrypoint.orders_auto_staled` | Counter | `firm`, `reason` | Orders auto-marked stale by the venue reactor after a desync signal (gap-at-reconnect / peer-terminate) (#132). |
| `trading.entrypoint.session_roll_stale_reconcile_failed` | Counter | `firm` | Confirmed session-roll reconciliation whose Working/PartiallyFilled staling phase failed — surviving orders need a manual admin `mark-stale` (#380/#503). |
| `trading.entrypoint_listener.outbound_drain_shutdown_timeout` | ObservableGauge (s) | (none) | Configured `EntryPointListener:Buffers:OutboundDrainShutdownTimeout` — build-info gauge, reflects reloads (#234). |
| `trading.fixp.outbound.drain.shutdown.abandoned` | Counter | (none) | Per-connection outbound drain loop abandoned >250 ms past the shutdown timeout; sibling of the structured warn log (#233). |

## Option-specific surveillance (OPT-F / #488)

`trading.orders.submitted.security_type` lets dashboards split equity
vs option flow (and surface "unknown" buckets that signal a symbol
missing from the directory). The `security_type` value is derived from
`SymbolDirectory.TryGetSpec(...).SecurityType` at submit time; when
the directory is not injected (most unit tests) the tag is `unknown`.

`trading.options.zero_price_orders_submitted` is the dedicated
surveillance signal for the OPT-C (#485) cabinet / worthless-OTM
closeout flow — orders that travel as `Limit Price=0` on the OPT
channel. A small steady stream is normal end-of-cycle hygiene; a
sudden spike on one `(symbol, firmId, put_call)` tuple is the
compliance alert (off-market levelling, wash-out, mis-keyed price).

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
TOKEN=$(curl -sX POST http://localhost:5050/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"alice","password":"wonderland"}' | jq -r .token)
curl -sX POST http://localhost:5050/api/orders/ \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"symbol":"PETR4","securityId":1,"side":"Buy","type":"Limit","quantity":100,"price":30.5}'

# 4. Watch it land in the collector
docker logs otelcol-test 2>&1 | grep "Name: trading."
#  -> Name: trading.orders.submitted
#  -> Name: trading.wal.appended
```

The full observability stack (collector + Prometheus + Grafana) ships
behind the `obs` compose profile in PR 7-2c.
