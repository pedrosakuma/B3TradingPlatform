namespace B3.Trading.Application;

/// <summary>
/// Pure helpers for the VWAP (Volume-Weighted Average Price) slice
/// scheduler (Q3.1 / #281). Mirrors the design of <see cref="TwapPlan"/>:
/// the plan is fully derivable from the parent parameters + the
/// volume-curve estimator's CDF + the parent's current executed cum, so
/// the scheduler and the engine can compute the same target without
/// sharing mutable state.
///
/// <para>
/// <b>Slot spacing.</b> The window <c>[startUtc, endUtc)</c> is divided
/// into evaluation slots of <c>tickInterval</c>. Slot <c>k</c> fires at
/// <c>startUtc + k * tickInterval</c>; the last slot is the largest
/// <c>k</c> such that <c>plannedAtUtc &lt; endUtc</c>. The window expiry
/// itself is enforced separately (no slot is scheduled at or after
/// <c>endUtc</c>), same convention as <see cref="TwapPlan"/>.
/// </para>
///
/// <para>
/// <b>Target curve.</b> <c>targetCumQty(t) = totalQty * F(t)</c> where
/// <c>F</c> is the volume-curve CDF supplied by the caller (the
/// <c>VolumeCurveEstimator</c> in production; an identity / uniform stub
/// in tests). F is clamped to <c>[0, 1]</c>; out-of-window samples
/// saturate at the boundaries.
/// </para>
///
/// <para>
/// <b>Slice quantity.</b> At each slot the engine asks "what should I
/// submit?" via <see cref="SliceQty"/>: it computes the gap between the
/// target cumulative quantity and what has already executed, then applies
/// the per-slice caps (<c>sliceMaxPct</c>, <c>participationCap</c>) and
/// the parent's <c>remainingQuantity</c>. Returns 0 when the parent is
/// ahead of the curve — the engine treats that as "skip this slot, wait
/// for the next tick".
/// </para>
///
/// <para>
/// <b>Determinism.</b> Like <see cref="TwapPlan"/>, the offset is
/// computed in ticks so the same call from the scheduler thread and the
/// engine thread produces byte-identical results. Without this, the two
/// threads could disagree about whether a slot is due and slice-fire
/// would race.
/// </para>
/// </summary>
public static class VwapPlan
{
    /// <summary>
    /// UTC instant at which evaluation slot <paramref name="sliceSeq"/>
    /// is due. Pure function of the parent parameters; no clock side
    /// effect.
    /// </summary>
    public static DateTimeOffset PlannedAtUtc(
        DateTimeOffset startUtc, TimeSpan tickInterval, int sliceSeq)
    {
        if (tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval), "tickInterval must be positive.");
        if (sliceSeq < 0)
            throw new ArgumentOutOfRangeException(nameof(sliceSeq), "sliceSeq must be non-negative.");

