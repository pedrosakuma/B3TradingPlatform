using System.Diagnostics.Metrics;

namespace B3.Trading.Application.Observability;

/// <summary>
/// OpenTelemetry-compatible metric instruments using
/// <c>System.Diagnostics.Metrics</c>. The meter name <c>B3.Trading</c>
/// is what an OTel sidecar / Prometheus scraper subscribes to.
///
/// Mirrors the convention from <c>B3MarketDataPlatform</c>'s
/// <c>MetricsRegistry</c> (single static class, instruments owned at
/// process scope). No exporter is wired in-process; observation is the
/// host's job.
/// </summary>
public static class MetricsRegistry
{
    public static readonly Meter Meter = new("B3.Trading", "1.0.0");

    // Order flow
    public static readonly Counter<long> OrdersSubmitted =
        Meter.CreateCounter<long>("trading.orders.submitted");
    public static readonly Counter<long> OrdersRejectedByRisk =
        Meter.CreateCounter<long>("trading.orders.rejected_by_risk");
    public static readonly Counter<long> OrdersGatewayFailed =
        Meter.CreateCounter<long>("trading.orders.gateway_failed");
    public static readonly Counter<long> OrdersCancelRequested =
        Meter.CreateCounter<long>("trading.orders.cancel_requested");
    /// <summary>
    /// Q1.3 (#255). Counts every GTD expiry the scheduler dispatched.
    /// Tagged with <c>cancel_result</c> = the
    /// <see cref="OrderCancelResultKind"/> the cancel pipeline
    /// returned (Accepted / NotFound / Stale / WalBackpressure /
    /// GatewayFailed) so a sustained climb on a non-Accepted bucket
    /// flags scheduler-vs-pipeline drift (e.g. the scheduler keeps
    /// firing for orders the book no longer knows about).
    /// </summary>
    public static readonly Counter<long> GtdOrdersExpired =
        Meter.CreateCounter<long>("trading.orders.gtd_expired");
    public static readonly Counter<long> OrdersModifyRequested =
        Meter.CreateCounter<long>("trading.orders.modify_requested");

    // Execution reports inbound
    public static readonly Counter<long> ExecutionReportsReceived =
        Meter.CreateCounter<long>("trading.er.received");
    // Idempotency: an ER carries the same (or older) cumulative-quantity /
    // terminal-state we already have. Expected after FIXP retransmit on
    // reconnect; surfaced so a sustained spike is visible to operators.
    public static readonly Counter<long> ExecutionReportsReplayDeduped =
        Meter.CreateCounter<long>("trading.er.replay_dedup");
    // Fill ER advanced cumulative-quantity by an amount that doesn't equal
    // its own LastQuantity — i.e. an intermediate fill ER was lost or
    // delivered out-of-order. Position is still booked at the reported
    // delta and lastPx (best-effort attribution); the gauge is for
    // operators to spot delivery issues.
    public static readonly Counter<long> ExecutionReportsFillDeltaMismatch =
        Meter.CreateCounter<long>("trading.er.fill_delta_mismatch");
    // A fill ER arrived for an order already in a terminal state
    // (Cancelled/Rejected). Position is still booked — the exchange's
    // cumulative-quantity is the source of truth — but the order keeps
    // its terminal status.
    public static readonly Counter<long> ExecutionReportsLateFillAfterTerminal =
        Meter.CreateCounter<long>("trading.er.late_fill_after_terminal");
    // Issue #241: an ER resolved to a known owner via OrderOwnershipMap
    // but the corresponding Order is absent from WorkingOrderBook —
    // we have nowhere to apply the fill / status mutation. Most often
    // a venue cancel-as-replace (priority-lost) path that wasn't
    // intercepted as a replacement. A non-zero rate here means a
    // silent fill loss with position/cash divergence; alertable.
    public static readonly Counter<long> ExecutionReportsDroppedKnownOwnerMissingOrder =
        Meter.CreateCounter<long>("trading.execution_reports.dropped_known_owner_missing_order");

    // Issue #247: CommitReplace landed on the reservation ledger but
    // neither the original nor the transient (Prepare-side) entry was
    // present, so the venue-confirmed remaining notional could not be
    // tracked under the new ClOrdID. With Margin.Enabled=true this is
    // a Prepare/Commit pipeline mismatch (some path registered a
    // replace intent without going through the coordinator) and means
    // the owner's reserved figure will leak until restart. Alertable.
    public static readonly Counter<long> MarginCommitReplaceDropped =
        Meter.CreateCounter<long>("trading.margin.commit_replace_dropped");

