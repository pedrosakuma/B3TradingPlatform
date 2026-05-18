using System.Diagnostics;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace B3.Trading.Infrastructure.Persistence;

/// <summary>
/// Drives recovery at process startup: load the latest snapshot, then
/// replay every WAL event with <c>seq &gt; snapshot.seq</c>. Runs
/// synchronously before the host begins accepting traffic — wired by
/// <c>Program.cs</c> via <see cref="RunAsync"/> right after
/// <c>WebApplication.Build()</c> and before <c>app.Run()</c>.
/// </summary>
public sealed class PersistenceRecovery
{
    private readonly IEventStore _store;
    private readonly StateSnapshotter _snapshotter;
    private readonly EventReplayer _replayer;
    private readonly SnapshotStore _snapshots;
    private readonly ILogger<PersistenceRecovery> _logger;

    public PersistenceRecovery(
        IEventStore store,
        StateSnapshotter snapshotter,
        EventReplayer replayer,
        SnapshotStore snapshots,
        ILogger<PersistenceRecovery> logger)
    {
        _store = store;
        _snapshotter = snapshotter;
        _replayer = replayer;
        _snapshots = snapshots;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        long since = 0;
        var snap = _snapshots.LoadLatest();
        if (snap is not null)
        {
            _snapshotter.Restore(snap);
            since = snap.Seq;
            _logger.LogInformation("Persistence recovery: restored snapshot at seq={Seq} ({Orders} orders, {Positions} positions).",
                snap.Seq, snap.WorkingOrders.Count, snap.Positions.Count);
        }
        else
        {
            _logger.LogInformation("Persistence recovery: no snapshot found; full WAL replay.");
        }

        var replayed = 0;
        await foreach (var (seq, evt) in _store.ReadFromAsync(since, ct).ConfigureAwait(false))
        {
            _replayer.Apply(seq, evt);
            replayed++;
        }
        // Q2.3 (#270) pass-3. After draining the WAL, materialise any
        // ER-fill fee synths that were not superseded by a durable
        // FeeAccruedEvent — these are the true ER-append-then-crash
        // window cases. Surviving entries are counted on
        // trading.fees.replay_synth{reconciled=false}.
        var synthesised = _replayer.FinalizeReplay();
        if (synthesised > 0)
        {
            _logger.LogWarning(
                "Persistence recovery: materialised {Count} fee synths with no matching FeeAccruedEvent (crash window).",
                synthesised);
        }
        MetricsRegistry.RecoveryEventsReplayed.Add(replayed);
        _logger.LogInformation("Persistence recovery: replayed {Count} events past seq={Since}.", replayed, since);
    }
}

/// <summary>
/// Periodic snapshot writer. Acquires the dispatcher lock briefly to
/// capture a consistent <c>(seq, state)</c> view, then writes the
/// snapshot to disk outside the lock.
/// </summary>
public sealed class SnapshotService : Microsoft.Extensions.Hosting.BackgroundService
{
    private readonly EventDispatcher _dispatcher;
    private readonly StateSnapshotter _snapshotter;
    private readonly SnapshotStore _store;
    private readonly TimeSpan _interval;
    private readonly ILogger<SnapshotService> _logger;

    private readonly bool _enabled;

    public SnapshotService(
        EventDispatcher dispatcher,
        StateSnapshotter snapshotter,
        SnapshotStore store,
        IOptions<PersistenceOptions> opts,
        ILogger<SnapshotService> logger)
    {
        _dispatcher = dispatcher;
        _snapshotter = snapshotter;
        _store = store;
        _interval = opts.Value.SnapshotInterval;
        _enabled = opts.Value.Enabled;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                TryTakeSnapshot();
            }
        }
        finally
        {
            // Final snapshot on graceful shutdown so the next boot is fast.
            TryTakeSnapshot();
        }
    }

    public void TryTakeSnapshot()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Two-phase capture (RFC §5.8 / P6).
            //
            // Phase 1 — under the dispatcher lock — captures only the
            // raw arrays of point-in-time values. No projection, no
            // sorting, no enum→string formatting, no DTO allocation.
            // This keeps the lock-hold within the F8 budget (≤ 1ms p99
            // at 50k working orders) and preserves §4.3 by construction:
            // Order/Algo/Position mutable scalars are captured by value
            // into the per-element raw structs while still holding the
            // lock, so the projection step never re-reads the live
            // aggregate after the lock is released.
            //
            // Phase 2 — outside the dispatcher lock — runs the
            // expensive projection (per-DTO allocation, OrderBy sort,
            // enum.ToString, final List<T> materialisation) and then
            // the disk write.
            RawPlatformSnapshot? raw = null;
            _dispatcher.WithSnapshotLock(seq => raw = _snapshotter.CaptureRaw(seq));
            if (raw is null) return;
            var snap = StateSnapshotter.Project(raw);
            _store.Write(snap);
            sw.Stop();
            MetricsRegistry.SnapshotsTaken.Add(1);
            MetricsRegistry.SnapshotDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogDebug("Snapshot written at seq={Seq}.", snap.Seq);
        }
        catch (Exception ex)
        {
            MetricsRegistry.SnapshotsFailed.Add(1);
            _logger.LogError(ex, "Snapshot attempt failed; will retry on next interval.");
        }
    }
}
