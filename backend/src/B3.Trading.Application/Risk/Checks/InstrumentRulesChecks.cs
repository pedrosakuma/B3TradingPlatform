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
        if (!_directory.TryGetSpec(ctx.Symbol, out var spec) || spec.TickSize is not { } tick || tick <= 0m)
            return RiskDecision.Approve;

        // decimal arithmetic is exact for the values we care about
        // (tick sizes are 0.01 / 0.001 etc., prices have at most 4-6
        // decimals). decimal.Remainder avoids the FP precision trap
        // that would bite a double-based modulus.
        if (decimal.Remainder(ctx.Price.Value, tick) != 0m)
            return RiskDecision.Reject(
                $"price {ctx.Price.Value} is not a multiple of tick size {tick} for {ctx.Symbol}");
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
                $"quantity {ctx.Quantity} is not a multiple of lot size {lot} for {ctx.Symbol}");
        return RiskDecision.Approve;
    }
}