    // Risk / kill-switch
    public static readonly Counter<long> KillSwitchToggled =
        Meter.CreateCounter<long>("trading.kill_switch.toggled");

    public static readonly Counter<long> SymbolHaltToggled =
        Meter.CreateCounter<long>("trading.symbol_halt.toggled");

    /// <summary>
    /// Session-phase change counter (#108). Tagged <c>scope=default|symbol</c>
    /// and <c>phase</c> with the new value (or <c>cleared</c> when an
    /// override is removed). Drives ops dashboards for "did the venue
    /// transition into/out of an auction" and audit-trail correlation
    /// with reject spikes.
    /// </summary>
    public static readonly Counter<long> SessionPhaseChanged =
        Meter.CreateCounter<long>("trading.session_phase.changed");

    // WAL
    public static readonly Counter<long> WalAppended =
        Meter.CreateCounter<long>("trading.wal.appended");
    public static readonly Counter<long> WalBackpressure =
        Meter.CreateCounter<long>("trading.wal.backpressure");
    public static readonly Counter<long> WalSegmentsRotated =
        Meter.CreateCounter<long>("trading.wal.segments_rotated");

    // Snapshots / recovery
    public static readonly Counter<long> SnapshotsTaken =
        Meter.CreateCounter<long>("trading.snapshots.taken");
    public static readonly Counter<long> SnapshotsFailed =
        Meter.CreateCounter<long>("trading.snapshots.failed");
    public static readonly Histogram<double> SnapshotDurationMs =
        Meter.CreateHistogram<double>("trading.snapshots.duration_ms");
    public static readonly Counter<long> RecoveryEventsReplayed =
        Meter.CreateCounter<long>("trading.recovery.events_replayed");

    // Q2.3 (#270). Fee-keeper deterministic replay synth — surfaces the
    // crash window between ER append (seq N) and FeeAccruedEvent append
    // (seq N+1). Tag `reconciled` is true when a durable FeeAccruedEvent
    // arrived later in the replay and superseded the pending synth (the
    // happy path — process didn't actually crash, just the synth got
    // queued first by ER replay ordering). Tag `reconciled` is false
    // when FinalizeReplay had to materialise the synth because no
    // durable fee event was found — that IS the crash-window case and
    // ops should be alerted when this fires above baseline noise.
    public static readonly Counter<long> FeeReplaySynth =
        Meter.CreateCounter<long>("trading.fees.replay_synth");

    // Q2.4 (#271). P&L engine.
    // pnl.realized_appended — bumped on every successful RealizedPnlEvent
    // append via the dispatcher path (live, NOT replay). pnl.replay_synth
    // mirrors the FeeKeeper synth metric: tag reconciled=true when a
    // durable RealizedPnlEvent superseded a pending synth (happy path),
    // false when FinalizeReplay had to materialise the synth (the actual
    // ER-then-crash window). pnl.endpoint_requests counts /pnl/today
    // hits.
    public static readonly Counter<long> PnlRealizedAppended =
        Meter.CreateCounter<long>("trading.pnl.realized_appended");
    public static readonly Counter<long> PnlReplaySynth =
        Meter.CreateCounter<long>("trading.pnl.replay_synth");
    public static readonly Counter<long> PnlEndpointRequests =
        Meter.CreateCounter<long>("trading.pnl.endpoint_requests");

