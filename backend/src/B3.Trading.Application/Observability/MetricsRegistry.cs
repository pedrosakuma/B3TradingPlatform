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

    public static readonly Counter<long> EntryPointTranslationErrors =
        Meter.CreateCounter<long>("trading.entrypoint.translation_errors");
    public static readonly Counter<long> EntryPointBusinessRejects =
        Meter.CreateCounter<long>("trading.entrypoint.business_rejects");
    public static readonly Counter<long> EntryPointTerminated =
        Meter.CreateCounter<long>("trading.entrypoint.terminated");

    // MarketData consumer (B3.MarketData.WebSocketClient)
    public static readonly Counter<long> MarketDataSubscribeErrors =
        Meter.CreateCounter<long>("trading.marketdata.subscribe_errors");
}
