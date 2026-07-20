using System.Diagnostics;
using B3.Trading.Application;
using B3.Trading.Application.Audit;
using B3.Trading.Application.Observability;
using B3.Trading.Application.Persistence;
using B3.Trading.Application.Outbound;
using B3.Trading.Application.Risk;
using B3.Trading.Application.Risk.Accounting;
using B3.Trading.Domain;
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
    // Pass-1 review (#322) P1.1. Optional. When wired AND a snapshot
    // is loaded, recovery does a pre-pass over the WAL filtered to
    // AuditLogEvent for seq <= snapshot.Seq so the in-memory audit
    // ring rehydrates the pre-snapshot history that the main replay
    // (which starts at snapshot.Seq+1) cannot see. Cost is O(N) where
    // N is total WAL events; the keeper's bounded ring caps memory.
    private readonly AuditLogKeeper? _auditKeeper;
    // Q4.7 (#307). Optional. When wired AND a snapshot is loaded,
    // recovery does a pre-pass over the WAL filtered to
    // ExecutionReportReceivedEvent for seq <= snapshot.Seq so the
    // in-memory FillProjection rehydrates the pre-snapshot history
    // that the main replay (which starts at snapshot.Seq+1) cannot
    // see. Mirrors the AuditLogKeeper rehydration design.
    private readonly FillProjection? _fillProjection;
    private readonly WorkingOrderBook? _orders;
    private readonly OrderOwnershipMap? _ownership;
    // #380 path B (refined by #419). Optional. When wired AND the
    // loaded snapshot carries per-firm SessionVerIds, recovery compares
    // each firm's current gateway SessionVerId against the stored value
    // after WAL replay. For any firm whose verId has advanced past the
    // snapshot, every confirmed working order attached to that firm is
    // flagged stale (Order.MarkStale) — Cancel/Modify is blocked at the
    // API until a real ER lifts the flag, but the order stays visible
    // in the blotter and accounting because the venue (B3 and the local
    // matching) persists orders across FIXP session rolls. Only
    // never-acked PendingNew orders are retired outright (MarkCancelled):
    // an order whose first ER never returned to us cannot have a
    // matching record on the venue side under any session version.
    private readonly IFirmSessionStatusProvider? _firmSessionStatus;
    private readonly ReserveOnSubmitMarginProvider? _marginProvider;
    private readonly PendingReplacementRegistry? _replacements;
    private readonly PendingCancelRegistry? _pendingCancels;
    private readonly IRiskRecoveryFence[] _riskRecoveryFences;
    private readonly ReconciliationMarkerRecovery? _reconciliationMarkers;
    private readonly ColdStartLifecycleGuard? _coldStartLifecycleGuard;
    private readonly OutboundMutationLedger? _outboundLedger;
    private readonly OutboundProcessEpoch? _processEpoch;
    private readonly TimeProvider _timeProvider;

    public PersistenceRecovery(
        IEventStore store,
        StateSnapshotter snapshotter,
        EventReplayer replayer,
        SnapshotStore snapshots,
        ILogger<PersistenceRecovery> logger,
        AuditLogKeeper? auditKeeper = null,
        FillProjection? fillProjection = null,
        WorkingOrderBook? orders = null,
        OrderOwnershipMap? ownership = null,
        IFirmSessionStatusProvider? firmSessionStatus = null,
        ReserveOnSubmitMarginProvider? marginProvider = null,
        PendingReplacementRegistry? replacements = null,
        IEnumerable<IRiskRecoveryFence>? riskRecoveryFences = null,
        ReconciliationMarkerRecovery? reconciliationMarkers = null,
        ColdStartLifecycleGuard? coldStartLifecycleGuard = null,
        OutboundMutationLedger? outboundLedger = null,
        OutboundProcessEpoch? processEpoch = null,
        TimeProvider? timeProvider = null,
        PendingCancelRegistry? pendingCancels = null)
    {
        _store = store;
        _snapshotter = snapshotter;
        _replayer = replayer;
        _snapshots = snapshots;
        _logger = logger;
        _auditKeeper = auditKeeper;
        _fillProjection = fillProjection;
        _orders = orders;
        _ownership = ownership;
        _firmSessionStatus = firmSessionStatus;
        _marginProvider = marginProvider;
        _replacements = replacements;
        _riskRecoveryFences = riskRecoveryFences?.ToArray()
            ?? Array.Empty<IRiskRecoveryFence>();
        _reconciliationMarkers = reconciliationMarkers;
        _coldStartLifecycleGuard = coldStartLifecycleGuard;
        _outboundLedger = outboundLedger;
        _processEpoch = processEpoch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pendingCancels = pendingCancels;
    }

    public Task RunAsync(CancellationToken ct = default) =>
        RunAsync(initializeReplayBaselines: null, ct);

    public async Task RunAsync(Action? initializeReplayBaselines, CancellationToken ct = default)
    {
        long since = 0;
        var snap = _snapshots.LoadLatest(
            ValidateSnapshot,
            reason => _logger.LogError(
                "Persistence recovery rejected a snapshot candidate: {Reason}",
                reason));
        if (snap is not null)
        {
            _snapshotter.Restore(snap);
            since = snap.Seq;
            _logger.LogInformation("Persistence recovery: restored snapshot at seq={Seq} ({Orders} orders, {Positions} positions).",
                snap.Seq, snap.WorkingOrders.Count, snap.Positions.Count);

            // Pass-1 review (#322) P1.1. Audit-only pre-pass for
            // seq <= snapshot.Seq. The main replay below starts at
            // since+1 and would otherwise leave the audit ring empty
            // of pre-snapshot history (the keeper is not part of the
            // snapshot envelope by design — see AuditLogKeeper doc).
            // The ring is bounded so streaming the full prefix is
            // safe; oldest entries silently fall off the head as the
            // pre-pass scans forward.
            if (_auditKeeper is not null && since > 0)
            {
                var rehydrated = 0;
                await foreach (var (seq, evt) in _store.ReadFromAsync(0, ct).ConfigureAwait(false))
                {
                    if (seq > since) break;
                    if (evt is AuditLogEvent ae)
                    {
                        _auditKeeper.Apply(seq, ae);
                        rehydrated++;
                    }
                }
                _logger.LogInformation("Persistence recovery: rehydrated {Count} audit envelopes from pre-snapshot WAL prefix (cap={Cap}).",
                    rehydrated, _auditKeeper.Capacity);
            }

            // Q4.7 (#307). Fill-projection pre-pass for seq <= snapshot.Seq.
            // The main replay below starts at since+1 and the projection
            // is not part of the snapshot envelope (the WAL is the source
            // of truth — same design as the audit ring), so without this
            // pre-pass every Fill / PartialFill captured before the
            // snapshot would be invisible to GET /fills/{id}/touch after
            // restart. Resolves Symbol / Side / FirmId from the restored
            // WorkingOrderBook and Owner from OrderOwnershipMap — both
            // are restored above. We deliberately do NOT re-run the full
            // processor (that would double-apply positions / fees / PnL);
            // we only repopulate the projection.
            if (_fillProjection is not null && _orders is not null && _ownership is not null && since > 0)
            {
                var rehydratedFills = 0;
                await foreach (var (seq, evt) in _store.ReadFromAsync(0, ct).ConfigureAwait(false))
                {
                    if (seq > since) break;
                    if (evt is not ExecutionReportReceivedEvent er) continue;
                    if (!Enum.TryParse<ExecKind>(er.ExecKind, ignoreCase: true, out var k)) continue;
                    if (k is not ExecKind.Fill and not ExecKind.PartialFill) continue;
                    if (er.LastQuantity <= 0) continue;
                    var lookupId = er.OrigClOrdId != 0 ? er.OrigClOrdId : er.ClOrdId;
                    if (!_orders.TryGet(lookupId, out var order) || order is null) continue;
                    _fillProjection.RecordIfAbsent(
                        clOrdId: lookupId,
                        cumulativeQuantityAfterFill: er.CumulativeQuantity,
                        owner: order.Owner,
                        firmId: order.FirmId,
                        symbol: order.Symbol,
                        side: order.Side,
                        lastQuantity: er.LastQuantity,
                        lastPrice: er.LastPrice,
                        timestampUtc: er.TimestampUtc,
                        bookTouch: er.BookTouch);
                    rehydratedFills++;
                }
                _logger.LogInformation("Persistence recovery: rehydrated {Count} fill touch records from pre-snapshot WAL prefix.",
                    rehydratedFills);
            }
        }
        else
        {
            _logger.LogInformation("Persistence recovery: no snapshot found; full WAL replay.");
        }

        // Configuration-backed opening balances are baselines rather than WAL
        // events. Materialise any missing baseline after snapshot restore but
        // before replay so a fill-only WAL cannot debit an implicit zero
        // balance. SeedIfAbsent keeps restored snapshot balances authoritative.
        initializeReplayBaselines?.Invoke();

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

        var unresolvedMarkers = _reconciliationMarkers?.Apply() ?? 0;
        if (unresolvedMarkers > 0)
        {
            _logger.LogCritical(
                "Persistence recovery found {Count} unresolved outbound reconciliation markers; readiness remains closed.",
                unresolvedMarkers);
        }
        if (_outboundLedger is not null && _processEpoch is not null)
        {
            var legacyMigrationWasCompleted =
                _outboundLedger.LegacyMigrationCompleted;
            var reconciledLegacy = _outboundLedger.ReconcileLegacyPendingState(
                _orders?.Snapshot()
                    .Where(static order => string.Equals(
                        order.Status,
                        nameof(OrderStatus.PendingNew),
                        StringComparison.OrdinalIgnoreCase))
                    .Select(static order => order.ClOrdId)
                    ?? Array.Empty<ulong>(),
                _pendingCancels?.Snapshot()
                    .Select(static pending => pending.CancelClOrdId)
                    ?? Array.Empty<ulong>(),
                _replacements?.Snapshot()
                    .Select(static pending => pending.Intent.NewClOrdId)
                    ?? Array.Empty<ulong>(),
                _timeProvider.GetUtcNow());
            if (reconciledLegacy > 0)
            {
                _logger.LogInformation(
                    "Persistence recovery terminalised {Count} legacy outbound rows whose domain pending projection had already resolved.",
                    reconciledLegacy);
            }
            _outboundLedger.CompleteLegacyMigration();
            if (!legacyMigrationWasCompleted
                && _store.WalGeneration != Guid.Empty)
            {
                await _store.FlushThroughAsync(
                    _store.LastAdmittedSeq, ct).ConfigureAwait(false);
                var migrationSnapshot = StateSnapshotter.Project(
                    _snapshotter.CaptureRaw(
                        _store.LastCommittedSeq,
                        _store.WalGeneration));
                _snapshots.Write(migrationSnapshot);
                snap = migrationSnapshot;
                since = migrationSnapshot.Seq;
                _logger.LogInformation(
                    "Persistence recovery published the outbound legacy-migration checkpoint at seq={Seq}.",
                    migrationSnapshot.Seq);
            }
        }
        var unresolvedLifecycle = _coldStartLifecycleGuard?.Apply() ?? 0;
        if (unresolvedLifecycle > 0)
        {
            _logger.LogCritical(
                "Persistence recovery retained {Count} unresolved lifecycle intents; readiness remains closed.",
                unresolvedLifecycle);
        }

        // #380 path B. Session-version guard. Compares the snapshot's
        // per-firm SessionVerId record against each gateway's current
        // live verId; for any firm whose verId has advanced past the
        // snapshot, every WorkingOrderBook entry attached to that firm
        // is retired (MarkCancelled). Skipped silently when either side
        // is absent: legacy snapshots have no FirmSessionVerIds, Mock /
        // Stub / Unavailable exchange modes have no IFirmSessionStatusProvider.
        if (snap is not null && _firmSessionStatus is not null && _orders is not null)
        {
            ReconcileFirmSessionVerIds(snap);
        }

        if (_marginProvider is not null && _orders is not null)
        {
            var capacityReleasedNewIds = _outboundLedger?.SnapshotMutations()
                .Where(mutation =>
                    mutation.Kind == OutboundMutationKind.New
                    && mutation.OperatorEvidence.Any(evidence => evidence.CapacityReleased))
                .Select(mutation => mutation.PrimaryClOrdId)
                .ToHashSet()
                ?? new HashSet<ulong>();
            var capacityReleasedReplaceIds = _outboundLedger?.SnapshotMutations()
                .Where(mutation =>
                    mutation.Kind == OutboundMutationKind.Replace
                    && mutation.OperatorEvidence.Any(evidence => evidence.CapacityReleased))
                .Select(mutation => mutation.PrimaryClOrdId)
                .ToHashSet()
                ?? new HashSet<ulong>();
            var restoredReservations = _marginProvider.RestoreRecoveryState(
                _orders.Snapshot().Where(order =>
                    !capacityReleasedNewIds.Contains(order.ClOrdId)),
                (_replacements?.Snapshot() ?? Array.Empty<PendingReplacementEntrySnapshot>())
                    .Where(replacement =>
                        !capacityReleasedReplaceIds.Contains(replacement.Intent.NewClOrdId)));
            _logger.LogInformation(
                "Persistence recovery: restored {Orders} order margin reservations and {Replacements} pending-replace reservations.",
                restoredReservations.Orders,
                restoredReservations.Replacements);
        }

        if (snap is not null || replayed > 0)
        {
            foreach (var fence in _riskRecoveryFences)
                fence.EnterRecoveryFence();
            if (_riskRecoveryFences.Length > 0)
            {
                _logger.LogWarning(
                    "Persistence recovery: activated conservative order-rate and rolling-notional restart fences.");
            }
        }
    }

    private SnapshotValidationResult ValidateSnapshot(PlatformSnapshot snapshot)
    {
        if (snapshot.Seq < 0)
            return SnapshotValidationResult.Reject("Snapshot sequence is negative.");

        if (snapshot.FormatVersion == 0)
        {
            if (snapshot.WalGeneration != Guid.Empty
                || snapshot.OutboundLedger is not null)
            {
                return SnapshotValidationResult.Reject(
                    "Legacy snapshot contains versioned lineage or outbound-ledger fields.");
            }
            return snapshot.Seq <= _store.LastCommittedSeq
                ? SnapshotValidationResult.Accept()
                : SnapshotValidationResult.Reject(
                    $"Legacy snapshot seq={snapshot.Seq} is ahead of committed WAL seq={_store.LastCommittedSeq}.");
        }

        if (snapshot.WalGeneration == Guid.Empty)
        {
            return SnapshotValidationResult.Reject(
                "Versioned snapshot has an empty WAL generation.");
        }
        if (snapshot.WalGeneration != _store.WalGeneration)
        {
            return SnapshotValidationResult.Reject(
                $"Snapshot WAL generation {snapshot.WalGeneration} does not match active generation {_store.WalGeneration}.");
        }
        if (snapshot.Seq > _store.LastCommittedSeq)
        {
            return SnapshotValidationResult.Reject(
                $"Snapshot seq={snapshot.Seq} is ahead of committed WAL seq={_store.LastCommittedSeq}.");
        }
        if (snapshot.FormatVersion != PlatformSnapshot.CurrentFormatVersion)
        {
            return SnapshotValidationResult.Fatal(
                $"Snapshot format version {snapshot.FormatVersion} is unsupported for the active WAL lineage; recovery is fail-closed.");
        }
        if (snapshot.OutboundLedger is null)
        {
            return SnapshotValidationResult.Reject(
                "Versioned snapshot is missing its outbound-ledger envelope.");
        }
        if (snapshot.OutboundLedger.Version
                == OutboundLedgerSnapshot.LegacyVersionWithoutInboundEvidence
            && snapshot.OutboundLedger.InboundEvidence.Count == 0)
        {
            return SnapshotValidationResult.Accept();
        }
        if (snapshot.OutboundLedger.Version
                == OutboundLedgerSnapshot.LegacyVersionWithoutInboundEvidence
            && snapshot.OutboundLedger.InboundEvidence.Count > 0)
        {
            return SnapshotValidationResult.Reject(
                "Outbound-ledger snapshot v1 contains inbound evidence with an obsolete payload fingerprint; rebuild from the committed WAL.");
        }
        if (snapshot.OutboundLedger.Version != OutboundLedgerSnapshot.CurrentVersion)
        {
            return SnapshotValidationResult.Fatal(
                $"Outbound-ledger snapshot version {snapshot.OutboundLedger.Version} is unsupported for the active WAL lineage; recovery is fail-closed.");
        }
        return SnapshotValidationResult.Accept();
    }

    private void ReconcileFirmSessionVerIds(PlatformSnapshot snap)
    {
        if (snap.FirmSessionVerIds is null || snap.FirmSessionVerIds.Count == 0)
        {
            // Pre-#380 snapshot — no recorded baseline. Cannot
            // distinguish "venue rolled" from "first restart since
            // the field was added"; skip rather than retire blindly.
            return;
        }

        var currentByFirm = _firmSessionStatus!.Snapshot();
        foreach (var status in currentByFirm)
        {
            if (!snap.FirmSessionVerIds.TryGetValue(status.FirmId, out var storedVerId)
                || storedVerId == 0)
            {
                // No baseline for this firm — first snapshot after the
                // firm was added, or capture happened before the firm
                // ever Established. Skip.
                continue;
            }
            if (status.SessionVerId <= storedVerId)
            {
                // Same or earlier session — orders attached at the
                // stored verId are still valid at the venue.
                continue;
            }

            // Session-version advanced past the snapshot. Per #504, this
            // does NOT imply uncertainty about order state — the FIXP
            // protocol handles synchronization via retransmission during
            // recovery. If no terminal ER (Cancel/Fill) arrives during
            // FIXP recovery, the order is still valid on the venue.
            //
            // PendingNew is preserved too: a session roll is not authoritative
            // venue-absence evidence and cannot release its reservation.
            // Boot reconcile runs single-threaded before app start, so no
            // dispatcher lock is needed here.
            B3.Trading.Application.FirmSessionRollReconciliation.PreservePendingNewForRolledFirm(
                _orders!, status.FirmId, storedVerId, status.SessionVerId, _logger);
        }
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
    private readonly IOutboundRecoveryGate _recovery;

    public SnapshotService(
        EventDispatcher dispatcher,
        StateSnapshotter snapshotter,
        SnapshotStore store,
        IOptions<PersistenceOptions> opts,
        ILogger<SnapshotService> logger,
        IOutboundRecoveryGate? recovery = null)
    {
        _dispatcher = dispatcher;
        _snapshotter = snapshotter;
        _store = store;
        _interval = opts.Value.SnapshotInterval;
        _enabled = opts.Value.Enabled;
        _logger = logger;
        _recovery = recovery ?? ImmediateOutboundRecoveryGate.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await _recovery.WaitUntilClassificationCompleteAsync(stoppingToken)
            .ConfigureAwait(false);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                await TryTakeSnapshotAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Final snapshot on graceful shutdown so the next boot is fast.
            await TryTakeSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public void TryTakeSnapshot() =>
        _ = TryTakeSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task<bool> TryTakeSnapshotAsync(
        CancellationToken cancellationToken = default)
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
            var raw = _dispatcher.CaptureSnapshot(
                context => _snapshotter.CaptureRaw(
                    context.AppliedSeq,
                    context.WalGeneration));

            // The lock is deliberately released before awaiting disk
            // durability. Dispatch/projection can continue and the WAL writer
            // can advance the marker without any dispatcher↔writer deadlock.
            // A failed or cancelled fence publishes no snapshot.
            await _dispatcher.FlushThroughAsync(raw.Seq, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var snap = StateSnapshotter.Project(raw);
            cancellationToken.ThrowIfCancellationRequested();
            _store.Write(snap);
            sw.Stop();
            MetricsRegistry.SnapshotsTaken.Add(1);
            MetricsRegistry.SnapshotDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            _logger.LogDebug("Snapshot written at seq={Seq}.", snap.Seq);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MetricsRegistry.SnapshotsFailed.Add(1);
            _logger.LogDebug(
                "Snapshot attempt cancelled before its committed-prefix fence completed; nothing was published.");
            return false;
        }
        catch (Exception ex)
        {
            MetricsRegistry.SnapshotsFailed.Add(1);
            _logger.LogError(ex, "Snapshot attempt failed; will retry on next interval.");
            return false;
        }
    }
}