    // Q2.5 (#272). Daily statement endpoint counters.
    // statement.endpoint_requests is tagged with {format=json|csv} so
    // operators can split JSON consumers from CSV exports. The
    // day_trade_detected gauge increments per request that returned at
    // least one IR day-trade row (informational only; not driving any
    // tax collection on the platform).
    public static readonly Counter<long> StatementEndpointRequests =
        Meter.CreateCounter<long>("trading.statement.endpoint_requests");
    public static readonly Counter<long> StatementDayTradeDetected =
        Meter.CreateCounter<long>("trading.statement.day_trade_detected");
    // Pass-1 review (#278) P1#1. Bumped once per (endClient, symbol)
    // row when StateSnapshotter restores a legacy snapshot whose
    // PnlAvgCost block is empty but Positions has rows — the avg-cost
    // basis is reconstructed from the position's AverageEntryPrice so
    // a subsequent close still realises against the carried basis.
    // A non-zero count just means the platform restored from a
    // pre-#271 snapshot at least once.
    public static readonly Counter<long> PnlLegacySnapshotBasisSeeded =
        Meter.CreateCounter<long>("trading.pnl.legacy_snapshot_basis_seeded");
    // Pass-2 review (#278) P1#2. Bumped per (endClient, symbol) row
    // skipped by SeedAvgCostFromLegacyPositions because the legacy
    // position carries a zero AverageEntryPrice — seeding such a
    // degenerate row would realize phantom P&L against a zero basis
    // on the first close after restore. A non-zero count flags a
    // snapshot containing position rows the host could not derive a
    // basis from (operator follow-up).
    public static readonly Counter<long> PnlLegacySnapshotBasisSkippedZero =
        Meter.CreateCounter<long>("trading.pnl.legacy_snapshot_basis_skipped_zero");
    // Pass-4 review (#278) P2#3. Bumped per (endClient, symbol) row
    // when Restore observes the same key in BOTH PnlAvgCost AND
    // PnlUnknownBasis (a malformed snapshot — the two collections are
    // mutually exclusive by construction in the live keeper). Recovery
    // applies a "prefer unknown" policy: the avg-cost entry is dropped
    // so subsequent fills go through the unknown-basis path (realising
    // 0 instead of phantom against the stale basis), and the metric
    // surfaces the inconsistency for ops to investigate the snapshot
    // writer.
    public static readonly Counter<long> PnlSnapshotBasisInconsistent =
        Meter.CreateCounter<long>("trading.pnl.snapshot_basis_inconsistent");
    // Pass-1 review (#278) P1#3. Bumped each time the refprice
    // fan-out coalesced one or more (subscriber, symbol) updates into
    // a single pnl.me delta publish under the per-symbol throttle.
    public static readonly Counter<long> PnlRefPricePublishes =
        Meter.CreateCounter<long>("trading.pnl.refprice_publishes");
    public static readonly Counter<long> PnlRefPriceThrottled =
        Meter.CreateCounter<long>("trading.pnl.refprice_throttled");

    // WebSocket fan-out
    public static readonly UpDownCounter<int> WsConnectionsActive =
        Meter.CreateUpDownCounter<int>("trading.ws.connections.active");
    public static readonly Counter<long> WsMessagesSent =
        Meter.CreateCounter<long>("trading.ws.messages.sent");

    // Drain
    public static readonly Counter<long> DrainRejections =
        Meter.CreateCounter<long>("trading.drain.rejections");
    // RFC §5.3.2 / P8 / F3. Bumped each time the per-FIXP-connection
    // outbound drain loop ignored cancellation for >250 ms past the
    // configured shutdown timeout and the writer abandoned it
    // (FixpOutboundChannelWriter.WaitForDrainAsync). Sibling of the
    // existing structured warning log of the same name; the log
    // remains the source of truth for the `connectionId` field, the
    // counter is intentionally untagged to keep cardinality bounded
    // (one series per process, no per-connection labels). Issue #233.
    public static readonly Counter<long> FixpOutboundDrainShutdownAbandoned =
        Meter.CreateCounter<long>("trading.fixp.outbound.drain.shutdown.abandoned");

    // Algo engine (RFC algo-orders-v0 §7 C1)
    public static readonly Counter<long> AlgoSignalsConsumed =
        Meter.CreateCounter<long>("trading.algo.signals_consumed");
    // Producer-side back-pressure: the bounded signal channel rejected a
    // write because it was full. Should be flat at zero in healthy
    // operation; non-zero indicates a stuck consumer or a runaway loop.
    public static readonly Counter<long> AlgoSignalsDropped =
        Meter.CreateCounter<long>("trading.algo.signals_dropped");
    // RFC §5.2 / F2. Bumped each time the WS hub fan-out per-sink channel
    // overflows and DropOldest evicts an event. Non-zero means the WS hub
    // drain thread can't keep up with the dispatcher; subscribers should
    // observe sequence gaps and reconnect-and-replay (existing WS path).
    public static readonly Counter<long> WsHubFanOutDropped =
        Meter.CreateCounter<long>("trading.dispatcher.ws_fanout_dropped");
    // Child orders submitted by the engine on behalf of an algo parent.
    // Tagged by algo type so iceberg vs twap are distinguishable.
    public static readonly Counter<long> AlgoChildrenSubmitted =
        Meter.CreateCounter<long>("trading.algo.children_submitted");

