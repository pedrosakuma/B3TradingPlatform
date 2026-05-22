using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;

namespace B3.Trading.Application;

/// <summary>
/// Q2.3 (#270). Per-end-client / per-day total fees projected from the
/// <see cref="FeeAccruedEvent"/> WAL stream. Drives the daily statement
/// (#272) and feeds the P&amp;L pipeline (#271).
///
/// <para>
/// <b>Idempotence.</b> Each <see cref="FeeAccruedEvent.ExecutionId"/>
/// is the deterministic combination of <c>ClOrdId + cumulative quantity
/// after the fill</c> (see <see cref="FeeAccruedEvent.ExecutionId"/>);
/// the keeper guards <see cref="Apply"/> with a seen-set so re-applying
/// the same event (FIXP retransmit, WAL replay) cannot double-charge
/// the running totals. The seen-set is captured into the snapshot too —
/// that way a snapshot+tail recovery ends in the same state as a
/// WAL-only replay.
/// </para>
///
/// <para>
/// <b>Day boundary.</b> The day key is derived from
/// <c>FeeAccruedEvent.TimestampUtc</c> as <c>DateOnly.FromDateTime(ts.UtcDateTime)</c> —
/// UTC by construction (matches every other audit timestamp in the
/// platform; the BR session boundary handling lives in the statement
/// projection, not in the keeper).
/// </para>
/// </summary>
public sealed class FeeKeeper
{
    private readonly ConcurrentDictionary<(string EndClient, DateOnly Day), decimal> _totals = new();
    private readonly ConcurrentDictionary<string, byte> _seenExecutionIds = new();
    /// <summary>
    /// #387. Optional cash sink: when wired, every successful (non-
    /// duplicate) fee application also debits the end-client's free
    /// cash via <see cref="CashLedger.ApplyFee"/>. Optional so tests
    /// and the daily-statement projection (which only consumes the
    /// keeper's totals) can construct a FeeKeeper without a cash
    /// dependency.
    /// </summary>
    private readonly CashLedger? _cash;

    public FeeKeeper(CashLedger? cash = null)
    {
        _cash = cash;
    }
    /// <summary>
    /// Pass-3 review (#277). Holds ER-synthesised fee placeholders
    /// during a replay run. The WAL sequencing guarantees a durable
    /// <see cref="FeeAccruedEvent"/> (if it exists) follows its ER, so
    /// we defer the synth until the entire WAL has been drained and
    /// then materialise only those without a matching durable event —
    /// the actual crash-window cases. The reconciled path keeps the
    /// persisted breakdown (computed under the original
    /// <c>FeeOptions</c> snapshot), so a hot-reload between the
    /// original run and recovery cannot silently change historical
    /// fees.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingReplaySynth> _pendingReplaySynths = new();

    /// <summary>
    /// Captured inputs to the deterministic fee calculator for a
    /// fill ER observed during replay. Materialised at
    /// <see cref="FinalizeReplay"/> if no durable
    /// <see cref="FeeAccruedEvent"/> arrived in the meantime.
    /// </summary>
    private readonly record struct PendingReplaySynth(
        string EndClientId, string Symbol, B3.Trading.Domain.OrderSide Side,
        long FillQuantity, decimal FillPrice, DateTimeOffset TimestampUtc);

    public decimal GetDayTotal(string endClient, DateOnly day) =>
        _totals.TryGetValue((endClient, day), out var t) ? t : 0m;