        var offsetTicks = tickInterval.Ticks * sliceSeq;
        return startUtc.AddTicks(offsetTicks);
    }

    /// <summary>
    /// Number of evaluation slots in the window. The last slot fires
    /// strictly before <c>endUtc</c>; window expiry is the engine's
    /// separate boundary.
    /// </summary>
    public static int SlotCount(DateTimeOffset startUtc, DateTimeOffset endUtc, TimeSpan tickInterval)
    {
        if (endUtc <= startUtc)
            throw new ArgumentException("endUtc must be after startUtc.", nameof(endUtc));
        if (tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval), "tickInterval must be positive.");

        var windowTicks = (endUtc - startUtc).Ticks;
        // ceil so any non-empty window has at least one slot at startUtc.
        var slots = (int)((windowTicks + tickInterval.Ticks - 1) / tickInterval.Ticks);
        return Math.Max(1, slots);
    }

    /// <summary>
    /// Target cumulative quantity at <paramref name="at"/>:
    /// <c>round(totalQty * cdf)</c>. The caller clamps <paramref name="cdf"/>
    /// to <c>[0, 1]</c>; we still defensively clip to handle floating-
    /// point fuzz from the estimator.
    /// </summary>
    public static long TargetCumQty(long totalQuantity, double cdf)
    {
        if (totalQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalQuantity), "totalQuantity must be positive.");
        if (double.IsNaN(cdf) || cdf <= 0) return 0;
        if (cdf >= 1) return totalQuantity;
        // Round to nearest to keep the target curve close to the analytic
        // value; the per-slice cap downstream limits any rounding error
        // to one share of slice quantity.
        var v = (long)Math.Round(totalQuantity * cdf, MidpointRounding.AwayFromZero);
        return Math.Clamp(v, 0L, totalQuantity);
    }

    /// <summary>
    /// Quantity to submit at the current evaluation slot. Computed as:
    /// <c>min(targetCumQty - executedCum, remainingQty, sliceMaxPct*total,
    /// participationCap*recentMarketVolume)</c>. Returns 0 when the
    /// parent is ahead of the curve — the engine treats that as "skip
    /// this slot, wait for the next tick".
    /// </summary>
    /// <param name="targetCumQty">Output of <see cref="TargetCumQty"/> at
    /// the slot's <c>plannedAtUtc</c>.</param>
    /// <param name="executedCum">Sum of fills already booked against the
    /// parent.</param>
    /// <param name="remainingQuantity">Parent's residue
    /// (<c>totalQty - executedCum</c>); the engine passes
    /// <c>Algo.RemainingQuantity</c> which is monotonic-non-increasing.</param>
    /// <param name="totalQuantity">Parent total (for the sliceMaxPct cap).</param>
    /// <param name="sliceMaxPct">Per-slice fraction cap; null = no cap.</param>
    /// <param name="participationCap">Per-slice fraction of recent market
    /// volume; null = no cap.</param>
    /// <param name="recentMarketVolume">Volume seen in the current
    /// participation window. Pair with <paramref name="participationCap"/>;
    /// ignored when either is null.</param>
    public static long SliceQty(
        long targetCumQty,
        long executedCum,
        long remainingQuantity,
        long totalQuantity,
        decimal? sliceMaxPct,
        decimal? participationCap,
        long recentMarketVolume)
    {
        if (totalQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalQuantity));
        if (remainingQuantity <= 0) return 0;

        var gap = targetCumQty - executedCum;
        if (gap <= 0) return 0;

        var capped = Math.Min(gap, remainingQuantity);

        if (sliceMaxPct is { } pct && pct > 0)
        {
            var maxSlice = (long)Math.Floor((decimal)totalQuantity * pct);
            if (maxSlice < 1) maxSlice = 1; // a 0% cap would freeze the algo
            capped = Math.Min(capped, maxSlice);
        }

        if (participationCap is { } pcap && pcap > 0 && recentMarketVolume > 0)
        {
            var maxByMarket = (long)Math.Floor((decimal)recentMarketVolume * pcap);
            if (maxByMarket < 1) maxByMarket = 1;
            capped = Math.Min(capped, maxByMarket);
        }

        return Math.Max(0, capped);
    }

    /// <summary>
    /// Resolves the child LIMIT price. If a <paramref name="priceLimit"/>
    /// is configured the engine never crosses past it; otherwise the
    /// <paramref name="refPrice"/> is used as-is. For Buy side, the
    /// effective price is <c>min(refPrice, priceLimit)</c>; for Sell side,
    /// <c>max(refPrice, priceLimit)</c>.
    /// </summary>
    public static decimal? ClampPrice(decimal? refPrice, decimal? priceLimit, B3.Trading.Domain.OrderSide side)
    {
        if (priceLimit is null) return refPrice;
        if (refPrice is null) return priceLimit;
        return side == B3.Trading.Domain.OrderSide.Buy
            ? Math.Min(refPrice.Value, priceLimit.Value)
            : Math.Max(refPrice.Value, priceLimit.Value);
    }
}