    // TWAP scheduler observability (RFC §4.11). Jitter is the wall-clock
    // delay between the deterministic plannedAtUtc and the moment the
    // scheduler actually enqueued the slice signal — captures both
    // scheduler-tick granularity (~100ms) and consumer back-pressure.
    public static readonly Histogram<double> AlgoTwapSliceFireJitter =
        Meter.CreateHistogram<double>(
            "trading.algo.twap.slice_fire_jitter",
            unit: "ms",
            description: "now − plannedAtUtc at the moment the scheduler fires a TWAP slice.");
    // How long a single scheduler tick takes end-to-end. Surfaces "the
    // scheduler is starting to spend non-trivial time in its tick" before
    // tick interval starts to slip.
    public static readonly Histogram<double> AlgoSchedulerTickDuration =
        Meter.CreateHistogram<double>(
            "trading.algo.scheduler.tick_duration",
            unit: "ms",
            description: "Wall-clock duration of a single AlgoScheduler tick.");
    // Live depth of the algo signal channel. Healthy operation has this
    // close to zero; a sustained climb means the consumer is falling
    // behind the scheduler/ER hot path.
    public static readonly UpDownCounter<long> AlgoSignalQueueDepth =
        Meter.CreateUpDownCounter<long>(
            "trading.algo.signal_queue_depth",
            description: "Estimated number of signals queued for the AlgoEngine consumer.");

    /// <summary>
    /// 1 when the host booted with <c>Trading:Exchange:AllowErInjection=true</c>
    /// (admin-gated <c>POST /admin/simulator/er</c> is mapped). Set once at
    /// startup and never decremented (config is fixed at runtime). Surfaces
    /// "this host accepts synthetic ERs" to dashboards / alerts so any
    /// production drift is loud — replaces the old
    /// <c>trading.simulator.mode_active</c> + <c>trading.simulator.mode_deprecated</c>
    /// pair after #163 collapsed Simulator into Mock.
    /// </summary>
    public static readonly UpDownCounter<int> ErInjectionEnabled =
        Meter.CreateUpDownCounter<int>("trading.er_injection.enabled");

    // EntryPoint upstream client (real adapter)
    public static readonly UpDownCounter<int> EntryPointConnected =
        Meter.CreateUpDownCounter<int>("trading.entrypoint.connected");
    public static readonly Counter<long> EntryPointEventsReceived =
        Meter.CreateCounter<long>("trading.entrypoint.events_received");
    public static readonly Counter<long> EntryPointReconnectAttempts =
        Meter.CreateCounter<long>("trading.entrypoint.reconnect_attempts");
    public static readonly Counter<long> EntryPointReconnectSucceeded =
        Meter.CreateCounter<long>("trading.entrypoint.reconnect_succeeded");
    public static readonly Counter<long> EntryPointReconnectFailed =
        Meter.CreateCounter<long>("trading.entrypoint.reconnect_failed");

    /// <summary>
    /// Slice 2 of #132. Counts orders auto-marked stale by the
    /// <c>OrderStaleningVenueReactor</c> after a venue desync signal
    /// (FIXP <c>InboundGapAtReconnect</c> or peer-terminate when the
    /// <c>Trading:AutoStale:OnPeerTerminate</c> flag is on). Tagged
    /// <c>{firm, reason}</c>; one Add per bulk-mark with the count of
    /// orders flipped, so a sum gives total ghost candidates.
    /// </summary>
    public static readonly Counter<long> OrdersAutoStaledByVenueDesync =
        Meter.CreateCounter<long>("trading.entrypoint.orders_auto_staled");

