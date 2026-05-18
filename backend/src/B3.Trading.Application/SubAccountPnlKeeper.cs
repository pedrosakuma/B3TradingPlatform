using System.Collections.Concurrent;
using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Q4.1 (#301). Parallel realized-P&amp;L store keyed by
/// <c>(FirmId, EndClientId, SubAccountId, Symbol, Day)</c> for
/// sub-account segregation. Fed by <see cref="ExecutionReportProcessor"/>
/// alongside the master <see cref="PnlKeeper"/> whenever the
/// originating order carries a non-null
/// <see cref="SubAccountId"/>; the master keeper continues to
/// receive every fill (sub-account-null and sub-account-tagged
/// alike) so that endpoint reads without a filter naturally show
/// the aggregate.
///
/// <para>
/// <b>Firm namespace.</b> The same login under FIRM01 and FIRM02
/// with the same <c>SubAccountId</c> MUST NOT share realized P&amp;L
/// — sub-accounts are scoped per-firm, mirroring
/// <see cref="SubAccountsRegistry"/>.
/// </para>
/// </summary>
public sealed class SubAccountPnlKeeper
{
    /// <summary>
    /// PR #316 P2. Sentinel sub-account key for the MASTER bucket
    /// (fills with <c>SubAccountId == null</c>). Master bucket basis
    /// is tracked alongside per-sub bucket basis in
    /// <see cref="_bucketAvgCost"/> so realized-PnL on a master fill
    /// is computed against master-only history, not the aggregate
    /// position. Empty string is safe — <see cref="SubAccountId"/>
    /// rejects empty/whitespace at construction.
    /// </summary>
    public const string MasterBucketKey = "";

    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string SubAccount, string Symbol, DateOnly Day), decimal>
        _realized = new();

    /// <summary>
    /// PR #316 P2. Per-bucket avg-cost basis keyed by
    /// <c>(FirmId, EndClient, SubAccount, Symbol)</c> where
    /// <c>SubAccount</c> is <see cref="MasterBucketKey"/> for master
    /// fills. Each bucket realises P&amp;L against its OWN basis: a
    /// sub-account fill that offsets the aggregate position (because
    /// master is long) realises 0 if the sub-bucket itself has no
    /// prior position — it opens a fresh leg in the sub-bucket. The
    /// master bucket's basis is updated only by
    /// <c>SubAccountId == null</c> fills, so sub activity never
    /// pollutes master avg-cost. The aggregate
    /// <see cref="PnlKeeper"/> still tracks the all-fills basis for
    /// statement / legacy snapshot paths (its returned realized delta
    /// is no longer the authoritative event source, see
    /// <c>ExecutionReportProcessor.OnFill</c>).
    /// </summary>
    private readonly ConcurrentDictionary<(string FirmId, string EndClient, string SubAccount, string Symbol), PnlKeeper.AvgCostState>
        _bucketAvgCost = new();

    /// <summary>
    /// Adds <paramref name="delta"/> to the (firm, owner, subAccount,
    /// symbol, day) bucket. Idempotence is the caller's
    /// responsibility — the master <see cref="PnlKeeper"/> already
    /// dedupes on <see cref="Persistence.RealizedPnlEvent.ExecutionId"/>
    /// and gates its sub-account peer behind the same gate, so a
    /// double-apply here would require a double-apply on the master
    /// keeper too. Tracking a second seen-set would add storage for
    /// no marginal protection.
    /// </summary>
    public void Add(string firmId, string endClient, SubAccountId subAccount, string symbol, DateOnly day, decimal delta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentNullException.ThrowIfNull(subAccount);
        _realized.AddOrUpdate((firmId, endClient, subAccount.Value, symbol, day),
            _ => delta,
            (_, prev) => prev + delta);
    }

    public decimal GetDayRealized(string firmId, string endClient, SubAccountId subAccount, string symbol, DateOnly day) =>
        _realized.TryGetValue((firmId, endClient, subAccount.Value, symbol, day), out var v) ? v : 0m;

    /// <summary>
    /// PR #316 P2. Reads the (firm, owner, bucket, symbol) avg-cost
    /// basis. Pass <c>null</c> for <paramref name="subAccount"/> to
    /// query the master bucket. Returns <c>null</c> for an unseen
    /// bucket (flat position; no basis yet).
    /// </summary>
    public PnlKeeper.AvgCostState? GetBucketAvgCost(string firmId, string endClient, SubAccountId? subAccount, string symbol)
    {
        var key = (firmId, endClient, subAccount?.Value ?? MasterBucketKey, symbol);
        return _bucketAvgCost.TryGetValue(key, out var s) ? s : null;
    }

    /// <summary>
    /// PR #316 P2. Live-path entry point used by
    /// <c>ExecutionReportProcessor</c>: computes the realized delta
    /// for <paramref name="subAccount"/>'s bucket (master when null)
    /// against THAT BUCKET's avg-cost basis — never against the
    /// aggregate. Then advances the bucket basis using the same
    /// projection rules as <see cref="PnlKeeper.ProjectAvgCost"/>:
    /// same-side fills grow the average, opposing-side closes don't
    /// move avg until flip-through-zero, and a sign flip resets avg
    /// to the fill price.
    ///
    /// <para>
    /// Returns <c>0</c> for opening fills (the leg has no prior
    /// basis), same-side adds, and the no-position case — matching
    /// <see cref="PnlKeeper.ComputeRealizedDelta"/>. Caller is
    /// expected to gate the <c>RealizedPnlEvent</c> emission on a
    /// non-zero return.
    /// </para>
    /// </summary>
    /// <summary>
    /// PR #316 P1.1. Seed the master-bucket avg-cost basis from a
    /// host-startup <see cref="PositionKeeper.SeedIfAbsent"/> entry
    /// so subsequent realised-PnL math on master fills, AND the
    /// daily-statement master-row avg-price, can read the seed
    /// directly from the bucket store rather than falling back to
    /// the aggregate avg (which gets polluted the moment a
    /// sub-account fill mirrors into the aggregate
    /// <see cref="PositionKeeper"/>). No-op when a basis already
    /// exists for the master bucket: mirrors
    /// <see cref="PositionKeeper.SeedIfAbsent"/>'s "warm restart
    /// preserves recovered state" rule (snapshots restore bucket
    /// basis BEFORE seeding runs).
    /// </summary>
    public bool SeedMasterBucketBasisIfAbsent(
        string firmId, string endClient, string symbol, long signedQuantity, decimal avgPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (signedQuantity == 0) return false;
        var key = (firmId, endClient, MasterBucketKey, symbol);
        return _bucketAvgCost.TryAdd(key, new PnlKeeper.AvgCostState(signedQuantity, avgPrice));
    }

    /// <summary>
    /// PR #316 P1 (review). Backfill the per-bucket avg-cost basis from a
    /// legacy snapshot whose <see cref="Persistence.PlatformSnapshot.SubAccountPnlBasis"/>
    /// block is empty or incomplete (pre-#316 snapshots have positions
    /// but no bucket basis at all; partial snapshots may carry basis
    /// for some buckets but not others). Without this seed, the first
    /// closing fill on a restored master/sub bucket whose basis is
    /// absent goes through <see cref="ApplyBucketFill"/>, hits the
    /// "missing basis" branch, treats the close as a FRESH open, and
    /// emits realized P&amp;L = 0 — even though the live (pre-restart)
    /// keeper would have computed the realized delta against the
    /// existing avg-cost basis. The aggregate <see cref="PnlKeeper"/>
    /// already has its own legacy backfill
    /// (<c>SeedAvgCostFromLegacyPositions</c>), but the ER processor
    /// wires its return value as the authoritative event source for
    /// the sub-keeper only when the sub-keeper is unwired — so once
    /// the sub-keeper exists, only its own backfilled basis is
    /// consulted on the close.
    ///
    /// <para>
    /// Mirrors <see cref="PnlKeeper.SeedAvgCostFromLegacyPositions"/>:
    /// idempotent (never overwrites an entry that <see cref="Restore"/>
    /// loaded from <see cref="Persistence.SubAccountPnlBasisSnapshot"/>),
    /// skips zero-quantity rows, and skips degenerate zero-avg rows
    /// (the basis cannot be recovered; opening a leg at zero would
    /// realise phantom P&amp;L on the first close — same treatment as
    /// the aggregate keeper's pass-3 fix).
    /// </para>
    ///
    /// <para>
    /// <b>Seed-price caveat.</b> The master bucket basis is seeded
    /// from the aggregate <see cref="Persistence.PositionSnapshot"/>'s
    /// <c>AverageEntryPrice</c> (best available — when there's no sub
    /// activity yet, aggregate == master). Sub-bucket basis is seeded
    /// from its own <see cref="Persistence.SubAccountPositionSnapshot.AverageEntryPrice"/>;
    /// when the sub-position snapshot pre-dates per-bucket basis
    /// tracking (zero avg), the row is skipped — there's no per-bucket
    /// history to recover and seeding from the aggregate avg would
    /// import pollution from sibling buckets. After at least one
    /// snapshot is taken post-recovery, <see cref="SnapshotBasis"/>
    /// includes the seeded buckets and subsequent recoveries hydrate
    /// them directly without going through this path.
    /// </para>
    /// </summary>
    /// <returns>The number of (bucket, symbol) rows seeded.</returns>
    public int SeedBucketBasisFromLegacyPositions(
        IEnumerable<Persistence.PositionSnapshot> masterPositions,
        IEnumerable<Persistence.SubAccountPositionSnapshot> subPositions)
    {
        ArgumentNullException.ThrowIfNull(masterPositions);
        ArgumentNullException.ThrowIfNull(subPositions);
        var seeded = 0;
        foreach (var p in masterPositions)
        {
            if (p.NetQuantity == 0) continue;
            if (p.AverageEntryPrice <= 0m) continue;
            var key = (p.FirmId, p.EndClientId, MasterBucketKey, p.Symbol);
            if (_bucketAvgCost.TryAdd(key, new PnlKeeper.AvgCostState(p.NetQuantity, p.AverageEntryPrice)))
                seeded++;
        }
        foreach (var p in subPositions)
        {
            if (p.NetQuantity == 0) continue;
            if (p.AverageEntryPrice <= 0m) continue;
            var key = (p.FirmId, p.EndClientId, p.SubAccountId, p.Symbol);
            if (_bucketAvgCost.TryAdd(key, new PnlKeeper.AvgCostState(p.NetQuantity, p.AverageEntryPrice)))
                seeded++;
        }
        return seeded;
    }

    public decimal ApplyBucketFill(
        string firmId, string endClient, SubAccountId? subAccount, string symbol,
        OrderSide side, long fillQuantity, decimal fillPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firmId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (fillQuantity <= 0) return 0m;
        var key = (firmId, endClient, subAccount?.Value ?? MasterBucketKey, symbol);
        if (!_bucketAvgCost.TryGetValue(key, out var current))
        {
            var signed = side == OrderSide.Buy ? fillQuantity : -fillQuantity;
            _bucketAvgCost[key] = new PnlKeeper.AvgCostState(signed, fillPrice);
            return 0m;
        }
        var realized = PnlKeeper.ComputeRealizedDelta(current.NetQuantity, current.AvgPrice, side, fillQuantity, fillPrice);
        var projected = PnlKeeper.ProjectAvgCost(current, side, fillQuantity, fillPrice);
        if (projected.NetQuantity == 0)
            _bucketAvgCost.TryRemove(key, out _);
        else
            _bucketAvgCost[key] = projected;
        return realized;
    }

    public IEnumerable<(string Symbol, decimal Realized)> ForSubAccountDay(string firmId, string endClient, SubAccountId subAccount, DateOnly day)
    {
        foreach (var kv in _realized)
            if (string.Equals(kv.Key.FirmId, firmId, StringComparison.Ordinal)
                && kv.Key.EndClient == endClient
                && kv.Key.SubAccount == subAccount.Value
                && kv.Key.Day == day)
                yield return (kv.Key.Symbol, kv.Value);
    }

    /// <summary>
    /// Lock-side snapshot capture for the two-phase pipeline.
    /// </summary>
    public Persistence.SubAccountPnlSnapshot[] Snapshot()
    {
        var pairs = _realized.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.SubAccountPnlSnapshot>();
        var arr = new Persistence.SubAccountPnlSnapshot[pairs.Length];
        for (var i = 0; i < pairs.Length; i++)
            arr[i] = new Persistence.SubAccountPnlSnapshot(
                pairs[i].Key.FirmId, pairs[i].Key.EndClient, pairs[i].Key.SubAccount,
                pairs[i].Key.Symbol, pairs[i].Key.Day, pairs[i].Value);
        return arr;
    }

    /// <summary>
    /// PR #316 P2. Lock-side capture of per-bucket avg-cost rows.
    /// Persisted additively alongside <see cref="Snapshot"/> so a
    /// snapshot+tail recovery preserves bucket-level basis (and
    /// therefore bucket-level realized correctness on the first
    /// post-restore closing fill). Legacy snapshots without this
    /// block hydrate to an empty basis map; the next fill on each
    /// bucket establishes a fresh basis at the fill price — best-
    /// effort recovery consistent with the pre-#316 (aggregate-only)
    /// behaviour for the no-sub-account case and benign for the
    /// new-in-this-PR sub-account case (no pre-merge data exists).
    /// </summary>
    public Persistence.SubAccountPnlBasisSnapshot[] SnapshotBasis()
    {
        var pairs = _bucketAvgCost.ToArray();
        if (pairs.Length == 0) return Array.Empty<Persistence.SubAccountPnlBasisSnapshot>();
        var buf = new Persistence.SubAccountPnlBasisSnapshot[pairs.Length];
        var n = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            var v = pairs[i].Value;
            if (v.NetQuantity == 0) continue;
            buf[n++] = new Persistence.SubAccountPnlBasisSnapshot(
                pairs[i].Key.FirmId, pairs[i].Key.EndClient, pairs[i].Key.SubAccount,
                pairs[i].Key.Symbol, v.NetQuantity, v.AvgPrice);
        }
        if (n == buf.Length) return buf;
        var trimmed = new Persistence.SubAccountPnlBasisSnapshot[n];
        Array.Copy(buf, trimmed, n);
        return trimmed;
    }

    public void Restore(IEnumerable<Persistence.SubAccountPnlSnapshot> snaps)
        => Restore(snaps, basis: null);

    /// <summary>
    /// PR #316 P2. Restore with bucket basis. <paramref name="basis"/>
    /// is optional for backwards-compat with pre-#316 snapshots and
    /// legacy test fixtures.
    /// </summary>
    public void Restore(
        IEnumerable<Persistence.SubAccountPnlSnapshot> snaps,
        IEnumerable<Persistence.SubAccountPnlBasisSnapshot>? basis)
    {
        ArgumentNullException.ThrowIfNull(snaps);
        _realized.Clear();
        _bucketAvgCost.Clear();
        foreach (var s in snaps)
            _realized[(s.FirmId, s.EndClientId, s.SubAccountId, s.Symbol, s.Day)] = s.RealizedTotal;
        if (basis is not null)
            foreach (var b in basis)
                if (b.NetQuantity != 0)
                    _bucketAvgCost[(b.FirmId, b.EndClientId, b.SubAccountId, b.Symbol)] =
                        new PnlKeeper.AvgCostState(b.NetQuantity, b.AvgPrice);
    }
}
