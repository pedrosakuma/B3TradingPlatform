namespace B3.Trading.Application.Risk;

/// <summary>
/// Stable canonical machine-readable codes for pre-trade risk
/// rejections (#288). Surfaced on <see cref="RiskDecision.Code"/> and
/// propagated through <c>OrderSubmissionResult.Code</c> /
/// <c>OrderModifyResult.Code</c> and into the REST surface so the FE
/// and CVM / drop-copy / observability consumers can branch on a fixed
/// identifier instead of parsing the human-readable reason.
///
/// <para>
/// The default fallback inside <see cref="RiskPipeline.Evaluate"/> is
/// the rejecting <see cref="IRiskCheck.Name"/> (lower_snake_case), so
/// new checks automatically surface a non-null code even when they
/// only call <c>RiskDecision.Reject(reason)</c>. The constants here
/// document the canonical spelling used by the checks that have been
/// audited and stabilised — the FE / clients should depend on these
/// rather than on the free-text reason.
/// </para>
/// </summary>
public static class RiskRejectCodes
{
    /// <summary>
    /// <see cref="Checks.MinTickSizeCheck"/> — price is not a whole
    /// multiple of the instrument's tick size. Maps to FIX-44
    /// <c>OrderExceedsTickSize</c> rejection family.
    /// </summary>
    public const string MinTickSize = "min_tick_size";

    /// <summary>
    /// <see cref="Checks.MinLotSizeCheck"/> — quantity is not a whole
    /// multiple of the instrument's round lot.
    /// </summary>
    public const string MinLotSize = "min_lot_size";

    /// <summary>
    /// OPT-E (#487) — <see cref="Checks.PriceBandCheck"/> — order
    /// price is outside the venue-published dynamic price band
    /// (<c>PriceBand_22</c>). Maps to FIX-44
    /// <c>OrderPriceExceedsCurrentPriceBand</c> family. Distinct from
    /// <see cref="Checks.PriceCollarCheck"/> (static-config fat-finger
    /// collar) because the band is authoritative and intraday — a
    /// rejection here means the venue would have refused the order on
    /// the wire.
    /// </summary>
    public const string PriceBand = "price_band";
}
