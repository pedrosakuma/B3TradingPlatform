namespace B3.Trading.Application;

/// <summary>
/// Pure helpers for the POV (Percentage of Volume) slice scheduler
/// (Q3.2 / #282). Mirrors the shape of <see cref="VwapPlan"/>: the plan
/// is fully derivable from the parent parameters + the live cumulative
/// market volume + the parent's executed cum, so the scheduler thread
/// and the engine thread can compute the same target without sharing
/// mutable state.
///
/// <para>
/// <b>Slot spacing.</b> The window <c>[startUtc, endUtc)</c> is divided
/// into evaluation slots of <c>tickInterval</c>. Slot <c>k</c> fires at
/// <c>startUtc + k * tickInterval</c>. The window expiry itself is
/// enforced separately (no slot is scheduled at or after <c>endUtc</c>),
/// same convention as <see cref="VwapPlan"/>.
/// </para>
///
/// <para>
/// <b>Slice quantity.</b> At each slot the engine asks "how much do I owe
/// the market to stay at <c>rate</c>?" via <see cref="SliceQty"/>:
/// <c>pending = floor(cumMarketVolume * rate) - executedCum</c>, clamped
/// to the parent's residue. When the gap is below <c>minSliceQty</c> the
/// slot is skipped — the next tick re-evaluates once more volume has
/// printed.
/// </para>
///
/// <para>
/// <b>End-of-window.</b> POV is opportunistic by definition; leftover
/// quantity at <c>endUtc</c> is NOT force-filled. The engine routes the
/// parent to <see cref="B3.Trading.Domain.AlgoStatus.Expired"/> with
/// <see cref="B3.Trading.Domain.AlgoTerminalReason.PovWindowExpired"/>.
/// </para>
///
/// <para>
/// <b>Determinism.</b> Same as <see cref="VwapPlan"/>: offsets are
/// computed in ticks so scheduler and engine threads cannot drift on
/// "is this slot due?".
/// </para>
/// </summary>
public static class PovPlan
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
    /// Quantity to submit at the current evaluation slot.
    /// <c>pending = floor(cumMarketVolume * participationRate) - executedCum</c>,
    /// clamped to <paramref name="remainingQuantity"/>. Returns 0 when
    /// the gap is below <paramref name="minSliceQty"/> — the engine
    /// treats that as "skip this slot, wait for the next tick".
    /// </summary>
    /// <param name="cumMarketVolume">Sum of market trade qty for the
    /// symbol observed in <c>[startUtc, evaluateAtUtc)</c>.</param>
    /// <param name="executedCum">Sum of fills already booked against the
    /// parent.</param>
    /// <param name="remainingQuantity">Parent residue
    /// (<c>totalQty - executedCum</c>).</param>
    /// <param name="participationRate">Target participation in
    /// <c>(0, 1]</c>.</param>
    /// <param name="minSliceQty">Floor on a non-zero slice; pending
    /// below this is deferred to the next tick. Must be &gt;= 1.</param>
    public static long SliceQty(
        long cumMarketVolume,
        long executedCum,
        long remainingQuantity,
        decimal participationRate,
        long minSliceQty)
    {
        if (participationRate <= 0m || participationRate > 1m)
            throw new ArgumentOutOfRangeException(nameof(participationRate),
                "participationRate must be in (0, 1].");
        if (minSliceQty < 1)
            throw new ArgumentOutOfRangeException(nameof(minSliceQty),
                "minSliceQty must be >= 1.");
        if (remainingQuantity <= 0) return 0;
        if (cumMarketVolume <= 0) return 0;

        var target = (long)Math.Floor((decimal)cumMarketVolume * participationRate);
        var pending = target - executedCum;
        if (pending <= 0) return 0;

        var capped = Math.Min(pending, remainingQuantity);
        if (capped < minSliceQty) return 0;
        return capped;
    }

    /// <summary>
    /// Resolves the child LIMIT price. If a <paramref name="priceLimit"/>
    /// is configured the engine never crosses past it; otherwise the
    /// <paramref name="refPrice"/> is used as-is. For Buy side, the
    /// effective price is <c>min(refPrice, priceLimit)</c>; for Sell side,
    /// <c>max(refPrice, priceLimit)</c>. Mirrors <see cref="VwapPlan.ClampPrice"/>.
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
