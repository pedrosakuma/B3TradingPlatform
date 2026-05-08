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

    // WebSocket fan-out
    public static readonly UpDownCounter<int> WsConnectionsActive =
        Meter.CreateUpDownCounter<int>("trading.ws.connections.active");
    public static readonly Counter<long> WsMessagesSent =
        Meter.CreateCounter<long>("trading.ws.messages.sent");

    // Drain
    public static readonly Counter<long> DrainRejections =
        Meter.CreateCounter<long>("trading.drain.rejections");

    // Algo engine (RFC algo-orders-v0 §7 C1)
    public static readonly Counter<long> AlgoSignalsConsumed =
        Meter.CreateCounter<long>("trading.algo.signals_consumed");
    // Producer-side back-pressure: the bounded signal channel rejected a
    // write because it was full. Should be flat at zero in healthy
    // operation; non-zero indicates a stuck consumer or a runaway loop.
    public static readonly Counter<long> AlgoSignalsDropped =
        Meter.CreateCounter<long>("trading.algo.signals_dropped");
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
    /// 1 when the host booted with <c>ExchangeMode.Simulator</c> active, 0
    /// otherwise. Set once at startup and never decremented (mode is fixed
    /// at runtime). Surfaces "this host is injecting synthetic ERs" to
    /// dashboards / alerts so production drift is loud.
    /// </summary>
    public static readonly UpDownCounter<int> SimulatorModeActive =
        Meter.CreateUpDownCounter<int>("trading.simulator.mode_active");

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
}
