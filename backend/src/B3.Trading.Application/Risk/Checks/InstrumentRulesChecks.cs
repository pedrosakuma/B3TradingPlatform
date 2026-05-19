namespace B3.Trading.Application.Risk.Checks;

/// <summary>
/// Server-side fat-finger guard (slice 6 of pre-trade risk v2).
///
/// <para>
/// Rejects orders whose limit price is not a whole multiple of the
/// instrument's tick size. B3 enforces tick rules at the venue, but a
/// client-side mistake (extra digit, wrong contract) routinely turns
/// into a malformed order that the venue rejects with a generic
/// reason; catching it here gives the trader a precise message and
/// keeps a misconfigured UI from spamming the gateway.
/// </para>
///
/// <para>
/// #360. Supports the CVM-style tiered tick ladder via
/// <see cref="InstrumentSpec.TickLadder"/>: the active tick is
/// resolved per-price (binary-style scan over canonicalized bands).
/// When no ladder is configured the legacy flat
/// <see cref="InstrumentSpec.TickSize"/> still applies, so symbols
/// not yet migrated to the ladder schema keep the prior behavior
/// and message verbatim.
/// </para>
///
/// <para>
/// <b>Fail-open:</b> symbols missing from
/// <see cref="SymbolDirectory.TryGetSpec"/> approve. The fat-finger
/// checks are additive — never blocking on a symbol whose spec wasn't
/// loaded — so onboarding a new ticker doesn't silently halt trading
/// while ops update the directory.
/// </para>
///
/// <para>
/// Market orders (no price) are skipped — the venue picks the price.
/// </para>
/// </summary>
public sealed class MinTickSizeCheck : IRiskCheck
{
    private readonly SymbolDirectory _directory;
    public MinTickSizeCheck(SymbolDirectory directory) => _directory = directory;

    public int Order => 50; // run before max-quantity / collar so a malformed price fails fast
    public string Name => "min_tick_size";

    public RiskDecision Check(RiskContext ctx)
    {
        if (!ctx.Price.HasValue) return RiskDecision.Approve;
        if (!_directory.TryGetSpec(ctx.Symbol, out var spec)) return RiskDecision.Approve;

        var price = ctx.Price.Value;
        var tick = spec.ResolveTick(price);
        if (tick is not { } t || t <= 0m) return RiskDecision.Approve;

        // decimal arithmetic is exact for the values we care about
        // (tick sizes are 0.01 / 0.001 etc., prices have at most 4-6
        // decimals). decimal.Remainder avoids the FP precision trap
        // that would bite a double-based modulus.
        if (decimal.Remainder(price, t) != 0m)
        {
            // #360. When a ladder is in play surface the band in the
            // reject reason so the trader sees *why* the tick changed
            // ("...in band [10,100)" vs the global flat "...0.05").
            var band = spec.ResolveBand(price);
            var reason = band is { } b
                ? $"price {price} is not a multiple of tick size {t} (band [{b.LowerInclusive},{(b.UpperExclusive?.ToString() ?? "+inf")})) for {ctx.Symbol}"
                : $"price {price} is not a multiple of tick size {t} for {ctx.Symbol}";
            return RiskDecision.Reject(RiskRejectCodes.MinTickSize, reason);
        }
        return RiskDecision.Approve;
    }
}

/// <summary>
/// Server-side fat-finger guard. Rejects orders whose quantity is not
/// a whole multiple of the instrument's lot size (round lot for the
/// equities cash market). Same fail-open posture as
/// <see cref="MinTickSizeCheck"/>: an unconfigured symbol approves.
/// </summary>
public sealed class MinLotSizeCheck : IRiskCheck
{
    private readonly SymbolDirectory _directory;
    public MinLotSizeCheck(SymbolDirectory directory) => _directory = directory;

    public int Order => 50;
    public string Name => "min_lot_size";

    public RiskDecision Check(RiskContext ctx)
    {
        if (!_directory.TryGetSpec(ctx.Symbol, out var spec) || spec.LotSize is not { } lot || lot <= 0)
            return RiskDecision.Approve;

        if (ctx.Quantity % lot != 0)
            return RiskDecision.Reject(
                RiskRejectCodes.MinLotSize,
                $"quantity {ctx.Quantity} is not a multiple of lot size {lot} for {ctx.Symbol}");
        return RiskDecision.Approve;
    }
}
