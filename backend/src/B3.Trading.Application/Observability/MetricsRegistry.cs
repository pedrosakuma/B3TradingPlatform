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
    public static readonly Counter<long> EntryPointTranslationErrors =
        Meter.CreateCounter<long>("trading.entrypoint.translation_errors");
    public static readonly Counter<long> EntryPointBusinessRejects =
        Meter.CreateCounter<long>("trading.entrypoint.business_rejects");
    public static readonly Counter<long> EntryPointTerminated =
        Meter.CreateCounter<long>("trading.entrypoint.terminated");
}
