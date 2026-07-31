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
/// durable — the GET /api/pnl/today endpoint and the WS push channel
/// project it on the fly from <see cref="PositionKeeper"/> +
/// <c>IReferencePrice</c>.
/// </para>
/// </summary>
public sealed class PnlKeeper
{
    /// <summary>
    /// PR #316 P1. Sentinel firm id used when a call site has not yet
    /// been migrated to the firm-aware API (legacy overloads, snapshots
    /// pre-dating the firm dimension on owner-keyed state).
    /// </summary>
    /// <summary>
    /// Sentinel firm bucket. Matches <see cref="PositionKeeper.DefaultFirmId"/>
    /// (PR #316 P1) so legacy WAL events and snapshot DTOs lacking a
    /// firm tag converge on the same bucket as the master keepers.
    /// </summary>
    public const string DefaultFirmId = "DEFAULT";

    /// <summary>
    /// PR #316 P1. Case-insensitive normalisation at the keeper boundary
    /// (see <see cref="PositionKeeper.NormalizeFirmId"/> for rationale).
    /// </summary>
    internal static string Norm(string firmId) =>
        string.IsNullOrEmpty(firmId) ? DefaultFirmId : firmId.ToUpperInvariant();

    /// <summary>Per-(firm, end-client, symbol, day) cumulative realized total.</summary>
    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string Symbol, DateOnly Day), decimal> _realizedByDay = new();

    /// <summary>
    /// Per-(firm, end-client, symbol) avg-cost basis: tracked here in PARALLEL
    /// to <see cref="PositionKeeper"/> so a P&amp;L-only snapshot+restore
    /// path (no <see cref="PositionKeeper"/> rehydration) round-trips. In
    /// production both keepers receive every fill and stay in lockstep;
    /// the parallel track defends against a future refactor that splits
    /// the snapshots.
    /// </summary>
    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string Symbol), AvgCostState> _avgCost = new();

    /// <summary>
    /// Pass-3 review (#278) P1. Per-(firm, end-client, symbol) net quantity
    /// for positions whose basis is UNKNOWN — i.e. seeded from a legacy
    /// <see cref="PositionSnapshot"/> row whose <c>AverageEntryPrice</c>
    /// was zero (pre-#271 snapshot format). Keys here are mutually
    /// exclusive with <see cref="_avgCost"/>: a key in this set has no
    /// usable avg-price, so <see cref="ApplyFillToAvgCost(string, string, string, OrderSide, long, decimal)"/>
    /// realises 0 for any fill against it (no phantom P&amp;L from an
    /// invented basis); fills only adjust the unknown qty until the
    /// position goes flat (entry removed → next fresh fill establishes
    /// a real basis via the normal opening path) or flips through zero
    /// (the closing portion realises 0; the residual opens fresh at
    /// the fill price as a real basis — matching the standard avg-cost
    /// convention for sign flips on a known basis).
    /// </summary>
    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string Symbol), long> _unknownBasisQty = new();

    private readonly ConcurrentDictionary<string, byte> _seenExecutionIds = new();
    private readonly ConcurrentDictionary<string, PendingReplaySynth> _pendingReplaySynths = new();

    private readonly record struct PendingReplaySynth(
        string FirmId, string EndClientId, string Symbol, OrderSide Side,
        long FillQuantity, decimal FillPrice, DateTimeOffset TimestampUtc,
        long PreFillQuantity, decimal PreFillAvgPrice, string? SubAccountId);

    public sealed record AvgCostState(long NetQuantity, decimal AvgPrice);

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #4). Discriminates which of <see cref="_avgCost"/> /
    /// <see cref="_unknownBasisQty"/> a <see cref="PnlSymbolBasisSnapshot"/>
    /// was captured from, so <see cref="RestoreSymbolBasis"/> writes
    /// back to the correct dictionary instead of collapsing an
    /// unknown-basis leg into a known one (or vice versa).
    /// </summary>
    public enum PnlBasisKind
    {
        /// <summary>No row in either dictionary — never traded, or flat with no legacy leg.</summary>
        Absent,
        /// <summary>A known avg-cost basis row in <see cref="_avgCost"/>.</summary>
        Known,
        /// <summary>An unknown-basis leftover quantity row in <see cref="_unknownBasisQty"/>.</summary>
        UnknownQty,
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #4). Point-in-time capture of one (firm, endClient, symbol)
    /// basis cell, produced by <see cref="CaptureSymbolBasis"/> and
    /// consumed by <see cref="RestoreSymbolBasis"/>. <see cref="AvgPrice"/>
    /// is always <c>0m</c> for <see cref="PnlBasisKind.UnknownQty"/> and
    /// <see cref="PnlBasisKind.Absent"/> (there is no price to carry).
    /// </summary>
    public readonly record struct PnlSymbolBasisSnapshot(PnlBasisKind Kind, long NetQuantity, decimal AvgPrice)
    {
        public static readonly PnlSymbolBasisSnapshot Absent = new(PnlBasisKind.Absent, 0L, 0m);
    }

    // --- Firm-aware public surface (PR #316 P1) ----------------------

    public decimal GetDayRealized(string firmId, string endClient, string symbol, DateOnly day) =>
        _realizedByDay.TryGetValue((Norm(firmId), endClient, symbol, day), out var v) ? v : 0m;

    /// <summary>Sum of realized totals across every (symbol, day) for the (firm, end-client) on the given day.</summary>
    public decimal GetDayRealizedTotal(string firmId, string endClient, DateOnly day)
    {
        var norm = Norm(firmId);
        var sum = 0m;
        foreach (var kv in _realizedByDay)
            if (kv.Key.EndClient == endClient && kv.Key.Day == day
                && string.Equals(kv.Key.FirmId, norm, StringComparison.Ordinal))
                sum += kv.Value;
        return sum;
    }

    public IEnumerable<(string Symbol, decimal Realized)> ForEndClientDay(string firmId, string endClient, DateOnly day)
    {
        var norm = Norm(firmId);
        foreach (var kv in _realizedByDay)
            if (kv.Key.EndClient == endClient && kv.Key.Day == day
                && string.Equals(kv.Key.FirmId, norm, StringComparison.Ordinal))
                yield return (kv.Key.Symbol, kv.Value);
    }

    public AvgCostState? GetAvgCost(string firmId, string endClient, string symbol) =>
        _avgCost.TryGetValue((Norm(firmId), endClient, symbol), out var s) ? s : null;

    public long GetUnknownBasisQty(string firmId, string endClient, string symbol) =>
        _unknownBasisQty.TryGetValue((Norm(firmId), endClient, symbol), out var q) ? q : 0;

    // --- Legacy (no-firm) overloads — delegate to DefaultFirmId.
    // Preserves test-host compatibility and any call site we haven't
    // migrated yet. Owner-scoped REST/WS read paths MUST use the
    // firm-aware variants above to avoid cross-firm leaks.
    public decimal GetDayRealized(string endClient, string symbol, DateOnly day) =>
        GetDayRealized(DefaultFirmId, endClient, symbol, day);

    public decimal GetDayRealizedTotal(string endClient, DateOnly day) =>
        GetDayRealizedTotal(DefaultFirmId, endClient, day);

    public IEnumerable<(string Symbol, decimal Realized)> ForEndClientDay(string endClient, DateOnly day) =>
        ForEndClientDay(DefaultFirmId, endClient, day);

    public AvgCostState? GetAvgCost(string endClient, string symbol) =>
        GetAvgCost(DefaultFirmId, endClient, symbol);

    public long GetUnknownBasisQty(string endClient, string symbol) =>
        GetUnknownBasisQty(DefaultFirmId, endClient, symbol);

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
        // PR #316 P1. Legacy WAL events (pre-firm-dimension) carry a
        // null FirmId — they hydrate into the DefaultFirmId bucket so
        // a snapshot+tail recovery built off old segments lands in the
        // same legacy slice the no-firm Restore path produces.
        var firmId = Norm(evt.FirmId ?? DefaultFirmId);
        var key = (firmId, evt.EndClientId, evt.Symbol, evt.DayKey);
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
    /// Pass-2 review (#278) P1#1. Serialisation is provided by the
    /// caller (the dispatcher lock on both the live and the
    /// WAL-backpressure fallback paths — see
    /// <see cref="EventDispatcher.RunExclusive"/>). No per-key lock is
    /// taken here: an inner per-key lock under the dispatcher lock is
    /// fine in isolation, but the previous design also acquired the
    /// per-key lock OUTSIDE the dispatcher lock on the fallback path
    /// (router caught WalBackpressureException → no dispatcher lock →
    /// processor took per-key lock → nested Dispatch took dispatcher
    /// lock), creating a classic AB-BA inversion against the live
    /// path. Eliminating the per-key lock removes the inversion;
    /// dispatcher serialisation is sufficient because all live ER
    /// processing flows through it.
    /// </para>
    /// </summary>
    public decimal ApplyFillToAvgCost(string endClient, string symbol, OrderSide side, long fillQuantity, decimal fillPrice) =>
        ApplyFillToAvgCost(DefaultFirmId, endClient, symbol, side, fillQuantity, fillPrice);

    public decimal ApplyFillToAvgCost(string firmId, string endClient, string symbol, OrderSide side, long fillQuantity, decimal fillPrice)
    {
        if (fillQuantity <= 0) return 0m;
        var normFirm = Norm(firmId);
        var key = (normFirm, endClient, symbol);

        // Pass-3 review (#278) P1. Unknown-basis path: legacy snapshot
        // seeded a quantity but no usable avg price, so we cannot
        // compute realized against an invented basis without surfacing
        // phantom P&L. Realise 0 unconditionally and adjust the
        // unknown qty by the fill (signed by side):
        //   * if newQty == 0  → position is flat: drop the unknown
        //     entry. The next fresh fill goes through the opening
        //     branch below and establishes a real basis at its price.
        //   * if newQty flips sign (e.g. legacy long 100 + sell 150
        //     → short 50): the closing portion (|current|) realises 0
        //     against the unknown basis; the residual (|newQty|)
        //     opens fresh at the fill price as a KNOWN basis. This
        //     matches the standard avg-cost convention for sign flips
        //     on a known basis (Position.ApplyFill / ProjectAvgCost),
        //     and is safe because the residual leg is fully attributable
        //     to the current fill.
        //   * otherwise (same direction, or shrink without flip):
        //     update the unknown qty in place. No real basis is
        //     formed yet — only a flat-then-reopen sequence resets
        //     the basis to known.
        if (_unknownBasisQty.TryGetValue(key, out var unknownQty))
        {
            var signedFill = side == OrderSide.Buy ? fillQuantity : -fillQuantity;
            var newQty = unknownQty + signedFill;
            if (newQty == 0)
            {
                _unknownBasisQty.TryRemove(key, out _);
            }
            else if (Math.Sign(newQty) != Math.Sign(unknownQty))
            {
                _unknownBasisQty.TryRemove(key, out _);
                _avgCost[key] = new AvgCostState(newQty, fillPrice);
            }
            else
            {
                _unknownBasisQty[key] = newQty;
            }
            return 0m;
        }

        if (!_avgCost.TryGetValue(key, out var current))
        {
            var signed = side == OrderSide.Buy ? fillQuantity : -fillQuantity;
            _avgCost[key] = new AvgCostState(signed, fillPrice);
            return 0m;
        }
        var realized = ComputeRealizedDelta(current.NetQuantity, current.AvgPrice, side, fillQuantity, fillPrice);
        var projected = ProjectAvgCost(current, side, fillQuantity, fillPrice);
        if (projected.NetQuantity == 0)
        {
            _avgCost.TryRemove(key, out _);
        }
        else
        {
            _avgCost[key] = projected;
        }
        return realized;
    }

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
    /// #671/#753 (RFC PR 1, code-review addendum). Companion to
    /// <see cref="PositionKeeper.SetAbsolute"/>: replaces the tracked
    /// avg-cost basis for (<paramref name="firmId"/>,
    /// <paramref name="endClient"/>, <paramref name="symbol"/>) with an
    /// ABSOLUTE (<paramref name="netQuantity"/>,
    /// <paramref name="averageEntryPrice"/>) pair outright, discarding
    /// any prior accumulated basis — known (<see cref="_avgCost"/>) or
    /// unknown (<see cref="_unknownBasisQty"/>) — so the two keepers
    /// never drift out of lockstep after an admin position adjustment.
    /// A zero <paramref name="netQuantity"/> CLEARS the basis entirely
    /// (a flat position carries no cost basis) rather than leaving a
    /// stale <c>(0, 0m)</c> entry behind.
    ///
    /// <para>
    /// Does not touch <see cref="_realizedByDay"/>: an absolute
    /// position overwrite resets the basis going forward, it does not
    /// retroactively realize or unwind P&amp;L already booked against
    /// the prior basis (that stays exactly as recorded).
    /// </para>
    ///
    /// <para>
    /// Must be invoked in the SAME dispatcher-serialised apply as
    /// <see cref="PositionKeeper.SetAbsolute"/> (see
    /// <c>AdminEndpoints.HandlePositionAdjustment</c> and the
    /// <c>PositionAdjustmentEvent</c> replay case in
    /// <c>EventReplayer.Apply</c>) so the two keepers' state transitions
    /// for a given adjustment are never observed interleaved with a
    /// concurrent mutation of either keeper alone.
    /// </para>
    ///
    /// <para>
    /// Invariant re-checked here as defense-in-depth (mirrors
    /// <see cref="PositionKeeper.SetAbsolute"/> exactly): zero
    /// <paramref name="netQuantity"/> requires zero
    /// <paramref name="averageEntryPrice"/>; non-zero requires a
    /// strictly positive average entry price.
    /// </para>
    /// </summary>
    public void SetAbsoluteAvgCost(string endClient, string symbol, long netQuantity, decimal averageEntryPrice) =>
        SetAbsoluteAvgCost(DefaultFirmId, endClient, symbol, netQuantity, averageEntryPrice);

    public void SetAbsoluteAvgCost(string firmId, string endClient, string symbol, long netQuantity, decimal averageEntryPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (netQuantity == 0 && averageEntryPrice != 0m)
            throw new ArgumentException("averageEntryPrice must be 0 when netQuantity is 0", nameof(averageEntryPrice));
        if (netQuantity != 0 && averageEntryPrice <= 0m)
            throw new ArgumentException("averageEntryPrice must be > 0 when netQuantity is non-zero", nameof(averageEntryPrice));

        var key = (Norm(firmId), endClient, symbol);
        // An absolute overwrite always establishes (or clears) a KNOWN
        // basis — drop any stale unknown-basis leg unconditionally.
        _unknownBasisQty.TryRemove(key, out _);
        if (netQuantity == 0)
        {
            _avgCost.TryRemove(key, out _);
            return;
        }
        _avgCost[key] = new AvgCostState(netQuantity, averageEntryPrice);
    }

    /// <summary>
    /// #671/#753 (RFC: admin account reset, PR 3, code-review addendum
    /// #4). Precise, discriminated capture of a single (firm,
    /// endClient, symbol) basis cell across the THREE mutually
    /// exclusive states this keeper represents: a KNOWN avg-cost basis
    /// (<see cref="_avgCost"/>), an UNKNOWN-basis leftover quantity
    /// (<see cref="_unknownBasisQty"/> — see its class-level remarks),
    /// or true ABSENCE (never traded, or already flat with no legacy
    /// leg). <see cref="GetAvgCost(string, string, string)"/> alone
    /// cannot distinguish the latter two — both read back <c>null</c>
    /// — which is exactly the gap that made a naive
    /// <c>SetAbsoluteAvgCost(before?.NetQuantity ?? 0, ...)</c>
    /// rollback silently wipe a legacy unknown-basis leg to true zero
    /// instead of restoring it. Exclusively for rollback-precision use
    /// by the admin reset endpoint; paired with
    /// <see cref="RestoreSymbolBasis"/>.
    /// </summary>
    public PnlSymbolBasisSnapshot CaptureSymbolBasis(string firmId, string endClient, string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var key = (Norm(firmId), endClient, symbol);
        if (_avgCost.TryGetValue(key, out var known))
            return new PnlSymbolBasisSnapshot(PnlBasisKind.Known, known.NetQuantity, known.AvgPrice);
        if (_unknownBasisQty.TryGetValue(key, out var unknownQty) && unknownQty != 0)
            return new PnlSymbolBasisSnapshot(PnlBasisKind.UnknownQty, unknownQty, 0m);
        return PnlSymbolBasisSnapshot.Absent;
    }

    /// <summary>
    /// Rollback companion to <see cref="CaptureSymbolBasis"/>: restores
    /// EXACTLY the captured state, never routing through
    /// <see cref="SetAbsoluteAvgCost(string, string, string, long, decimal)"/>
    /// (which unconditionally treats "no known basis" as "clear the
    /// unknown-basis leg too" — correct for a genuine reset, wrong for
    /// a rollback that must undo one). Each branch below writes to
    /// exactly the one dictionary the captured
    /// <see cref="PnlSymbolBasisSnapshot.Kind"/> belongs in and removes
    /// any stale entry from the other, preserving the two dictionaries'
    /// mutual-exclusivity invariant.
    /// </summary>
    public void RestoreSymbolBasis(string firmId, string endClient, string symbol, PnlSymbolBasisSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var key = (Norm(firmId), endClient, symbol);
        switch (snapshot.Kind)
        {
            case PnlBasisKind.Known:
                _unknownBasisQty.TryRemove(key, out _);
                _avgCost[key] = new AvgCostState(snapshot.NetQuantity, snapshot.AvgPrice);
                break;
            case PnlBasisKind.UnknownQty:
                _avgCost.TryRemove(key, out _);
                _unknownBasisQty[key] = snapshot.NetQuantity;
                break;
            default:
                _avgCost.TryRemove(key, out _);
                _unknownBasisQty.TryRemove(key, out _);
                break;
        }
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
        DateTimeOffset timestampUtc, long preFillQuantity, decimal preFillAvgPrice) =>
        RegisterPendingReplaySynth(DefaultFirmId, executionId, endClientId, symbol,
            side, fillQuantity, fillPrice, timestampUtc, preFillQuantity, preFillAvgPrice, subAccountId: null);

    public void RegisterPendingReplaySynth(
        string firmId, string executionId, string endClientId, string symbol,
        OrderSide side, long fillQuantity, decimal fillPrice,
        DateTimeOffset timestampUtc, long preFillQuantity, decimal preFillAvgPrice) =>
        RegisterPendingReplaySynth(firmId, executionId, endClientId, symbol,
            side, fillQuantity, fillPrice, timestampUtc, preFillQuantity, preFillAvgPrice, subAccountId: null);

    /// <summary>
    /// PR #316 P1.2. Overload that carries the originating
    /// <see cref="SubAccountId"/> through to <see cref="FinalizeReplay"/>
    /// so the materialised delta can be folded into the per-bucket
    /// realised total in <see cref="SubAccountPnlKeeper"/>. Without
    /// this, a sub-account fill whose <see cref="RealizedPnlEvent"/>
    /// did not survive the ER-then-crash window would leak its realised
    /// delta into the aggregate keeper only — the per-bucket total
    /// (which is what <c>?subAccount=A</c> reads) would silently drift
    /// from the live path.
    /// </summary>
    public void RegisterPendingReplaySynth(
        string firmId, string executionId, string endClientId, string symbol,
        OrderSide side, long fillQuantity, decimal fillPrice,
        DateTimeOffset timestampUtc, long preFillQuantity, decimal preFillAvgPrice,
        string? subAccountId)
    {
        ArgumentNullException.ThrowIfNull(executionId);
        if (_seenExecutionIds.ContainsKey(executionId)) return;
        _pendingReplaySynths.TryAdd(executionId,
            new PendingReplaySynth(Norm(firmId), endClientId, symbol, side, fillQuantity, fillPrice,
                timestampUtc, preFillQuantity, preFillAvgPrice, subAccountId));
    }

    /// <summary>
    /// Materialises any pending replay synths for which no durable
    /// <see cref="RealizedPnlEvent"/> arrived during recovery — the true
    /// ER-then-crash window. Each surviving entry is folded into totals
    /// using the pre-fill snapshot captured at registration time so the
    /// outcome is deterministic from position state. Increments
    /// <c>pnl.replay_synth{reconciled=false}</c> per materialised row.
    ///
    /// <para>
    /// PR #316 P1.2. When <paramref name="subAccountPnl"/> is wired,
    /// each materialised delta whose <see cref="PendingReplaySynth.SubAccountId"/>
    /// is non-null is ALSO folded into the per-bucket realised total
    /// so the sub-account view matches the live path. Aggregate-only
    /// callers (test fixtures using the no-argument overload below)
    /// retain the original behaviour.
    /// </para>
    /// </summary>
    public int FinalizeReplay() => FinalizeReplay(subAccountPnl: null);

    public int FinalizeReplay(SubAccountPnlKeeper? subAccountPnl)
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
                var key = (p.FirmId, p.EndClientId, p.Symbol, day);
                _realizedByDay.AddOrUpdate(key, delta, (_, current) => current + delta);
                if (subAccountPnl is not null && p.SubAccountId is { } sa)
                    subAccountPnl.Add(p.FirmId, p.EndClientId, new SubAccountId(sa), p.Symbol, day, delta);
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
            buf[n++] = new PnlRealizedRaw(pairs[i].Key.EndClient, pairs[i].Key.Symbol, pairs[i].Key.Day, pairs[i].Value, pairs[i].Key.FirmId);
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
            buf[n++] = new PnlAvgCostRaw(pairs[i].Key.EndClient, pairs[i].Key.Symbol, v.NetQuantity, v.AvgPrice, pairs[i].Key.FirmId);
        }
        if (n == buf.Length) return buf;
        var trimmed = new PnlAvgCostRaw[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    /// <summary>
    /// Pass-3 review (#278) P1. Phase-1 (lock-side) capture of the
    /// unknown-basis qty rows. Persisted alongside <see cref="RawSnapshotAvgCost"/>
    /// so a snapshot+tail recovery doesn't lose the "this leg has no
    /// usable basis" fact and re-seed the same degenerate position
    /// from <see cref="PositionSnapshot"/> on every restore.
    /// </summary>
    public PnlUnknownBasisRaw[] RawSnapshotUnknownBasis()
    {
        var pairs = _unknownBasisQty.ToArray();
        if (pairs.Length == 0) return Array.Empty<PnlUnknownBasisRaw>();
        var buf = new PnlUnknownBasisRaw[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            if (pairs[i].Value == 0) continue;
            buf[n++] = new PnlUnknownBasisRaw(pairs[i].Key.EndClient, pairs[i].Key.Symbol, pairs[i].Value, pairs[i].Key.FirmId);
        }
        if (n == buf.Length) return buf;
        var trimmed = new PnlUnknownBasisRaw[n];
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
        IEnumerable<string>? seenExecutionIds = null,
        IEnumerable<PnlUnknownBasisSnapshot>? unknownBasisRows = null)
    {
        ArgumentNullException.ThrowIfNull(realizedByKey);
        ArgumentNullException.ThrowIfNull(avgCostRows);
        _realizedByDay.Clear();
        _avgCost.Clear();
        _unknownBasisQty.Clear();
        _seenExecutionIds.Clear();
        foreach (var kv in realizedByKey)
        {
            if (!TryParseRealizedKey(kv.Key, out var firmId, out var ec, out var sym, out var day)) continue;
            _realizedByDay[(Norm(firmId), ec, sym, day)] = kv.Value;
        }
        foreach (var row in avgCostRows)
        {
            var firmId = Norm(row.FirmId);
            _avgCost[(firmId, row.EndClientId, row.Symbol)] = new AvgCostState(row.NetQuantity, row.AvgPrice);
        }
        if (unknownBasisRows is not null)
            foreach (var row in unknownBasisRows)
                if (row.NetQuantity != 0)
                {
                    var firmId = Norm(row.FirmId);
                    _unknownBasisQty[(firmId, row.EndClientId, row.Symbol)] = row.NetQuantity;
                }
        // Pass-4 review (#278) P2#3. Enforce mutual exclusivity
        // between _avgCost and _unknownBasisQty after restore. The
        // live keeper holds these collections strictly disjoint by
        // construction (ApplyFillToAvgCost moves a key out of one
        // before populating the other), but a malformed snapshot
        // could carry the same key in both blocks. Without this
        // fix-up the Apply path would route fills through the
        // unknown-basis branch (the first check in
        // ApplyFillToAvgCost), then once the unknown leg fully
        // closed the stale _avgCost entry would resurface and the
        // next fill would realise phantom P&L against it.
        //
        // Policy: prefer unknown (best-effort recovery) and surface
        // the inconsistency on a metric so ops can investigate the
        // snapshot writer rather than silently masking the bug.
        if (_unknownBasisQty.Count > 0 && _avgCost.Count > 0)
        {
            foreach (var key in _unknownBasisQty.Keys)
            {
                if (_avgCost.TryRemove(key, out _))
                    Observability.MetricsRegistry.PnlSnapshotBasisInconsistent.Add(1);
            }
        }
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
            // Pass-3 review (#278) P1. A non-flat row with a zero
            // AverageEntryPrice is degenerate (legacy snapshots
            // produced before the position keeper started carrying
            // basis). Previously we simply skipped these rows, but
            // PositionKeeper STILL holds the open leg — so the next
            // sell against an existing long became a synthetic SHORT
            // opening in PnlKeeper at the sell price, and subsequent
            // fills realised phantom P&L against that invented basis.
            //
            // Track the qty as "unknown basis" instead. ApplyFillToAvgCost
            // then realises 0 for any fill against the unknown leg
            // (no phantom P&L), adjusts the unknown qty in place,
            // drops the entry on flat (next fresh fill establishes a
            // real basis), and treats sign-flips as residual-fresh-open
            // at the fill price. The skipped_zero counter still bumps
            // so ops can spot the legacy-recovery transition.
            if (p.AverageEntryPrice <= 0m)
            {
                Observability.MetricsRegistry.PnlLegacySnapshotBasisSkippedZero.Add(1);
                var firmId = Norm(p.FirmId);
                var unknownKey = (firmId, p.EndClientId, p.Symbol);
                if (!_avgCost.ContainsKey(unknownKey))
                    _unknownBasisQty.TryAdd(unknownKey, p.NetQuantity);
                continue;
            }
            var keyFirmId = Norm(p.FirmId);
            var key = (keyFirmId, p.EndClientId, p.Symbol);
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
    /// <c>Dictionary&lt;string, decimal&gt;</c> shape. New format:
    /// <c>{firmId}|{endClient}|{symbol}|{yyyy-MM-dd}</c>. The legacy
    /// 3-segment format (<c>{endClient}|{symbol}|{yyyy-MM-dd}</c>) is
    /// still accepted by <see cref="TryParseRealizedKey(string, out string, out string, out string, out DateOnly)"/>
    /// and parses as <see cref="DefaultFirmId"/>.
    /// </summary>
    public static string FormatRealizedKey(string firmId, string endClient, string symbol, DateOnly day) =>
        Norm(firmId) + "|" + endClient + "|" + symbol + "|" + day.ToString("yyyy-MM-dd");

    /// <summary>Legacy helper — formats as the default-firm bucket.</summary>
    public static string FormatRealizedKey(string endClient, string symbol, DateOnly day) =>
        FormatRealizedKey(DefaultFirmId, endClient, symbol, day);

    public static bool TryParseRealizedKey(string key, out string firmId, out string endClient, out string symbol, out DateOnly day)
    {
        firmId = DefaultFirmId;
        endClient = string.Empty;
        symbol = string.Empty;
        day = default;
        if (string.IsNullOrEmpty(key)) return false;
        var parts = key.Split('|');
        if (parts.Length == 3)
        {
            // Legacy (pre-PR #316) format. Hydrates as DefaultFirmId.
            if (!DateOnly.TryParseExact(parts[2], "yyyy-MM-dd", out day)) return false;
            if (parts[0].Length == 0 || parts[1].Length == 0) return false;
            endClient = parts[0];
            symbol = parts[1];
            return true;
        }
        if (parts.Length == 4)
        {
            if (!DateOnly.TryParseExact(parts[3], "yyyy-MM-dd", out day)) return false;
            if (parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return false;
            firmId = parts[0];
            endClient = parts[1];
            symbol = parts[2];
            return true;
        }
        return false;
    }

    public static bool TryParseRealizedKey(string key, out string endClient, out string symbol, out DateOnly day) =>
        TryParseRealizedKey(key, out _, out endClient, out symbol, out day);
}
