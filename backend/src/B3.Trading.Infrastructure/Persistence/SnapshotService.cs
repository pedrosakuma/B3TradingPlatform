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
            _replayer.Apply(evt);
            replayed++;
            _ = seq;
        }
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
        try
        {
            PlatformSnapshot? snap = null;
            _dispatcher.WithSnapshotLock(seq => snap = _snapshotter.Capture(seq));
            if (snap is null) return;
            _store.Write(snap);
            _logger.LogDebug("Snapshot written at seq={Seq}.", snap.Seq);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot attempt failed; will retry on next interval.");
        }
    }
}
