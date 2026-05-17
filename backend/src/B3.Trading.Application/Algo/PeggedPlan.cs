using B3.Trading.Domain;

namespace B3.Trading.Application;

/// <summary>
/// Pure helpers for the Pegged orders algo (Q3.3 / #283). The shape
/// mirrors <see cref="PovPlan"/> / <see cref="VwapPlan"/>: every
/// pricing decision is derivable from the parent parameters + a live
/// reference price + the current child price, so the engine and any
/// future scheduler thread can reproduce the same answer without
/// sharing mutable state.
///
/// <para>
/// <b>Target.</b> <c>target = round(ref + offsetTicks * tickSize)</c>.
/// Rounded to <c>tickSize</c> so the venue never sees a sub-tick price.
/// Caller is responsible for supplying a positive tick size.
/// </para>
///
/// <para>
/// <b>Repeg gate.</b> A repeg fires when the absolute distance between
/// the current child price and the target is &gt;= one tick. Strict
/// inequality at the boundary would oscillate against a market that
/// drifts by exactly one tick.
/// </para>
///
/// <para>
/// <b>Price limit.</b> <see cref="ClampToLimit"/> mirrors
/// <see cref="VwapPlan.ClampPrice"/>: for Buy side the engine never
/// crosses above <c>priceLimit</c>; for Sell side it never crosses
/// below. When the clamped target equals the current child price the
/// caller skips the repeg (handled in the engine).
/// </para>
/// </summary>
public static class PeggedPlan
{
    /// <summary>
    /// Reference-price + offset → tick-aligned target. Returns <c>null</c>
    /// when <paramref name="refPrice"/> is <c>null</c> (no live ref).
    /// </summary>
    public static decimal? ComputeTarget(decimal? refPrice, int offsetTicks, decimal tickSize)
    {
        if (tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tickSize), "tickSize must be positive.");
        if (refPrice is null) return null;
        var raw = refPrice.Value + offsetTicks * tickSize;
        // Round to nearest tick (banker's rounding) so the venue never
        // sees a sub-tick price after the offset arithmetic. Using
        // AwayFromZero would bias every other repeg by a tick in
        // pathological cases (ref sitting exactly on a half-tick).
        var ticks = Math.Round(raw / tickSize, MidpointRounding.ToEven);
        return ticks * tickSize;
    }

    /// <summary>
    /// True when the live working slice has drifted at least one tick
    /// away from <paramref name="targetPrice"/>. The <c>&gt;= 1 tick</c>
    /// tolerance is the spec for #283 ("differs from target by ≥ 1 tick").
    /// </summary>
    public static bool IsRepegNeeded(decimal currentPrice, decimal targetPrice, decimal tickSize)
    {
        if (tickSize <= 0m)
            throw new ArgumentOutOfRangeException(nameof(tickSize));
        return Math.Abs(currentPrice - targetPrice) >= tickSize;
    }

    /// <summary>
    /// Applies <paramref name="priceLimit"/> as a hard side-aware clamp.
    /// Mirrors <see cref="VwapPlan.ClampPrice"/>. When the limit is
    /// <c>null</c> the target is returned unchanged.
    /// </summary>
    public static decimal ClampToLimit(decimal targetPrice, decimal? priceLimit, OrderSide side)
    {
        if (priceLimit is null) return targetPrice;
        return side == OrderSide.Buy
            ? Math.Min(targetPrice, priceLimit.Value)
            : Math.Max(targetPrice, priceLimit.Value);
    }
}