    /// <summary>
    /// Folds <paramref name="evt"/> into the running totals. Idempotent
    /// on <see cref="FeeAccruedEvent.ExecutionId"/>: a re-applied event
    /// with the same id is a no-op. Returns <c>true</c> when the event
    /// advanced the totals; <c>false</c> on a duplicate.
    /// </summary>
    public bool Apply(FeeAccruedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        // A durable fee event always wins over the ER-synth placeholder:
        // remove any pending entry for this ExecutionId so
        // FinalizeReplay won't materialise it. The persisted breakdown
        // becomes authoritative even if FeeOptions changed between runs.
        // When this fires it means the ER replay (or live ER apply on
        // some future code path that pre-registers) had queued a synth
        // that we just superseded — emit reconciled=true so ops can
        // distinguish the harmless replay-ordering case from a true
        // crash-window materialisation (reconciled=false in
        // FinalizeReplay).
        if (_pendingReplaySynths.TryRemove(evt.ExecutionId, out _))
        {
            Observability.MetricsRegistry.FeeReplaySynth.Add(1,
                new KeyValuePair<string, object?>("reconciled", true));
        }
        if (!_seenExecutionIds.TryAdd(evt.ExecutionId, 0)) return false;
        var day = DateOnly.FromDateTime(evt.TimestampUtc.UtcDateTime);
        var key = (evt.EndClientId, day);
        _totals.AddOrUpdate(key, evt.Total, (_, current) => current + evt.Total);
        // #387. Mirror the fee debit into CashLedger so the trader's
        // Available reflects post-fee cash. Gated by the seen-set above,
        // so replay (FeeAccruedEvent re-applied on WAL drain) is a
        // no-op for cash too. Optional dep — when null, only totals
        // advance (test contexts / statement-only deployments).
        _cash?.ApplyFee(new B3.Trading.Domain.EndClientId(evt.EndClientId), evt.Total);
        return true;
    }

    /// <summary>
    /// Pass-3 review (#277). Called from
    /// <see cref="ExecutionReportProcessor.Apply"/> on the replay path
    /// (isReplay=true or no dispatcher) instead of applying a synthetic
    /// fee directly. The synth is deferred so that a durable
    /// <see cref="FeeAccruedEvent"/> arriving later in the same replay
    /// can supersede it via <see cref="Apply(FeeAccruedEvent)"/> —
    /// preserving the original breakdown (and the original FeeOptions
    /// snapshot) under historical recovery.
    ///
    /// If the ExecutionId is already in the seen-set (snapshot+tail
    /// case where the snapshot already recorded this fee) the
    /// registration is a no-op — the totals are already correct.
    /// </summary>
    public void RegisterPendingReplaySynth(
        string executionId, string endClientId, string symbol,
        B3.Trading.Domain.OrderSide side, long fillQuantity, decimal fillPrice,
        DateTimeOffset timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        if (_seenExecutionIds.ContainsKey(executionId)) return;
        _pendingReplaySynths.TryAdd(executionId,
            new PendingReplaySynth(endClientId, symbol, side, fillQuantity, fillPrice, timestampUtc));
    }

    /// <summary>
    /// Pass-3 review (#277). Materialises any pending replay synths
    /// for which no durable <see cref="FeeAccruedEvent"/> arrived
    /// during recovery — i.e. the true ER-append-then-crash window.
    /// Called by <see cref="Infrastructure.Persistence.PersistenceRecovery"/>
    /// at the end of its WAL drain. Each surviving entry is folded
    /// into totals via <paramref name="calculator"/> using the current
    /// <c>FeeOptions</c> snapshot — the documented limitation of the
    /// synth path (a future FeeRateChangedEvent with seq markers would
    /// close it). Increments
    /// <see cref="MetricsRegistry.FeeReplaySynth"/> tagged
    /// <c>reconciled=false</c> for each materialised row;
    /// reconciled-true increments are emitted on the
    /// <see cref="Apply(FeeAccruedEvent)"/> side instead (see that
    /// path).
    /// </summary>
    public int FinalizeReplay(IFeeCalculator calculator)
    {
        ArgumentNullException.ThrowIfNull(calculator);
        if (_pendingReplaySynths.IsEmpty) return 0;
        var materialised = 0;
        foreach (var kv in _pendingReplaySynths.ToArray())
        {
            if (!_pendingReplaySynths.TryRemove(kv.Key, out var p)) continue;
            if (!_seenExecutionIds.TryAdd(kv.Key, 0)) continue;
            var breakdown = calculator.Compute(p.Symbol, p.Side, p.FillQuantity, p.FillPrice);
            var day = DateOnly.FromDateTime(p.TimestampUtc.UtcDateTime);
            var key = (p.EndClientId, day);
            _totals.AddOrUpdate(key, breakdown.Total, (_, current) => current + breakdown.Total);
            // #387. Same cash-debit hook as Apply, executed only for
            // ER-then-crash window survivors. The seen-set TryAdd above
            // is the idempotency gate — a fee that was already in the
            // snapshot (loaded via Restore + seen-set) never reaches
            // here.
            _cash?.ApplyFee(new B3.Trading.Domain.EndClientId(p.EndClientId), breakdown.Total);
            Observability.MetricsRegistry.FeeReplaySynth.Add(1,
                new KeyValuePair<string, object?>("reconciled", false));
            materialised++;
        }
        return materialised;
    }

