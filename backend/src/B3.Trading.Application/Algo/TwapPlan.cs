namespace B3.Trading.Application;

/// <summary>
/// Pure helpers that produce the deterministic slice plan for a TWAP
/// parent (RFC algo-orders-v0 §4.6 + §4.8).
///
/// <para>
/// The plan is intentionally <b>not</b> persisted as a separate artefact —
/// it is fully derivable from <c>(startUtc, endUtc, sliceCount,
/// totalQuantity)</c>. Recovery just re-runs the same functions to
/// reproduce the schedule, so engine and scheduler agree on slice timing
/// and quantity by construction (no drift between the two threads).
/// </para>
///
/// <para>
/// <b>Spacing.</b> Slice <c>k</c> fires at <c>start + k * (end - start) / sliceCount</c>.
/// Slice 0 fires at <c>startUtc</c>; the last slice (<c>sliceCount-1</c>)
/// fires at <c>start + (sliceCount-1)/sliceCount * window</c>. Window
/// expiry is checked separately at <c>endUtc</c> — no slice is scheduled
/// at or after <c>endUtc</c>.
/// </para>
///
/// <para>
/// <b>Quantity rounding.</b> Slices 0..n-2 each carry
/// <c>floor(total/n)</c> and slice n-1 carries the remainder, so the
/// parent total matches exactly. When a <c>lotSize &gt; 1</c> is supplied
/// (issue #518) the distribution is computed in <b>lot units</b> instead:
/// slices 0..n-2 carry <c>floor(totalLots/n) * lot</c> and the last slice
/// carries the remainder, so every slice is a whole multiple of the
/// instrument lot and <c>MinLotSizeCheck</c> never rejects an algo child.
/// The caller (<c>POST /api/algo</c>) guarantees <c>total % lot == 0</c> and
/// <c>total / lot &gt;= sliceCount</c>, so no slice rounds to zero.
/// </para>
/// </summary>
public static class TwapPlan
{
    /// <summary>
    /// UTC instant at which slice <paramref name="sliceSeq"/> is due to
    /// fire. Pure function of the parent parameters; no clock side effect.
    /// </summary>
    public static DateTimeOffset PlannedAtUtc(
        DateTimeOffset startUtc, DateTimeOffset endUtc, int sliceCount, int sliceSeq)
    {
        if (sliceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCount), "sliceCount must be positive.");
        if (sliceSeq < 0 || sliceSeq >= sliceCount)
            throw new ArgumentOutOfRangeException(nameof(sliceSeq),
                $"sliceSeq must be in [0,{sliceCount}).");
        if (endUtc <= startUtc)
            throw new ArgumentException("endUtc must be after startUtc.", nameof(endUtc));

        // Compute the offset in ticks to avoid floating-point drift across
        // recoveries. The plan must be byte-identical between scheduler
        // ticks and post-restart reconciliation.
        var windowTicks = (endUtc - startUtc).Ticks;
        var offsetTicks = windowTicks * sliceSeq / sliceCount;
        return startUtc.AddTicks(offsetTicks);
    }

    /// <summary>
    /// Quantity for slice <paramref name="sliceSeq"/>. All but the last
    /// slice get <c>floor(total/sliceCount)</c>; the last slice carries
    /// the remainder so the parent total reconciles exactly.
    ///
    /// <para>
    /// When <paramref name="lotSize"/> is greater than 1 (issue #518) the
    /// distribution is computed in lot units so every slice is a whole
    /// multiple of the instrument lot: slices 0..n-2 carry
    /// <c>floor(totalLots / sliceCount) * lotSize</c> and the last slice
    /// carries the lot-valid remainder. The caller validates
    /// <c>total % lot == 0</c> and <c>total / lot &gt;= sliceCount</c> at
    /// submit time, so the floor never collapses an interior slice to zero.
    /// </para>
    /// </summary>
    public static long SliceQty(long totalQuantity, int sliceCount, int sliceSeq, long lotSize = 1)
    {
        if (totalQuantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalQuantity), "totalQuantity must be positive.");
        if (sliceCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(sliceCount), "sliceCount must be positive.");
        if (sliceSeq < 0 || sliceSeq >= sliceCount)
            throw new ArgumentOutOfRangeException(nameof(sliceSeq),
                $"sliceSeq must be in [0,{sliceCount}).");
        if (lotSize < 1)
            throw new ArgumentOutOfRangeException(nameof(lotSize), "lotSize must be positive.");

        // Lot-unit distribution (#518): floor in whole lots so no interior
        // slice is an odd lot the venue's MinLotSizeCheck would reject.
        // lotSize == 1 degenerates to the original share-level floor.
        var totalLots = totalQuantity / lotSize;
        var floorQty = (totalLots / sliceCount) * lotSize;
        if (sliceSeq < sliceCount - 1)
            return floorQty;

        // Last slice: pick up whatever the floor-rounding left on the
        // table so sum-of-slices == totalQuantity exactly.
        var prior = floorQty * (sliceCount - 1);
        return totalQuantity - prior;
    }

    /// <summary>
    /// Per-slice floor quantity (RFC §4.8); echoed in the
    /// <c>POST /api/algo</c> error body when validation rejects the
    /// parameters because the implied per-slice quantity is zero.
    ///
    /// <para>
    /// When <paramref name="lotSize"/> is greater than 1 the floor is
    /// computed in lot units (<c>floor(total/lot/sliceCount) * lot</c>),
    /// matching <see cref="SliceQty"/> so the endpoint's zero-check sees
    /// the same lot-aligned interior slice the engine will emit.
    /// </para>
    /// </summary>
    public static long FloorSliceQty(long totalQuantity, int sliceCount, long lotSize = 1)
    {
        if (sliceCount <= 0 || lotSize < 1) return 0;
        var totalLots = totalQuantity / lotSize;
        return (totalLots / sliceCount) * lotSize;
    }
}