    /// <summary>
    /// #153. Counts cash-margin reservation Restore calls (admin
    /// clear-stale path) that pushed the per-owner reserved figure
    /// above the resolved base capacity. Restore intentionally never
    /// fails (an admin clear must succeed once the WAL event is
    /// committed) so overcommit is the safety valve — operators
    /// should monitor this counter and reconcile by cancelling stale
    /// orders. Tagged <c>{owner}</c>.
    /// </summary>
    /// <summary>
    /// #108 — DuplicateClOrdID guard. Counts pre-flight rejections
    /// where <see cref="ClOrdIdPrefixRegistry"/> generated a ClOrdID
    /// that collided with an existing entry in
    /// <see cref="WorkingOrderBook"/> (or, for modify, also in
    /// <see cref="PendingReplacementRegistry"/>). This counter MUST
    /// be flat at zero in healthy operation; non-zero indicates a
    /// registry/snapshot/WAL-replay regression where the per-end-client
    /// counter watermark fell behind the persisted state — alert and
    /// investigate. Tagged <c>{op=submit|modify, scope=book|pending}</c>.
    /// </summary>
    public static readonly Counter<long> ClOrdIdDuplicateDetected =
        Meter.CreateCounter<long>("trading.orders.duplicate_clordid");

    /// <summary>
    /// ClOrdID registry corruption detector (#157). Fires when WAL
    /// replay observes a structurally invalid ClOrdID or a per-end-client
    /// prefix mismatch (same end-client mapped to a different prefix
    /// than the snapshot/restore path remembers). MUST be flat at zero
    /// in healthy operation; non-zero means manual investigation —
    /// schema migration, partial snapshot, or registry bug.
    /// Tagged <c>{end_client, reason=invalid_observed_clordid|prefix_mismatch}</c>.
    /// </summary>
    public static readonly Counter<long> ClOrdIdRegistryCorruption =
        Meter.CreateCounter<long>("trading.orders.clordid_registry_corruption");

    /// <summary>
    /// #160. Counts AlgoIdRegistry watermark-advance refusals during WAL
    /// replay (mirror of <see cref="ClOrdIdRegistryCorruption"/> for the
    /// per-firm AlgoId counter). MUST be flat at zero in healthy
    /// operation. Tagged <c>{firm, reason=invalid_observed_algoid}</c>.
    /// </summary>
    public static readonly Counter<long> AlgoIdRegistryCorruption =
        Meter.CreateCounter<long>("trading.algos.algoid_registry_corruption");

    public static readonly Counter<long> MarginOvercommitOnRestore =
        Meter.CreateCounter<long>("trading.risk.margin_overcommit_on_restore");

    /// <summary>
    /// #153. Counts margin-side stale-transition failures (the
    /// <see cref="OrderStalenessService"/> committed the WAL event and
    /// then could not apply <see cref="ExecKind.Suspended"/> /
    /// <see cref="ExecKind.Restored"/> to the in-process reservation
    /// ledger). The WAL state stays authoritative and the next
    /// process restart reconstructs reservations from scratch via ER
    /// replay, so this is a recoverable inconsistency — but a
    /// non-zero rate signals a code bug or a misbehaving sink.
    /// Tagged <c>{kind}</c>.
    /// </summary>
    public static readonly Counter<long> MarginStaleTransitionFailed =
        Meter.CreateCounter<long>("trading.risk.margin_stale_transition_failed");