    /// <summary>
    /// Phase-1 (lock-side) capture for the two-phase snapshot pipeline
    /// (RFC §5.8). Caller must hold <c>EventDispatcher.WithSnapshotLock</c>.
    /// Skips zero rows because they re-materialise on the next event
    /// (same convention as <see cref="CashKeeper.RawSnapshot"/>).
    /// </summary>
    public FeeKeeperRaw[] RawSnapshot()
    {
        var pairs = _totals.ToArray();
        if (pairs.Length == 0) return Array.Empty<FeeKeeperRaw>();
        var buf = new FeeKeeperRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Value == 0m) continue;
            buf[n++] = new FeeKeeperRaw(pairs[i].Key.EndClient, pairs[i].Key.Day, pairs[i].Value);
        }
        if (n == buf.Length) return buf;
        var trimmed = new FeeKeeperRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    /// <summary>
    /// Phase-1 (lock-side) capture of the seen-set for idempotence.
    /// Persisted alongside the totals so a snapshot+tail recovery ends
    /// in the same state as a WAL-only replay (the tail's
    /// <see cref="FeeAccruedEvent"/> rows are filtered through the same
    /// guard, otherwise a snapshot taken after a fill plus a tail
    /// containing that fill's event would double-count).
    /// </summary>
    public string[] RawSnapshotSeenIds()
    {
        var ids = new string[_seenExecutionIds.Count];
        var n = 0;
        foreach (var kv in _seenExecutionIds)
        {
            if (n >= ids.Length) break;
            ids[n++] = kv.Key;
        }
        if (n == ids.Length) return ids;
        var trimmed = new string[n];
        Array.Copy(ids, trimmed, n);
        return trimmed;
    }

    public void Restore(IReadOnlyDictionary<string, decimal> totalsByKey, IEnumerable<string>? seenExecutionIds = null)
    {
        ArgumentNullException.ThrowIfNull(totalsByKey);
        _totals.Clear();
        _seenExecutionIds.Clear();
        foreach (var kv in totalsByKey)
        {
            if (!TryParseKey(kv.Key, out var endClient, out var day)) continue;
            _totals[(endClient, day)] = kv.Value;
        }
        if (seenExecutionIds is not null)
        {
            foreach (var id in seenExecutionIds)
                _seenExecutionIds.TryAdd(id, 0);
        }
    }

    /// <summary>
    /// Composite key serialisation for the snapshot's
    /// <c>Dictionary&lt;string, decimal&gt;</c> shape. Format is
    /// <c>{endClient}|{yyyy-MM-dd}</c>; the pipe is illegal in
    /// end-client ids (the API validator rejects it) so the split is
    /// unambiguous.
    /// </summary>
    public static string FormatKey(string endClient, DateOnly day) =>
        endClient + "|" + day.ToString("yyyy-MM-dd");

    public static bool TryParseKey(string key, out string endClient, out DateOnly day)
    {
        endClient = string.Empty;
        day = default;
        if (string.IsNullOrEmpty(key)) return false;
        var pipe = key.LastIndexOf('|');
        if (pipe <= 0 || pipe == key.Length - 1) return false;
        if (!DateOnly.TryParseExact(key.AsSpan(pipe + 1), "yyyy-MM-dd", out day)) return false;
        endClient = key.Substring(0, pipe);
        return true;
    }
}
