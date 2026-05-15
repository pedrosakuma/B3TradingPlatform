using System.Collections.Concurrent;
using B3.Trading.Application.Persistence;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q2.4 (#271). Per-(end-client, symbol, day) cumulative <b>realized</b>
/// P&amp;L projected from the <see cref="RealizedPnlEvent"/> WAL stream
/// + a per-(end-client, symbol) avg-cost basis state used to compute
/// fresh <see cref="RealizedPnlEvent.DeltaRealized"/> values for new
/// fills. Mirrors the snapshot+replay shape of <see cref="FeeKeeper"/>
/// (#270): seen-set for idempotence, per-day bucketing for daily reset
/// without losing historical totals, and a deferred replay-synth path
/// for the ER-then-crash window (#277).
///
/// <para>
/// <b>Avg-cost basis (decision documented in #271).</b> Realized
/// proceeds are computed as <c>(price - avgPrice) * closedQty</c> for
/// long positions and <c>(avgPrice - price) * closedQty</c> for shorts,
/// where <c>closedQty = min(|fillQty|, |preFillQty|)</c> and the
/// remainder (if the fill flipped the position past zero) opens fresh
/// at <see cref="OrderSide"/>'s price — matching
/// <see cref="Position.ApplyFill"/>'s avg-cost reset on flip. <b>Avg
/// price does not change on offsetting fills</b> until the position
/// flips through zero (consistent with <see cref="Position.ApplyFill"/>).
/// </para>
///
/// <para>
/// Unrealized P&amp;L is purely derived
/// (<c>(refPrice - avgPrice) * position</c> for longs;
/// <c>(avgPrice - refPrice) * position</c> for shorts) and is NEVER
/// durable — the GET /pnl/today endpoint and the WS push channel
/// project it on the fly from <see cref="PositionKeeper"/> +
/// <c>IReferencePrice</c>.
/// </para>
/// </summary>
public sealed class PnlKeeper
{
    /// <summary>Per-(end-client, symbol, day) cumulative realized total.</summary>
    private readonly ConcurrentDictionary<(string EndClient, string Symbol, DateOnly Day), decimal> _realizedByDay = new();

    /// <summary>
    /// Per-(end-client, symbol) avg-cost basis: tracked here in PARALLEL
    /// to <see cref="PositionKeeper"/> so a P&amp;L-only snapshot+restore
    /// path (no <see cref="PositionKeeper"/> rehydration) round-trips. In
    /// production both keepers receive every fill and stay in lockstep;
    /// the parallel track defends against a future refactor that splits
    /// the snapshots.
    /// </summary>
    private readonly ConcurrentDictionary<(string EndClient, string Symbol), AvgCostState> _avgCost = new();

    /// <summary>
    /// Pass-1 review (#278) P1#2. Per-(end-client, symbol) lock used to
    /// serialise the whole "compute pre-fill basis → mutate avg-cost →
    /// fold realized delta into running total → produce
    /// <see cref="RealizedPnlEvent"/> → keeper Apply" sequence on the
    /// live path. The dispatcher's process-global lock alone is NOT
    /// sufficient because the WAL-backpressure fallback path
    /// (<c>EntryPointExecutionReportRouter</c>) re-invokes
    /// <c>ExecutionReportProcessor.Apply</c> WITHOUT the dispatcher
    /// lock — two concurrent fills for the same key on that path would
    /// otherwise race and persist inconsistent <c>RunningTotal</c>
    /// values into the WAL.
    ///
    /// <para>
    /// Lock ordering: per-key lock OUTSIDE the dispatcher lock. The
    /// snapshot path takes the dispatcher lock only and never the
    /// per-key locks, so a fill in flight does not block snapshots.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<(string EndClient, string Symbol), object> _keyLocks = new();

    private object LockFor(string endClient, string symbol) =>
        _keyLocks.GetOrAdd((endClient, symbol), static _ => new object());

    /// <summary>
    /// Public hook for callers that need to perform a multi-step
    /// operation atomically per (endClient, symbol) — primarily the
    /// processor's "compute realized + Dispatch + Apply" critical
    /// section. See <see cref="_keyLocks"/> for the rationale.
    /// </summary>
    public T WithKeyLock<T>(string endClient, string symbol, Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (LockFor(endClient, symbol)) return body();
    }

    private readonly ConcurrentDictionary<string, byte> _seenExecutionIds = new();
    private readonly ConcurrentDictionary<string, PendingReplaySynth> _pendingReplaySynths = new();

    private readonly record struct PendingReplaySynth(
        string EndClientId, string Symbol, OrderSide Side,
        long FillQuantity, decimal FillPrice, DateTimeOffset TimestampUtc,
        long PreFillQuantity, decimal PreFillAvgPrice);

    public sealed record AvgCostState(long NetQuantity, decimal AvgPrice);

    public decimal GetDayRealized(string endClient, string symbol, DateOnly day) =>
        _realizedByDay.TryGetValue((endClient, symbol, day), out var v) ? v : 0m;

    /// <summary>Sum of realized totals across every (symbol, day) for the end-client on the given day.</summary>
    public decimal GetDayRealizedTotal(string endClient, DateOnly day)
    {
        var sum = 0m;
        foreach (var kv in _realizedByDay)
            if (kv.Key.EndClient == endClient && kv.Key.Day == day) sum += kv.Value;
        return sum;
    }

    public IEnumerable<(string Symbol, decimal Realized)> ForEndClientDay(string endClient, DateOnly day)
    {
        foreach (var kv in _realizedByDay)
            if (kv.Key.EndClient == endClient && kv.Key.Day == day)
                yield return (kv.Key.Symbol, kv.Value);
    }

    public AvgCostState? GetAvgCost(string endClient, string symbol) =>
        _avgCost.TryGetValue((endClient, symbol), out var s) ? s : null;

    /// <summary>
    /// Pure avg-cost realized-delta calculator. Public so the ER processor
    /// can compute the value BEFORE mutating <see cref="PositionKeeper"/>
    /// (we need the pre-fill (qty, avg) snapshot). Returns 0 for
    /// same-side fills (which only grow the position) and for the
    /// no-position case.
    /// </summary>
    public static decimal ComputeRealizedDelta(
        long preFillQty, decimal preFillAvgPrice,
        OrderSide side, long fillQuantity, decimal fillPrice)
    {
        if (fillQuantity <= 0) return 0m;
        if (preFillQty == 0) return 0m;
        var fillSign = side == OrderSide.Buy ? 1 : -1;
        var posSign = Math.Sign(preFillQty);
        if (fillSign == posSign) return 0m; // same-side fills don't realize

        var closedQty = Math.Min(Math.Abs(preFillQty), fillQuantity);
        // Long closed by sell  → (price - avg) * closedQty
        // Short closed by buy  → (avg - price) * closedQty   (posSign = -1)
        return posSign == 1
            ? (fillPrice - preFillAvgPrice) * closedQty
            : (preFillAvgPrice - fillPrice) * closedQty;
    }

    /// <summary>
    /// Folds <paramref name="evt"/> into the running totals. Idempotent
    /// on <see cref="RealizedPnlEvent.ExecutionId"/>: a re-applied event
    /// with the same id is a no-op. Updates the avg-cost basis state
    /// based on <paramref name="evt"/> when the caller hasn't already
    /// done so via <see cref="ApplyFillToAvgCost"/> — i.e. on replay
    /// from durable events alone, where we project the post-fill avg
    /// from the pre-fill snapshot embedded in the basis tracker.
    /// </summary>
    public bool Apply(RealizedPnlEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (_pendingReplaySynths.TryRemove(evt.ExecutionId, out _))
        {
            Observability.MetricsRegistry.PnlReplaySynth.Add(1,
                new KeyValuePair<string, object?>("reconciled", true));
        }
        if (!_seenExecutionIds.TryAdd(evt.ExecutionId, 0)) return false;
        var key = (evt.EndClientId, evt.Symbol, evt.DayKey);
        // Use the persisted RunningTotal as authoritative — see record
        // doc-comment. A re-projection from the in-memory delta would
        // race a snapshot+tail recovery whose snapshot already baked in
        // an earlier fill but whose avg-cost basis hasn't yet caught up.
        _realizedByDay[key] = evt.RunningTotal;
        return true;
    }

    /// <summary>
    /// Live-path entry point used by <c>ExecutionReportProcessor</c>:
    /// updates the avg-cost basis using the same recomputation as
    /// <see cref="Position.ApplyFill"/> so the keeper's basis tracks
    /// the position keeper without needing to read it back. Returns the
    /// realized delta this fill produced (computed off the pre-fill
    /// state) so the processor can decide whether to append a
    /// <see cref="RealizedPnlEvent"/>.
    ///
    /// <para>
    /// Pass-1 review (#278) P1#2. The mutation runs under the per-key
    /// lock (see <see cref="WithKeyLock{T}"/>), eliminating the
    /// outer-variable capture race that the previous
    /// <c>AddOrUpdate</c>-based implementation had under contention.
    /// </para>
    /// </summary>
    public decimal ApplyFillToAvgCost(string endClient, string symbol, OrderSide side, long fillQuantity, decimal fillPrice)
    {
        if (fillQuantity <= 0) return 0m;
        var key = (endClient, symbol);
        lock (LockFor(endClient, symbol))
        {
            if (!_avgCost.TryGetValue(key, out var current))
            {
                var signed = side == OrderSide.Buy ? fillQuantity : -fillQuantity;
                _avgCost[key] = new AvgCostState(signed, fillPrice);
                return 0m;
            }
            var realized = ComputeRealizedDelta(current.NetQuantity, current.AvgPrice, side, fillQuantity, fillPrice);
            _avgCost[key] = ProjectAvgCost(current, side, fillQuantity, fillPrice);
            return realized;
        }
    }

    /// <summary>
    /// Pass-1 review (#278) P1#2. Atomic per-(endClient, symbol)
    /// "compute realized + advance basis + fold into running total"
    /// primitive used by the live ER path. Runs the supplied
    /// <paramref name="onComputed"/> callback INSIDE the per-key lock
    /// so the caller can append the <see cref="RealizedPnlEvent"/> to
    /// the WAL (via the dispatcher) and call <see cref="Apply"/> back
    /// on this keeper without a second concurrent fill on the same
    /// key racing the running-total computation.
    ///
    /// <para>
    /// The <paramref name="onComputed"/> callback receives a
    /// <see cref="PnlFillContext"/> with the pre-fill snapshot, the
    /// realized delta this fill produced, and the running total AFTER
    /// folding the delta. Both values are derived from state held
    /// under the lock, so they are guaranteed consistent with the
    /// avg-cost mutation already applied.
    /// </para>
    ///
    /// <para>
    /// The avg-cost is mutated regardless of the realized delta. The
    /// callback decides whether the realized delta is worth a WAL
    /// event (matches the existing live-path policy: only non-zero
    /// realized deltas append a <see cref="RealizedPnlEvent"/>).
    /// </para>
    /// </summary>
    public void ApplyFillUnderLock(
        string endClient,
        string symbol,
        OrderSide side,
        long fillQuantity,
        decimal fillPrice,
        DateOnly day,
        Action<PnlFillContext> onComputed)
    {
        ArgumentNullException.ThrowIfNull(onComputed);
        if (fillQuantity <= 0) return;
        var key = (endClient, symbol);
        lock (LockFor(endClient, symbol))
        {
            var pre = _avgCost.TryGetValue(key, out var c) ? c : new AvgCostState(0, 0m);
            var realized = ComputeRealizedDelta(pre.NetQuantity, pre.AvgPrice, side, fillQuantity, fillPrice);
            _avgCost[key] = ProjectAvgCost(pre, side, fillQuantity, fillPrice);
            var prevTotal = _realizedByDay.TryGetValue((endClient, symbol, day), out var t) ? t : 0m;
            var running = prevTotal + realized;
            onComputed(new PnlFillContext(pre.NetQuantity, pre.AvgPrice, realized, running));
        }
    }

    /// <summary>
    /// Snapshot of the per-key state produced inside
    /// <see cref="ApplyFillUnderLock"/>. Carries both the pre-fill
    /// (qty, avg) so the caller can build the WAL event and the
    /// already-computed running total so the persisted
    /// <see cref="RealizedPnlEvent.RunningTotal"/> matches the value
    /// the keeper observed under the lock.
    /// </summary>
    public readonly record struct PnlFillContext(
        long PreFillQuantity,
        decimal PreFillAvgPrice,
        decimal RealizedDelta,
        decimal RunningTotal);

    /// <summary>
    /// Pure projection of the avg-cost state under one fill. Mirrors
    /// <see cref="Position.ApplyFill"/>'s avg-price update rules: same
    /// side grows the average, opposing side keeps it until flip,
    /// flip-through-zero resets to fill price, flat resets to 0.
    /// </summary>
    public static AvgCostState ProjectAvgCost(AvgCostState current, OrderSide side, long quantity, decimal price)
    {
        var signed = side == OrderSide.Buy ? quantity : -quantity;
        var newQty = current.NetQuantity + signed;
        decimal newAvg;
        if (current.NetQuantity == 0 || Math.Sign(current.NetQuantity) == Math.Sign(signed))
        {
            var prior = (decimal)Math.Abs(current.NetQuantity);
            var added = (decimal)quantity;
            var total = prior + added;
            newAvg = total == 0 ? 0m : ((current.AvgPrice * prior) + (price * added)) / total;
        }
        else if (newQty != 0 && Math.Sign(newQty) != Math.Sign(current.NetQuantity))
        {
            newAvg = price;
        }
        else
        {
            newAvg = current.AvgPrice;
        }
        if (newQty == 0) newAvg = 0m;
        return new AvgCostState(newQty, newAvg);
    }

    /// <summary>
    /// Pass-3 review (#277) parallel — defers a replay-time synth so a
    /// durable <see cref="RealizedPnlEvent"/> arriving later in the
    /// drain can supersede it via <see cref="Apply(RealizedPnlEvent)"/>.
    /// The pre-fill (qty, avg) snapshot is captured at registration time
    /// — by the time <see cref="FinalizeReplay"/> runs the avg-cost
    /// state has already been advanced by other replayed events, so we
    /// could not recompute the delta from the live state.
    /// </summary>
    public void RegisterPendingReplaySynth(
        string executionId, string endClientId, string symbol,
        OrderSide side, long fillQuantity, decimal fillPrice,
        DateTimeOffset timestampUtc, long preFillQuantity, decimal preFillAvgPrice)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        if (_seenExecutionIds.ContainsKey(executionId)) return;
        _pendingReplaySynths.TryAdd(executionId,
            new PendingReplaySynth(endClientId, symbol, side, fillQuantity, fillPrice,
                timestampUtc, preFillQuantity, preFillAvgPrice));
    }

    /// <summary>
    /// Materialises any pending replay synths for which no durable
    /// <see cref="RealizedPnlEvent"/> arrived during recovery — the true
    /// ER-then-crash window. Each surviving entry is folded into totals
    /// using the pre-fill snapshot captured at registration time so the
    /// outcome is deterministic from position state. Increments
    /// <c>pnl.replay_synth{reconciled=false}</c> per materialised row.
    /// </summary>
    public int FinalizeReplay()
    {
        if (_pendingReplaySynths.IsEmpty) return 0;
        var materialised = 0;
        foreach (var kv in _pendingReplaySynths.ToArray())
        {
            if (!_pendingReplaySynths.TryRemove(kv.Key, out var p)) continue;
            if (!_seenExecutionIds.TryAdd(kv.Key, 0)) continue;
            var delta = ComputeRealizedDelta(p.PreFillQuantity, p.PreFillAvgPrice, p.Side, p.FillQuantity, p.FillPrice);
            if (delta != 0m)
            {
                var day = DateOnly.FromDateTime(p.TimestampUtc.UtcDateTime);
                var key = (p.EndClientId, p.Symbol, day);
                _realizedByDay.AddOrUpdate(key, delta, (_, current) => current + delta);
            }
            Observability.MetricsRegistry.PnlReplaySynth.Add(1,
                new KeyValuePair<string, object?>("reconciled", false));
            materialised++;
        }
        return materialised;
    }

    // --------- snapshot / restore ---------

    /// <summary>Phase-1 (lock-side) capture of per-day realized totals.</summary>
    public PnlRealizedRaw[] RawSnapshotRealized()
    {
        var pairs = _realizedByDay.ToArray();
        if (pairs.Length == 0) return Array.Empty<PnlRealizedRaw>();
        var buf = new PnlRealizedRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Value == 0m) continue;
            buf[n++] = new PnlRealizedRaw(pairs[i].Key.EndClient, pairs[i].Key.Symbol, pairs[i].Key.Day, pairs[i].Value);
        }
        if (n == buf.Length) return buf;
        var trimmed = new PnlRealizedRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    /// <summary>Phase-1 (lock-side) capture of avg-cost basis rows.</summary>
    public PnlAvgCostRaw[] RawSnapshotAvgCost()
    {
        var pairs = _avgCost.ToArray();
        if (pairs.Length == 0) return Array.Empty<PnlAvgCostRaw>();
        var buf = new PnlAvgCostRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var v = pairs[i].Value;
            if (v.NetQuantity == 0) continue;
            buf[n++] = new PnlAvgCostRaw(pairs[i].Key.EndClient, pairs[i].Key.Symbol, v.NetQuantity, v.AvgPrice);
        }
        if (n == buf.Length) return buf;
        var trimmed = new PnlAvgCostRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

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

    public void Restore(
        IReadOnlyDictionary<string, decimal> realizedByKey,
        IEnumerable<PnlAvgCostSnapshot> avgCostRows,
        IEnumerable<string>? seenExecutionIds = null)
    {
        ArgumentNullException.ThrowIfNull(realizedByKey);
        ArgumentNullException.ThrowIfNull(avgCostRows);
        _realizedByDay.Clear();
        _avgCost.Clear();
        _seenExecutionIds.Clear();
        foreach (var kv in realizedByKey)
        {
            if (!TryParseRealizedKey(kv.Key, out var ec, out var sym, out var day)) continue;
            _realizedByDay[(ec, sym, day)] = kv.Value;
        }
        foreach (var row in avgCostRows)
            _avgCost[(row.EndClientId, row.Symbol)] = new AvgCostState(row.NetQuantity, row.AvgPrice);
        if (seenExecutionIds is not null)
            foreach (var id in seenExecutionIds)
                _seenExecutionIds.TryAdd(id, 0);
    }

    /// <summary>
    /// Pass-1 review (#278) P1#1. Backfills the avg-cost basis from
    /// the snapshot's <see cref="PositionSnapshot"/> rows for every
    /// (endClient, symbol) NOT already populated by the (newer-format)
    /// <see cref="PnlAvgCostSnapshot"/> block. Required for legacy
    /// snapshots taken before #271 shipped: those carry positions but
    /// no PnlAvgCost block, and without this seed the next sell on a
    /// pre-existing position would compute realized off a zero basis
    /// and silently realise nothing.
    ///
    /// <para>
    /// Idempotent: never overwrites an entry that <see cref="Restore"/>
    /// already loaded from <see cref="PnlAvgCostSnapshot"/> (the
    /// PnlKeeper's own snapshot block is authoritative when present).
    /// Skips zero-quantity rows (a flat position carries no basis to
    /// seed and the snapshot writer already drops them).
    /// </para>
    ///
    /// <para>
    /// Each seeded row bumps
    /// <c>trading.pnl.legacy_snapshot_basis_seeded</c> so ops can spot
    /// the legacy-recovery transition. After the next snapshot is
    /// taken, <see cref="RawSnapshotAvgCost"/> includes the seeded
    /// basis and the metric returns to zero on subsequent recoveries.
    /// </para>
    /// </summary>
    public int SeedAvgCostFromLegacyPositions(IEnumerable<PositionSnapshot> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var seeded = 0;
        foreach (var p in positions)
        {
            if (p.NetQuantity == 0) continue;
            var key = (p.EndClientId, p.Symbol);
            if (_avgCost.ContainsKey(key)) continue;
            if (_avgCost.TryAdd(key, new AvgCostState(p.NetQuantity, p.AverageEntryPrice)))
            {
                seeded++;
                Observability.MetricsRegistry.PnlLegacySnapshotBasisSeeded.Add(1);
            }
        }
        return seeded;
    }

    /// <summary>
    /// Composite key serialisation for the snapshot's
    /// <c>Dictionary&lt;string, decimal&gt;</c> shape. Format:
    /// <c>{endClient}|{symbol}|{yyyy-MM-dd}</c>.
    /// </summary>
    public static string FormatRealizedKey(string endClient, string symbol, DateOnly day) =>
        endClient + "|" + symbol + "|" + day.ToString("yyyy-MM-dd");

    public static bool TryParseRealizedKey(string key, out string endClient, out string symbol, out DateOnly day)
    {
        endClient = string.Empty;
        symbol = string.Empty;
        day = default;
        if (string.IsNullOrEmpty(key)) return false;
        var lastPipe = key.LastIndexOf('|');
        if (lastPipe <= 0 || lastPipe == key.Length - 1) return false;
        if (!DateOnly.TryParseExact(key.AsSpan(lastPipe + 1), "yyyy-MM-dd", out day)) return false;
        var firstPipe = key.IndexOf('|');
        if (firstPipe <= 0 || firstPipe == lastPipe) return false;
        endClient = key.Substring(0, firstPipe);
        symbol = key.Substring(firstPipe + 1, lastPipe - firstPipe - 1);
        return true;
    }
}