    /// <summary>
    /// Memory-growth observability for the cash-margin reservation
    /// ledger (#153 follow-up). Tagged <c>{state=active|suspended}</c>:
    /// <list type="bullet">
    /// <item><description><c>active</c>: live working / partially-filled orders that hold cash in <c>_reserved</c>.</description></item>
    /// <item><description><c>suspended</c>: stale-flagged orders whose cash was released but whose tracking entry stays so a future <see cref="ExecKind.Restored"/> can re-acquire. A sustained climb of this count signals stale orders that admin never cleared and the venue never terminalized — the dictionary leaks until host restart.</description></item>
    /// </list>
    /// Source registered by the host once at startup via
    /// <see cref="RegisterMarginReservationCountsSource"/>; absent
    /// when the provider is not wired (tests, NoOp mode).
    /// </summary>
    private static volatile Func<(int Active, int Suspended)>? _marginReservationCountsSource;
    public static readonly ObservableGauge<int> MarginReservations =
        Meter.CreateObservableGauge<int>(
            "trading.risk.margin_reservations",
            () =>
            {
                var src = _marginReservationCountsSource;
                if (src is null) return Array.Empty<Measurement<int>>();
                var (active, suspended) = src();
                return new[]
                {
                    new Measurement<int>(active, new KeyValuePair<string, object?>("state", "active")),
                    new Measurement<int>(suspended, new KeyValuePair<string, object?>("state", "suspended")),
                };
            });
    public static void RegisterMarginReservationCountsSource(Func<(int Active, int Suspended)> source) =>
        _marginReservationCountsSource = source;
    // Last SessionVerId successfully Established for the firm. Reported as
    // an observable gauge so a stuck reconnect (gauge frozen while attempts
    // counter climbs) is visible at a glance.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _sessionVerIdByFirm = new();
    public static readonly ObservableGauge<long> EntryPointSessionVerId =
        Meter.CreateObservableGauge<long>(
            "trading.entrypoint.session_ver_id",
            () => _sessionVerIdByFirm.Select(kv =>
                new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("firm", kv.Key))));
    public static void RecordSessionVerId(string firmId, uint verId) =>
        _sessionVerIdByFirm[firmId] = verId;

    // Per-firm FIXP wire-protocol state, sourced from the SDK's FixpClientState.
    // Emitted as a one-hot gauge: for each firm, exactly one row has value 1
    // (the current state) and the rest are 0. Source callbacks are pull-based
    // so the metric always reflects the live SDK state on each scrape rather
    // than a stale push from the last transition.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        Func<IEnumerable<KeyValuePair<string, int>>>> _sessionStateSources = new();
    public static readonly ObservableGauge<int> EntryPointSessionState =
        Meter.CreateObservableGauge<int>(
            "trading.entrypoint.session_state",
            () => _sessionStateSources.SelectMany(firm =>
                firm.Value().Select(row => new Measurement<int>(
                    row.Value,
                    new KeyValuePair<string, object?>("firm", firm.Key),
                    new KeyValuePair<string, object?>("state", row.Key)))));
    public static void RegisterSessionStateSource(string firmId, Func<IEnumerable<KeyValuePair<string, int>>> source) =>
        _sessionStateSources[firmId] = source;
    public static void UnregisterSessionStateSource(string firmId) =>
        _sessionStateSources.TryRemove(firmId, out _);

    // Per-firm "is the gateway currently inside its reconnect loop" flag.
    // Combined with session_state, this distinguishes "SDK terminated and
    // we're actively trying to bring it back" (reconnecting=1) from
    // "SDK terminated, gave up / no peer" (reconnecting=0).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<int>> _reconnectingByFirm = new();
    public static readonly ObservableGauge<int> EntryPointReconnecting =
        Meter.CreateObservableGauge<int>(
            "trading.entrypoint.reconnecting",
            () => _reconnectingByFirm.Select(kv =>
                new Measurement<int>(kv.Value(), new KeyValuePair<string, object?>("firm", kv.Key))));
    public static void RegisterReconnectingSource(string firmId, Func<int> source) =>
        _reconnectingByFirm[firmId] = source;
    public static void UnregisterReconnectingSource(string firmId) =>
        _reconnectingByFirm.TryRemove(firmId, out _);

    // Inbound seqnum gap detected by our defensive check on top of the SDK's
    // own retransmit handling. A non-zero rate indicates the SDK delivered an
    // out-of-order or skipped batch — not necessarily fatal (SDK may still
    // recover internally) but worth alerting on.
    public static readonly Counter<long> EntryPointGapDetected =
        Meter.CreateCounter<long>("trading.entrypoint.gap_detected");
    // Companion to gap_detected — a duplicate / out-of-order replay we
    // dropped (or the ER processor will idempotently dedup).
    public static readonly Counter<long> EntryPointDuplicateInbound =
        Meter.CreateCounter<long>("trading.entrypoint.duplicate_inbound");

    // Order-entry latency probes. Two histograms tagged by firm + op
    // (submit/cancel/replace) so dashboards can isolate cancel-side from
    // submit-side wire performance:
    //
    //   * order_entry_call_ms — duration of the local SDK await (network
    //     write + SDK serialization). Only successful calls are recorded;
    //     failures are already tracked by OrdersGatewayFailed.
    //   * order_entry_to_ack_ms — submit-to-first-ER round trip. The probe
    //     starts the timer BEFORE the SDK await to avoid losing samples
    //     when the ER arrives before the await completes (full-duplex).
    //
    // BusinessReject is not surfaced to the probe because it lacks the
    // ClOrdID needed for correlation; those rejections are still counted
    // by EntryPointBusinessRejects.
    public static readonly Histogram<double> OrderEntryCallMs =
        Meter.CreateHistogram<double>("trading.entrypoint.order_entry_call_ms");
    public static readonly Histogram<double> OrderEntryToAckMs =
        Meter.CreateHistogram<double>("trading.entrypoint.order_entry_to_ack_ms");

    public static readonly Counter<long> EntryPointTranslationErrors =
        Meter.CreateCounter<long>("trading.entrypoint.translation_errors");
    public static readonly Counter<long> EntryPointBusinessRejects =
        Meter.CreateCounter<long>("trading.entrypoint.business_rejects");
    public static readonly Counter<long> EntryPointTerminated =
        Meter.CreateCounter<long>("trading.entrypoint.terminated");

    // MarketData consumer (B3.MarketData.WebSocketClient)
    public static readonly Counter<long> MarketDataSubscribeErrors =
        Meter.CreateCounter<long>("trading.marketdata.subscribe_errors");

    // Reference-price observability for the price-collar check (slice 5).
    //
    // RefPriceLookups counts every IReferencePrice.Lookup made by the
    // collar, tagged with (symbol, source) where source is one of
    // "live" | "fallback" | "missing". A sustained drop in source=live
    // (or rise in source=fallback) is the signal that the live MD feed
    // has degraded and the collar is now leaning on the static config
    // table — which is fail-open by design and worth alerting on.
    public static readonly Counter<long> RefPriceLookups =
        Meter.CreateCounter<long>("trading.risk.refprice.lookups");

    // Counts the cases where PriceCollarCheck approved an order purely
    // because it could not obtain any reference price for the symbol.
    // These are unguarded orders from the collar's perspective; tag is
    // the symbol so a single misconfigured ticker doesn't get lost in
    // the aggregate.
    public static readonly Counter<long> CollarBypassedNoReference =
        Meter.CreateCounter<long>("trading.risk.collar.bypassed_no_reference");

    // Q1.2 (#254). Counts the cases where StopTriggerCheck approved a
    // Stop* order purely because it could not obtain a reference price
    // for the symbol — the StopPrice > 0 invariant still ran, but the
    // Buy>=ref / Sell<=ref relation was skipped. Tagged by symbol so
    // ops can spot the coverage gap before it hides a fat-finger stop.
    public static readonly Counter<long> StopCheckSkippedNoRef =
        Meter.CreateCounter<long>("trading.risk.stop_check_skipped_no_ref");

    // Per-symbol age (seconds) of the last live MD update held in the
    // MarketDataReferencePrice cache. Sourced from a callback registered
    // by the provider on construction (singleton). Symbols that have
    // never been observed simply don't appear — no synthetic samples.
    private static readonly System.Collections.Concurrent.ConcurrentBag<Func<IEnumerable<KeyValuePair<string, double>>>> _refPriceStalenessSources = new();
    public static readonly ObservableGauge<double> RefPriceStalenessSeconds =
        Meter.CreateObservableGauge<double>(
            "trading.risk.refprice.staleness_seconds",
            () => _refPriceStalenessSources.SelectMany(src => src()).Select(kv =>
                new Measurement<double>(kv.Value, new KeyValuePair<string, object?>("symbol", kv.Key))));
    public static void RegisterRefPriceStalenessSource(
        Func<IEnumerable<KeyValuePair<string, double>>> source) =>
        _refPriceStalenessSources.Add(source);

    // Slice 7 — throttle/limit observability. All gauges are
    // intentionally aggregate (no per-end-client tag) to keep
    // observability cardinality bounded under tenant churn.
    // Per-tenant detail is exposed via GET /admin/risk/throttle.

    /// <summary>
    /// Bumped when <see cref="Risk.Accounting.RollingNotionalAccountant"/>
    /// cannot price a market order because no reference price exists
    /// for the symbol — the order is recorded with notional 0
    /// (fail-open). A sustained non-zero rate means the rolling cap
    /// is being silently underestimated for those symbols.
    /// </summary>
    public static readonly Counter<long> RollingNotionalBypassedNoReference =
        Meter.CreateCounter<long>("trading.risk.rolling_notional.bypassed_no_reference");

    private static volatile Func<int>? _rollingNotionalActiveBucketsEc;
    private static volatile Func<int>? _rollingNotionalActiveBucketsFirm;
    public static readonly ObservableGauge<int> RollingNotionalActiveBuckets =
        Meter.CreateObservableGauge<int>(
            "trading.risk.rolling_notional.active_buckets",
            () =>
            {
                var ec = _rollingNotionalActiveBucketsEc?.Invoke() ?? 0;
                var fm = _rollingNotionalActiveBucketsFirm?.Invoke() ?? 0;
                return new[]
                {
                    new Measurement<int>(ec, new KeyValuePair<string, object?>("scope", "end_client")),
                    new Measurement<int>(fm, new KeyValuePair<string, object?>("scope", "firm")),
                };
            });
    public static void RegisterRollingNotionalSources(Func<int> endClient, Func<int> firm)
    {
        _rollingNotionalActiveBucketsEc = endClient;
        _rollingNotionalActiveBucketsFirm = firm;
    }

    private static volatile Func<int>? _orderRateActiveBucketsEc;
    private static volatile Func<int>? _orderRateActiveBucketsFirm;
    public static readonly ObservableGauge<int> OrderRateActiveBuckets =
        Meter.CreateObservableGauge<int>(
            "trading.risk.order_rate.active_buckets",
            () =>
            {
                var ec = _orderRateActiveBucketsEc?.Invoke() ?? 0;
                var fm = _orderRateActiveBucketsFirm?.Invoke() ?? 0;
                return new[]
                {
                    new Measurement<int>(ec, new KeyValuePair<string, object?>("scope", "end_client")),
                    new Measurement<int>(fm, new KeyValuePair<string, object?>("scope", "firm")),
                };
            });
    public static void RegisterOrderRateSources(Func<int> endClient, Func<int> firm)
    {
        _orderRateActiveBucketsEc = endClient;
        _orderRateActiveBucketsFirm = firm;
    }

    // Issue #234 — build-info gauges for perf-v0 tunables.
    //
    // Both gauges report the *runtime configured value* of the
    // associated <c>IOptionsMonitor&lt;T&gt;</c> binding, so a config
    // reload (file-watcher or IConfigurationRoot.Reload()) is
    // reflected on the next scrape without a host restart. The source
    // callbacks are intentionally untagged: one series per process,
    // no high-cardinality labels — these are config drift signals,
    // not per-tenant operational metrics. See
    // <c>docs/ops/perf-v0-alerts.md</c> §1.1 for the matching
    // PromQL drift rules.
    private static volatile Func<double>? _outboundDrainShutdownTimeoutSecondsSource;
    public static readonly ObservableGauge<double> FixpOutboundDrainShutdownTimeoutSeconds =
        Meter.CreateObservableGauge<double>(
            "trading.entrypoint_listener.outbound_drain_shutdown_timeout",
            () =>
            {
                var src = _outboundDrainShutdownTimeoutSecondsSource;
                return src is null
                    ? Array.Empty<Measurement<double>>()
                    : new[] { new Measurement<double>(src()) };
            },
            unit: "s",
            description: "Configured EntryPointListener:Buffers:OutboundDrainShutdownTimeout (seconds). Build-info-style gauge sourced from IOptionsMonitor; reflects config reloads.");
    public static void RegisterOutboundDrainShutdownTimeoutSource(Func<double> sourceSeconds) =>
        _outboundDrainShutdownTimeoutSecondsSource = sourceSeconds;

    private static volatile Func<int>? _groupCommitMaxRecordsSource;
    public static readonly ObservableGauge<int> PersistenceGroupCommitMaxRecords =
        Meter.CreateObservableGauge<int>(
            "trading.persistence.group_commit_max_records",
            () =>
            {
                var src = _groupCommitMaxRecordsSource;
                return src is null
                    ? Array.Empty<Measurement<int>>()
                    : new[] { new Measurement<int>(src()) };
            },
            unit: "records",
            description: "Configured Trading:Persistence:GroupCommitMaxRecords. Build-info-style gauge sourced from IOptionsMonitor; reflects config reloads.");
    public static void RegisterGroupCommitMaxRecordsSource(Func<int> source) =>
        _groupCommitMaxRecordsSource = source;
}
